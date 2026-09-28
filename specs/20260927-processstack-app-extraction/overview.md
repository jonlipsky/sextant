# Sextant as an ordinary ProcessStack app: overview

> **Approved at G1 (2026-09-27). Tracking: elevenworks/ProcessStack#3159.** Committed by SX-0, which restores the `specs/` convention.
> Evidence refers to `jonlipsky/sextant` `origin/main` `9544f76`. "PS:" marks `elevenworks/ProcessStack` `main`.
> Prod-specific values (host names, addresses, ids, key ids, image tags) appear as `<placeholders>`. The operator holds the concrete values.

| File | Contents |
|---|---|
| `overview.md` (this file) | Goals, architecture, query path, who owns what |
| [`service-changes.md`](service-changes.md) | Service PR units SVC-1…8, SVC-F; contracts, env, migration 024, tests |
| [`app.md`](app.md) | `apps/processstack/sextant/` v2.0.0: manifest, flows, scenario tests, README, CI |
| [`app-activities.md`](app-activities.md) | G1-A amendment: the app's own activities (`Sextant.ProcessStack.Activities`), build scripts, CI (SX-13) |
| [`security.md`](security.md) | SSRF host policy, assertion verification, tenant binding, visibility, hardening |
| [`pr-plan.md`](pr-plan.md) | SX-0…SX-12, stacking, dependencies on PS features |
| [`cutover-runbook.md`](cutover-runbook.md) | G2a/G2b/G2c, validation, rollback to v1.0.1 |
| [`processstack-deletion.md`](processstack-deletion.md) | PS-11 deletion, generic grant prune |

## Goals

| # | Goal |
|---|---|
| G-1 | Sextant reaches ProcessStack users only as an app (`apps/processstack/sextant/`, manifest `name: sextant`, **v2.0.0**, the next version of the existing asset). It is shipped with `processstack app validate\|test\|publish\|activate`. |
| G-2 | The app uses only generic PS primitives: a `type: mcp` connection, a `type: http-api` connection, the `github` connection, App State, user memory, `connection-event`/`schedule`/`prompt` triggers, trigger run-now (for the one-shot G2c enrollment import), and the per-app MCP surface. |
| G-3 | The service enforces per-user repository visibility from a verified caller assertion (option A). The app never sees or filters query results. |
| G-4 | Domain rules move into the service: URL/host policy (SVC-5), branch-head CAS (SVC-6+7), grants (SVC-4), and federated symbol search (SVC-F). |
| G-5 | Core libraries (`Sextant.Core/.Store/.Indexer/.Daemon/.Mcp`) stay ProcessStack-agnostic. Only generic, default-off hooks are added (`CLAUDE.md:37-39`; `tests/Sextant.Service.Tests/ArchitectureBoundaryTests.cs:5-55`). |
| G-6 | Local stdio MCP and local indexing keep working with zero service dependency and stay byte-identical (`CLAUDE.md:63`, criterion 6). |
| G-7 | Zero-downtime cutover: v2.0.x dual-writes the legacy stores so that the old gateway and a rollback to v1.0.1 stay consistent (see the runbook). |

## Non-goals

