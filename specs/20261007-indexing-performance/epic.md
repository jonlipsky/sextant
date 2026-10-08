# Epic #266 (filed)

**Filed 2026-10-07:** epic #266; sub-issues #267–#275 (phases 1–9, in order).

Labels used: the epic carries `enhancement` + `epic`; every sub-issue carries `enhancement` (the canonical taxonomy
was applied to the repo by `/bootstrap-repo` the same day).

---

## Epic #266: Indexing performance — index near compile speed, and index less

**Labels:** `enhancement`

A full service index of elevenworks/ProcessStack (187 projects, ~1.48 M lines) takes **27.6 min** on cloudserver1;
a clean restore + build of the same commit on the same host takes **4.6 min**. Measured causes (spec:
`specs/20261007-indexing-performance/`):

- **Load, 15.2 min:** the union path opens projects one at a time, launching ~8.6 BuildHost processes per project; one
  `OpenSolutionAsync` over the same code took 78 s with 2 launches (#247, #131).
- **Symbols, 7.5 min:** a single-threaded pass (~1 core).
- **Occurrences, 2.9 min:** serial compilations and discarded per-occurrence work.
- **System:** one gate spans the whole job, strict FIFO with no coalescing of superseded commits, cold NuGet cache and
  fresh clone per job, no reuse between commits.

Targets: ProcessStack full index ≤ 8 min; ≤ 2 full indexes for 3 rapid pushes to one branch; a one-project change
publishes in ≤ 25% of a full index.

- [ ] #267 Measure every indexing phase
- [ ] #268 Load the solution union in one evaluation pass
- [ ] #269 Parallelize the symbol pass
- [ ] #270 Trim occurrence-pass work and overlap compilations
- [ ] #271 Tune the catalog writer
- [ ] #272 Keep package and git caches between jobs
- [ ] #273 Coalesce superseded commits and prioritize the queue
- [ ] #274 Reuse unchanged projects across commits
- [ ] #275 Prepare the next job outside the writer

Release note: Phases 2–3 can change what a commit publishes; ship them together under one `AnalyzerVersion` bump so
operators pay the re-index burst once. Phase 4 is output-neutral and ships in the same release.

---

## Sub-issue: Measure every indexing phase
**Labels:** `enhancement` · Part of #266 · Spec: `phase-1-measure-every-phase.md`

Record wall and CPU time per phase (checkout, SDK pin, restore, load, scan, symbols, occurrences, comments,
dependencies, API surface, publish), per-project load/compile/analyze/persist times and BuildHost launch counts in
job status and `/control/metrics`; add a service-path mode to the benchmark harness; record the baseline.
Acceptance: phase-1 file, criteria 1–4. Ships first and alone.

## Sub-issue: Load the solution union in one evaluation pass
**Labels:** `enhancement` · Part of #266 · Fixes #247, part of #131 · Spec: `phase-2-load-union-in-one-pass.md`

Load the deduplicated union through one generated solution and one `OpenSolutionAsync`, with chunked then
per-project fallback, deadline checks at chunk boundaries, deterministic order and documented `$(SolutionDir)`
handling. Acceptance: ProcessStack load ≤ 2 min with ≤ 5 BuildHost launches; parity on ProcessStack and the monorepo.

## Sub-issue: Parallelize the symbol pass
**Labels:** `enhancement` · Part of #266 · Spec: `phase-3-parallel-symbol-pass.md`

Run symbol extraction through the bounded deterministic pipeline, stop descending into bodies whose symbols are
discarded, memoize keys and renderings. Acceptance: ProcessStack symbols ≤ 2.5 min at ≥ 4 cores; byte-identical
output at every parallelism.

## Sub-issue: Trim occurrence-pass work and overlap compilations
**Labels:** `enhancement` · Part of #266 · Spec: `phase-4-trim-occurrence-pass.md`

Drop the discarded `Snippet()`, memoize declaration/enclosing-member keys, collapse paired semantic calls, overlap the
next project's compilation (bounded), and stop re-decompressing unchanged source-text blobs. Acceptance:
ProcessStack occurrences ≤ 1.5 min; identical rows.

## Sub-issue: Tune the catalog writer
**Labels:** `enhancement` · Part of #266 · Spec: `phase-5-tune-catalog-writer.md`

Honor `IndexWriteOptions` in the service; size `cache_size`/`mmap_size`/checkpointing for a multi-GB catalog; defer
FTS and drop unused write-heavy indexes only if measurement warrants. Acceptance: persist time −30% or recorded as
< 10% of extraction.

## Sub-issue: Keep package and git caches between jobs
**Labels:** `enhancement` · Part of #266 · Spec: `phase-6-persistent-caches.md`

Per-repository persistent NuGet cache (no cross-repository poisoning) and incremental git fetch from a persistent
object store, keeping credential hardening. Acceptance: restore ≤ 15 s after a previous job; checkout transfers only new
objects; isolation test across repositories.

## Sub-issue: Coalesce superseded commits and prioritize the queue
**Labels:** `enhancement` · Part of #266 · Spec: `phase-7-coalesce-and-prioritize-queue.md`

Supersede queued ensures for the same repository/branch with the newest one (new terminal job status `coalesced`, CAS
carried forward), and add priority lanes (default branch and user ensures first). Acceptance: 3 rapid pushes produce
≤ 2 indexes; default branch never queued behind feature webhooks.

## Sub-issue: Reuse unchanged projects across commits
**Labels:** `enhancement` · Part of #266 · Related #44 · Spec: `phase-8-cross-commit-project-reuse.md`

Design gate first (fingerprint, closure model, retention, provenance), then map unchanged project versions from the
branch's previous snapshot into the new one. Acceptance: one-leaf-project change publishes in ≤ 25% of a full index;
differential equivalence test against a full index.

## Sub-issue: Prepare the next job outside the writer
**Labels:** `enhancement` · Part of #266 · Spec: `phase-9-pipeline-job-preparation.md`

Optional, decided by measurement after Phases 2–8: per-commit worktrees, per-process sandbox environment, a
preparation slot ahead of the single writer. Acceptance: overlapped preparation, no cross-job secret leakage, memory
within budget.
