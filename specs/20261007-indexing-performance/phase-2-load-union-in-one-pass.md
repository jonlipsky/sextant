# Phase 2 — Load the solution union in one evaluation pass

## Goal

Replace the per-project union load with one evaluation pass, cutting ProcessStack's load from 15.2 minutes to about
2. Fixes #247 and the sequential half of #131.

## Scope

- When more than one solution is selected, generate a solution file for the deduplicated union in job scratch
  (the same way `PackageRestoreRunner` generates `RestoreUnion.proj`) and load it with one `OpenSolutionAsync`.
- Keep per-project fault isolation: if the single load throws or returns an unusable solution, fall back to
  **chunked** loads (one generated solution per chunk of the union, e.g. 25 projects), and only within a failed
  chunk to today's per-project loop.
- Keep the load deadline (`EvaluationTimePlan.LoadDeadline`): with one call there is no between-project check, so
  cancel the call at the deadline and fall back to chunks for what remains, or load in chunks from the start when
  the plan's load budget is small. Chunk boundaries are where the deadline and progress are checked.
- Keep deterministic project order: the generated solution lists projects in union first-appearance order; the
  resulting `Solution` is re-ordered to the union order before extraction if Roslyn does not preserve it.
- `$(SolutionDir)` and friends (#131): with a generated solution they point at scratch. Pass, per project, the
  globals of its owning solution (the last declaring solution, the rule the restore union already uses) as
  per-project global properties where MSBuildWorkspace allows; otherwise document the difference, measure it on the
  monorepo and ProcessStack, and record it in coverage.
- Same treatment for the single-solution path's fallback (`LoadPerProjectAsync`): chunked before per-project.

## Technical design / files

- `Sextant.Indexer/MultiSolutionLoader.cs:120-167` (branching, union), `SolutionLoader.cs:151-245`
  (`LoadProjectsIndividuallyAsync`, `LoadPerProjectAsync`), `SolutionLoader.cs:40,103,209`
  (`MSBuildWorkspace.Create()` sites).
- New `UnionSolutionWriter` (generates `.slnx`, Debug|AnyCPU only, mirroring `PackageRestoreRunner`'s conditions
  for when a union can be represented faithfully: `CanPreserveSolutionGlobals`, contained project references).
  When the union cannot be represented, keep today's path and log why (as restore does).
- `ReconcileLoads` stays the single place that turns failures into skipped/degraded entries; the generated solution
  must not change attribution (failures are keyed by project path).
- Investigate first, with Phase 1's counter: why per-project loads launch ~8.6 BuildHosts each (one per
  `LoadProjectInfoAsync`? per TFM? per referenced project?). If a cheaper fix exists inside the per-project path
  (e.g. reusing one BuildHost), it may complement the generated solution for the fallback path.

## Acceptance criteria

1. ProcessStack's union load on cloudserver1 takes **≤ 2 minutes** (from 15.2) with ≤ 5 BuildHost launches when no
   project fails.
2. Parity: the same declared/loaded/skipped/degraded project sets as the per-project path on ProcessStack and the
   monorepo (`elevenworks/monorepo`, 61 solutions), except documented `$(SolutionDir)` differences; symbol and
   reference counts within ±0.5% with the differences explained.
3. A union where one project's evaluation throws still loads every other project (chunk fallback), proven by a test
   with a malformed project in a multi-solution fixture; the skipped list names it.
4. A load that exceeds its deadline publishes partial with the projects loaded so far, as today.
5. Project order is identical across runs (determinism test over the generated solution).

## Notes / risks / dependencies

- Depends on Phase 1 for measurement. Changes what can be published (`$(SolutionDir)`, evaluation context): ship
  with Phases 3–4 under one `AnalyzerVersion` bump.
- Memory: one `OpenSolutionAsync` holds all projects' evaluations in one BuildHost; X2 peaked at 3.1 GB working set
  for 130 projects in-process. Phase 1 adds BuildHost memory accounting; the sandbox memory budget must count it.
- If the BuildHost churn turns out to be Roslyn behavior worth reporting upstream, file it there too.
