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

## Runbook: schema upgrades (with rehearsal)

Migrations are **additive/forward-only** from `011` onward; `LatestSchemaVersion` auto-derives from the
highest migration file. A restored/older catalog is upgraded on next startup.

**Rehearsal (do this before upgrading production).**
1. Take a backup of the current production catalog: `sextant service backup /backups/pre-upgrade`.
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
3. **Re-provide credentials.** The backup contains **no secrets**; re-export the environment variables the
   manifest's `credentials_boundary` lists (`SEXTANT_SERVICE_CONTROL_TOKEN`, `…_QUERY_TOKEN`,
   `…_CONTRIBUTE_TOKEN`, `SEXTANT_LLM_API_KEY`).
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

---

## Pilot exit criteria (criterion 7)

Promote from pilot to production **only** when all of the following hold for the target workload class.
`GET /control/pilot?workload=<trusted|untrusted>&hard_isolation=<bool>&recent_backup=<bool>` evaluates them
and returns a machine-readable go/no-go with the failing checks named.

| # | Exit criterion | How it is checked |
| --- | --- | --- |
| 1 | Authorization enforced (read policy on) | `authorization_enabled` |
| 2 | Evaluation sandbox enforced | `sandbox_enforced` |
| 3 | **Untrusted/multi-tenant only:** OS-hard out-of-process isolation (#76) available | `hard_os_isolation` — **blocking** for `untrusted` |
| 4 | A recent backup exists and DR restore was rehearsed | `restorable_backup` |
| 5 | Catalog recovery completed cleanly on last start | `catalog_recovered` |
| 6 | Worker capacity available | `worker_capacity` |
| 7 | No active **critical** alerts | `no_critical_alerts` |

- **Trusted single-tenant** pilots may exit with checks 1, 2, 4–7 green; #76 is informational (not blocking).
- **Untrusted / multi-tenant** pilots **cannot** exit until #76 is green — the gate returns *not ready* and
  names `hard_os_isolation` as the blocker, no matter how healthy everything else is.

The gate is exercised by `PilotReadinessTests` (untrusted blocked without #76; trusted allowed).

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
