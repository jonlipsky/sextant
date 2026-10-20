# Sextant index-service operations runbooks & pilot rollout

Operational runbooks for the standalone **Sextant index service** (`docs/service.md`), plus the **pilot
exit criteria** and **rollback procedures** required to promote the service from a pilot to production
(Phase 17, criterion 7). Each runbook is written so an on-call operator can act without reading the source.

> **Scope.** These runbooks cover the *service* data plane only. The local stdio MCP path and standalone
> local indexing have **zero** service dependency and need none of this — if you are not running
> `sextant service`, stop here.

---

## Observability quick reference

All operator signals are on the **control plane** (`/control/*`, control-token gated) and never the query
plane — audit rows and per-repository cost would otherwise leak repository/snapshot existence and
cross-tenant counts to a tenant (criterion-1 leakage guard).

| Need | Where |
| --- | --- |
| Latency / queue delay / success / capacity / storage / cache / query latency | `GET /control/metrics` (JSON) or `?format=prometheus` |
| Who did what, to which repo, at what cost | `GET /control/audit` |
| Is a workload safe to pilot? | `GET /control/pilot?workload=…` |
| Distributed traces | `Sextant.Service` `ActivitySource` (wire OpenTelemetry) |

Alerts evaluated over the metrics snapshot: `queue_delay_high`, `low_success_rate`, `low_completeness_rate`,
`worker_exhaustion`, `storage_pressure`.

`low_completeness_rate` evaluates complete / terminal jobs completed in the rolling 24 hours; it warns
below 90% and is critical below 50%, after at least five recent terminal jobs. The metrics snapshot reports
the window as `[window_start_unix_ms, window_end_unix_ms)` and reports the sample count. Equality at 90%
is healthy; equality at 50% is warning, not critical. Partial publication remains a success for the
cumulative `low_success_rate`, but lowers recent completeness. These defaults
are configurable through `AlertThresholds.CompletenessRateWarn`, `CompletenessRateCrit`, and the shared
`SuccessRateMinSamples` programmatic settings (no environment-variable overrides). The cumulative
`jobs` counts/rates are retained for compatibility; neither alerting nor pilot readiness substitutes its
empty-history `1.0` rate for the recent window. Inspect
`/control/status/{jobId}` and `/control/resolve` for `coverage.reasons`, then check the repository's
`solutions` configuration and project-load/submodule diagnostics. With too few recent terminal jobs the rate
is not assessed; the cumulative no-sample exported value of 1.0 is not proof of healthy coverage. This signal uses
recorded job outcomes, not a new coverage audit of legacy snapshots.

---

## Runbook: previous run ended without a clean shutdown (possible OOM kill)

Symptom: the service log shows `WARNING: previous service run ended without a clean shutdown at ~<time>; possible
crash or kill (e.g. OOM)`, and `sextant_service_unclean_shutdowns_total` in `/control/metrics` increased (alert on
its increase). A `Note: previous service run abandoned the lease` (drain timeout, counted in
`sextant_service_abandoned_runs_total`) or `could not release the lease` is not a crash. Often accompanied by `Took over the stale single-writer lease of '<holder>'`, which means the dead
process's lease was provably dead (same host/container, pid gone or restarted) and was taken over without waiting
for the TTL. Start failures with `Another writer holds the single-writer lease` that persist mean the holder
could not be proven dead (another host/container, or a live process): stop it or wait for the TTL.

Check for an out-of-memory kill:

