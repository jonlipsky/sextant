# The standalone Sextant index service (Phase 13)

The **Sextant index service** is a persistent, independently deployable **data plane** that hosts
committed-branch snapshots so a client only has to index its local diff, deduplicates shared submodules
once for everyone, and answers low-latency semantic queries over authenticated HTTP MCP. It is the server
the distributed-indexing initiative was built toward.

It lives in two new projects that depend on the core libraries — **never the reverse**:

- **`Sextant.Service`** — the data-plane library: the `SnapshotService` control core, service contracts,
  on-disk volume management, and the base-snapshot federation sources. It depends on `Sextant.Store`,
  `Sextant.Indexer`, and `Sextant.Core`.
- **`Sextant.Service.Host`** — the ASP.NET Core composition root: the HTTP surface, auth middleware, and
  MCP transport. It depends on `Sextant.Service` and `Sextant.Mcp`.

> **Core stays ProcessStack-agnostic.** `Sextant.Core`, `.Store`, `.Indexer`, `.Daemon`, and `.Mcp` never
> reference the service or ProcessStack. Phase 14 will integrate ProcessStack as an orchestrator *over*
> this service's APIs; the dependency must not invert. `ArchitectureBoundaryTests` asserts this.

> **The service is additive, never required.** The local stdio MCP path and standalone local indexing
> remain fully functional with **zero** service dependency (acceptance criterion 6).

## Running it

```bash
# Single-node development: zero config. DB + volumes default under the repo's .sextant/service.
sextant service

# Scaled deployment: point volumes at durable storage and require tokens.
SEXTANT_SERVICE_DB_PATH=/data/sextant/catalog.db \
SEXTANT_SERVICE_DATA_ROOT=/data/sextant/volumes \
SEXTANT_SERVICE_CONTROL_TOKEN=... \
SEXTANT_SERVICE_QUERY_TOKEN=... \
sextant service
```

The `sextant service` CLI command is additive; every other CLI command (`index`, `query`, `serve` for the
local stdio MCP, `retention`, …) is unchanged and needs no service.

## Configuration (`ServiceOptions`)

All settings bind from `SEXTANT_SERVICE_*` environment variables, falling back to the repo `sextant.json`
for the database path and to `<db-dir>/service` for the volume root, so a bare `sextant service` works out
of the box.

