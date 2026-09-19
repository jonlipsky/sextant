# Phase 17 — Secure and operationalize distributed indexing

## Goal

Make distributed indexing safe, supportable, recoverable, and ready for staged production adoption.

## Scope

- Enforce tenant/repository authorization on every control and query path.
- Sandbox repository evaluation and restrict secrets, filesystem, network, CPU, memory, disk, and time.
- Add quotas, retention, garbage collection, backup/restore, disaster recovery, and cache warming.
- Add service-level dashboards, traces, audit logs, alerts, and cost attribution.
- Run load, failure-injection, worker-loss, duplicate-event, corrupted-artifact, and authorization tests.
- Roll out from explicit pilot repositories to broader defaults.

## Technical design / files

- Add policy enforcement at snapshot resolution before semantic query planning.
- Use opaque authorized repository/snapshot IDs externally where appropriate.
- Treat empty unauthorized scope differently from authorized zero results internally while avoiding information leakage.
- Add durable reconciliation for orphaned jobs, staged artifacts, branch pointers, and retention roots.
- Define backup boundaries for catalog, immutable artifacts, configuration, and credentials.
- Publish runbooks for unsupported workloads, stuck indexing, worker exhaustion, schema upgrades, and snapshot corruption.

## Acceptance criteria

1. Cross-tenant and unauthorized cross-repository tests reveal no data, counts, names, timing-sensitive existence, or artifact access.
2. Index workers execute untrusted repositories under enforced resource and secret isolation.
3. Worker/service loss at every stage reconciles to retryable, failed, or complete state without corrupt publication.
4. Retention never deletes protected branch heads, open-PR snapshots, submodule pins, or active-overlay bases.
5. Dashboards report indexing latency, queue delay, success/completeness, worker capacity, storage, cache reuse, and query latency.
6. Backup/restore and schema-upgrade rehearsals recover a queryable authorized service.
7. Pilot exit criteria and rollback procedures are documented and exercised.

## Notes / risks / dependencies

- MSBuild project evaluation is an untrusted execution boundary even for private repositories.
- Cross-repository usefulness must not weaken repository ACL semantics.
