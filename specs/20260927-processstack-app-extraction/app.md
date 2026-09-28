# App: `apps/processstack/sextant/` (v2.0.0)

> **Approved at G1 (2026-09-27). Tracking: elevenworks/ProcessStack#3159.** "PS:" refers to `elevenworks/ProcessStack` `main`. The v1 reference is PS:`samples/applications/sextant/psapp.yaml` (1.0.0; prod runs 1.0.1).
> Prod-specific values (addresses, ids, key ids) appear as `<placeholders>`. The operator holds the concrete values.
> Every step uses **generic** PS activities and triggers only. The app contains no Sextant-specific platform code, stores no secrets in App State, and never proxies queries through processes.
> PS features referenced: PS-5 (GitHub repository events), PS-6 (scenario stubs), PS-7 (F4 caller identity), PS-8 (F3 connection tools), and PS-13 (`DeleteMyMemory`).

> **Open: app repo location (decision pending with the human; needed before SX-9).** `apps/processstack/sextant/` in this repo stays the working path. The options are:
> - **A. Keep the app in this public repo.** CI restores the CLI with a classic `read:packages` PAT stored as the secret `PROCESSSTACK_PACKAGES_TOKEN`.
> - **B. Move the app to a private elevenworks repo** (e.g. `elevenworks/processstack-sextant`). Its `GITHUB_TOKEN` is granted package access through the package's "Manage Actions access", so no PAT is needed, and this repo stays ProcessStack-agnostic.
>
> The layout, flows and scenarios below are the same under either option; only the repo root and the CI auth differ.

## Layout

```
apps/processstack/sextant/
  psapp.yaml
  README.md
  orchestrations/  on-repository-change.yaml  reconcile.yaml  configure-watched-repos.yaml
  processes/       start-indexing.yaml  get-indexing-status.yaml  import-legacy-watches.yaml
                   reconcile-target.yaml  ensure.yaml  grant-watch.yaml  tenant-grant.yaml  legacy-dual-write.yaml
  tests/           *.scenario.yaml   (list below)
.github/workflows/processstack-app.yml
```

## Connections (manifest dependencies, bound at `app activate`)

