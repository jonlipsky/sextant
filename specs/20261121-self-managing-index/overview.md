# Self-managing index: active branches stay current, everything else cleans itself up

**Status:** Approved · **Date:** 2026-11-21 · **Owner:** Jon Lipsky

Decisions 1–7 answered by the owner on 2026-11-21, decision 8 on 2026-11-22. Epic #418.

## Why this exists

The owner, 2026-11-21:

> If the sextant index is growing by 1GB per day, we have a serious design flaw we need to look into. Code doesn't change enough in one day to drive that level of storage requirements.

> If sextant can't restart indexing and organically get us back to a working state, it's not a production ready tool.

> The whole original design of sextant was supposed to be that it kept up to date indexes of active branches and cleaned it self up automatically with stale branches, projects, data... And... If/when something became active again, the index would be rebuilt.

What production showed on cloudserver1 (2026-11-21, read-only investigation):

- **The catalog grew about 20 GB a day.**
  - It went from 33.6 GB on Nov 8 to 173.7 GB on Nov 21, per `index_runs.final_db_bytes`.
  - The disk reached 100%. The catalog would have filled the remaining space in about 8 days.
- **Every default-branch push stores a complete new copy of the repository's index.**
  - For each committed snapshot, each project gets a fresh row, `canonical_id = "<id>:<snapshotId>"` (`src/Sextant.Indexer/IndexOrchestrator.cs:634-646`, `src/Sextant.Store/ProjectStore.cs:58-88`).
  - Files, symbols, occurrences, relationships, API surfaces and FTS rows all hang off that row, so all of them are copied too. Unchanged projects are shared only in local overlays.
  - A TextEditor commit costs about 420 MB, at about 50 pushes a day. A ProcessStack commit costs about 270 MB, at about 14 a day.
- **Retention exists, but deletes copies row by row.**
  - Each delete touches 4 occurrence indexes, 6 symbol indexes and an FTS trigger (`RetentionService.cs:512`, `Migrations/011…sql:118`).
  - Deletions fell from 40–150M rows a day (early November) to 1–8M rows a day (Nov 19–21), leaving about 466 eligible snapshots (about 160 GB) undeleted.