| Env var | Purpose | Default |
| --- | --- | --- |
| `SEXTANT_SERVICE_DB_PATH` | Durable catalog + semantic store (the Phase-9 snapshot catalog) | repo `sextant.json` `db_path` |
| `SEXTANT_SERVICE_DATA_ROOT` | Root for the persistent volumes | `<db-dir>/service` |
| `SEXTANT_SERVICE_CHECKOUT_ROOT` | Persistent base-branch checkouts | `<data-root>/checkouts` |
| `SEXTANT_SERVICE_ARTIFACT_ROOT` | Persistent published artifacts | `<data-root>/artifacts` |
| `SEXTANT_SERVICE_CACHE_ROOT` | Bounded local caches (federation pages) | `<data-root>/cache` |
| `SEXTANT_SERVICE_SCRATCH_ROOT` | **Ephemeral** per-job worker scratch | `<data-root>/scratch` |
| `SEXTANT_SERVICE_CONTROL_TOKEN` | Bearer token for `/control/*` | none (open, dev only) |
| `SEXTANT_SERVICE_QUERY_TOKEN` | Bearer token for `/mcp` + `/query/*` | none (anonymous read) |
| `SEXTANT_SERVICE_CONTRIBUTE_TOKEN` | Least-privilege token for `/control/contribute` only (issue #71); the control token remains a superset that also authorizes it | none (falls back to control token) |
| `SEXTANT_SERVICE_READ_POLICY` | Enforced query-plane read-authorization policy (Phase 17) | disabled (open read) |
| `SEXTANT_SERVICE_CONTROL_PORT` | HTTP port | `3011` |
| `SEXTANT_SERVICE_QUERY_PORT` | Optional dedicated query port (shares the control port when unset) | none (shared) |
| `SEXTANT_SERVICE_LEASE_TTL_SECONDS` | Single-writer lease TTL | `30` |
| `SEXTANT_SERVICE_PEERS` | Comma-separated peer base URLs for federation | none |
| `SEXTANT_SERVICE_REMOTE_TIMEOUT_SECONDS` | Per-request remote-fetch timeout | `10` |
| `SEXTANT_SERVICE_CONTRIB_REQUIRE_AUTH` | Require contribution authorization (Phase 16) | `false` (dev-open) |
| `SEXTANT_SERVICE_CONTRIB_REQUIRE_GIT_VERIFY` | Require Git-content verification of uploads (Phase 16) | `false` |
| `SEXTANT_SERVICE_CONTRIB_MAX_ARTIFACT_BYTES` | Max accepted contribution artifact size (Phase 16) | policy default |
| `SEXTANT_SERVICE_SANDBOX_ENABLED` | Enforce the evaluation sandbox (Phase 17) | `true` |
| `SEXTANT_SERVICE_SANDBOX_TIME_BUDGET_SECONDS` | Wall-clock evaluation time budget | policy default |
| `SEXTANT_SERVICE_SANDBOX_MEMORY_BUDGET_BYTES` | Watchdog memory ceiling | policy default |
| `SEXTANT_SERVICE_SANDBOX_ALLOW_NETWORK` | Allow network during evaluation | `false` |
| `SEXTANT_SERVICE_SANDBOX_SCRUB_SECRETS` | Scrub secrets from the evaluation environment | `true` |

Boolean toggles accept `1/0`, `true/false`, `yes/no`, `on/off` (case-insensitive); any other non-empty
value **fails startup** rather than silently disabling a security-relevant control (fail-closed). The
per-repository/profile `platform_routing` policy and the `retention` policy are read from the repo
`sextant.json` / `SEXTANT_*` config (see [configuration.md](configuration.md)).

The **wire format is snake_case** (`ServiceJson.Options` = `SnakeCaseLower` + ignore-null, matching the
rest of Sextant's JSON). Response bodies are serialized with `ServiceJson.Options` explicitly; request
bodies bind through a matching `Http.Json.JsonOptions` registration so control-plane bodies like
`{ "repository_remote_url": ... }` bind to the contract records.

## HTTP surface — control vs query planes

The host deliberately **separates control endpoints from query endpoints**, and health from readiness:

| Endpoint | Plane | Auth | Meaning |
| --- | --- | --- | --- |
| `GET /health` | — | open | Service **AVAILABILITY**: the process is up and the catalog is reachable. |
| `GET /ready` | — | open | Worker **CAPACITY**: `503` when this node has no worker (query-only), so an operator can tell "up" from "can index". |
| `POST /control/ensure` | control | control token | Idempotent ensure-snapshot (criterion 1). Accepts an optional monotonic `branch_head_sequence` for forward-only branch-head advance (Phase 14, issue #84). |
| `POST /control/contribute` | control | control **or** contribute token | Ingest a client/CI semantic contribution (Phase 16); the least-privilege contribute token authorizes this endpoint only. |
| `GET /control/status/{jobId}` | control | control token | Job status + per-project diagnostics (criterion 5). |
| `GET /control/resolve` | control | control token | Resolve a repository branch to its current complete snapshot. |
| `POST /control/retention` | control | control token | Run the service-owned retention/GC pass (`?execute=true` to apply). |
| `GET /control/metrics` | control | control token | Observability snapshot (criterion 5); `?format=prometheus` for text exposition, else JSON. |
| `GET /control/audit` | control | control token | Durable audit log (criterion 5); optional `action`/`repository`/`limit` filters. **Operator-only.** |
| `GET /control/pilot` | control | control token | Pilot-readiness gate (criterion 7); `?workload=trusted\|untrusted&hard_isolation=&recent_backup=`. |
| `POST /control/backup` | control | control token | Write a consistent catalog + artifact backup to `?dir=` (criterion 6). |
| `GET /query/snapshots/{identityHash}/symbols` | query | query token | One immutable page of a snapshot's symbols, cursor-paged (federation, issue #51). |
| `POST /mcp` | query | query token | Authenticated HTTP MCP semantic queries (criterion 4). |

A null token disables that plane's auth (single-node development). Token checks are constant-time. Query
reads use a connection **independent** of the service writer (Phase-9 WAL supports concurrent readers), so
a low-latency query never blocks behind a running index.

## The `SnapshotService` data plane

`SnapshotService` owns the durable snapshot catalog + semantic store on a **single writer connection**,
serialized by an async gate (a raw `SqliteConnection` is not thread-safe), and guarded by the
cross-process single-writer lease. On `Start` it, in order: runs migrations **without** recovery,
acquires the writer lease (**fail-closed** — throws if a live writer already holds it), recovers a valid
WAL / sweeps abandoned staging generations, and reconciles orphaned jobs.

### Idempotent ensure (criterion 1)

`EnsureSnapshotAsync` computes the request's Phase-9 `SnapshotIdentity.Hash` — filling an omitted
`config_hash` from the service's `DefaultConfigHash` (the profile's `ConfigurationHash`) so a client that
doesn't send one still lands on the **same** identity the worker publishes under. `EnsureJob` then creates
or attaches to the **one** durable `snapshot_jobs` row for that identity. The caller attaches immediately
**only** when the job is terminal **and** its result is still usable — a `complete` job whose published
snapshot was later reclaimed by retention (issue #46) is **stale**, so it is requeued and regenerated rather
than reported as a phantom-complete. Otherwise production is serialized under the write gate so only **one**
worker runs per identity while concurrent callers attach. The pluggable `ISnapshotWorker` produces the
snapshot under a per-job **scratch** directory; its output is validated — a worker that claims
`complete`/`partial` but published no complete snapshot, **or** published a snapshot whose identity does not
match the requested hash, is **downgraded to `failed`** — and published through the catalog, then a terminal
status + per-project diagnostics are recorded. A job **cancelled** mid-run is requeued (transient), never
recorded as a permanent failure.

**Forward-only branch-head advance (Phase 14, issue #84).** An ensure request may carry an optional
monotonic `branch_head_sequence`. Because the service ensures *every* delivered commit (including
out-of-order/older ones) to build immutable content-addressed snapshots, the control plane owns commit
ordering and supplies this sequence so Sextant advances the data-plane branch pointer **forward-only**: the
pointer (and stored `branches.head_sequence`) advance only when the supplied sequence is strictly greater
than the stored one; a lower/equal sequence still ensures/attaches the immutable snapshot but leaves the
branch pointer untouched (no transient regression to a stale snapshot). A NULL sequence — the local
CLI/daemon path — advances unconditionally and never writes the column, preserving pre-#84 behavior.

### Restart recovery (criterion 2)

The catalog, published data, branch pointers, and dependency edges are all durable SQLite. A restart
holds a **fresh** lease token, so `ReconcileOrphanedJobs` resets every `running` job **not** owned by the
new token back to `queued` for re-attempt — a job a dead worker abandoned is never stuck `running`, and a
job a still-live writer owns is left alone.

### Structured diagnostics (criterion 5)

A partial / failed / unsupported job records one `snapshot_job_diagnostics` row per affected project with a
machine-parseable `code`, `severity`, and `message`. `GET /control/status/{jobId}` returns them, so a
client learns **which** projects failed and **why** (extending the Phase-9 completeness gate + Phase-8
capability meta) instead of a single opaque failure. A node with no configured worker uses
`UnavailableSnapshotWorker`: it is available for **queries** but reports no capacity, and any ensure
resolves to an `unsupported` job rather than hanging queued forever.

## Platform-specific routing by worker capability (Phase 15)

The ProcessStack server runs on **Linux**, but a C# graph may target Windows (`net8.0-windows`) or Apple
platforms (`net8.0-ios`, `-maccatalyst`, …). Linux evaluates *most* such projects fine with restored
reference packs; only projects whose evaluation Linux genuinely **cannot** complete are routed to a native
worker. Routing keys off **demonstrated evaluation success + policy, NEVER the TFM name alone** — a
`net8.0-windows` class library that Linux loaded cleanly is never needlessly routed.

**The capability model + routing decision are ProcessStack-agnostic and live in `Sextant.Core.Platform`:**

- `WorkerCapability` — a stable, comparable advertisement of what a worker can faithfully evaluate (OS,
  architecture, SDK bands, installed workloads, reference packs, target platforms, custom tools). Its
  deterministic `Fingerprint` (SHA-256) is recorded in snapshot provenance and folded into the Phase-9
  snapshot identity **only when non-null**, so a snapshot built under one capability set is never silently
  reused under an incompatible one (extends the Phase-8 config-hash + Phase-11 read-time compat gate).
- `CapabilityRequirement` — what a project graph needs; a *portable* requirement is satisfied by any
  worker. A requirement derived from the TFM string is only a **hint** (`RequirementSource.Declared`);
  routing escalates only on a `DemonstratedFailure`.
- `CapabilityRouter` — the **pure** decision engine: given each project's discovered requirement, its
  demonstrated Linux outcome, the available worker capabilities, and the policy, it decides per project to
  keep it on Linux, escalate it to the least-specialized compatible native worker, or fail it closed. It
  never executes anything.
- `PlatformRoutingPolicy` — per-repo/profile policy (`platform_routing` in `sextant.json`, env
  `SEXTANT_PLATFORM_ROUTING`): `auto` (default — escalate to any compatible native worker) or `linux_only`
  (never escalate; a non-Linux-evaluable project is marked unsupported). An `allowed_native_operating_systems`
  set can enable one OS while withholding another during capacity provisioning.

**The execution seam is service-side (`Sextant.Service.Placement`), behind the same core contract:**

- `IWorkerPlacement` — advertises a `Capability` and produces a snapshot when selected. `LocalPlacement`
  (the Phase-13 in-process indexer) is the **default (Linux)** placement. The actual native Windows/macOS
  worker EXECUTION is ProcessStack's trusted-placement job (**Phase 14, deferred**) — a Phase-14 placement
  implements this same interface without the core routing contract ever depending on ProcessStack.
- `IPlatformEvaluationProbe` — observes the demonstrated Linux outcome per project. The default
  `AssumeLinuxCapableProbe` reports everything Linux-capable (correct zero-cost behaviour for the common
  portable case); a deployment with native workers substitutes a real probe.
- `CapabilityRoutingSnapshotWorker` — the `ISnapshotWorker` that ties probe → `RouteJob` → fail-closed
  diagnostics **or** execute the selected placement. Routing is **job-granular** in Phase 15 (one worker
  per job; per-project mixed production is a later phase).

**Fail closed (criterion 4).** When no compatible worker exists (or no single worker covers the union of a
job's escalated requirements), the job resolves to `unsupported` with a structured `no_compatible_worker`
diagnostic per affected project (reusing `snapshot_job_diagnostics`) and **no** complete snapshot is
published — never a silent empty success. A routed success additionally records a `routed_to_native_worker`
info diagnostic in provenance.

**Local operation is untouched (CRITICAL 2).** A plain single-node/local run leaves the snapshot
capability null (identity byte-identical to pre-Phase-15) and, with only the default placement registered,
the routing worker never escalates — so a local Linux/Windows/macOS dev box indexes its own platform
exactly as before, with **zero** routing infrastructure required.

Because the native placements are a substitutable seam, the routing **decision** + fail-closed + fingerprint
logic are fully covered on a Linux CI with fake Windows/macOS placements (criterion 6). The env-gated
`PlatformFixtureMatrixTests` (`SEXTANT_RUN_PLATFORM_MATRIX=1`, `[TestCategory("Performance")]`) sweeps the
full fixture matrix without destabilizing the default suite on runners lacking a native toolchain.

## On-disk volumes — scratch is quarantined (criterion 3)

`ServicePaths` materializes four roots and enforces the load-bearing invariant that **worker scratch is
separate from the persistent checkout/artifact/cache volumes**:

- The constructor asserts the scratch root is not nested inside any persistent volume (or vice versa) and
  throws otherwise.
- Scratch is allocated **per job** under the scratch root; `ReleaseScratch` refuses any path that resolves
  outside the scratch root (a persistent volume, the catalog directory, or a `..` escape).

So a botched worker-scratch cleanup can **never** reach — let alone delete — a published snapshot's durable
data. This extends the Phase-8 retention servable guard + Phase-9 `BranchPointerProtection`.

## Untrusted evaluation sandbox (Phase 17, criterion 2)

MSBuild project evaluation is an **untrusted execution boundary — even for a private repo** (imported
targets, SDK resolvers, inline `UsingTask`/`Exec` tasks run arbitrary code). Every service-worker evaluation
of a checkout therefore runs through `EvaluationSandbox`, which enforces a wall-clock **time budget**, a
watchdog **memory ceiling**, **secret scrubbing** + an **offline/no-telemetry** environment, and
**fail-closed scratch confinement** (it refuses to run if the per-job scratch is not under the scratch root,
so evaluation can never write into a persistent volume).

> ⚠️ **Defense in depth, NOT a hard security boundary.** The untrusted work runs **in-process**, so a
> hostile project can still read/write arbitrary filesystem paths the worker user can reach, spawn child
> processes, open network sockets, and ignore the cooperative cancellation (a tight native loop never
> observes the token). The memory ceiling is a cooperative abort, not an OS hard cap; the offline posture is
> best-effort environment, not a kernel network block.

**Operational rule:** do **not** host untrusted third-party repositories in multi-tenant production on this
in-process tier. It is adequate for local/single-node use and for **explicitly-onboarded, trusted pilot
repositories**. True OS-hard isolation (job object / cgroup + rlimits + network namespace / `sandbox-exec`,
over an **out-of-process** evaluator) is tracked as **issue #76** and is a **documented precondition** for
untrusted multi-tenant production — it will be wired into the security runbook and the pilot exit criteria
(criterion 7), and cross-referenced from the ProcessStack integration (#19). The single-node local CLI/daemon
path does not run this worker, so leaving the sandbox unwired there keeps local operation byte-identical.

## Retention & GC — the service is the lease owner (#46 / #37 / #54 / #38)

Now that the service owns the durable catalog and a `/control/retention` endpoint, it performs the
snapshot-DATA garbage collection Phase 9 deferred:

- **#46 — server-side GC of unreferenced snapshot data.** `RetentionService` computes a **protected set**
  (pending snapshots, snapshots on a non-deletable/servable generation, and branch-pointed snapshots),
  transitively expands it across `base_snapshot_id` + `snapshot_dependencies` (consumer→provider) to a
  fixpoint, and deletes only the orphaned (non-pending, unprotected) snapshot semantic rows. GC is
  **bounded** and protected-set-aware.
- **#37 — bounded, protected-set-aware source-blob prune.** The orphan source-blob prune now honors the
  same protected set instead of being per-row/unbounded.
- **#54 — providers of *retained* consumers are protected.** The protected set includes providers
  referenced by **any retained** consumer generation (not just branch-pointed heads), coupled with the
  Phase-12 published-status gate, so a historical-scope cross-repo usage query over a superseded consumer
  cannot lose provider rows GC'd out from under it.
- **#38 — single-writer lease.** Retention/publish/GC run under the `writer_lease`, so they can never race
  a live daemon/service. The lease is acquired fail-closed at `Start`, heartbeated for the service's
  lifetime, and a stale lease may be stolen so a crash never wedges the DB.

## Remote snapshot federation (#51)

Phase 11 built the **local** federation planner, immutable-snapshot pinning, and a base-snapshot-source
seam. This service provides the **remote** half:

- `GET /query/snapshots/{identityHash}/symbols` serves one immutable page of a snapshot's symbols by its
  portable identity hash, with a stable cursor (result **paging**), from a short-lived per-request read
  connection.
- `RemoteHttpBaseSnapshotSource` is the client seam Phase-11's planner slots into. It is **cache-first**
  (**caching by snapshot id** via `SnapshotPageCache`, a bounded LRU keyed by `{identityHash}:{cursor}:
  {limit}`), enforces a per-request **timeout** (linked CTS), and on timeout / connection failure / 401 /
  403 raises `RemoteSnapshotUnavailableException`. A page already in cache is served **without contacting
  the peer** — the transparent **offline fallback** to a cached base.

## Provider-snapshot growth is immutable (#53)

A provider snapshot that *gains* a project after publish must never mutate an already-complete generation
(the same immutability contract as Phase 9/10/12). A late-referenced provider project produces a **new
pending** provider generation and republishes atomically; `MarkComplete` is a guarded no-op on a snapshot
that is already complete. `ProviderGrowthImmutabilityTests` is the regression.

## Observability (criterion 5)

The service exposes a **clean, dependency-free** observability surface on the **control plane only** — every
signal is operator data, so none of it is reachable with a query token (criterion-1 leakage guard: audit
rows and per-repository cost would otherwise reveal repository/snapshot existence and cross-tenant counts).

- **`GET /control/metrics`** returns a point-in-time [`MetricsSnapshot`](../src/Sextant.Service/Observability/MetricsSnapshot.cs):
  indexing latency (worker run time), queue delay (wait before a worker claimed a job), success &
  completeness rates, worker capacity, storage (catalog + artifact + cache + checkout bytes), cache reuse
  (idempotent-ensure attach rate + federation page-cache hit rate), and query latency (p50/p95/max). Add
  `?format=prometheus` for text exposition a scraper/dashboard can ingest directly. Alerts are evaluated
  over the snapshot (`queue_delay_high`, `low_success_rate`, `worker_exhaustion`, `storage_pressure`).
- **`GET /control/audit`** serves the durable [`audit_log`](../src/Sextant.Store/Migrations/020_audit_log.sql)
  (migration 020): who did what to which repository scope, with what outcome and at what worker cost.
  The actor is stored as a **non-reversible hash**, never the raw token; the raw secret never touches the DB.
- **`GET /control/pilot`** evaluates the pilot-readiness gate (see runbooks).
- **Traces:** ensure / retention / backup emit `System.Diagnostics.Activity` spans on the
  `Sextant.Service` `ActivitySource`, so an operator can wire OpenTelemetry without any code change.

Observability is **optional** — the local stdio MCP path and standalone local indexing never construct any
of this and stay byte-identical (criterion 6 / zero service dependency).

## Backup, restore & disaster recovery (criterion 6)

A backup captures the two durable, non-reconstructable stores and **nothing else**:

- the **catalog** database, copied with SQLite's **online backup API** so it is a transactionally
  consistent point-in-time image even while the writer is active (no torn WAL); and
- the immutable **artifact** volume (published snapshot outputs).

Worker scratch (ephemeral) and the checkout/cache volumes (reconstructable from Git / federation) are not
backed up, and **secrets are never written** into a backup — the `manifest.json` records a *credentials
boundary* listing the environment variables an operator re-provides on restore.

```bash
sextant service backup   /backups/2026-09-19   # consistent catalog + artifact copy
sextant service restore  /backups/2026-09-19   # lay catalog + artifacts back down (offline)
sextant service                                # start: migrate → recover → reconcile → RE-ENFORCE authz
```

Restore refuses a backup taken at a **newer** schema than the restoring build (same forward-only guard as
`IndexDatabase.CheckReadiness`). Because a restored service goes through the normal `Start` flow — run
migrations, recover the WAL, reconcile orphaned jobs, and load the read-authorization policy from
configuration — a restored service is a **queryable AUTHORIZED** service, never a policy-stripped one, and
the immutable snapshot it serves is byte-for-byte the one that was backed up. `POST /control/backup` writes
a backup from a running service under its writer gate; restore is offline (it must precede startup). See
[`runbooks.md`](runbooks.md) for the schema-upgrade rehearsal and DR drill.

## Migration & schema

Migration `016_service_job_catalog.sql` adds `snapshot_jobs`, `snapshot_job_diagnostics`, and
`writer_lease`; `017_snapshot_capability_fingerprint.sql` adds `snapshots.capability_fingerprint`
(Phase 15); `018_client_contributions.sql` and `019_pull_request_retention_roots.sql` are the Phase-17
slice-1/2 additions; `020_audit_log.sql` adds the durable operational + security **audit log** (Phase 17
slice 3, criterion 5); `021_branch_head_sequence.sql` adds `branches.head_sequence` for the forward-only
branch-head advance on the ensure path (Phase 14, issue #84). All are additive/forward-only. See
[`schema.md`](schema.md) for the table definitions. `LatestSchemaVersion` auto-derives from the highest
migration and is **21**.

## Testing

Service tests are fast and hermetic: an **in-process `TestServer`** (no real network), an ephemeral SQLite
catalog, and a `FakeSnapshotWorker`. The suite maps to the acceptance criteria:

| Criterion | Test coverage |
| --- | --- |
| 1 — idempotent ensure | `SnapshotServiceTests` (concurrent ensures attach to one job; worker runs once) |
| 2 — restart recovery | `SnapshotServiceTests` (catalog survives restart; orphaned `running` jobs reconciled) |
| 3 — scratch cannot delete published | `ServicePathsTests` (scratch/persistent separation + `ReleaseScratch` refusal) |
| 4 — query via HTTP MCP without ProcessStack | `ServiceHttpTests` (`/mcp` mapped + auth-gated; `/query` paging) |
| 5 — structured per-project diagnostics | `SnapshotServiceTests` (partial/failed/unsupported diagnostics) |
| 6 — local-only remains functional | `ArchitectureBoundaryTests` (core assemblies never reference the service; local query without a service) |

Plus store-level regressions: `RetentionSnapshotGcTests` (#46/#37/#54), `WriterLeaseTests` (#38),
`ProviderGrowthImmutabilityTests` (#53), and `RemoteFederationTests` (#51 paging/caching/offline/timeout/
auth).

### Phase 15 — platform routing tests

| Criterion | Test coverage |
| --- | --- |
| 1 — portable graphs stay on Linux | `CapabilityRouterTests`, `CapabilityRoutingWorkerTests` (default placement, no route) |
| 2 — Windows project routes to a Windows worker | `CapabilityRoutingWorkerTests` (fake Windows placement publishes) |
| 3 — Apple project routes to a macOS worker | `CapabilityRoutingWorkerTests` (fake macOS placement publishes) |
| 4 — no compatible worker → fail closed | `CapabilityRouterTests`, `CapabilityRoutingWorkerTests` (`unsupported` + `no_compatible_worker`, no complete snapshot) |
| 5 — capability fingerprint gates reuse | `SnapshotIdentityTests`, `SnapshotCapabilityStoreTests`, `FederatedReadContextTests` (identity fold + read-time compat) |
| 6 — matrix validated / env-gated; core stays agnostic | `PlatformFixtureMatrixTests` (`SEXTANT_RUN_PLATFORM_MATRIX`), `ArchitectureBoundaryTests` (`WorkerCapability`/`CapabilityRouter` on the core side) |

The demonstrated-not-TFM rule is unit-tested by `LinuxEvaluationAnalyzerTests`; criteria 2/3 native legs run
through a substitutable fake placement on Linux (real native execution is Phase 14).
