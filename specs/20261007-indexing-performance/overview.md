# Indexing performance

**Status:** Draft · **Date:** 2026-10-07 · **Owner:** Jon Lipsky

## Why this exists

A full service index of a large repository takes far longer than compiling it. elevenworks/ProcessStack
(187 projects including submodules, ~9,700 C# files, ~1.48 M lines) takes **27.6 minutes** to index on
cloudserver1, while a clean `dotnet restore` + `dotnet build` of the same commit on the same host takes
**4.6 minutes**, and its CI build step takes 59 seconds. Because the service indexes every delivered commit
in full, one at a time, behind one writer, the cost compounds: a single-line push costs a full index, three
pushes to the same branch cost three, and a queue of feature-branch pushes delays the default branch by hours.
After an identity change (an `AnalyzerVersion` bump) the nightly reconcile re-indexes every live branch of every
watched repository, which on cloudserver1 is 97 branches.

The goal: a full index should cost a modest margin over a parallel compile, and the service should run far
fewer full indexes.

## What we measured (see [evidence.md](evidence.md))

Production job 359 (ProcessStack `main` @ `3a82c57b1`, service container, 2026-10-07), against an experiment on
the same host and commit:

| Phase | Production (container, union path, 187 projects) | Host experiment (one `OpenSolutionAsync`, 130 projects, fresh DB) | Production CPU (cores) |
|---|---|---|---|
| Restore | 1.4 min | 0.7 min (cold cache) | parallel |
| **Load** | **15.2 min** | **1.3 min** | **1.25** |
| **Symbols** | **7.5 min** | **7.4 min** | **1.05** |
| Occurrences | 2.9 min | 3.3 min | ~5 |
| **Total** | **27.6 min** | **12.3 min** | host idle 81–86% during load/symbols |
| Compile floor | | 4.6 min (`restore` 41 s + `build` 233 s) | |

Findings:
1. **The load is per-project process churn.** The multi-solution union path opens one project at a time with
   `OpenProjectAsync`; production launched **103 BuildHost processes while opening 12 projects** in a 90-second
   sample (~8.6 per project). One `OpenSolutionAsync` over the same code launched **2** and took 78 s. This also
   explains #247's "4x slower in prod": it is the load path, not the container.
2. **The symbol pass is single-threaded** (1.05 cores for 7.5 min), in the container and on the host alike.
3. **Extraction is not storage- or hardware-bound.** Symbols and occurrences took the same time against the
   35 GB production catalog as against a fresh database; I/O wait ~0%, CPU steal ~4%.
4. **The system multiplies the cost**: one write gate spans clone→publish, the queue is strict FIFO with no
   coalescing (three MixAndMatch `main` commits were all queued for full indexes), each job starts with a cold
   NuGet cache and a fresh depth-1 clone, and nothing is reused between commits.

## The model / approach

Two halves, in order:

- **Make one index cheap** (phases 1–5): measure every phase, then remove the three ceilings the measurements
  show, in order of size: the per-project load (one evaluation pass over the union), the single-threaded
  symbol pass (the same bounded, deterministic parallel pipeline the occurrence pass already uses), and the
  per-occurrence waste and serial compilations. Tune the writer only where Phase 1's persist timings say it
  matters.
- **Run fewer full indexes** (phases 6–9): keep caches between jobs, skip queued commits a newer commit of the
  same branch has superseded, prioritize default branches, and reuse unchanged projects from the branch's
  previous snapshot instead of re-extracting them.

Every change keeps Sextant's invariants: deterministic output (row order and content identical to a serial
run), the single writer, immutable published snapshots, bounded memory, and honest coverage. A change that can
alter what a commit publishes bumps `AnalyzerVersion` (the repository's identity convention); identity-changing
phases should ship in **one** release so operators pay the re-index burst once.

## What already exists (build against, not greenfield)

- `ParallelExtractionPipeline` (Phase 6 of the scalable-extraction epic): bounded per-document parallelism with
  a deterministic ordinal merge and a single persisting consumer; today only the occurrence pass uses it.
- `SolutionLoader.LoadSolutionResilientlyAsync` (one `OpenSolutionAsync`) and the per-project
  fault-isolation fallback `LoadPerProjectAsync`; `MultiSolutionLoader`'s deterministic, deduplicated union.
- `PackageRestoreRunner`'s generated `RestoreUnion.proj`: the precedent for generating a union artifact in
  scratch.
- `IndexWriteSession` batching, `IndexWriteOptions` (not honored by the service's catalog writer today), the benchmark harness
  (`tests/Sextant.Benchmarks`, per-phase `duration_ms`, peak memory/disk), `EvaluationTimePlan` phase deadlines.
- Overlay `MapProject` sharing of base project-version rows, and submodule provider-snapshot dedup: the
  precedents for cross-snapshot reuse.
- Related issues: #247 (union load slow in prod), #131 (union opens projects sequentially, `$(SolutionDir)`
  unset), #44 (incremental vs immutable snapshots), #146 (unbounded test subprocess helpers).

## Non-goals

- Distributed or multiple concurrent writer nodes. The single writer stays.
- Out-of-process sandbox isolation (#76), except where a phase must account for BuildHost memory.
- Query-side performance (MCP tool latency); this initiative is about producing snapshots.
- Changing what is indexed (symbol/reference semantics) beyond removing work whose output is discarded.
- Local daemon/CLI incremental indexing, except where shared code improves it for free.

## Phases

| # | Title | Outcome | Spec |
|---|---|---|---|
| 1 | Measure every indexing phase | Per-phase and per-project timings (restore, load, compile, extract, persist) in job status, metrics and the harness; a service-path benchmark; recorded baseline | [phase-1](phase-1-measure-every-phase.md) |
| 2 | Load the solution union in one evaluation pass | Union loads through one generated solution; ProcessStack load 15 min → ~2 min | [phase-2](phase-2-load-union-in-one-pass.md) |
| 3 | Parallelize the symbol pass | Symbol pass uses the bounded deterministic pipeline; 7.5 min → ~2 min | [phase-3](phase-3-parallel-symbol-pass.md) |
| 4 | Trim occurrence-pass work and overlap compilations | No discarded per-occurrence work; next project's compilation overlaps analysis | [phase-4](phase-4-trim-occurrence-pass.md) |
| 5 | Tune the catalog writer | Service honors write options; cache/checkpoint sized for a multi-GB catalog, by measurement | [phase-5](phase-5-tune-catalog-writer.md) |
| 6 | Keep package and git caches between jobs | Per-repository NuGet cache and incremental fetch; restore and checkout near-free for a new commit | [phase-6](phase-6-persistent-caches.md) |
| 7 | Coalesce superseded commits and prioritize the queue | A queued commit superseded by a newer one on its branch is skipped; default branches first | [phase-7](phase-7-coalesce-and-prioritize-queue.md) |
| 8 | Reuse unchanged projects across commits | A new commit re-extracts only changed projects and their dependents | [phase-8](phase-8-cross-commit-project-reuse.md) |
| 9 | Prepare the next job outside the writer | Checkout, restore and load of job N+1 overlap job N; decided after phases 2–8 | [phase-9](phase-9-pipeline-job-preparation.md) |

Phases 2–3 change what a commit can publish (load path, symbol walk), so they should be released together under
one `AnalyzerVersion` bump; Phase 4 is output-neutral but ships in the same release. Phase 1 ships first and alone; every later phase states its result in Phase 1's terms.

## Acceptance criteria

1. On cloudserver1, a full service index of ProcessStack `main` takes **≤ 8 minutes** (from 27.6), measured by
   Phase 1's instrumentation, with the load **≤ 2 minutes** and the symbol pass **≤ 2.5 minutes**.
2. **Parity:** for the same commit, the new path publishes the same projects, symbol count and reference count as
   the old path, except for differences each phase documents and justifies (e.g. `$(SolutionDir)`-dependent
   evaluation); determinism tests (`ParallelDeterminismTests` and successors) pass at every parallelism setting.
3. Peak memory stays within the service's configured budget, **including BuildHost processes**, at default
   settings on a 7 GiB container.
4. Three commits pushed to one branch while the writer is busy produce **at most two** full indexes (the in-flight
   one and the newest), and a default-branch ensure is never queued behind feature-branch webhook work.
5. A commit that changes one leaf project of ProcessStack publishes in **≤ 25%** of a full index's time, with
   identical query results to a full index of that commit (Phase 8; the threshold is confirmed or revised at its
   design gate).
6. The benchmark harness can reproduce the service's union path, and `docs/benchmarks.md` records the before/after
   numbers for each phase.
