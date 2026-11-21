# Phase 2 — active set and per-branch retention

## Goal
The service keeps exactly the active set: the newest head plus one earlier snapshot (K = 1) earlier snapshots per active branch, and open-PR heads. Everything else expires, and expired targets rebuild when they're used again.

## Scope
- **Activity tracking:** last push, ensure or query per (repository, branch) and per repository. Default branches of watched repositories are always active. Window: 7 days (decision 2).
- **Retention policy:**
  - per branch: newest head plus one earlier snapshot (K = 1), open-PR heads, pinned snapshots;
  - replaces the global "newest 3 runs" rule (`RetentionService.cs:1129-1160`, `RetentionPolicy.KeepCompleteGenerations`).
- **Expiry:** deleted branches (delete events and reconcile), closed or merged PRs, and branches or repositories inactive past the window. Catalog rows, checkouts, git objects and package caches for them are reclaimed.
- **Reactivation:**
  - a query or ensure for an expired target answers `not-indexed` with `rebuilding: true` (decision 4) and queues an index of the current remote head;
  - the next answer after completion is normal;
  - document this in `docs/mcp-tools.md` and `docs/service.md`.
- **Back-pressure** (decision 6): over budget, defer non-default-branch indexing and run retention bursts.

## Testing seams
- **Service integration** with fixture repositories:
  - K respected per branch;
  - a deleted branch reclaimed;
  - a closed PR reclaimed;
  - an inactive repository expired, then queried, then rebuilt and answered;
  - the default branch never expires.

## Acceptance criteria
1. Retention keeps exactly the active set.
2. Every expiry path reclaims catalog rows and disk.
3. Reactivation rebuilds without operator action.

## Demo
Delete a branch on a test repository; the next pass removes its data. Query an expired test repository; the answer says it's rebuilding, and a later query answers from the new index.