- **The keep rule is global.** It keeps the newest 3 complete runs across the whole service, not per branch (`RetentionService.cs:1129-1160`).
- **Pending snapshots are protected forever.** 40 snapshots of abandoned runs, dating back to October, were still `pending`. Retention protects every pending snapshot (`RetentionService.cs:1298`), so about 5.6 GB could never be reclaimed.
- **The queue builds every push in order.**
  - Coalescing (#273) required an exact commit chain, so it rarely fired. The service fell 5 days behind (queue-delay p95 about 5.5 days).
  - The backlog was invisible: `sextant_jobs_queued` counted only jobs that already had a turn.
- **The recovery test (2026-11-21).** The owner had the 174 GB catalog deleted and the service restarted empty. The question is whether it rebuilds on its own. The outcome is recorded in Phase 0's notes and is the baseline for acceptance criterion 6.

## The model / approach

Sextant manages an **active set** and nothing else:

1. **Active branches stay current.**
   - A branch is active while it has had pushes, ensures or queries within the activity window, or is a default branch, or is the head of an open PR.
   - Each active branch keeps its **newest head**, plus at most K earlier snapshots.
   - Bursts of pushes collapse to the newest head.
2. **Everything else expires automatically.**
   - Superseded snapshots beyond K, inactive branches, deleted branches, closed PRs, repositories nobody has asked about within the window, and their checkouts and caches are reclaimed by the service without operator action.
3. **Nothing is protected without a live owner.**
   - Every protected object has a reason that is itself current: an active branch head, an open PR, or a running job with a heartbeat.
   - A `pending` snapshot whose job is gone is failed and reclaimed. A crash, an abandoned run or a lost turn can never pin data forever. The same rule applies to staging generations, queued ensures and checkouts.
4. **Reactivation rebuilds.** A query or ensure for an expired branch or repository returns "not indexed / rebuilding", queues an index of its current head, and serves it when done. There's no manual re-enrolment.
5. **Storage is proportional to change, and reclaiming it is cheap.**
   - Unchanged project versions are shared between snapshots (#274).
   - Data is laid out so a whole snapshot or project version can be dropped at once, not row by row.
6. **The service proves it can recover.** From an empty or lost catalog it returns to a fully current active set on its own, using only its normal triggers (pushes, reconcile, healer, grants). It rebuilds one repository at a time, as host resources allow, and never overloads its host.

## What already exists (build against, not greenfield)

- **Coalescing and lanes:** #273 and #405, plus the `queued_ensures` table (durable ensures, #406–#409).
- **Default-branch pointer advance and healer:** #412, #414, #415. The ancestry cache (#416).
- **Retention:** `RetentionService` and `RetentionPolicy` (`KeepCompleteGenerations=3`), maintenance passes (`MaintenancePolicy`: daily from 03:00 UTC, 600 s of writer time, 5-minute continuations), incremental auto-vacuum.
- **Checkout and cache eviction:** a 14-day inactivity window for checkouts and git objects, and `PACKAGE_CACHE_MAX_MB` for NuGet.
- **Specs:** `specs/20261007-indexing-performance/` phase 7 (coalesce, #273) and phase 8 (cross-commit project reuse, #274). This spec finishes #273 for default branches, and treats #274 as the storage phase here.
- **Monitoring:** `index_runs.final_db_bytes`, retention audit rows (`chunk_rows`), daily-health and monitor issues.

## Non-goals

- Changing query semantics or MCP tool contracts, apart from the "rebuilding" answer for expired targets.
- Multi-writer or multi-host indexing.
- Historical snapshots beyond K per branch (time travel), unless pinned.
- Any ProcessStack-side change. The ProcessStack app keeps sending pushes, grants and reconciles as today.

## Phases

| # | Title | Outcome | Demo | File |
|---|---|---|---|---|
| 0 | Phase 0 — walking skeleton | Bursts collapse to the newest default-branch head, the backlog is visible, and the watched-repository list survives catalog loss | 5 rapid pushes produce 1 index; `/control/status` shows queued depth and age; after an empty-catalog restart every watched repository is re-queued | [phase-0-walking-skeleton.md](phase-0-walking-skeleton.md) |
| 1 | Phase 1 — every protection has a live owner | Pending snapshots, staging generations, queued ensures and checkouts without a live owner are failed and reclaimed | Kill a running index mid-run; within one maintenance tick its pending snapshot is failed and its rows reclaimed | [phase-1-live-owners.md](phase-1-live-owners.md) |
| 2 | Phase 2 — active set and per-branch retention | K snapshots per active branch; inactive or deleted branches, closed PRs and idle repositories expire; reactivation rebuilds | Delete a branch, and its data is gone in the next pass; query an expired repository, and it rebuilds and answers | [phase-2-active-set.md](phase-2-active-set.md) |
| 3 | Phase 3 — cheap reclamation | Each project version lives in its own file; dropping one deletes the file | Drop of a TextEditor-sized snapshot takes seconds, and the file shrinks after the pass | [phase-3-cheap-reclamation.md](phase-3-cheap-reclamation.md) |
| 4 | Phase 4 — storage proportional to change | Unchanged project versions are shared between snapshots (#274) | A one-file TextEditor commit adds ≤ 5% of a full snapshot's bytes | [phase-4-share-unchanged-projects.md](phase-4-share-unchanged-projects.md) |
| 5 | Phase 5 — storage alerts | Alerts on bytes a day, retention throughput, backlog age, free disk and prolonged budget enforcement | Replaying the November history makes each alert fire | [phase-5-storage-alerts.md](phase-5-storage-alerts.md) |
| 6 | Phase 6 — compact storage | Each project version and the working storage beside the catalog (package folders, checkouts, git objects) are stored compactly; package folders stay under their bound | The storage harness shows bytes per symbol and per occurrence at ≤ 50% of the 2026-11-22 measurement with identical answers; `/control/status` reports bytes per storage class | [phase-6-compact-storage.md](phase-6-compact-storage.md) |

## Acceptance criteria

1. Under the production push rate, steady-state catalog size is bounded. The target is **under 20 GB** for today's active set, and daily growth is flat.
2. A burst of N pushes to a default branch produces 1 index of the newest head, and queue depth and age are visible in metrics.
3. No object is protected without a live owner. An abandoned run's data is reclaimed within one maintenance tick.
4. Deleted branches, closed PRs and branches or repositories inactive past the window are fully reclaimed (catalog rows, checkouts, caches) without operator action.
5. A query or ensure for an expired target triggers a rebuild and is answered when done.
6. From an empty catalog, the service returns to a fully current active set using only its normal triggers: sequentially, gated on host resources, with no operator action and no time limit. Every watched repository comes back, including ones Sextant registered itself.
7. Reclaiming a snapshot costs seconds, not hours, and space is returned to the OS.
8. A one-file commit costs storage proportional to the projects it changes.
9. Growth, retention throughput, backlog and free disk have alerts.
10. Stored rows are compact: bytes per symbol and per occurrence at most half of the 2026-11-22 measurement, with identical answers. Working storage beside the catalog stays within its bounds and is reported per storage class.

## Testing seams

- **Service integration tests through the control and query HTTP API.** Real boundary: a real SQLite catalog and real indexing of small fixture repositories. Proves 2, 3, 4, 5 and 7.
- **Empty-catalog recovery integration test** (Phase 0). Real boundary: the real service with an empty DB and fixture repositories, including `self`-registered ones. Proves 6, on every change.
- **Storage measurement harness** (the Phase 1 harness from the indexing-performance spec). Real boundary: real indexing of TextEditor-sized fixtures. Proves 1 and 8.
- **Production read-only checks** after each phase's deploy: `index_runs.final_db_bytes`, retention audit and queue metrics. Prove 1 and 9.
- **Deliberately not tested:** multi-writer races, which are excluded by the single-writer design.

## Decisions

1. **History per branch.** **Approved:** keep the newest head plus one earlier snapshot (K = 1), plus the heads of open PRs and any pinned snapshots.
2. **Activity window.** **Approved:** 7 days. A branch with no push, ensure or query for 7 days is inactive and expires. Default branches of watched repositories never expire.
3. **Live owner of a pending snapshot.** **Approved:** each job holds a heartbeat lease. A missed lease plus a grace period of two heartbeats makes its snapshot `failed`. The sweep runs on **every maintenance pass**, not only at startup.
4. **Answer for an expired target.** **Approved:** answer at once with `not-indexed`, `rebuilding: true` and an estimated time, and queue the rebuild in the background. Never block the caller.
5. **Reclamation layout.** **Approved:** one SQLite file per project version, attached as needed. Removing a snapshot or project version deletes its file. There's no prototype comparison: Phase 3 builds this layout and measures only query latency, to confirm no regression.
6. **Storage budget.** **Approved:** enforce a configured budget. While over it, defer non-default-branch indexing and run retention bursts until under; default branches keep indexing.
7. **Recovery.** **Approved:** no time limit. Recovery is best effort and sequential, gated on the host's available resources (CPU, memory and disk, within the single-writer design), so a rebuild never overloads the server it runs on. The bar is that every watched target comes back with no operator action. **The list of watched repositories must survive losing the catalog.** The 2026-11-21 reset showed that repositories Sextant had registered itself (TextEditor, MixAndMatch, Confluo, Crdt and others) did not return, while the ones re-sent by a tenant grant did.
8. **Sharing identical package files (Phase 6).** **Approved 2026-11-22:** share by content hash through copy-on-write reflinks where the filesystem supports them; otherwise (ext4, cloudserver1 today) keep separate copies under the fixed bound. No plain hard links.
