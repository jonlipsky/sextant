# Phase 1 — Measure every indexing phase

## Goal

Every later phase must state its effect in numbers the service itself records. Today the only phase timings are
the benchmark harness's (fresh DB, single-solution path) and log-line timestamps; production attribution in this
spec was reconstructed by sampling `docker stats` and grepping logs. Make the service and the harness report the
same per-phase, per-project timings, and make the harness able to reproduce the service's union path.

## Scope

- **Job phase timings**, recorded on the job and in `/control/metrics`: checkout, SDK-pin overlay, restore, load,
  coverage scan, symbols, occurrences, comments, dependencies, API surface, publish. Wall time and process CPU time
  per phase (CPU ÷ wall = achieved parallelism).
- **Per-project timings** inside load and extraction: `OpenProjectAsync` wall time (split: opened directly vs pulled
  in through references), compilation time, analysis time, persist time (time the single consumer spends in SQLite
  per project), rows written.
- **BuildHost accounting**: count of BuildHost process launches during the load, and their peak resident memory
  (sampled from `/proc`, since the sandbox watchdog only sees the service process today).
- **Harness `--service-path` mode**: drive `MultiSolutionLoader` with solution selection as the service does
  (union path for multiple solutions), restore through `PackageRestoreRunner`, and optionally write into a copy of
  a large existing catalog (`--catalog <path>`) so DB-size effects are measurable.
- **Baseline record**: run the harness and one production job on cloudserver1 and record both in
  `docs/benchmarks.md` (this spec's evidence numbers are the starting point).

## Technical design / files

- `Sextant.Indexer/IndexOrchestrator.cs`: phase boundaries already exist (`registering_projects`,
  `extracting_symbols`, `extracting_occurrences`, …); add a `PhaseTimer` that records wall + CPU
  (`Process.TotalProcessorTime` deltas) and a per-project breakdown; split persist time from analysis inside
  `ParallelExtractionPipeline` (consumer side) and in the symbol loop (`IndexOrchestrator.cs:679-760`).
- `Sextant.Indexer/SolutionLoader.cs` / `MultiSolutionLoader.cs`: time each `OpenProjectAsync` and
  `OpenSolutionAsync`; count BuildHost launches by observing child processes of the service (Linux: scan
  `/proc/*/cmdline` for `BuildHost.dll` with the service's session; best effort, reported as a count).
- `Sextant.Service/LocalIndexerSnapshotWorker.cs`: wrap checkout, SDK pin, restore, load and coverage scan.
- Persist the timings as a JSON column on the job (or `snapshot_job_diagnostics` rows with a `timing` code) and
  surface them in `GET /control/status/{job}` and aggregated in `/control/metrics` (p50/p95 per phase over the
  recent window, alongside `recent_jobs`).
- `tests/Sextant.Benchmarks`: `--service-path`, `--catalog`; report BuildHost launches and CPU/wall per phase.

## Acceptance criteria

1. `GET /control/status/{job}` returns wall and CPU time for every phase above, plus the top 10 slowest projects for
   load and for extraction, and the BuildHost launch count.
2. The harness reproduces a service-path run of ProcessStack and reports the same phase structure; its load-phase
   time for the union path is within 20% of production's on the same host.
3. Instrumentation overhead is ≤ 2% of a full index (measured with timing on vs off).
4. `docs/benchmarks.md` records the baseline (harness and production) in these terms.

## Notes / risks / dependencies

- Ships first and alone. No identity change.
- CPU time includes the service process only; BuildHost and restore CPU are separate processes and are reported
  as their own counters.
- The 90-second BuildHost sample (103 launches for 12 projects) should be confirmed over a whole load before
  Phase 2 picks its design; this phase provides that.
