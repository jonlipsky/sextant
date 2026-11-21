# Phase 1 — every protection has a live owner

## Goal
Nothing in the catalog or on disk is protected without a current reason. Abandoned or orphaned work is failed and reclaimed automatically.

## Scope
- **Invariant:** an object is protected only by a live owner. The owners are an active branch head, an open-PR head, a pinned snapshot, or a running job holding a heartbeat lease.
- **Pending snapshots:** a pending snapshot whose job lease expired (decision 3), or whose run is abandoned, failed or missing, becomes `failed` and is reclaimable. Remove the unconditional pending protection at `RetentionService.cs:1298`.
- **Staging generations, queued ensures, checkouts and scratch** follow the same rule: no live owner means reclaimed. Queued ensures already expire by age (168 h); keep that, and also drop ensures for deleted branches.
- **Sweeps:** at startup, and on every maintenance tick (not only the 03:00 pass), with an audit row per reclaim.
- **Migration:** existing pending snapshots of abandoned runs are failed on first start.

## Testing seams
- **Service integration:** start an index, kill the job (simulate the crash with the lease clock via `TimeProvider`); on the next tick the snapshot is failed and its rows reclaimed. A live job's snapshot is never touched.
- **Property-style test:** a randomized sequence of start, crash, complete and delete-branch operations never leaves a protected object without an owner.

## Acceptance criteria
1. No pending snapshot outlives its job by more than one tick plus grace.
2. Existing leaked pending snapshots are reclaimed on upgrade.
3. Every reclaim is audited.

## Demo
On a scratch catalog: kill a running index; within one maintenance tick the audit shows `pending → failed → reclaimed`, and its bytes are freed (after Phase 3, returned to the OS).
