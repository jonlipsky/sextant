# Archive: the retired ProcessStack-embedded Sextant query gateway (2026-09-22 design)

> **Status: retired.** This is a short summary, written for this repository, of a design that lived in the
> private elevenworks/ProcessStack repository: its `docs/SEXTANT_INTEGRATION.md`,
> `docs/SEXTANT_REDEPLOY_RUNBOOK.md` and `specs/20260922-sextant-query-gateway/`. Those files are deleted by
> PS-11 ([`20260927-processstack-app-extraction/processstack-deletion.md`](20260927-processstack-app-extraction/processstack-deletion.md))
> and stay in that repository's git history. Nothing here is copied from them. Hosts, identifiers,
> configuration values and internal URLs are left out on purpose.
>
> The replacement is specified in [`20260927-processstack-app-extraction/`](20260927-processstack-app-extraction/overview.md)
> and documented in [`docs/service.md`](../docs/service.md), [`docs/onboarding.md`](../docs/onboarding.md)
> and [`apps/processstack/sextant/README.md`](../apps/processstack/sextant/README.md).

## What the embedded design did

ProcessStack itself acted as Sextant's control plane and query front door. It had three parts.

**1. Indexing control (the "Phase 14" integration).**
- A GitHub repository-event mapper inside the platform's GitHub connection. It normalized push, branch and
  pull-request webhooks into one repository-change event and de-duplicated deliveries by delivery id.
- A platform activity package (`ProcessStack.Activities.Sextant`), with a typed client for the service's
  ensure, status and resolve calls, and a branch-head guard. The guard kept a forward-only
  `branch_head_sequence` per branch, so a late or replayed event could not move a branch backwards.
- A sample `sextant` application (v1.0.x) with `start-indexing` and `get-indexing-status` processes. It was
  control-only by design, and kept the service URL, the control token and the branch heads in App State.

**2. The query gateway (spec `20260922-sextant-query-gateway`, a re-scope of an older platform issue).**
It was specified in phases:
- **A. MCP proxy.** A platform MCP endpoint at `/v1/{tenant}/mcp/_sextant`, on a reserved application slug,
  behind its own `mcp:sextant:read` permission. The proxy answered `initialize` and `ping` itself,
  forwarded `tools/list` and `tools/call` to the service's `/mcp` under a read-only tool allow-list, and
  capped response size and SSE streaming.
- **B. Authorization.** Deny-by-default, per principal. A repository was readable only if the caller's user
  memory (`sextant.watched-repos`) listed it, or the tenant had enrolled it, with strict tenant isolation.
- **C. Chat.** A chat flow to watch, unwatch and list repositories, writing that user memory.
- **D. Gateway-native tools.** A federated `sextant.search_symbols`, which resolved each watched repository
  to a snapshot and paged the service's `/query/snapshots/{identityHash}/symbols` with a composite cursor,
  and a `list_watched` tool.
- **E. Scale-out.** A bounded nightly reconcile of watched and enrolled repositories, and a multi-service
  routing table (each repository host owned by exactly one service, with per-tenant entitlement).

It relied on two service changes that were only proposed at the time: a compare-and-swap on the branch
head, and branch retire.

**3. Operations.** A redeploy runbook for the service and the gateway, including its own known-gaps list
(mainly that a single query could not yet span snapshots transparently).

The extraction's review of this design ([`overview.md`](20260927-processstack-app-extraction/overview.md),
"Findings that shape this spec") found three more defects: the gateway's `search_symbols` DTO expected `kind` as a string while
the service sends an integer, so it likely never worked against the real service; the default allow-list
named a tool (`find_usages`) that the service does not have; and `/control/resolve` returned no
`commit_sha`, so the reconcile re-ensured every target every night.

## Why it was removed

The platform/app boundary (PS-1): platform code must not be specific to one application. The gateway
reserved an application slug, registered an app-named permission, carried Sextant DTOs and a Sextant
authorization model in the platform, and duplicated authorization that belongs to the service. Every
Sextant change needed a platform release, and the platform had to trust its own copy of "who may read
which repository".

The extraction inverts that. ProcessStack gains **generic** features, Sextant decides authorization
itself, and the Sextant integration becomes an ordinary app that lives in this repository.

## What replaced each piece

| Retired piece | Replacement |
|---|---|
| The platform's Sextant-shaped GitHub event mapper | Generic GitHub repository connection events (PS-5), consumed by the app's `on-repository-change` flow through connection-event triggers |
| The activity package's ensure/status/resolve client | The app's `ensure`, `start-indexing` and `get-indexing-status` processes, calling the service's control plane through the `sextant-control` connection |
| The forward-only branch-head guard, and the proposed CAS/retire | Service-side `expected_head_commit` CAS, `branch_update: none` and `POST /control/branches/retire` (SVC-6+7) |
| Watched-repos user memory and tenant enrollment as the authorization source | Service-held repository grants: `/control/grants/self` for users and `/control/grants/tenant` for the tenant (SVC-4). The app dual-writes the legacy stores until v2.1, so a rollback to v1 keeps working |
| Forwarded platform headers as caller identity | A signed caller assertion on every request (SVC-3; PS-7 outbound caller identity), verified by the service against a per-tenant key |
| The `/mcp/_sextant` proxy and its allow-list | The app's MCP surface `/v1/{tenant}/mcp/sextant` (PS-8 `mcp.connectionTools`), which forwards the service's remote query tools under their own names to the `sextant-query` connection |
| Gateway-native `sextant.search_symbols` and `list_watched` | Service-side, grant-scoped `search_symbols` (SVC-F, with per-call cost bounds) and `list_repositories` |
| Multi-service host routing | The service's repository URL policy (`REPOSITORY_HOSTS` / `REPOSITORY_OWNERS`, SVC-5) plus the operator-configured connection URL |
| The gateway's nightly reconcile | The app's `reconcile` flow on its `nightly-reconcile` schedule, which also imports the legacy enrollments into tenant grants until v2.1 |
| The `mcp:sextant:read` permission and snapshot keys | Live-bounded API keys scoped to the app (`mcp:run:sextant`, PS-4). The old permission is pruned by PS-3 at G3 |
| The v1 chat | The app's `configure-watched-repos` chat, with lazy and on-demand (`import-legacy-watches`) import of v1 watches |
| The known defects above | Fixed on the Sextant side: `search_symbols` is service-side and renders `kind` by name, the app forwards only real service tool names (pinned by `ProcessStackAppManifestTests`), and `/control/resolve` returns `commit_sha` |