| id | type | Config set at registration (the runbook's G2b) | Used for |
|---|---|---|---|
| `github` | `github` | The existing GitHub App connection; webhooks need `push`, `delete` and `pull_request` | Triggers, `GetRepository`, `ListBranches` |
| `sextant-query` | `mcp` (http) | URL `http://<sextant-service-addr>/mcp`; header `Authorization: Bearer <delegate token>`; `callerIdentity {mode: signed-header, audience: sextant, keyId: <kid>, signingKey: <key>, ttlSeconds: 60}` | `mcp.connectionTools` only |
| `sextant-control` | `http-api` | `baseUrl http://<sextant-service-addr>`; control bearer; **the same** `callerIdentity` (set `keyId` explicitly, because F4 defaults `keyId` to the connection instance id, which would make two kids) | Every `HttpRequest` step |

Activities refer to a connection by its manifest id through the `connectionId` input, resolved from the deployment binding (the pattern of PS:`samples/applications/app-store-digest/psapp.yaml:45-49`).

## `psapp.yaml` sketch

```yaml
name: sextant                 # same asset as v1 → next version
version: 2.0.0
description: Sextant semantic code index — ProcessStack app (queries served by the Sextant service via connection tools)
connections:
  - { id: github,          type: github }
  - { id: sextant-query,   type: mcp }
  - { id: sextant-control, type: http-api }
mcp:
  expose: true
  displayName: "Sextant"
  defaultTimeoutSeconds: 60
  include: [start-indexing, get-indexing-status, import-legacy-watches]
  connectionTools:            # PS-8 (F3)
    - connection: sextant-query
      prefix: ""
      timeoutSeconds: 120
      include: [find_symbol, find_references, find_by_attribute, find_by_signature, find_comments,
                find_tests, find_unreferenced, find_cross_repository_usages, find_submodule_consumers,
                get_api_surface, get_call_hierarchy, get_file_symbols, get_impact, get_implementors,
                get_index_status, get_namespace_tree, get_project_dependencies, get_type_dependents,
                get_type_hierarchy, get_type_members, semantic_search, trace_value,
                list_repositories, search_symbols]      # research_codebase excluded (LLM-backed)
entrypoints:
  - { name: on-repository-change,    type: orchestration, path: orchestrations/on-repository-change.yaml, isDefault: true }
  - { name: reconcile,               type: orchestration, path: orchestrations/reconcile.yaml }
  - { name: configure-watched-repos, type: orchestration, path: orchestrations/configure-watched-repos.yaml }
  - { name: start-indexing,          type: process, path: processes/start-indexing.yaml }
  - { name: get-indexing-status,     type: process, path: processes/get-indexing-status.yaml }
  - { name: import-legacy-watches,   type: process, path: processes/import-legacy-watches.yaml }
  # internal processes (reconcile-target, ensure, grant-watch, tenant-grant, legacy-dual-write): type process, not in mcp.include
triggers:
  - id: gh-push
    type: connection-event
    inputs: { connection: github, event: push }
    action: { type: start-orchestration, orchestration: on-repository-change, inputs: { kind: "push", ev: "= event" } }
  - { id: gh-delete,     type: connection-event, inputs: { connection: github, event: delete },                   action: { type: start-orchestration, orchestration: on-repository-change, inputs: { kind: "delete", ev: "= event" } } }
  - { id: gh-pr-open,    type: connection-event, inputs: { connection: github, event: pull_request.opened },      action: { type: start-orchestration, orchestration: on-repository-change, inputs: { kind: "pr", ev: "= event" } } }
  - { id: gh-pr-sync,    type: connection-event, inputs: { connection: github, event: pull_request.synchronize }, action: { type: start-orchestration, orchestration: on-repository-change, inputs: { kind: "pr", ev: "= event" } } }
  - { id: gh-pr-reopen,  type: connection-event, inputs: { connection: github, event: pull_request.reopened },    action: { type: start-orchestration, orchestration: on-repository-change, inputs: { kind: "pr", ev: "= event" } } }
  # G2c: the operator fires this once with trigger run-now (act=application) to import legacy enrollments (reconcile step 1)
  - { id: nightly-reconcile, type: schedule, inputs: { cron: "0 3 * * *", timezone: Etc/UTC }, action: { type: start-orchestration, orchestration: reconcile } }
  - id: prompt
    type: prompt
    listed: true
    action:
      type: start-orchestration
      orchestration: configure-watched-repos
      # senderId is passed through as in v1 and is never used for service identity (that is the immutable RunCaller, PS-7)
      inputs: { prompt: "= event.text", conversationId: "= event.conversationId", senderId: "= event.senderId",
                isFirstTurn: "= event.isFirstTurn", channelType: "= event.channelType", conversationState: "= event.conversationState" }
chat:
  entryOrchestration: configure-watched-repos
  channels: [slack, web, api, cli]
  conversationDefaultsTo: { slack: thread-ts, web: explicit, api: explicit, cli: explicit }
permissions: []
testDirectory: tests
```

- **Passing `ev: "= event"`** (the whole metadata object) is an assumption. If PS requires flat inputs, map each key used below (`= event.owner`, …); v1 maps keys one by one (PS:`samples/applications/sextant/psapp.yaml`).
- **The enrollment import has no separate entrypoint.** It must run as `act=application`, because `PUT /control/grants/tenant` rejects a user. It is step 1 of `reconcile` (v2.0.x).
  - At G2c the operator fires `nightly-reconcile` once with the generic trigger run-now, `POST /v1/{tenant}/triggers/run/{triggerId}` (PS:`TriggersController.cs:399-416`, `triggers:write`).
  - Run-now re-enqueues the exact recurring-job definition (`HangfireSchedulerService.RunNowCore`), so it runs as `act=application`.
  - After that the nightly run keeps it in sync.

## Caller identity per flow (PS-7, `RunCaller`)

| Flow | Started by | `act` / `idp` | Consequence at the service |
|---|---|---|---|
| Connection tools (`find_*`, …) | Agent via `/mcp/sextant` | user / `processstack` (key's user) | Visibility = the user's grants ∪ tenant grants |
| `start-indexing`, `get-indexing-status`, `import-legacy-watches` | Agent/user via `/mcp/sextant` | user / `processstack` | Ensure/status need visibility (403 `not_granted` / 404) |
| `configure-watched-repos` on web/api/cli | Chat prompt, internal channel | user / `processstack` | `/control/grants/self` |
| `configure-watched-repos` on Slack | Chat prompt, external peer | user / `slack`; `sub = slack:{connectionInstanceId}:{peerId}` | **Refused in v2.0.** The app gates it (see the flow), and the service would return 403 `caller_not_allowed` anyway, because `CALLER_IDPS` is `processstack` only (decided at G1, see `security.md`). The app replies "Watch management isn't enabled for this channel; use the web chat or CLI." If `slack` is ever allowed, drop the gate |
| `on-repository-change` (GitHub repository events are non-conversational) | Connection event | application, even when `senderLogin` equals a platform user id | `/control/grants/tenant`; ensure is unrestricted |
| `reconcile` (schedule or run-now) | Schedule | application | `?scope=tenant`, `/control/grants/tenant` |

## Flows

`HttpRequest` below always means `connectionId: sextant-control`, `failOnErrorStatus: false`, with an explicit `statusCode` decision. Errors are logged with `Log` and never echo bodies to chat.

### `ensure` (internal process)

| Step | Activity | Detail |
|---|---|---|
| 1 | `SetVariable` | `exp = before == "0000000000000000000000000000000000000000" ? "" : before` |
| 2 | `HttpRequest` | `POST /control/ensure?wait=false`, body `{repository_remote_url, commit_sha, branch_name?, is_default_branch?, expected_head_commit?, forced?, branch_update?}` |
| 3 | decision | 200/202 → outputs `{job_id, identity_hash, status, snapshot_id, attached, branch_advanced}`. 400 `rejected` → `Log` + outcome `rejected`. 503 → outcome `unavailable`, and the nightly reconcile heals it |

### `on-repository-change` (`act=application`)

| `kind` / condition | Steps |
|---|---|
| push, `refType == tag` | No-op (`Log`) |
| push, `deleted == true` | `HttpRequest POST /control/branches/retire {repository: cloneUrl, branch, expected_head_commit: before}` (409 `head_mismatch`/`default_branch` → `Log`). Then `legacy-dual-write` removes the enrolled row, **only for the default branch**. v1 enrolls only on branch-head advance |
| push, otherwise | 1. Process `tenant-grant` with `{repository: cloneUrl}` (`PUT /control/grants/tenant`, the default-branch enrollment, idempotent; there is no per-feature-branch tenant grant). 2. `ensure` with `{cloneUrl, after, branch, is_default_branch: branch == defaultBranch, expected_head_commit: before, forced}`. 3. If `branch == defaultBranch`: `legacy-dual-write` upserts `enrolled/{connectionId}/{slug}` |
| delete (`refType == branch`) | `HttpRequest POST /control/branches/retire {repository: cloneUrl, branch}` (no CAS; a newer re-create push is ordered by its own CAS) |
| pr (opened/synchronize/reopened) | `ensure` base `{cloneUrl, baseSha, baseRef, branch_update: none}`; `ensure` head `{headCloneUrl, headSha, headRef, branch_update: none}`. For a fork head, the SVC-5 host policy still applies. **No branch pointer moves** (PS:`GitHubRepositoryEventMapper.cs:202,216` parity) |

**URL spelling rule.** Always send `event.cloneUrl` / `event.headCloneUrl` verbatim. They are GitHub's `clone_url`, the same spelling the v1 publisher sent (PS:`src/ProcessStack.Connections.GitHub/RepositoryEvents/GitHubRepositoryEventMapper.cs:254,277`). The identity hashes the raw URL (`src/Sextant.Service/ServiceContracts.cs:86-97`), so a different spelling re-indexes.

### `reconcile` (schedule or run-now, `act=application`)

| Step | Activity | Detail |
|---|---|---|
| 1 | (v2.0.x only) legacy enrollment import | `ListAppValues {prefix: "enrolled/"}` → parse the PascalCase JSON → `for-each` process `tenant-grant` (`HttpRequest PUT /control/grants/tenant {repository: CloneUrl}`; non-2xx → `Log`). Idempotent. The G2c run-now does the real import; later runs are no-ops, because after activation only v2 writes enrolled rows and it grants them itself. Removed in v2.1 |
| 2 | `HttpRequest GET /control/grants?scope=tenant` | `targets[]`: distinct (repository, branch), with no user ids |
| 3 | `for-each` over targets, `parallel: true`, `maxConcurrency: 4`, process `reconcile-target` | |
| 3a | `GetRepository` (github) | 404/403 → `Log` "not reachable by the tenant connection", skip. `branch == ""` → `defaultBranch` |
| 3b | `ListBranches` (github) | Branch absent → skip. Retire is left to delete events, so a missed delete stays harmless |
| 3c | `HttpRequest GET /control/resolve?repository=&branch=` | 200 with `commit_sha == head` → up to date. 200 otherwise → `exp = commit_sha`. 404 → `exp = ""` |
| 3d | `ensure` | `{repository, head, branch, is_default_branch, expected_head_commit: exp}` |
| 4 | `Log` | Counts: up-to-date / re-ensured / skipped / failed |

- **The head comparison relies on SVC-6+7** (`commit_sha` in resolve).
- **URLs.** `targets[].repository` is the grant's stored `remote_url`: the spelling first submitted, i.e. GitHub `clone_url` (see SVC-4). The owner/repo for `GetRepository`/`ListBranches` are parsed from it with `SetVariable` string ops. The ensure sends it verbatim, so it hits the same identity as push-time ensures.

### `configure-watched-repos` (chat, `act=user`; internal channels only in v2.0)

- **Slack gate.** When `channelType == slack`, every command replies with the "not enabled for this channel" text and makes no service call. The service would reject it anyway (`idp=slack` is not in the default `CALLER_IDPS`). The lazy import is skipped too.
- **Identity.** The service sees only the immutable `RunCaller` (PS-7), never the `senderId` input.

| Command node pattern (same set as v1) | Steps |
|---|---|
| `watch {repo} on branch {branch}` · `watch {repo} on {branch}` · `watch {repo}` · track/start-watching variants | Process `grant-watch`: 1. `GetRepository` (github): refuse "not visible to this workspace's GitHub connection" (the #111 mitigation, see `security.md`). 2. `HttpRequest PUT /control/grants/self {repository: repo.cloneUrl, branch?}` (400 → the reason; 409 → the limit). 3. If the target has no snapshot (`status.snapshot_status == missing`), `ListBranches` → head, then `ensure` with CAS. 4. `legacy-dual-write` upserts memory `sextant.watched-repos:{slug}` |
| `stop watching {repo} [on {branch}]` · unwatch · untrack | `HttpRequest DELETE /control/grants/self?repository=&branch=`; `legacy-dual-write` writes the tombstone |
| `what am i watching*` · `list watched*` · `show my watched*` | `HttpRequest GET /control/grants/self` → formatted list (branch, snapshot status, short sha) |
| anything else | Help text |
| first turn (`isFirstTurn` or no `sextant.app:legacy-import-v1` flag), internal channel only | Run `import-legacy-watches` once, then `SetMyMemory {scope: sextant.app, key: legacy-import-v1, value: "<iso>"}` |

### Processes exposed over MCP (`act=user`)

| Process | Inputs → outputs | Steps |
|---|---|---|
| `start-indexing` | `repositoryRemoteUrl, commitSha, branchName?, treeSha?, configHash?` → `jobId, identityHash, indexingState, snapshotId, attached, branchAdvanced` | If `branchName` is set: `ListBranches` → head. `commitSha == head` → `ensure` with `expected_head_commit = resolve.commit_sha` (CAS advance). Else `branch_update: none` (historical commit, no regress). 403 `not_granted` → "watch the repository first" |
| `get-indexing-status` | `jobId` → `found, indexingState, terminal, snapshotId, lastError` | `HttpRequest GET /control/status/{jobId}`. 404 → `found=false`. Map `job.status`/`job.snapshot_id`/`job.last_error` |
| `import-legacy-watches` | — → `imported, skipped, failed` | `ListMyMemory {scope: sextant.watched-repos}` → skip null values (tombstones) → `for-each` `PUT /control/grants/self {repository: value.cloneUrl, branch: value.branch}`, with `source` recorded server-side as `self`. Idempotent |

## Legacy dual-write (v2.0.x; dropped in v2.1 post-G3)

| Store | Key | Value (exact shape) | Write | Remove |
|---|---|---|---|---|
| User memory (`SetMyMemory`) | scope `sextant.watched-repos`, key `{host}/{owner}/{repo}@{branch}` (PS `WatchedRepoScope.SlugFor`, PS:`src/ProcessStack.Activities.Sextant/WatchedRepos/WatchedRepoScope.cs:205-206`: host lowercased without userinfo/port, owner lowercased, repo lowercased without `.git`, branch trimmed) | camelCase `{"owner","repo","branch","cloneUrl","addedAt"}` | watch | **No `DeleteMyMemory` activity exists yet** (`IUserMemoryStore.DeleteAsync` exists; PS-13 adds the activities). v2.0 writes a JSON `null` tombstone, which v1 skips (PS:`src/ProcessStack.Activities.Sextant/WatchedRepos/WatchedRepoStore.cs:33-35`) |
| App State (`SetAppValue`, app `sextant`) | `enrolled/{connectionId}/{slug}` (PS:`src/ProcessStack.Activities.Sextant/Gateway/EnrolledRepoState.cs:70,81`) | **PascalCase** `{"ConnectionId","Host","Owner","Repo","Branch","CloneUrl","Slug","EnrolledAt"}` (`JsonSerializerDefaults.General`, `:23-31,72`) | Default-branch push | `DeleteAppValue` on a default-branch delete |

v2.1 cleanup, run through the app (depends on PS-13):
- `ListAppValues(enrolled/)` → `DeleteAppValue`;
- `DeleteAppValue` for `sextant-service-url` and `sextant-control-token`;
- `DeleteMyMemory` (PS-13) for each user's `sextant.watched-repos` keys, tombstones included, run lazily on that user's next chat or `import-legacy-watches` call, because `*MyMemory` is caller-scoped;
- drop reconcile step 1.

## Scenario tests (`tests/`)

These assume the PS-6 scenario stubs: `principal`, `seed.userMemory`, `seed.appState`, `mcp:` and `http:` stubs, and `expected.httpCalls`/`mcpCalls`, with caller claims visible to `expected`.

| # | Scenario | Asserts |
|---|---|---|
| 1 | watch-default-branch | `GetRepository` ok → `PUT grants/self` without branch; memory upsert (camelCase) |
| 2 | watch-on-branch | `PUT` with `branch`; memory key `…@feature/x` |
| 3 | watch-inaccessible | `GetRepository` 404 → no `PUT`; refusal text |
| 4 | watch-rejected | `PUT` 400 `rejected` → the reason is shown; no memory write |
| 5 | watch-triggers-first-ensure | status `missing` → `ListBranches` + ensure CAS `""` |
| 6 | unwatch | `DELETE` + `null` tombstone |
| 7 | unwatch-absent | `DELETE` → `deleted: 0` → "not watching" |
| 8 | list-empty / 9 list-two | `GET grants/self` rendering |
| 10 | help-fallback | No HTTP calls |
| 11 | lazy-legacy-import | Seeded memory (2 entries + 1 tombstone) → 2 `PUT`s + flag set; the second turn does not re-import |
| 12 | import-legacy-watches-process | Output counts |
| 13 | push-default-branch | `PUT grants/tenant`, ensure (CAS `before`, `is_default_branch: true`), enrolled PascalCase upsert |
| 14 | push-feature-branch | Ensure with `is_default_branch: false`; no enrolled write |
| 15 | push-branch-create | `before` = zeros → `expected_head_commit: ""` |
| 16 | push-deleted | Retire with CAS `before`; no ensure |
| 17 | push-tag | No calls |
| 18 | delete-event-branch | Retire without CAS |
| 19 | pr-opened | Two ensures, both `branch_update: none`, head uses `headCloneUrl` |
| 20 | ensure-503 | Outcome `unavailable`, orchestration completes |
| 21 | reconcile-noop | No grants → no GitHub calls |
| 22 | reconcile-up-to-date | `resolve.commit_sha == head` → no ensure |
| 23 | reconcile-behind | Ensure with `expected_head_commit = resolved` |
| 24 | reconcile-missing | resolve 404 → CAS `""` |
| 25 | reconcile-branch-gone | No ensure |
| 26 | reconcile-legacy-sync | `reconcile` with seeded `enrolled/…` rows → step 1 issues `PUT grants/tenant` per row before the grants listing; a second run with the same seed is idempotent |
| 27 | start-indexing-advance / 28 start-indexing-historical | CAS vs `branch_update: none` |
| 29 | get-indexing-status found / 30 not-found | Output mapping |
| 31 | caller-claims | Scenario principal → `act=user`, `idp=processstack` on chat and MCP flows; connection-event and schedule triggers → `act=application` with no `idp`/`sub` (if claims are exposed to `expected`) |
| 32 | slack-watch-refused | `channelType: slack` → "not enabled for this channel" text; no HTTP calls; no lazy import |
| 33 | push-sender-is-user-id | A push whose `senderLogin` equals a seeded platform user id still runs as `act=application` |

## CI: `.github/workflows/processstack-app.yml`

| Item | Value |
|---|---|
| Triggers | `pull_request` + `push` to `main`, path filter `apps/processstack/**` and the workflow file |
| Steps | checkout → `actions/setup-dotnet` → `dotnet nuget add source https://nuget.pkg.github.com/elevenworks/index.json -n elevenworks -u x -p ${{ secrets.PROCESSSTACK_PACKAGES_TOKEN }} --store-password-in-clear-text` → `dotnet tool install -g ProcessStack.Cli --version <pinned>` → `processstack app validate apps/processstack/sextant` → `processstack app test apps/processstack/sextant` |
| Secret | **`PROCESSSTACK_PACKAGES_TOKEN`: a classic PAT with `read:packages` on elevenworks. The human must add it** to `jonlipsky/sextant`. The package is private and org-owned, so a repo outside the elevenworks org cannot be granted Actions access to it; that is why the public-repo option needs this PAT. If it is absent, the job emits `::notice::` and skips, so it does not fail fork PRs. Under option B (private elevenworks repo; see "Open: app repo location") it is replaced by `GITHUB_TOKEN` with `packages: read` plus "Manage Actions access" on the package |
| Prerequisite | **First `cli-v*` release to the private elevenworks GitHub Packages feed (approved; cut by the orchestrator after PS-6 merges).** `ProcessStack.Cli` has not been published yet: the PS "Publish CLI Tool" workflow has 0 runs, and there are no `cli-v*` tags. It is never published to nuget.org. The release must contain PS-8 (F3), the PS-6 scenario stubs and PS-5 trigger validation before this job can install `--version <pinned>` |
| Not in CI | `publish` / `activate`, which stay manual per the runbook |
| Separation | Independent of the .NET `Build & Test` job (30-min timeout, `CLAUDE.md`), so it adds no Sextant build dependency |

## README (deploy steps; details in [`cutover-runbook.md`](cutover-runbook.md))

1. **Service env (G2b):**
   - `SEXTANT_SERVICE_DELEGATE_TOKENS`;
   - `SEXTANT_SERVICE_CALLER_KEYS=<kid>=<key>@<tenantId>`;
   - `SEXTANT_SERVICE_CALLER_AUDIENCE=sextant`;
   - `SEXTANT_SERVICE_CALLER_APPS=sextant`;
   - `SEXTANT_SERVICE_CALLER_IDPS=processstack` (the default; set explicitly);
   - `SEXTANT_SERVICE_REPOSITORY_HOSTS=github.com`.

   Check that `/control/metrics` reports schema 24.
2. **Register connections:**
   - `sextant-query` (mcp http, delegate bearer, callerIdentity);
   - `sextant-control` (http-api, control bearer, the same callerIdentity **and the same explicit `keyId`**);
   - `github` with webhook events `push`, `delete` and `pull_request`.
3. **Publish:** `processstack app validate apps/processstack/sextant && processstack app test apps/processstack/sextant && processstack app publish apps/processstack/sextant`.
4. **Activate:** `processstack app activate sextant --version 2.0.0` with bindings `github`, `sextant-query` and `sextant-control`.
5. **Import:**
   - each user runs `import-legacy-watches` (or it happens lazily on first internal-channel chat);
   - enrollments: fire `nightly-reconcile` once with trigger run-now, `POST /v1/{tenant}/triggers/run/{triggerId}` (needs `triggers:write`). It runs as `act=application`, and its step 1 imports the `enrolled/*` rows.
6. **Agents:** `processstack api-key create --name sextant-agents --app sextant`, then point clients at `POST /v1/{tenant}/mcp/sextant`.
7. **Rollback:** `processstack app activate sextant --version 1.0.1`. The dual-write keeps v1's stores current.
