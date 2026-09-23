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

Alerts evaluated over the metrics snapshot: `queue_delay_high`, `low_success_rate`, `worker_exhaustion`,
`storage_pressure`.

---

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
   and the evaluation logs, then re-`ensure`.
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
   a live writer's staging generation.
4. Re-`ensure` the identity — ensure is **idempotent**; it attaches to the existing durable job rather than
   duplicating work.

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
   history, and source blobs with reclaimable bytes, and changes nothing.
2. Apply with `sextant retention --execute`.

Retention honors the repo [`retention` policy](configuration.md#retention) (`keep_complete_generations`,
`api_snapshot_keep_commits`, `prune_superseded_source_blobs`) and never deletes data referenced by a
protected/default branch, an open pull request, a submodule pin, or an active overlay base.

---

## Runbook: schema upgrades (with rehearsal)

Migrations are **additive/forward-only** from `011` onward; `LatestSchemaVersion` auto-derives from the
highest migration file. A restored/older catalog is upgraded on next startup.

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

---

## Runbook: snapshot corruption / disaster recovery

**Symptom.** A snapshot fails to serve; catalog integrity error; volume loss.

**Action.**
1. Stop the service.
2. `sextant service restore /backups/<latest>` — lays the consistent catalog + immutable artifact volume
   back down (clears stale WAL/SHM sidecars first).
3. **Re-provide credentials + authorization config.** The backup contains **no secrets**; re-export the
   environment variables the manifest's `credentials_boundary` lists (`SEXTANT_SERVICE_CONTROL_TOKEN`,
   `…_QUERY_TOKEN`, `…_CONTRIBUTE_TOKEN`, `SEXTANT_SERVICE_READ_POLICY`, `SEXTANT_LLM_API_KEY`). The read
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
  already prevents an unauthorized caller from learning anything. The `query`/`denied` vocabulary in
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

- **Trusted single-tenant** pilots may exit with #76 informational (not blocking), but still require a
  secured control plane (check 2) — a tokenless control plane exposes cross-tenant observability/audit and
  is dev-only.
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
