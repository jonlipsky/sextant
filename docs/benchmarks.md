# Indexing Benchmarks & Diagnostics

Phase 1 of the scalable-indexing initiative adds a reproducible harness that measures how long
indexing takes, how many rows it produces (and how many are duplicates), how much disk and memory
it consumes, and what workspace diagnostics Roslyn reports. The goal is a trustworthy **baseline**
so later phases can prove they made indexing faster and smaller.

The harness lives in `tests/Sextant.Benchmarks` (a console app) with correctness fixtures in
`tests/Sextant.Benchmarks.Tests` (run under `dotnet test`).

## Running the harness

```bash
dotnet run --project tests/Sextant.Benchmarks -- --corpus <name> [options]
```

Each run writes two files to the output directory:

- `benchmark-<corpus>-<utcstamp>.json` — machine-readable report (snake_case, schema-versioned).
- `benchmark-<corpus>-<utcstamp>.md` — human-readable summary.

### Corpora

| `--corpus` | Description | Restore | Incremental pass |
|---|---|---|---|
| `self` | Sextant's own solution (`Sextant.slnx`). Reproduces the recorded baseline. | assumed built | skipped (never mutates the working tree) |
| `correctness` | A small generated solution exercising duplicate member names, overloads, partial types, a project reference, and a multi-target project. | yes | yes |
| `large` | A generated N-project solution (chained references) for scale testing. Tunable with `--large-projects`, `--large-types`, `--large-methods`. | yes | yes |
| `external` | An opt-in path to a private solution via `--path`. **Always redacted.** | optional | skipped |

Generated corpora are written under `--work` (default: a temp directory) and are **never** written
inside the Sextant source tree. Generation is byte-for-byte deterministic.

### Options

| Option | Default | Meaning |
|---|---|---|
| `--corpus <name>` | `correctness` | Which corpus to run. |
| `--path <solution>` | — | Solution/project path (required for `external`; overrides discovery for `self`). |
| `--out <dir>` | `./benchmark-results` | Report output directory. |
| `--work <dir>` | temp | Scratch dir for generated corpora and isolated databases. |
| `--no-restore` | off | Skip `dotnet restore` before loading. |
| `--no-incremental` | off | Skip the incremental pass (generated corpora only). |
| `--redact` | off | Strip identifying data (implied for `external`). |
| `--interval-ms <n>` | `50` | Resource sampling interval. |
| `--large-projects <n>` | `25` | Project count for the `large` corpus. |
| `--large-types <n>` | `8` | Types per project for the `large` corpus. |
| `--large-methods <n>` | `6` | Methods per type for the `large` corpus. |
| `--machine <label>` | machine name | Machine label recorded in the report (cleared when redacted). |

### Examples

```bash
# Reproduce the baseline against Sextant's own solution.
dotnet run --project tests/Sextant.Benchmarks -- --corpus self --out ./benchmark-results

# Small correctness corpus (full + incremental), used by CI.
dotnet run --project tests/Sextant.Benchmarks -- --corpus correctness --out ./benchmark-results

# Large synthetic solution.
dotnet run --project tests/Sextant.Benchmarks -- --corpus large --large-projects 50 --out ./benchmark-results

# Private monorepo, redacted.
dotnet run --project tests/Sextant.Benchmarks -- --corpus external --path /path/to/Big.sln --out ./benchmark-results
```

## What is measured

### Timing
- `solution_load_ms` — time to load the Roslyn solution (excluded from the indexing total).
- `total_duration_ms` — wall-clock time for indexing.
- Per-phase `duration_ms` and `projects_processed`, in execution order, each with a terminal `status`.

### Rows and duplication
- `projects`, `symbols`, `references`, `relationships`, `call_graph_edges`, `comments`.
- `distinct_reference_occurrences` — reference rows deduplicated by the occurrence key
  **`(symbol_id, file_path, line, reference_kind)`**.
- `duplicate_reference_rows` = `references − distinct_reference_occurrences`.
- `duplicate_reference_ratio` — duplicates as a fraction of all reference rows.

The duplicate-occurrence metric is the headline signal for later phases: the current extractor
re-emits the same semantic occurrence many times.

### Storage — final vs. peak (important)
- `final_db_bytes` — the main database file size **after** a `PRAGMA wal_checkpoint(TRUNCATE)`.
- `final_wal_bytes` — the write-ahead log size at the end of the run, before the checkpoint.
- `final_shm_bytes` — the shared-memory index (`-shm`) size at the end of the run.
- `peak_db_plus_wal_bytes` — the **transient** maximum of main-db-plus-WAL sampled on a timer
  during the run. This is typically far larger than the final database because the WAL grows
  before it is checkpointed.
- `peak_wal_bytes` / `peak_shm_bytes` — the transient maxima of the WAL and SHM files alone,
  sampled on the same timer. `peak_wal_bytes` is the headline signal Phase 3 bounds: batched,
  checkpoint-bounded writes keep it near the size of one active batch instead of the whole run.
- `peak_staged_artifact_bytes` — the transient maximum of all on-disk artifacts a staging
  generation holds (main DB + WAL + SHM) while a new index is being built and not yet published.

The report always distinguishes these two so a small final database does not hide a large transient
disk footprint. Later phases must reduce the **peak**, not just the final size.

### Memory
- `peak_managed_bytes` — peak managed heap (`GC.GetTotalMemory`).
- `peak_working_set_bytes` — peak process working set.

