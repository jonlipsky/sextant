# Phase 13 — Build the standalone Sextant index service

## Goal

Provide a persistent, independently deployable data plane for snapshot requests, contribution ingestion, caching, status, and low-latency semantic queries.

## Scope

- Add service APIs for ensure-snapshot, status, contribution/artifact ingestion, branch resolution, retention, and query.
- Add durable snapshot catalog and semantic storage ownership.
- Add idempotent job attachment and atomic publication.
- Add direct authenticated HTTP MCP/query access.
- Add persistent checkout/artifact/cache volumes separate from worker scratch directories.
- Support single-node development and scalable production deployment.

## Technical design / files

- Add service/hosting projects without coupling core indexing libraries to ProcessStack.
- Separate control endpoints from query endpoints.
- Use immutable artifact identifiers and bounded local caches.
- Stage worker output, validate it, and publish through the snapshot catalog.
- Add health/readiness endpoints that distinguish service availability from worker capacity.
- Keep local stdio MCP and standalone local operation supported.

## Acceptance criteria

1. Repeated ensure requests for the same snapshot attach to one durable job/result.
2. Service restart preserves snapshot catalog, published data, and in-progress job reconciliation.
3. Worker scratch cleanup cannot delete a published snapshot.
4. A complete snapshot is queryable through authenticated HTTP MCP without ProcessStack.
5. Partial/failed/unsupported jobs expose structured project diagnostics.
6. Local-only Sextant installation remains functional and does not require service dependencies.

## Notes / risks / dependencies

- Initial deployment may be single-writer even when multiple analysis workers run.
- Storage sharding is deferred until measured snapshot/catalog size or write concurrency requires it.
