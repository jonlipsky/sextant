# Phase 7 — Coalesce superseded commits and prioritize the queue

## Goal

The queue is a FIFO `SemaphoreSlim` in which every delivered commit is produced in full, including commits that a
newer push to the same branch has already superseded (on 2026-10-07 three MixAndMatch `main` commits were queued
for three full indexes). Skip superseded work and let default-branch and user-initiated ensures go first.

## Scope

- **Supersede on admission.** When an ensure for repository R, branch B arrives while an older ensure for R/B is
  queued (not started), the older one ends with a new terminal **job** status `coalesced` (with the superseding job id;
  deliberately not `superseded`, which already names a published *snapshot* replaced by a branch advance),
  and only the newest is produced. Running jobs are never cancelled.
- **Branch-pointer correctness.** Superseding breaks an `expected_head_commit` chain (Y's CAS expects X, which never
  publishes). The newest ensure's CAS is rewritten to the head the superseded job expected, or `branch_head_sequence`
  ordering is used, so the pointer still advances safely.
- **Who may be superseded:** webhook and reconcile ensures. An explicit ensure that asked for a specific commit's
  snapshot (any caller polling a job id for that commit) receives `coalesced` and the superseding job id; docs state
  that the service no longer guarantees a snapshot for every delivered commit, only for every branch head.
- **Priority lanes:** default branch and user-initiated ensures before feature-branch webhook and reconcile
  ensures; FIFO within a lane; ordering per branch preserved (retire then re-create, #158's guarantee).
- Persistence: today queued registrations live in memory and a restart loses them. Keep that (the nightly reconcile
  repairs) or persist the lane order; decide and document.

## Technical design / files

- `Sextant.Service/SnapshotService.cs:45, 458, 502-515, 647-689, 736-799, 1748, 2450` (write gate, admission,
  identity coalescing, production loop, retire coalescing); `docs/service.md:1284-1335` (queued control writes,
  #158) and `1340-1366` (branch-head sequencing and superseded snapshots).
- Replace the bare semaphore with an explicit admission queue keyed by (repository, branch, lane), holding the same
  single production slot.

## Acceptance criteria

1. Three pushes to one branch while the writer is busy produce at most two productions (in-flight + newest); the
   middle job reports `coalesced` with the newest job's id.
2. The branch pointer ends at the newest commit with CAS semantics preserved (tests for out-of-order delivery and
   for a retire racing a supersede).
3. A default-branch ensure admitted behind N queued feature-branch webhooks starts next.
4. Audit records every coalesced job.

## Notes / risks / dependencies

- Behavior change for callers that expected a snapshot for every commit. ProcessStack's sextant app treats queued
  ensures as accepted; check `start-indexing` and reconcile handle `coalesced` (app follow-up in
  elevenworks/processstack-sextant).
- Independent of Phases 2–6; can ship any time after Phase 1.
