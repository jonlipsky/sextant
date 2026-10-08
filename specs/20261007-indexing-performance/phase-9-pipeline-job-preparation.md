# Phase 9 — Prepare the next job outside the writer

## Goal

One write gate spans the whole job, from checkout through publish, so while one job extracts, the next cannot even
clone. Prepare job N+1 (checkout, restore, load) while job N extracts and publishes. **Decision gate:** after
Phases 2–8, measure what fraction of a job is preparation; do this phase only if it is still worth it (e.g. ≥ 25% of
job time, or the queue routinely holds more than one job).

## Scope

- Per-commit worktrees instead of the single shared checkout directory and its atomic swap.
- Set the sandbox's environment per child process (BuildHost, restore) instead of process-wide, so two jobs'
  evaluations can coexist (`SandboxedEnvironmentScope` is "safe only because worker execution is serialized").
- Split the gate: a preparation slot (bounded to 1 ahead) and the single writer slot; extraction and publish stay
  under the writer.
- Memory: two loaded jobs at once must fit the budget, BuildHosts included; preparation waits when it would not.

## Technical design / files

- `Sextant.Service/SnapshotService.cs:458, 627-629, 654-689, 736-799, 869-947`,
  `Sandbox/SandboxedEnvironmentScope.cs:10-13`, `CloningCheckoutProvider.cs:176-178, 429-430`,
  `LocalIndexerSnapshotWorker.cs:207-340`.

## Acceptance criteria

1. With two queued jobs, the second job's checkout/restore/load overlaps the first job's extraction (timeline in
   Phase 1 metrics), and total time for both drops by at least the preparation time.
2. No secret leaks between overlapping jobs' environments (sandbox tests extended to two concurrent scopes).
3. Peak memory within budget with one job preparing and one extracting.

## Notes / risks / dependencies

- Optional; decided by measurement after Phases 2–8. Phase 7 (fewer jobs) and Phase 8 (cheaper jobs) may make it
  unnecessary.