- PS-side feature specs (PS-1…PS-8, PS-13). They live in ProcessStack and are referenced by slug below.
- Hard OS isolation of untrusted MSBuild (#76), per-request checkout credentials (#111), and service-side reconcile (SVC-10). These are listed as N units.
- Changing the snapshot identity hash (SVC-15, N).
- Exposing `research_codebase` on the app surface (decided at G1). It is LLM-backed and needs `SEXTANT_LLM_*` on the service. It is excluded by default and can be opted in later.
- Slack-chat watch management in v2.0 (decided at G1: `CALLER_IDPS=processstack` only; see `security.md`). Only `idp=processstack` callers act as users.

## Architecture

```mermaid
flowchart LR
  subgraph Agents
    A[Agent / MCP client<br/>app-scoped PS key]
    U[Chat user<br/>web/slack/cli]
  end
  subgraph PS[ProcessStack platform - generic]
    D["/v1/{tenant}/mcp/sextant<br/>per-app MCP dispatcher"]
    CT["PS-8 (F3) connection-tool exposure<br/>(allow-list)"]
    OCI["PS-7 (F4) outbound caller identity<br/>X-ProcessStack-Caller JWS<br/>(immutable RunCaller)"]
    GH[github connection<br/>repository events]
    RT[app runtime<br/>orchestrations/processes]
    ST[(App State + user memory)]
  end
  subgraph APP[apps/processstack/sextant v2.0.0]
    P1[start-indexing / get-indexing-status /<br/>import-legacy-watches]
    O1[on-repository-change]
    O2[reconcile nightly / run-now<br/>v2.0.x: legacy enrollment import]
    O3[configure-watched-repos chat<br/>internal channels in v2.0]
  end
  subgraph SX[Sextant service container]
    MCP["/mcp stateless<br/>query plane"]
    CTL["/control/*<br/>ensure, status, resolve,<br/>grants, branches/retire"]
    V[assertion verifier SVC-3<br/>grant authorizer SVC-4]
    DB[(catalog SQLite<br/>schema 24)]
  end
  A --> D --> CT -->|sextant-query conn<br/>delegate bearer + JWS| MCP
  U --> RT
  D --> P1
  GH -->|push/delete/PR| O1
  RT --- O1 & O2 & O3 & P1
  O1 & O2 & O3 & P1 -->|sextant-control conn<br/>control bearer + JWS| CTL
  O1 & O2 & O3 -.dual-write / legacy read v2.0.x.-> ST
  MCP --> V --> DB
  CTL --> V
  OCI -.attaches.-> CT & CTL
```

## Query path (a)

```mermaid
sequenceDiagram
  participant Ag as Agent
  participant PS as PS /v1/{t}/mcp/sextant
  participant Pool as pooled MCP client (sextant-query)
  participant Sx as Sextant /mcp
  Note over Pool,Sx: pool connect: initialize + tools/list<br/>delegate bearer, NO assertion (allowed, SVC-3)
  Ag->>PS: tools/call find_references {symbol_fqn, repository, branch?}
  PS->>PS: mcp:run:sextant; name in connectionTools.include (PS-8)
  PS->>Pool: forward arguments verbatim (timeout 120s)
  Pool->>Sx: POST /mcp  Authorization: delegate bearer<br/>X-ProcessStack-Caller: JWS{tid,act=user,idp=processstack,sub,app,...} (PS-7)
  Sx->>Sx: verify JWS; kid→tenant; tid match; idp/app allowed (SVC-3)
  Sx->>Sx: lift repository/branch args → selector (SVC-2)
  Sx->>Sx: TryBeginRead: grants(tid,sub) ∪ grants(tid,*) (SVC-4)
  Sx-->>Pool: CallToolResult (or uniform not-found)
  Pool-->>PS: verbatim
  PS-->>Ag: verbatim
```

- **One repository per call.** Tools read one pinned snapshot (`src/Sextant.Mcp/FederatedReadContext.cs:67-110`). The only multi-repository tools are:
  - `search_symbols` (SVC-F);
  - `list_repositories` (SVC-4);
  - the two cross-repository tools, which filter through `IReadAuthorizer.AuthorizeRepository` (`src/Sextant.Mcp/SnapshotProvenance.cs:150`).
- **Selection travels in reserved args (SVC-2).** The pooled MCP connection sends only static headers (PS:`src/ProcessStack.Connections.Mcp/McpConnection.cs:431-437`), so `X-Sextant-Repository` cannot vary per call.
- **No app process runs per query.** PS-8 (F3) forwards the call directly.

## Who owns what

| Concern | Platform (generic PS) | App (`apps/processstack/sextant`) | Service (Sextant) |
|---|---|---|---|
| Agent auth, `mcp:run:sextant`, app-scoped keys | ✓ (PS-8, PS-4 live-bounded keys) | — | — |
| Tool allow-list on the app surface | ✓ enforces (PS-8) | ✓ declares `connectionTools.include` | ✓ own `RemoteQueryTools` allow-list (`src/Sextant.Service.Host/ServiceApp.cs:115-125`) |
| Caller identity | ✓ stamps an immutable `RunCaller` and signs (PS-7); triggers are `act=application` | declares `callerIdentity` on its connections | ✓ verifies, binds kid→tenant, allow-lists `idp` and `app` (SVC-3) |
| Per-user visibility | — | writes grants via chat, enrollment, import | ✓ stores and enforces (SVC-4) |
| Repo URL / host policy (SSRF) | — | pre-checks watch with GitHub `GetRepository` | ✓ authoritative (SVC-5) |
| Webhook ingress, delivery dedup | ✓ (github-repository-events, PS-5) | maps event → ensure/retire | ✓ content-addressed dedup (`SnapshotService.cs:222-499`) |
| Forward-only branch heads | — | passes `before` as `expected_head_commit` | ✓ CAS (SVC-6+7) |
| Nightly reconcile | ✓ schedule trigger; trigger run-now for the one-shot G2c import | ✓ (v2.0.x) legacy enrollments → tenant grants; grants → GitHub head → resolve → ensure | resolve returns `commit_sha` (SVC-6+7) |
| Cross-repo symbol enumeration | — | — | ✓ `search_symbols` (SVC-F) |
| Secrets | ✓ connection secret store | **none in App State** (v1's `sextant-control-token` is removed in v2.1) | env only |

## PS feature dependencies

| PS # | Name | ProcessStack-side spec (slug) | Needed by |
|---|---|---|---|
| PS-1 | Platform/app boundary | `20260927-platform-app-boundary` | Guardrail (G3) |
| PS-2 | Sender-id hardening (elevenworks/ProcessStack#3141: stop honoring a client-supplied `SenderId`) | — | Prerequisite of PS-7 |
| PS-3 | Unregistered-grant pruning | `20260927-unregistered-grant-pruning` | Removing `mcp:sextant:read` (G3) |
| PS-4 | Live-bounded API keys | `20260927-live-bounded-api-keys` | Agent keys (`--app sextant`) |
| PS-5 | GitHub repository events | `20260927-github-repository-events` | APP-2 triggers (`push`, `delete`, `pull_request.*`); metadata keys used as `event.<key>` |
| PS-6 | Scenario connection stubs | `20260927-scenario-connection-stubs` | Every app scenario test (`mcp:`, `http:`, `seed.userMemory`, `principal`) |
| PS-7 | F4 outbound caller identity (immutable `RunCaller`, `idp` claim, namespaced external `sub`) | `20260927-outbound-caller-identity` | SX-5 (contract), every app→service call |
| PS-8 | F3 connection-tool exposure | `20260927-mcp-connection-tool-exposure` | The query path; APP-1 manifest |
| PS-13 | `DeleteMyMemory` / `DeleteUserMemory` activities (`IUserMemoryStore.DeleteAsync` exists; only the activities are missing) | — | APP v2.1 legacy cleanup. v2.0 writes a JSON `null` tombstone instead (PS:`src/ProcessStack.Activities.Sextant/WatchedRepos/WatchedRepoStore.cs:33-35` skips null values) |
| — | Trigger run-now (exists: `POST /v1/{tenant}/triggers/run/{triggerId}`, PS:`TriggersController.cs:399-416`) | — | G2c one-shot enrollment import via `nightly-reconcile` (`act=application`) |

## Findings that shape this spec

| # | Finding | Evidence | Handled in |
|---|---|---|---|
| 1 | PS's default gateway allow-list names `find_usages` and `search_symbols`. **Neither exists in the service**; the real tool is `find_references`, and `search_symbols` was gateway-native. | PS:`src/ProcessStack.Activities.Sextant/SextantGatewayOptions.cs:145-165`; `src/Sextant.Mcp/Tools/FindReferencesTool.cs:11`; `git grep find_usages` finds only a comment (`src/Sextant.Mcp/RemoteBaseSymbolFederation.cs:20`) | app.md include list; SVC-F |
| 2 | PS `sextant.search_symbols` **appears non-functional against the real service**. Its DTO expects `name`, `symbol_id`, a string `kind`, `namespace`, `file_path` and `line`. The service page sends `display_name`, `symbol_key`, an **integer** `kind`, `accessibility` and `project_canonical_id`. `kind` int→`string?` fails to deserialize. | PS:`src/ProcessStack.Activities.Sextant/Client/SextantQueryContracts.cs:10-31` + `SextantJson.cs:16-19`; `src/Sextant.Store/BaseSnapshotSource.cs:8-25`; `src/Sextant.Service/ServiceJson.cs:9-13` | SVC-F keeps the *semantics* (federation, cursor, pending), and its fields follow the Sextant page |
| 3 | `/control/resolve` returns a `SnapshotRow`, which has `commit_id` but **no `commit_sha`**. PS reconcile compares `resolved.CommitSha`, which is therefore always null, so it re-ensures every target every night. | `src/Sextant.Service.Host/ServiceApp.cs:261-273`; `src/Sextant.Store/SnapshotStore.cs:29-51`; PS:`src/ProcessStack.Activities.Sextant/Gateway/SextantWatchedReposReconciler.cs:188` | SVC-6+7 adds `commit_sha` |
| 4 | There is no ensure mode that publishes without moving a branch. PS's PR ensures set `AdvancesBranchHead=false`, but the service advances on the null-sequence worker path. | PS:`src/ProcessStack.Connections.GitHub/RepositoryEvents/GitHubRepositoryEventMapper.cs:202,216`; `src/Sextant.Indexer/IndexOrchestrator.cs:1690-1707`; `src/Sextant.Service/LocalIndexerSnapshotWorker.cs:762` | SVC-6+7 `branch_update: none` |
| 5 | PR retention roots exist (`RegisterPullRequestSnapshot` / `ClosePullRequestSnapshot`) but have **no HTTP route**. | `src/Sextant.Service/SnapshotService.cs:1407,1447`; the `Map*` calls in `ServiceApp.cs:198-396` | SVC-19 (N) |
| 6 | The PS publisher sent GitHub's `repository.clone_url` verbatim, and identity hashes the **raw** URL. The app must send the same spelling (`event.cloneUrl`), or every repo is re-indexed once at cutover. | PS:`src/ProcessStack.Connections.GitHub/RepositoryEvents/GitHubRepositoryEventMapper.cs:254,262-277`; `src/Sextant.Service/ServiceContracts.cs:86-97` | app.md, "URL spelling" |
| 7 | Legacy enrolled App State is **PascalCase**; legacy watched memory is camelCase. | PS:`src/ProcessStack.Activities.Sextant/Gateway/EnrolledRepoState.cs` (`JsonSerializerDefaults.General`); PS:`src/ProcessStack.Activities.Sextant/WatchedRepos/WatchedRepoStore.cs` | app.md, dual-write |
| 8 | CI needs `PROCESSSTACK_PACKAGES_TOKEN`, and **a human must add it**. | GitHub Packages NuGet requires a PAT with `read:packages` | app.md, CI |
