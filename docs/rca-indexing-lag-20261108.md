# RCA: default-branch indexing lag (2026-11-08, issue #403)

Symptom: a push to `elevenworks/ProcessStack` `main` was indexed 9-14 hours late; MCP calls timed out; the writer
lease logged `database is locked`.

## Evidence (read-only: catalog `snapshot_jobs`, `docker logs sextant-service`, GitHub push events)

1. **The queue is never idle and jobs run back to back.** Each job's `created_at == started_at ==` the previous job's
   `completed_at` (job 4145 was created 20:07:41, right after 4144 completed 20:01:00). Complete jobs: median ~749 s,
   p90 ~988 s, so ~5 productions/hour. Demand in 24 h: ~26 ProcessStack `main`, ~31 TextEditor `main` and ~36
   feature-branch productions. Demand exceeds capacity, so a backlog is permanent.
2. **The app is not the delay.** `POST /control/ensure?wait=False` reaches the service in real time (216 in 12 h; commits
   at 19:45:32/19:45:37/19:46:09 produced ensures at 19:45:37-19:46:02). The push orchestration is not serial and does
   not wait for jobs.
3. **Lag measured:** push 25ba30ae to 00797461 at 06:41Z had its job created 16:19Z (9.6 h); push 56023850 to 27930d63
   at 11-07 18:04Z had its coalesced job 4077 created 08:38Z (14.5 h).
4. **Why no job existed for ~30 newer commits:** an ensure takes its writer turn at admission, but its `snapshot_jobs`
   row is written only when the turn is granted. Queued ensures live in memory only (and are lost on restart: 1026
   jobs were cancelled "service run ended before its turn (restart)").

## Cause 1 (primary): one FIFO write gate, no priority for the default branch

