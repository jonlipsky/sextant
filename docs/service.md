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
| `SEXTANT_SERVICE_CONTROL_PORT` | HTTP port | `3011` |
| `SEXTANT_SERVICE_LEASE_TTL_SECONDS` | Single-writer lease TTL | `30` |
| `SEXTANT_SERVICE_PEERS` | Comma-separated peer base URLs for federation | none |
| `SEXTANT_SERVICE_REMOTE_TIMEOUT_SECONDS` | Per-request remote-fetch timeout | `10` |

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
| `POST /control/ensure` | control | control token | Idempotent ensure-snapshot (criterion 1). |
| `GET /control/status/{jobId}` | control | control token | Job status + per-project diagnostics (criterion 5). |
| `GET /control/resolve` | control | control token | Resolve a repository branch to its current complete snapshot. |
| `POST /control/retention` | control | control token | Run the service-owned retention/GC pass (`?execute=true` to apply). |
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

## On-disk volumes — scratch is quarantined (criterion 3)

`ServicePaths` materializes four roots and enforces the load-bearing invariant that **worker scratch is
separate from the persistent checkout/artifact/cache volumes**:

- The constructor asserts the scratch root is not nested inside any persistent volume (or vice versa) and
  throws otherwise.
- Scratch is allocated **per job** under the scratch root; `ReleaseScratch` refuses any path that resolves
  outside the scratch root (a persistent volume, the catalog directory, or a `..` escape).

So a botched worker-scratch cleanup can **never** reach — let alone delete — a published snapshot's durable
data. This extends the Phase-8 retention servable guard + Phase-9 `BranchPointerProtection`.

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

## Migration & schema

Migration `016_service_job_catalog.sql` adds `snapshot_jobs`, `snapshot_job_diagnostics`, and
`writer_lease` — additive/forward-only, extending (not duplicating) the Phase-9 catalog. See
[`schema.md`](schema.md) for the table definitions. `LatestSchemaVersion` is **16**.

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