1. `docker inspect <container> --format '{{.State.OOMKilled}} {{.State.ExitCode}} {{.State.FinishedAt}}'`
   (`true`/exit 137 = killed by the kernel; the flag only reflects the main process's last run).
2. Inside the container (cgroup v2): `cat /sys/fs/cgroup/memory.events` — a non-zero `oom_kill` counts kills;
   `memory.max` is the limit and `memory.peak` the high-water mark. cgroup v1: `memory.oom_control` and
   `memory.max_usage_in_bytes`.
3. On the host: `dmesg -T | grep -i -E 'killed process|out of memory'` or `journalctl -k | grep -i oom`.

Job rows that were `running` are reset by the startup reconcile and then settled (issue #327, see
[Startup settles orphaned queued jobs](#startup-settles-orphaned-queued-jobs)); no manual cleanup is needed. If OOM is
confirmed, raise the container memory limit or reduce load parallelism (`SEXTANT_MAX_PARALLELISM`).

The lease holder's `HostId` is the machine name, which **changes when a container is recreated**. A recreated
container therefore cannot prove its predecessor dead and waits for the lease TTL before taking over (safe, only
slower); a restart of the same container takes over at once.

## Runbook: RSS drops and the service pauses briefly after a job

Expected (issue #320). After the last job of a burst the service runs an end-of-job memory reclaim: it collects the
managed heap and trims glibc's native heap, so the process RSS falls from several GiB back toward the baseline and
every thread pauses for about 2 s. The log line is `Job <id>: memory reclaimed in <n> ms (working set <a> -> <b> MiB,
managed heap <c> MiB).`. It is skipped while another job is running or queued, during shutdown, and when the
working set is under 1 GiB; every skip logs `Job <id>: memory reclaim skipped (<reason>): ...` and is counted (see
`docs/service.md`, "Start-of-job reclaim and skip reasons"). A skipped post-job reclaim is made up by the NEXT job,
which reclaims before its restore (`start-of-job memory reclaimed`), so a burst never carries one job's memory into
the next. A reclaim never blocks or fails a job; if RSS stays high after an idle period, look for
another cause (a running job, or a non-glibc image where the native trim is a no-op).

## Runbook: container memory budget (issue #325)

The container limit (7 GiB in production) covers the service process **and** its children **and** page cache:

- **Service process, per job:** the managed heap peaks at several GiB while a job indexes. It is returned by the
  post-job reclaim, or by the start-of-job reclaim of the next job in a burst. Look for `memory reclaimed` /
  `memory reclaim skipped (<reason>)` lines (the per-phase timings in the reclaimed line show which of gen2 gc,
  finalizers, aggressive gc or malloc_trim took the time) and the `sextant_memory_reclaims_*{phase=post_job|pre_job}`
  counters; a rising `deferred` count
  just means bursts or a heap that stayed large after the reclaim (the next job made it up; a post-job reclaim whose
  managed heap stays more than 256 MiB above the service's baseline is retried every 15 s, up to 4 times, see
  `docs/service.md` "Off-worker-stack continuation and the residual retry"), while a service RSS of GiBs at the start of a restore with no
  start-of-job line means the working set was under the threshold or the reclaim could not run.
- **Restore nodes:** each MSBuild worker of a restore adds ~200-240 MB. `SEXTANT_SERVICE_RESTORE_MAX_NODES` (default
  2) caps them for every restore invocation; raise it only if restores are time-bound and memory is spare. The
  process-tree RSS (service + `dotnet` + nodes) is what the limit sees.
- **Page cache vs the limit:** `memory.current` includes file cache, which the kernel evicts under pressure, so a
  `memory.current` near the limit is not by itself a problem. Anonymous memory (`anon` in `memory.stat`) is the
  number to watch: it cannot be evicted, and OOM kill follows when it alone approaches the limit.
- **`memory.events` `max` growing** means the cgroup hit its limit and the kernel reclaimed (mostly page cache) to stay
  under it. A steadily growing `max` with `oom_kill 0` is memory pressure with heavy cache eviction (slower
  checkouts, package reads and SQLite pages), not data loss; `oom_kill` above 0 is the real failure (see the
  unclean-shutdown runbook above). Cut anonymous memory (reclaim, node cap) before raising the limit.

## Runbook: unsupported workloads

**Symptom.** `GET /control/metrics` shows a rising `jobs_unsupported`; `GET /control/status/{jobId}` returns
status `unsupported` with per-project diagnostics.

**Cause.** The worker could not evaluate the project (unsupported SDK/TFM, an evaluation the sandbox
refused, or a project shape the indexer does not model).

**Action.**
1. `GET /control/status/{jobId}` and read `snapshot_job_diagnostics` — each carries a machine-parseable
   `code`/`severity`/`message` and the offending project.
2. If it is a genuinely unsupported SDK/TFM, that is expected — communicate the supported matrix; the job is
   correctly terminal, not stuck.
3. If the sandbox refused a legitimate evaluation, review `SEXTANT_SERVICE_SANDBOX_*` limits (time/memory)
   and the evaluation logs. An aborted job (`evaluation_budget_exceeded`) is reused for its commit until the
   budget changes: after raising `SEXTANT_SERVICE_SANDBOX_TIME_BUDGET_SECONDS` or
   `SEXTANT_SERVICE_SANDBOX_MEMORY_BUDGET_BYTES`, the next ensure of that commit runs it again (issue #245).
   A repository that only ran out of time is published **partial** (`time_budget_exhausted`), not aborted.
4. Assembled-snapshot **Partial** states are a *known limitation*, not a failure — see "Known limitations"
   below (issue #79).

---

## Runbook: stuck indexing

**Symptom.** A job sits in `running`/`queued`; `queue_delay` p95 climbs; no snapshot publishes.

**Action.**
1. Confirm a worker exists: `GET /ready` (`503` ⇒ this node is query-only, it will never index — route
   ensures to a worker node).
2. `GET /control/metrics` → `worker_in_flight` vs `worker_capacity`. If in-flight is pinned at capacity, see
   "worker exhaustion".
3. If a job is `running` but the process crashed, restart the service. **Startup recovery** checkpoints the
   WAL, sweeps abandoned staging generations, and reconciles phantom terminal jobs (a job whose worker died
   is reconciled, never left "running" forever). Recovery runs under the **writer lease** so it never abandons
   a live writer's staging generation. It then settles every `queued` row left by the dead run (see below).
4. Re-`ensure` the identity — ensure is **idempotent**; it attaches to the existing durable job rather than
   duplicating work. A job the restart settled as `cancelled` or `coalesced` is produced again by this ensure.

---

## Startup settles orphaned queued jobs

**Symptom.** A startup log line `Startup settled N orphaned queued job(s) with no live turn: A coalesced
(superseded), C cancelled (not produced; a later ensure produces them).`, an audit row `reconcile` with detail
`settled_queued_coalesced_A_cancelled_C`, or `sextant_startup_jobs_settled_total` above 0.

**Meaning.** Jobs are produced only when a client calls `ensure`, so a `queued` row from the previous run had lost
its in-process turn (restart, crash, cancel, retention requeue). Startup settled it so it no longer looks like a
backlog or blocks idle-branch retirement and repository reclaim. `coalesced` rows had a newer job of their branch
that already ran; `cancelled` rows were never produced. Nothing is lost: the next `ensure` of the identity
produces it. A large count on the first start after upgrading is the backfill of old stale rows. A count at
every restart is the number of ensures that were still queued when the previous run stopped; frequent deploys
during pushes make it larger. After startup, `sextant_jobs_queued` is only jobs admitted in this run.

**Action.** None. If `sextant_jobs_queued` stays above 0 for long with `worker_in_flight` idle, see
"stuck indexing" above.

---

## Runbook: worker exhaustion

**Symptom.** `worker_exhaustion` alert; `worker_in_flight == worker_capacity`; `queue_delay` rising.

**Action.**
1. Scale out: add worker-capable nodes, or raise the node's concurrency if resources allow.
2. Shed load: pause non-critical `ensure` callers (CI fan-out is the usual culprit).
3. Verify retention is not starving the writer — run `POST /control/retention` (dry-run first, without
   `?execute=true`) to confirm GC is bounded.
4. Cost attribution (`GET /control/audit` / `sextant_repository_index_ms`) identifies the repositories
   consuming the most worker time.

---

## Runbook: local retention (`sextant retention`)

The standalone service runs its own retention/GC under the writer lease via `POST /control/retention`. A
**local** index (CLI/daemon) reclaims its own superseded history with the `sextant retention` command.

**Action.**
1. Dry-run first — `sextant retention` reports the protected, retained, and deletable generations, API
   history, and currently orphaned source blobs with read-only SELECTs, and changes nothing. It needs
   a current-schema catalog but takes no writer lease and performs no migration/recovery.
2. Apply with `sextant retention --execute`.

Retention honors the repo [`retention` policy](configuration.md#retention) (`keep_complete_generations`,
`api_snapshot_keep_commits`, `prune_superseded_source_blobs`) and never deletes data referenced by a
protected/default branch, an open pull request, a submodule pin, or an active overlay base.

The service endpoint has the same [bounded retention contract](service.md#bounded-retention-contract-127--252):
planning uses an independent reader and never queues behind indexing; execution yields the service writer
between freshly revalidated batches. Each pass has a 10-second work budget (including gate wait), a
2-second SQLite batch deadline, and a one-second lock retry limit. Inspect `more_remaining` and
`stop_reason` (`time_budget`, `unit_budget`, `batch_budget`, `sweep_budget`, `api_deferred`, `sqlite_busy`, `vacuum_budget`, `source_text_cap`, `source_text_undeletable`, `source_text_error`, `source_text_keys_budget`, `work_remaining`) and the additive `left_work` summary (what each step still has queued) before requesting a subsequent pass. The source-text sweep resumes from an in-memory cursor and wraps, so `texts=more` / `sweep_budget` clears once a full cycle finds no orphans; the scheduled day stays open only while the planned backlog or a sweep cursor advances (file-version/source-text deletions from ongoing indexing alone do not). Each sweep (file versions, source texts) runs at most one cycle per scheduled day: once a sweep has finished its cycle, later continuation passes that day report it done (`files=0`, `texts=0`), and the next scheduled day starts both over (a continuation chain that runs past 00:00 stays one day; scheduled passes use their own sweep cursors). Manual passes still sweep a slice per call. Caller cancellation stops
remaining work; already-committed batches stay applied. A large project cascade that cannot meet the
deadline is rolled back and deferred, so repeated budget stops need investigation, not an unbounded retry
loop. Local executed retention still requires exclusive writer-lease ownership; it must not run alongside
the daemon/service.

Plans report `reclaimed_bytes_known: false`: a read-only plan cannot simulate cascade freelist deltas.
Execution measures **reusable pages**. Retention never runs a full `VACUUM`. A catalog created with
`auto_vacuum = INCREMENTAL` shrinks at the end of an executed service pass through bounded
`incremental_vacuum` steps and a `wal_checkpoint(TRUNCATE)` (`pages_vacuumed` in the report). A project version is deleted
in chunks of at most 5000 rows per transaction, so the writer is never held for a whole cascade. An older catalog
(`auto_vacuum = NONE`, check with `PRAGMA auto_vacuum;`) only reuses its free pages; switch it with the offline
procedure below.

Snapshots with a legacy abandoned/NULL retry-generation pointer are deliberately retained until their
ownership can be established; this release neither migrates nor repairs them. All branch heads, pending
retries, open PR roots, and required providers/bases remain protected.

**Scheduling.** The service's maintenance loop runs one bounded pass a day and continues an unfinished one that made
progress after `SEXTANT_SERVICE_RETENTION_CONTINUE_MINUTES` (default 5; see [Bounded retention contract](service.md#bounded-retention-contract-127--252)).
Each batch is its own transaction; the pass holds the writer in a burst of `SEXTANT_SERVICE_RETENTION_BURST_SECONDS`
(default 120) per turn and spends `SEXTANT_SERVICE_RETENTION_PASS_SECONDS` only while holding it. Expect a waiting
production to be delayed by at most the burst plus one batch (~2 s).

**Maintenance deferrals (#333).** Maintenance writes (idle-branch retire, upstream-branch retire, retired-head prune, repository reclaim,
scheduled retention) take a priority lane on the writer gate, so continuous indexing cannot starve them. Watch
`sextant_maintenance_writer_wait_ms_max` / `sextant_maintenance_writer_wait_ms_total` /
`sextant_maintenance_writer_turns_total` (how long maintenance waited for the writer) and
`sextant_maintenance_deferrals_total{step=idle_branch_retire|upstream_branch_check|upstream_branch_retire|retired_head_prune|repository_reclaim|reclaim_audit|scheduled_retention}`.
A deferral means a step could not get the writer before the tick's shared deadline
(`SEXTANT_SERVICE_MAINTENANCE_TICK_DEADLINE_SECONDS`, default 900; one log line
`maintenance: <step> deferred`); it is retried on the next tick and later steps still ran. An occasional
deferral is normal under a long production; a rising counter or a wait near the bound means productions hold the
writer for minutes at a time (look at `sextant_job_*` durations and the stuck-indexing runbook).
`sextant_scheduled_retention_skipped_busy` is always 0 now.

---

## Runbook: offline catalog compaction (`sextant catalog compact`)

Use once per catalog created before `auto_vacuum = INCREMENTAL` (and when a large one holds far more free pages than it
needs). It rewrites the catalog into a new file; it needs the service **stopped** and free space in the output's directory of about the input's size (plus its `-wal`) and a
margin (check `PRAGMA freelist_count;` x page size for how much will be saved). The command refuses to run while the
input's writer lease is held or it has a non-empty `-wal`, refuses a leftover `<out>.compacting` file (it names it; delete
it yourself), points SQLite's temporary files at the output's directory (`SQLITE_TMPDIR`, `TMP` on Windows, restored
afterwards), and Ctrl-C removes its own staging files.

1. Stop the service (maintenance window). Leave the original file, its `-wal` and `-shm` in place.
2. `sextant catalog compact --db /data/catalog.db --out /data/catalog.compact.db` (add `--full-integrity-check` for the
   slower `PRAGMA integrity_check` instead of `quick_check`, and `--count-rows` to compare every table's row count
   between input and output). The input is opened read-only and never modified. The command runs
   `PRAGMA auto_vacuum = INCREMENTAL` then `VACUUM INTO`, confirms the output reports `auto_vacuum = 2`, checks it, prints
   both sizes and the elapsed time, and writes the final file only if the checks pass. It refuses an existing output.
3. Keep the old file. Move it (with its `-wal`/`-shm`) aside, rename the output to the catalog's path, start the service
   and watch `/health`, `/ready` and one `resolve` before removing the old file. To roll back, stop the service and put
   the old files back. Writes made after the swap are not in the old file.
4. The next executed retention pass now returns free pages incrementally.

On the 93 GB production-catalog copy (1.74 M free pages) the command took 3 h 40 min and produced an 81.7 GB file (11.4 GB saved), with `quick_check` taking about 2 h of that. Plan for several hours of downtime.

---

## Runbook: schema upgrades (with rehearsal)

Migrations are **additive/forward-only** from `011` onward; `LatestSchemaVersion` auto-derives from the
highest migration file. A restored/older catalog is upgraded on next startup.

**Rollback caveat (migration 032).** A pre-032 binary refuses a 032 catalog (`CheckReadiness`: newer schema), so
rolling the service back after it has started on 032 needs the pre-upgrade backup (the g2d backup) restored, not
just the old image. The crash counters (`writer_run_state`) live in the catalog and come back with a restored backup.

**Rehearsal (do this before upgrading production).**
1. Take a backup of the current production catalog. Stop the service and run
   `sextant service backup /backups/pre-upgrade` **offline**, or take it from the running service with the
   gated `POST /control/backup?dir=/backups/pre-upgrade`. (Offline `service backup` refuses to run while a
   live writer lease is held.)
2. On a staging host running the **new** build, `sextant service restore /backups/pre-upgrade`, then start
   the service. Startup runs the pending migrations against the restored copy.
3. Verify the service is **queryable and authorized**: an authorized query resolves; an unauthorized query
   gets the uniform not-found denial (authz is re-enforced from config, never dropped by restore).
4. Only after a green rehearsal, upgrade production.

> A backup taken at a **newer** schema than the restoring build is **refused** (forward-only guard) so a bad
> restore never half-lands. This is exercised by `SchemaUpgradeRehearsalTests`.

**Rolling back a binary across a migration.** An older build opening a catalog that a newer one migrated
applies nothing and starts, but `CheckReadiness` reports the catalog as built by a newer schema, so it serves
no MCP read until the catalog matches it. The supported rollback is to restore the pre-upgrade backup. For a
purely additive migration such as `026` (one nullable `symbols.declaration` column), a faster route keeps
the data: delete that version's row (`DELETE FROM schema_version WHERE version = 26;`) with the service
stopped. The older build never reads the column (it names its columns, never `SELECT *` on `symbols`) and
indexes with its own snapshot identities. To roll forward again, re-insert the row
(`INSERT INTO schema_version (version, applied_at) VALUES (26, datetime('now'));`) instead of letting startup
re-run `026`, which would fail on the existing column.

---

## Runbook: snapshot corruption / disaster recovery

**Symptom.** A snapshot fails to serve; catalog integrity error; volume loss.

**Action.**
1. Stop the service.
2. `sextant service restore /backups/<latest>` — lays the consistent catalog + immutable artifact volume
   back down (clears stale WAL/SHM sidecars first).
3. **Re-provide credentials + authorization config.** The backup contains **no secrets**; re-provide the
   environment variables the manifest's `credentials_boundary` lists (`SEXTANT_SERVICE_CONTROL_TOKEN`,
   `…_QUERY_TOKEN`, `…_CONTRIBUTE_TOKEN`, `SEXTANT_SERVICE_READ_POLICY`, `SEXTANT_SERVICE_DELEGATE_TOKENS`,
   `SEXTANT_SERVICE_CALLER_KEYS`) from the service's owner-only env file, never by
   typing a value at a prompt (see
   [Generating and installing secrets](service.md#generating-and-installing-secrets)). The read
   policy is on the boundary deliberately: a restore that forgot it would start an **anonymously readable**
   service — restore must re-enforce slice-1 authz, so re-supply the policy alongside the tokens.
4. Start the service. It runs migrations, recovers the WAL, reconciles orphaned jobs, and **re-enforces the
   read-authorization policy** — the restored service is a queryable *authorized* service, and the immutable
   snapshot is byte-for-byte the one that was backed up (restore never mutates it).
5. Confirm with `GET /control/metrics` (jobs reconciled, storage sane) and a spot authorized query.

The backup/restore → queryable-authorized round-trip is exercised by `ServiceBackupRestoreTests`.

---

## Security & isolation posture (READ BEFORE PILOTING UNTRUSTED REPOS)

The current build ships an **in-process** evaluation sandbox (Phase 17 slice 1): it applies
time/memory/secret/filesystem limits around MSBuild evaluation of untrusted checkouts. This is
**defense-in-depth, NOT a hard isolation boundary.**

- **Issue #76 (HARD PRECONDITION for untrusted / multi-tenant production).** OS-hard *out-of-process* worker
  isolation (Windows Job Object / Linux cgroup + rlimits + network namespace / macOS `sandbox-exec`) is
  **deferred**. Until #76 ships, a determined adversary who can execute code during evaluation is contained
  only by in-process defenses. **Do NOT mark the service pilot-ready for untrusted third-party or
  multi-tenant repositories without #76.** The pilot gate (`GET /control/pilot?workload=untrusted`)
  encodes this as a **blocking** check; it also gates the untrusted-multi-tenant ProcessStack deployment
  tracked in #19. Trusted single-tenant pilots are unaffected.
- **Issue #80 (durable-hardening deferred; mitigated).** The `WriterLease` abort probe reads a **cached
  heartbeat flag**, not a durable ownership check at the commit boundary. This is bounded: publication is
  still guarded by the `status = staging` `MarkComplete` check plus the startup **recovery sweep**, which is
  the current mitigation. Durable commit-boundary ownership verification is a tracked follow-up. This is a
  *defense-in-depth-now, durable-hardening-deferred* item — do not treat the lease as a hard cross-process
  mutual-exclusion guarantee.

Both #76 and #80 are "defense-in-depth now, durable hardening deferred." Neither is a correctness
regression; both are explicitly carried here so operators size the risk before hosting untrusted code.

---

## Known limitations

- **Assembled-snapshot Partial states (issue #79).** `AssemblyFinalizeGate` marks an assembled snapshot
  **Partial** when it cannot *prove* cross-contribution topological completeness (the #72 stitching
  boundary). This is deliberate — the gate reports what it can prove, it does not silently claim
  completeness. A Partial assembled snapshot is expected under partial contribution, not a failure to
  investigate.
- **In-process sandbox only (issue #76).** See "Security & isolation posture."
- **Cached-heartbeat writer-lease abort probe (issue #80).** See "Security & isolation posture."
- **Audit trail records accepted operator actions, not per-request query/denial decisions.** Audit rows
  are written on the service **writer path** (under the single-writer lease), so the log captures accepted
  operator control-plane actions (ensure/contribute/retention/backup) and their outcomes. A durable row
  per *unauthenticated* request is deliberately **not** emitted from the auth-middleware hot path — it would
  drive the single writer into contention (a DoS amplifier), and the uniform-not-found denial (slice 1)
  already prevents an unauthorized caller from learning anything. The one `denied` row written today is an
  **authenticated** control-plane intake refusal: an ensure whose repository URL the repository URL policy
  (SVC-5, see [service.md](service.md#repository-url-policy-svc-5)) refuses is audited `ensure`/`denied` with
  the reason code as `detail` (never the URL). The `query`/`denied` vocabulary for read-plane decisions in
  migration 020 is reserved for a future **out-of-band** (non-writer-path) audit sink; that sink is a
  tracked follow-up, not shipped here.
- **Cache-reuse vs cost attribution edge (tracked follow-up).** Cache-reuse metrics and cost suppression
  key off whether a snapshot *job row already existed* (`Attached`), not off whether the worker actually
  re-ran. A job that existed but re-executed (e.g. a reclaimed/regenerated snapshot) is counted as reuse and
  its index cost is not attributed. This under-counts cost in a rare regeneration path; it never over-reports
  completeness or leaks cross-tenant data. A `WorkerRan` signal to separate the two is a follow-up.
- **Offline CLI backup only.** `sextant service backup` copies the immutable artifact volume alongside the
  catalog **without** holding the writer lease, so it is **offline-only** and refuses to run while a live
  writer lease is held. To back up a **running** service use the gated online path `POST /control/backup`,
  which runs the copy under the writer gate. Offline `sextant service restore` likewise refuses a target
  catalog that a live service still owns.

---

## Pilot exit criteria (criterion 7)

Promote from pilot to production **only** when all of the following hold for the target workload class.
`GET /control/pilot?workload=<trusted|untrusted>` evaluates them and returns a machine-readable go/no-go
with the failing checks named. The security-critical capabilities are derived from the service's **actual
state**, never from request parameters — a caller cannot assert the #76 precondition (or a proven backup)
into existence with a query flag.

| # | Exit criterion | How it is checked |
| --- | --- | --- |
| 1 | Authorization enforced (read policy on) | `authorization_enabled` |
| 2 | Control plane secured with a control token (no anonymous observability/audit) | `control_plane_secured` |
| 3 | Evaluation sandbox enforced | `sandbox_enforced` |
| 4 | **Untrusted/multi-tenant only:** OS-hard out-of-process isolation (#76) available | `hard_os_isolation` — **blocking** for `untrusted`, derived from the worker's real capability (always false until #76 ships) |
| 5 | A recent backup exists and DR restore was rehearsed | `restorable_backup` — derived from a durable successful `backup` row in the audit log |
| 6 | Catalog recovery completed cleanly on last start | `catalog_recovered` |
| 7 | Worker capacity available | `worker_capacity` |
| 8 | No active **critical** alerts | `no_critical_alerts` |
| 9 | Job completeness meets the warning threshold with enough samples | `snapshot_completeness` — advisory for `trusted`, blocking for `untrusted`; missing/insufficient samples are not assessed and do not pass |

- **Trusted single-tenant** pilots may exit with #76 informational (not blocking), but still require a
  secured control plane (check 2) — a tokenless control plane exposes cross-tenant observability/audit and
  is dev-only.
- The completeness check is advisory for trusted workloads, but a **critical** `low_completeness_rate`
  alert still blocks them via check 8. For untrusted workloads, both low and unassessed completeness block.
- **Untrusted / multi-tenant** pilots **cannot** exit until #76 is green — the gate returns *not ready* and
  names `hard_os_isolation` as the blocker, no matter how healthy everything else is.

The gate is exercised by `PilotReadinessTests` (untrusted blocked without #76; trusted allowed; tokenless
control plane blocked) and `ObservabilityHttpTests` (the endpoint ignores request-supplied capability flags).

---

## Rollback procedure (criterion 7)

If a pilot regresses, roll back deterministically:

1. **Stop new work.** Pause `ensure` callers (CI) so no new jobs enter.
2. **Restore the last good backup.** `sextant service restore /backups/<last-good>`; re-provide credentials
   per the manifest boundary. Because snapshots are **immutable**, restoring an older catalog cannot corrupt
   a newer one — it replaces it wholesale.
3. **Roll back the build** to the previous service version. (A backup from a newer schema is refused by an
   older build — never force it; roll the *catalog* back to the matching backup instead.)
4. **Start and verify.** Startup re-runs migrations for that build, recovers, reconciles, and re-enforces
   authorization. Confirm `GET /control/metrics` and an authorized spot query.
5. **Fall back to local.** The local stdio MCP path is always available with zero service dependency; a
   client can index its local diff and query locally while the service is rolled back.

The restore-then-authorized-query and immutable-snapshot-unchanged invariants are exercised by
`ServiceBackupRestoreTests`; the local-path-still-works invariant by the existing local MCP tests, which
run unchanged on this branch.