### Diagnostics
- `workspace_diagnostics` — captured Roslyn workspace-failure messages (bounded sample).
- `workspace_diagnostic_count` — the full count (may exceed the retained sample).

### Status and partial results
Run status is one of `running`, `completed`, `cancelled`, `failed`. Phase metrics are recorded as
each phase starts, so **cancelled and failed runs still report every phase completed up to the
interruption**, plus the rows written so far. The CLI exits non-zero unless every requested run
(the full pass, and the incremental pass when applicable) completed.

## Recorded baseline (Sextant self-index, 13 projects)

Measured before this initiative on the reference machine. Reproduce with `--corpus self`; absolute
timings vary by hardware, but the ratios and volumes should hold.

| Metric | Baseline |
|---|---|
| Total indexing | ~33 s |
| Reference phase alone | ~16.5 s |
| Reference rows | 14,375 |
| Duplicate reference rows | ~10,872 (~75%) |
| Final database | ~8.8 MB |
| Peak DB + WAL (transient) | ~293 MB |

## Reduction targets

Every report embeds the initiative's acceptance targets under `targets`:

- **Runtime:** initial indexing at least **5× faster** than the baseline.
- **Peak disk:** peak DB+WAL at least **70% lower** than the baseline.

A later phase that cannot meet a target must record a revised threshold with rationale rather than
silently changing it.

## Phase 3 result — bounded, batched writes

Phase 3 replaces the per-row implicit-transaction write path with a single-writer unit-of-work
(`IndexWriteSession`) that reuses prepared commands, commits at project/document batch boundaries,
bounds the WAL with `wal_autocheckpoint` + `journal_size_limit`, and checkpoint-truncates at the end
of the run. Data for a run stages under an `index_runs` generation and is published atomically, so a
cancelled or crashed run never replaces the last complete index.

Reproduced with `--corpus self` on the same machine, before (Phase 2 base) vs. after (Phase 3);
absolute sizes vary by hardware and corpus, but the **WAL-to-final-DB ratio** is the invariant:

| Metric | Before (per-row) | After (batched) |
|---|--:|--:|
| Final database | 5.23 MB | 6.04 MB |
| Final WAL | 81.55 MB | 8 KB |
| Peak WAL | ~81.6 MB | 5.97 MB |
| Peak DB + WAL | 86.72 MB | 12.01 MB |
| Peak WAL ÷ final DB | ~15.6× | ~1.0× |

Peak DB+WAL drops **~86%** (target: ≥70%), and the WAL is no longer tens-of-times the final database
— the headline amplification the phase set out to remove. The FTS5 per-row triggers stay active in
the "after" run yet peak WAL remains ~1× the final DB, so the trigger writes are absorbed into each
batch's transaction; a bulk-defer/rebuild of FTS is therefore deferred to the schema phase (Phase 7)
rather than changed here.

**Scope of "published atomically."** Phase 3's atomicity is a *pointer* guarantee: the last-complete
generation flip (`index_runs.status`) commits inside the final write transaction, so `GetLastCompleteRun`
never returns a complete run without its committed data, and a cancelled/crashed run never advances that
pointer. It is **not** yet physical multi-version isolation — readers still query the shared symbol and
reference tables, so a reader running *during* a full re-index can observe a partially rebuilt (hybrid)
state, and a crash leaves already-committed batches in place for recovery to reconcile. Never-observe-a-
hybrid isolation across generations is Phase 9 (immutable snapshots). Two caveats on the WAL bound:
`wal_autocheckpoint` is a trigger, not a hard cap (peak WAL ≈ one active batch, not a strict ceiling),
and a long-lived concurrent reader pins the WAL tail and defers `wal_checkpoint(TRUNCATE)` — the bound
holds under the normal short-reader workload.

## Redaction guarantees

For the opt-in `external` corpus (and any run with `--redact`), the report is structurally scrubbed
before it is written:

- `machine_description` and `git_commit` are cleared.
- All captured diagnostic message bodies are dropped (only the **count** is kept).
- `failure_reason` is replaced with `redacted`.
- The corpus name is forced to `external`.
- Free-text progress logging is suppressed, so project, solution, and file names never reach the
  console or CI logs, and an uncaught error prints only its exception type.

Because every other field is an aggregate number, a redacted report contains no absolute paths,
repository names, symbol names, or source snippets. `redacted: true` marks such reports.

## Correctness fixtures and CI

`tests/Sextant.Benchmarks.Tests` runs under the normal test command and validates the harness
itself (deterministic corpus generation, duplicate-occurrence counting, final-vs-peak storage,
redaction, cancellation preserving partial phases, and an end-to-end full + incremental run on the
correctness corpus):

```bash
dotnet test Sextant.slnx --no-build --filter "FullyQualifiedName~Sextant.Benchmarks.Tests"
```

There is no CI workflow in this repository yet. When one is added, the benchmark can be wired in as
two steps — run the fixtures, then produce and upload an artifact:

```yaml
# Example GitHub Actions steps (no workflow is committed yet):
- name: Benchmark correctness fixtures
  run: dotnet test Sextant.slnx --filter "FullyQualifiedName~Sextant.Benchmarks.Tests"

- name: Produce baseline benchmark report
  run: dotnet run --project tests/Sextant.Benchmarks -- --corpus self --out $RUNNER_TEMP/benchmark-results

- name: Upload benchmark report
  uses: actions/upload-artifact@v4
  with:
    name: indexing-benchmark
    path: ${{ runner.temp }}/benchmark-results
```