Every ensure registration and production is a `WriteGate` turn (`TakeWriteTurnLocked`, #158). The gate had only a
normal lane plus a maintenance priority lane. A default-branch push waited behind every feature-branch build admitted
earlier. #273 coalescing only collapses *queued linked* advances, and the survivor still keeps its own, late FIFO
position; reconcile ensures (no CAS) never coalesce. With a 12-16 minute job time this serialised hours of work.

**Fix:** a third, *expedited* lane (`WriteGate`). An ensure with `default_branch: true` (only the application may send
it; a user ensure that does is refused) and not `branch_update: none` takes it, so it runs right after the current
holder (after maintenance). Fairness: after `WriteGate.DefaultExpeditedStreakLimit` (4) consecutive expedited turns while
a normal (feature-branch) turn waits, one normal turn runs, so feature branches are delayed, never starved. Same-lane
order stays FIFO, so push, retire and re-create ordering per branch is unchanged. Superseded default-branch pushes still
coalesce (#273), so only the newest head is built.

## Cause 2: long write-lock holds starved lease renewal and readers

114 `Write lock held for 5-11.7 s by 'index-write-session'` warnings in 12 h; 100 of them directly follow a
`comments extracted` line. The comment phase opened a write batch per project and parsed every document inside it.
The #344 hold cap (`MaxBatchHold` 2 s) is only evaluated when a row is written, so a project whose documents have no
comments never committed. The lock stayed open for the whole parse. `busy_timeout` is 5 s, so the lease heartbeat
(`writer lease renewal failed ... database is locked`) lost the race.

**Fix:** `IndexWriteSession.ReleaseIfHeldTooLong()`; the comment loop calls it before parsing each document and
`Begin()`s again before each insert. Holds are now bounded by `MaxBatchHold` (2 s default) plus one document parse.

## Contributing factors (not changed here)

- Retention/idle passes waited behind productions for 583-903 s and one idle pass took 722 s (the priority lane only
  orders them; it does not bound a production already running). `RETENTION_PASS_SECONDS` bounds them.
- Restore of `TextEditor.slnx` fails as a whole and retries 142 projects one by one (2-4 min per job).
- 7 of ~1190 MCP `tools/call` took over 3 s (12-27 s) while a production ran at ~100% CPU and 4.8 of 7 GiB. Readers use a
  separate WAL connection, so they are not blocked by the writer; the cause is CPU/memory pressure from the running
  job. Lowering `MAX_PARALLELISM` (or a larger container) is the lever; no code change made.
- Restart still loses queued ensures; the #400 upstream heal and the app's reconcile re-enqueue within ~2 h.
  (Fixed by #406, below.)

## Follow-ups (#406, #407, #408)

- **#406 queued ensures survive a restart.** Migration `033_queued_ensures` (identity-neutral service table). Each
  admitted ensure is written to `queued_ensures` (admission sequence, request JSON, `RestrictsImplicitDefault`,
  tenant, hashed actor, and for a user caller the tenant id and subject, never a token) on a side connection, off
  the write gate, before a `wait=false` 202 is returned; a no-op attach of an already-built identity writes no row.
  It is deleted when the ensure ends, and kept when shutdown, a lost lease or a requeue ended it. At startup, after
  the orphan settle, `ReplayQueuedEnsures` seeds the admission sequence from `MAX(seq)` over every row, then
  re-admits the rows in order through `StartEnsureOperation`, so lane, #273/#405 coalescing and the attach of an
  already-built identity apply as for a live admission. Each row is handled on its own: it is dropped (logged, and
  audited as `replay_*`) when malformed, older than `SEXTANT_SERVICE_QUEUED_ENSURE_MAX_AGE_HOURS` (default 168),
  refused by `RepositoryUrlPolicy` as now configured, or a user row whose grant is gone; a failing row never stops
  startup.
- **#407 maintenance and a production (scope reduced).** Maintenance passes still wait for a running production. A
  mid-production yield (`WriteGate.YieldToPriority` between write batches) was built and removed: letting retention
  run between a production's batches breaks invariants (the source-blob sweep can delete a blob before
  `CaptureAnalyzedHash` reads it; reused complete providers are unprotected until Phase 6 records their dependency
  edges; a production can be starved by repeated priority turns; and the #403 lane fairness is skewed). The waits it
  targeted are a minor contributor. A safe version needs an in-flight blob-key set honoured by the sweep, provider
  dependency edges recorded at reuse time, and a minimum of production progress between yields. What remains is the
  idle-branch pass bounded by `SEXTANT_SERVICE_BRANCH_IDLE_PASS_SECONDS` (default 120, max 3600); unapplied
  retirements stay queued for the next tick.
- **#408 restore fallback.** A failed whole-solution restore retries the projects as one traversal and bisects
  breadth first (so the cap cannot starve right-hand siblings) in halves when no project is named, at most 48
  invocations, checked before every call. The failing projects are remembered per repository and
  solution, independent of the commit (`restore-known-bad.json`, saved in a `finally` so a cancelled job keeps what it
  learned); the next job restores the rest in one call and tries
  the known-bad group separately, healing any that now restore. The root cause of the `TextEditor.slnx` whole-restore
  failure was not determined; the fallback no longer depends on it.
- **MCP latency under CPU pressure (configuration, no code).** The service runs the defaults: `max_parallelism` 0 =
  `min(cores, 8)`, `extraction_projects_in_flight` 0 = `min(parallelism, 4)`, and the container the RCA observed had
  7 GiB (4.8 GiB used by a production). The symbol pass can use about twice `max_parallelism` threads. Recommendation:
  set `SEXTANT_MAX_PARALLELISM` to about half the container's cores (e.g. 3-4) or grow the container, and re-measure
  the `tools/call` tail.

## Deploy note

1. **Service only** (`jonlipsky/sextant`, g2d deploy). No migration, no new config. Optional tuning already existing:
   `SEXTANT_SERVICE_INDEX_BATCH_MAX_HOLD_MS` (default 2000), `SEXTANT_SERVICE_INDEX_YIELD_TO_WAITERS_MS` (500).
2. **App** (`elevenworks/processstack-sextant`): no change and no publish. It already sends `default_branch: true` for
   pushes and reconcile of the default branch, which selects the new lane.
3. Deploy at low traffic: the restart loses in-memory queued ensures (heal ~2 h).
4. Verify after deploy: `Write lock held` warnings drop below ~3 s, and the job for a default-branch push is created
   directly after the running job (`snapshot_jobs.created_at` close to the push time plus the current job's remainder).

## Follow-up: an ensure ended `coalesced` with no snapshot (ProcessStack `main` 9bbadf74b)

**Symptom.** Job 4248 (9bbadf74b, created 23:37:35.765) was settled `coalesced` at .768, "superseded by commit
279bdede8", with a NULL snapshot. `branch_advance_handovers` holds 9bbadf74b -> 279bdede8, but there is no job, audit or
log line for 279bdede8: the successor never registered. `list_repositories` kept `main` at a72875a1. Jobs 4249/4250
(other default branches) then took turns, and feature-branch job 4253 ran at 23:44. Ids 4251/4252 were reserved, never
committed. (The successor's absence is inferred from this evidence, not directly observed.)

**Cause.** `#273` coalescing only asked "has a newer linked push been admitted?" (`ticket.SupersededBy`). It never
asked whether that successor would actually get a turn soon, or at all:
1. After #404, a default-branch ensure takes the expedited lane and a successor without `default_branch` the normal
   lane, so the predecessor stood down for a successor queued behind long feature-branch builds.
2. During the shutdown drain, queued ensures got their turn, coalesced (the coalesce runs before `ThrowIfStopping`),
   and their successors then threw and were cancelled by startup settle (the 22:53 jobs 4219 -> 4227, 4231).
3. A successor that failed/was cancelled before the predecessor's turn was equally trusted.

**Fix** (`SnapshotService`). `AdvanceTicket` now records `Expedited`, `Ended` and `Produced`. At its turn an ensure
coalesces only if `WillBuildBranchHead`: the service is not stopping, the successor has not ended without producing
(failed/unsupported/cancelled/thrown), and the successor is not in a slower lane than the predecessor. Otherwise the
ensure is built. Restart: cancelled-at-restart jobs were already never successors in `FindQueuedJobs` (only
complete/partial/failed/unsupported count); with no coalescing during shutdown no handover points at a cancelled job.
Request details are not persisted, so queued ensures are not re-admitted; a lost push is healed by the #400 upstream
heal and the app's reconcile.

**Tests** (`EnsureCoalescingTests`): expedited push is built, not handed to a normal-lane successor; expedited
successor still coalesces; ensures queued at shutdown stay queued (not coalesced), are cancelled at restart, and a new
ensure for the same commit builds; invariant helper asserts every `coalesced` job has a queued/running/built successor.
