# Evidence (2026-10-07, cloudserver1)

Host: Debian 13 arm64 VM, 14 vCPUs, 31 GB RAM, ext4 on a virtio disk. Service container: no CPU limit, 7 GiB
memory cap, catalog ~35 GB, Sextant `f3af0c2` (AnalyzerVersion 8), .NET 10.0.12. CPU steal ~4–6% throughout.

## Production job 359 — ProcessStack `main` @ `3a82c57b1`

4 selected solutions → multi-solution union path (`LoadProjectsIndividuallyAsync`), per-solution restore
fallback (`no-macos.slnx` declares solution-specific configuration). Published `partial` (12 platform projects
degraded: macOS, iOS, WPF, Xamarin; expected on Linux). 22:46:17 → 23:13:51 UTC = **27.6 min**.

| Phase | Start → end (UTC) | Duration | Service-container CPU, avg / max (100% = 1 core) | Host idle | I/O wait | Steal |
|---|---|---|---|---|---|---|
| Restore | 22:46:34 → 22:47:56 | 1.4 min | — | — | — | — |
| Load (187 projects) | 22:47:56 → 23:03:10 | 15.2 min | 125% / 250% | 81% | 0.2% | 4.4% |
| Symbols (130 projects) | 23:03:20 → 23:10:47 | 7.5 min | 105% / 245% | 86% | 0.7% | 3.5% |
| Occurrences | 23:10:48 → 23:13:39 | 2.9 min | 497% / 670% | 60% | 0.1% | 4.4% |

**BuildHost churn during the load** (1-second polling for 90 s, 22:50–22:52): **103 distinct BuildHost
processes** (`dotnet --roll-forward LatestMajor /app/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll`)
while 12 projects logged `Loading project i/n`; each ran ~100% of one core and exited.

Raw samples: `/home/jlipsky/sextant-deploy/perf/job359/` (vmstat, docker stats, per-thread `top -H`, process list
every ~5 s) and `job359-phases.txt` (log phase markers).

## Host experiments — same commit, outside the container, serial

The live service kept indexing queued jobs at ~1–5 cores throughout, so these numbers include that contention.

| Experiment | Result |
|---|---|
| X1a `dotnet restore no-macos.slnx`, cold `NUGET_PACKAGES` | 41 s |
| X1b `dotnet build no-macos.slnx --no-restore` (Debug, 130 projects) | 233 s, 0 errors |
| X2 benchmark harness `--corpus external --path no-macos.slnx --document-extractor` (one `OpenSolutionAsync`, parallelism 8, fresh isolated DB) | **737 s total**: load **78 s** (2 BuildHost launches), symbols **443.6 s**, occurrences **195.7 s**, comments 5.5 s, API surface 3.9 s; 124,435 symbols, 656,651 references; peak managed 2.45 GB, peak working set 3.09 GB, peak DB+WAL 207 MB |

Report: `/home/jlipsky/sextant-deploy/perf/x2-report/benchmark-external-20261007-233138.{json,md}`.

## Earlier run — job 342 (ProcessStack `main` @ `4ed8cc35`, AnalyzerVersion 7)

30.7 min: checkout 15 s, restore 79 s, load 16 min 10 s, symbols 8 min 37 s, occurrences 4 min 4 s, publish 13 s.

## Code-level findings (file:line references in the phase files)

- Union load opens each project with `OpenProjectAsync` in a sequential loop; no load concurrency knob;
  `MSBuildWorkspace.Create()` with no properties.
- Symbol pass: plain `foreach` over projects; `GetDeclaredSymbol` on every descendant node (including method
  bodies whose locals/lambdas are then discarded); several renderings per symbol; inserts on the same thread.
- Occurrence pass: one producer awaits each project's `GetCompilationAsync` serially, then analyzes its documents
  with ≤ 8 workers; one consumer persists. `Snippet()` is computed per reference and discarded;
  `DeclarationKey`/`EnclosingMemberKey` recomputed per occurrence without a memo.
- `SourceTextStore.Put` decompresses and re-hashes an existing blob for every unchanged file on every commit.
- SQLite: default `cache_size` (~2 MB) on a 35 GB catalog; the service constructs `IndexDatabase` with
  `IndexWriteOptions.Default`, ignoring configured batch/checkpoint settings.
- Service: one write gate spans the whole job; strict FIFO; coalescing only of identical identities; per-job
  scratch `NUGET_PACKAGES`; fresh `git init` + depth-1 fetch per new commit; no cross-commit reuse except
  submodule provider snapshots.
