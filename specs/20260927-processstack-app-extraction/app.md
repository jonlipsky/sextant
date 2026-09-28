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
  orchestrations/  on-repository-change.yaml  reconcile.yaml  reconcile-target.yaml
                   configure-watched-repos.yaml  grant-watch.yaml  revoke-watch.yaml
  processes/       start-indexing.yaml  get-indexing-status.yaml  import-legacy-watches.yaml
                   ensure.yaml  tenant-grant.yaml  legacy-dual-write.yaml
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
  - { name: configure-watched-repos, type: orchestration, path: orchestrations/configure-watched-repos.yaml, isDefault: true }
  - { name: start-indexing,          type: process, path: processes/start-indexing.yaml }
  - { name: get-indexing-status,     type: process, path: processes/get-indexing-status.yaml }
  - { name: import-legacy-watches,   type: process, path: processes/import-legacy-watches.yaml }
  - { name: on-repository-change,    type: orchestration, path: orchestrations/on-repository-change.yaml, internal: true }
  - { name: reconcile,               type: orchestration, path: orchestrations/reconcile.yaml, internal: true }
  - { name: reconcile-target,        type: orchestration, path: orchestrations/reconcile-target.yaml, internal: true }
  # internal (not in mcp.include): grant-watch, revoke-watch (orchestrations); ensure, tenant-grant, legacy-dual-write (processes)
triggers:
  # the flow reads the normalized fields from `event.*` itself, so no inputs are mapped
  - { id: gh-push,       type: connection-event, inputs: { connection: github, event: push },                     action: { type: start-orchestration, orchestration: on-repository-change } }
  - { id: gh-delete,     type: connection-event, inputs: { connection: github, event: delete },                   action: { type: start-orchestration, orchestration: on-repository-change } }
  - { id: gh-pr-open,    type: connection-event, inputs: { connection: github, event: pull_request.opened },      action: { type: start-orchestration, orchestration: on-repository-change } }
  - { id: gh-pr-sync,    type: connection-event, inputs: { connection: github, event: pull_request.synchronize }, action: { type: start-orchestration, orchestration: on-repository-change } }
  - { id: gh-pr-reopen,  type: connection-event, inputs: { connection: github, event: pull_request.reopened },    action: { type: start-orchestration, orchestration: on-repository-change } }
  # G2c: the operator fires this once with trigger run-now (act=application) to import legacy enrollments (reconcile's legacy import step)
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

- **No inputs are mapped for repository events** (SX-10). `app test` and the runtime hand a connection-event run the whole normalized metadata as `event`, and `on-repository-change` copies the fields it plans with from `event.*` itself (as strings; see the SX-10 notes). The `kind` input is not needed: the planner reads `event.event`/`event.action`.
- **The enrollment import has no separate entrypoint.** It must run as `act=application`, because `PUT /control/grants/tenant` rejects a user. It is step 2 of `reconcile` (v2.0.x), after the grants listing that proves the caller.
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
| 2 | `HttpRequest` | `POST /control/ensure?wait=false`, body `{repository_remote_url, commit_sha, branch_name?, default_branch?, expected_head_commit?, forced?, branch_update?}` |
| 3 | decision | 200/202 → outputs `{job_id, identity_hash, status, snapshot_id, attached, branch_advanced}`. 400 `rejected` → `Log` + outcome `rejected`. 503 → outcome `unavailable`, and the nightly reconcile heals it |

As built (SX-9), `ensure` sends neither `default_branch` nor `forced`: SX-10's push flows send their own bodies. It validates every input, and it confirms an advance against GitHub's head itself before sending; anything unconfirmed is sent as `branch_update: none`. See "`ensure` does not trust its caller" under the implementation notes.

### `on-repository-change` (`act=application`)

**Every actionable event runs `tenant-grant` first** with `{repository: cloneUrl}` (`PUT /control/grants/tenant`, the default-branch enrollment, idempotent; there is no per-feature-branch tenant grant). Only an answer of `granted` or `limit`, which only an application caller gets, lets the steps below run. Any other answer (a user's direct run gets 403) sends nothing else: no ensure, no GitHub listing, no retire.

| Event / condition (as built, SX-10) | Steps (after the grant) |
|---|---|
| push, `refType == tag`; `create`; closed or edited pull request; malformed event | No-op (`Log`; the planner's `reason`) |
| push, `deleted == true` | `HttpRequest POST /control/branches/retire {repository: cloneUrl, branch, expected_head_commit: before}` (409 `head_mismatch`/`default_branch` → `Log`). Then, **only for the default branch and only after the service accepted the retire**, `DeleteAppValue` removes the enrolled row. v1 enrolls only on branch-head advance |
| push, otherwise | 1. `POST /control/ensure?wait=false` with the planner's body `{repository_remote_url: cloneUrl, commit_sha: after, branch_name, default_branch: branch == defaultBranch, expected_head_commit: before, forced, branch_update: advance}`. 2. If `branch == defaultBranch` and the grant was `granted`: `SetAppValue` upserts `enrolled/{connectionId}/{slug}` |
| delete (`refType == branch`) | `GitHubListBranches` first: only when GitHub confirms the branch is still gone, `HttpRequest POST /control/branches/retire {repository: cloneUrl, branch}` (no CAS). Branch present → `branch_exists`; listing failed → `github_unavailable`; not a github.com URL → `not_verifiable`; all skip the retire |
| pr (opened/synchronize/reopened) | `POST /control/ensure?wait=false` with the planner's base body `{cloneUrl, baseSha, baseRef, branch_update: none}`, then its head body `{cloneUrl, headSha, headRef, branch_update: none}`. The head is sent only when `headCloneUrl` names the base repository (the same SVC-5 canonical key), under the base's spelling. A fork's head is skipped with `fork_head`, as is a deleted head repository (`head_repository_deleted`), and a side repeating the base's repository and commit is sent once. **No branch pointer moves** (PS:`GitHubRepositoryEventMapper.cs:202,216` parity) |

**URL spelling rule.** Always send `event.cloneUrl` verbatim (a pull request's head goes under that spelling too). It is GitHub's `clone_url`, the same spelling the v1 publisher sent (PS:`src/ProcessStack.Connections.GitHub/RepositoryEvents/GitHubRepositoryEventMapper.cs:254,277`). The identity hashes the raw URL (`src/Sextant.Service/ServiceContracts.cs:86-97`), so a different spelling re-indexes.

### `reconcile` (schedule or run-now, `act=application`)

| Step | Activity | Detail |
|---|---|---|
| 1 | `HttpRequest GET /control/grants?scope=tenant` | `targets[]`: distinct (repository, branch), with no user ids. Application only: 401/403 → `status: refused`, anything else → `unavailable`; either ends the run having sent nothing else |
| 2 | (v2.0.x only) legacy enrollment import | `ListAppValues {prefix: "enrolled/"}` → parse the PascalCase JSON → distinct `CloneUrl`s (at most 500) → sequential `for-each` process `tenant-grant` (`PUT /control/grants/tenant {repository: CloneUrl}`; non-2xx → `Log`). Idempotent. When a grant was created, step 1's listing is repeated so it is reconciled in this run (a failed repeat keeps the first listing). The G2c run-now does the real import; later runs create nothing, because after activation only v2 writes enrolled rows and it grants them itself. Removed in v2.1 |
| 3 | `for-each` over the targets (sorted, de-duplicated, at most 500), `parallel: true`, `maxConcurrency: 4`, `continueOnError`, orchestration `reconcile-target` | |
| 3a | `GetRepository` (github) | Failure → `Log` "not reachable by the tenant connection", skip (`not_reachable`). `branch == ""` → `defaultBranch` |
| 3b | `HttpRequest GET /control/resolve?repository=&branch=` | Before the listing, so the CAS covers every later push (`app-activities.md`). Network failure → skip (`resolve_failed`) |
| 3c | `ListBranches` (github) | Failure → skip (`not_reachable`) |
| 3d | `SextantPlanReconcile` | 200 with `commit_sha == head` → up to date (unless it is GitHub's default and `is_default` is false: promote, #199). 200 otherwise → `exp = commit_sha`. 404 → `exp = ""`. Branch gone from GitHub → retire under the CAS `commit_sha` (never the default) |
| 3e | `POST /control/ensure` or `/control/branches/retire` | The planned body verbatim: `{repository, head, branch, default_branch, expected_head_commit: exp, branch_update: advance}` |
| 4 | `Log` | Counts: up to date / ensured / retired / skipped / failed (a child that returned no result counts as failed) |

- **The head comparison relies on SVC-6+7** (`commit_sha` in resolve).
- **URLs.** `targets[].repository` is the grant's stored `remote_url`: the spelling first submitted, i.e. GitHub `clone_url` (see SVC-4). `reconcile-target` checks it and parses the owner/repo for `GetRepository`/`ListBranches` with `SextantNormalizeRepository`; a target outside github.com is skipped (`not_github`). The ensure sends the checked URL, which keeps an https spelling as submitted, so it hits the same identity as push-time ensures.

### `configure-watched-repos` (chat, `act=user`; internal channels only in v2.0)

- **Slack gate.** When `channelType == slack`, every command replies with the "not enabled for this channel" text and makes no service call. The service would reject it anyway (`idp=slack` is not in the default `CALLER_IDPS`). The lazy import is skipped too.
- **Identity.** The service sees only the immutable `RunCaller` (PS-7), never the `senderId` input.

| Command node pattern (same set as v1) | Steps |
|---|---|
| `watch {repo} on branch {branch}` · `watch {repo} on {branch}` · `watch {repo}` · track/start-watching variants | Process `grant-watch`: 1. `GetRepository` (github): refuse "not visible to this workspace's GitHub connection" (the #111 mitigation, see `security.md`). 2. `HttpRequest PUT /control/grants/self {repository: repo.cloneUrl, branch?}` (400 → the reason; 409 → the limit). 3. If the target has no snapshot (`status.snapshot_status == missing`), `ListBranches` → head, then `ensure` with CAS (`branch_name` = the watched branch, or `defaultBranch` for `watch {repo}`; never `default_branch: true`, SX-6d; on a repository with no default yet, the watched branch becomes the default only if the service confirms the remote's `HEAD` names it, #199). 4. `legacy-dual-write` upserts memory `sextant.watched-repos:{slug}` |
| `stop watching {repo} [on {branch}]` · unwatch · untrack | `HttpRequest DELETE /control/grants/self?repository=&branch=`; `legacy-dual-write` writes the tombstone |
| `what am i watching*` · `list watched*` · `show my watched*` | `HttpRequest GET /control/grants/self` → formatted list (branch, snapshot status, short sha) |
| anything else | Help text |
| every turn on an internal channel while the `sextant.app:legacy-import-v1` flag is unset | Run `import-legacy-watches` before the command. The process sets the flag itself (ISO time) once nothing is left to retry; its note, when there is one, is prepended to the reply. A failed import is a note, never a failed turn (SX-11 as built) |

### Processes exposed over MCP (`act=user`)

| Process | Inputs → outputs | Steps |
|---|---|---|
| `start-indexing` | `repositoryRemoteUrl, commitSha, branchName?, treeSha?, configHash?` → `jobId, identityHash, indexingState, snapshotId, attached, branchAdvanced` | The body is built from GitHub and the service, never from the arguments' spelling (#203): `GetRepository` → `repository_remote_url` from GitHub's `full_name` (github.com only). If `branchName` is set and there is no tree or config hash: `ListBranches` → head. `commitSha == head` → `ensure` with `commit_sha` = GitHub's head, `branch_name` and `expected_head_commit = resolve.commit_sha` (CAS advance). Else, or with no `branchName`, `branch_update: none` (historical commit, no regress). Never `default_branch: true` or a `branch_head_sequence`: a user ensure beyond these bounds is 400 (SX-6d, `service-changes.md`). 403 `not_granted` → "watch the repository first" |
| `get-indexing-status` | `jobId` → `found, indexingState, terminal, snapshotId, lastError, errorCode` | `HttpRequest GET /control/status/{jobId}`. 404 → `found=false`. Map `job.status`/`job.snapshot_id`; `job.last_error` → `lastError` (its first line, control characters replaced, at most 200 chars) and `errorCode` (the `^[a-z0-9_]{1,64}:` code it starts with, else `""`) |
| `import-legacy-watches` | `retryUnresolved?` → `imported, newlyImported, skipped, failed, remaining, complete, outcome, message` | `ListMyMemory {scope: sextant.watched-repos}` → skip JSON-null tombstones → `GET /control/grants/self` (an entry already held counts as imported) → `SextantPlanLegacyImport` → per entry (at most 200 per run): `GetRepository` (github) → `SextantNormalizeRepository` over GitHub's `full_name` → `PUT /control/grants/self {repository}` (a v1 watch on the default branch) or `{repository, branch}`, with `source` recorded server-side as `self`. The URL never comes from memory text. Idempotent. See "SX-11, as built" |

## Legacy dual-write (v2.0.x; dropped in v2.1 post-G3)

| Store | Key | Value (exact shape) | Write | Remove |
|---|---|---|---|---|
| User memory (`SetMyMemory`) | scope `sextant.watched-repos`, key `{host}/{owner}/{repo}@{branch}` (PS `WatchedRepoScope.SlugFor`, PS:`src/ProcessStack.Activities.Sextant/WatchedRepos/WatchedRepoScope.cs:205-206`: host lowercased without userinfo/port, owner lowercased, repo lowercased without `.git`, branch trimmed) | camelCase `{"owner","repo","branch","cloneUrl","addedAt"}` | watch | **No `DeleteMyMemory` activity exists yet** (`IUserMemoryStore.DeleteAsync` exists; PS-13 adds the activities). v2.0 writes a JSON `null` tombstone, which v1 skips (PS:`src/ProcessStack.Activities.Sextant/WatchedRepos/WatchedRepoStore.cs:33-35`) |
| App State (`SetAppValue`, app `sextant`) | `enrolled/{connectionId}/{slug}` (PS:`src/ProcessStack.Activities.Sextant/Gateway/EnrolledRepoState.cs:70,81`) | **PascalCase** `{"ConnectionId","Host","Owner","Repo","Branch","CloneUrl","Slug","EnrolledAt"}` (`JsonSerializerDefaults.General`, `:23-31,72`) | Default-branch push whose tenant grant answered `granted` (inline in `on-repository-change`) | `DeleteAppValue` after the service accepted a default-branch retire (inline) |

v2.1 cleanup, run through the app (depends on PS-13):
- `ListAppValues(enrolled/)` → `DeleteAppValue`;
- `DeleteAppValue` for `sextant-service-url` and `sextant-control-token`;
- `DeleteMyMemory` (PS-13) for each user's `sextant.watched-repos` keys, tombstones included, and the import's `sextant.app` keys (`legacy-import-v1`, `legacy-import-attempts`), run lazily on that user's next chat or `import-legacy-watches` call, because `*MyMemory` is caller-scoped;
- drop reconcile's legacy import step.

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
| 11 | lazy-legacy-import | Seeded memory (2 entries + 1 tombstone) → 2 `PUT`s, the reply starts with the import note; `lazy-import-flag-set-skips`: with the flag set, no import and no call |
| 12 | import-legacy-watches-process | Output counts, the flag and the attempt marks (`import-legacy-watches-*` cover the other paths) |
| 13 | push-default-branch | `PUT grants/tenant`, ensure (CAS `before`, `default_branch: true`), enrolled PascalCase upsert |
| 14 | push-feature-branch | Ensure with `default_branch: false`; no enrolled write |
| 15 | push-branch-create | `before` = zeros → `expected_head_commit: ""` |
| 16 | push-deleted | Retire with CAS `before`; no ensure |
| 17 | push-tag | No calls |
| 18 | delete-event-branch | Retire without CAS |
| 19 | pr-opened | Two ensures, both `branch_update: none`; the head (in the base repository) under the base's spelling. A fork's head is not sent (`fork_head`) |
| 20 | ensure-503 | Outcome `unavailable`, orchestration completes |
| 21 | reconcile-noop | No grants → no GitHub calls |
| 22 | reconcile-up-to-date | `resolve.commit_sha == head` → no ensure |
| 23 | reconcile-behind | Ensure with `expected_head_commit = resolved` |
| 24 | reconcile-missing | resolve 404 → CAS `""` |
| 25 | reconcile-branch-gone | No ensure |
| 26 | reconcile-legacy-sync | `reconcile` with seeded `enrolled/…` rows → after the grants listing, the import issues `PUT grants/tenant` per distinct repository, then lists the grants again because one was created; `reconcile-legacy-already-granted` pins the idempotent steady state (one listing) |
| 27 | start-indexing-advance / 28 start-indexing-historical | CAS vs `branch_update: none` |
| 29 | get-indexing-status found / 30 not-found | Output mapping |
| 31 | caller-claims | Scenario principal → `act=user`, `idp=processstack` on chat and MCP flows; connection-event and schedule triggers → `act=application` with no `idp`/`sub` (if claims are exposed to `expected`) |
| 32 | slack-watch-refused | `channelType: slack` → "not enabled for this channel" text; no HTTP calls; no lazy import |
| 33 | push-sender-is-user-id | A push whose `senderLogin` equals a seeded platform user id still runs as `act=application` |

## CI: `.github/workflows/processstack-app.yml`

| Item | Value |
|---|---|
| Triggers | `pull_request` + `push` to `main`, path filter `apps/processstack/**` and the workflow file |
| Steps | checkout → `actions/setup-dotnet` → `dotnet nuget add source https://nuget.pkg.github.com/elevenworks/index.json -n elevenworks -u x -p ${{ secrets.PROCESSSTACK_PACKAGES_TOKEN }} --store-password-in-clear-text` → `dotnet tool install -g ProcessStack.Cli --version <pinned>` → `processstack app validate -p apps/processstack/sextant` → `processstack app test -p apps/processstack/sextant --all` |
| Secret | **`PROCESSSTACK_PACKAGES_TOKEN`: a classic PAT with `read:packages` on elevenworks. The human must add it** to `jonlipsky/sextant`. The package is private and org-owned, so a repo outside the elevenworks org cannot be granted Actions access to it; that is why the public-repo option needs this PAT. If it is absent, the job emits `::notice::` and skips, so it does not fail fork PRs. Under option B (private elevenworks repo; see "Open: app repo location") it is replaced by `GITHUB_TOKEN` with `packages: read` plus "Manage Actions access" on the package |
| Prerequisite | **Met: `ProcessStack.Cli` 1.1.0 and 1.1.1 are on the private elevenworks GitHub Packages feed** (tags `cli-v1.1.0` and `cli-v1.1.1`). It is never published to nuget.org. CI pins 1.1.1: 1.1.0 carried PS-6, PS-7, PS-8 and PS-15, and 1.1.1 adds PS-19's GitHub stubs |
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
3. **Publish:** `processstack app validate -p apps/processstack/sextant && processstack app test -p apps/processstack/sextant --all && processstack app publish apps/processstack/sextant`.
4. **Deploy and activate:** bind `github`, `sextant-query` and `sextant-control` in the WebClient deploy dialog (the CLI sets no bindings), then `processstack app activate sextant` (it has no `--version`: it activates the published version).
5. **Import:**
   - each user runs `import-legacy-watches` (or it happens lazily on first internal-channel chat);
   - enrollments: fire `nightly-reconcile` once with trigger run-now, `POST /v1/{tenant}/triggers/run/{triggerId}` (needs `triggers:write`). It runs as `act=application`, and its legacy import step imports the `enrolled/*` rows.
6. **Agents:** `processstack api-key create --name sextant-agents --app sextant`, then point clients at `POST /v1/{tenant}/mcp/sextant`.
7. **Rollback:** `processstack app rollback sextant 1.0.1` (publishes 1.0.1's content as a new, auto-bumped version), then `processstack app activate sextant`. The dual-write keeps v1's stores current.

## Implementation notes (SX-9, as built)

SX-9 builds the manifest, the chat, the MCP processes, `ensure` and the v1 memory dual-write. SX-10 adds the GitHub triggers and `reconcile`; SX-11 adds `mcp.connectionTools`, `import-legacy-watches` and the lazy import. Where the code differs from the sections above, the code wins.

**Manifest**
- **The `prompt` trigger maps only `prompt`, `isFirstTurn`, `channelType` and `conversationState`.** `conversationId` and `senderId` are server-hydrated reserved names. The app never maps, defaults or sets them, and nothing reads `senderId`: the service sees only the run's caller (PS-7).
- **`mcp.include` is `[start-indexing, get-indexing-status, import-legacy-watches]`**, and `mcp.connectionTools` re-exposes the service's query tools (SX-11, below).

**Flows**
- **`grant-watch` and `revoke-watch` are orchestrations**, not processes. A GitHub activity throws on a 404, and `HttpRequest` throws on a network failure. Only an orchestration can route that with `on-error` to a reply instead of failing the chat turn. Each returns one reply line in `message`; `configure-watched-repos` runs one per repository with a sequential `for-each` (`continueOnError`, so a failed child becomes a "Something went wrong" line).
- **The command parse is the `SextantParseWatchCommand` activity**, not a command node. Its `errors` (an invalid token, too many repositories) are appended to the reply. A rejected branch (`branchInvalid`) refuses the whole command with "Nothing was changed", because the `""` it leaves would mean the default branch (watch) or every branch (unwatch).
- **Only github.com repositories are watchable.** The workspace's GitHub connection answers for github.com only, so another host is refused before any call.
- **A `GitHubGetRepository` failure is "not visible" unless it is known to be transient.** `grant-watch` routes on `_lastErrorType` with an allowlist. A 5xx (`ApiException`), a rate or abuse limit (`RateLimitExceededException`, `SecondaryRateLimitExceededException`, `AbuseException`), a network failure (`HttpRequestException`) or a timeout (`TaskCanceledException`, `OperationCanceledException`, `TimeoutException`) says nothing about visibility, so the reply is "try again later". Everything else fails closed as the visibility refusal: a 404 or 403, a refused credential (401), and a GitHub App with no installation for the owner, which throws `InvalidOperationException` before any request (the scenario harness cannot raise that one, so `watch-github-credentials-refused` pins the route with a 401). A `GitHubListBranches` failure before the first ensure leaves the watch in place, and the reply says the nightly reconcile starts indexing (SX-10: a push cannot, because its CAS is its own `before`, which a branch with no pointer never matches).
- **`GitHubGetRepository` returns no clone URL.** `grant-watch` normalizes GitHub's `fullName` with `SextantNormalizeRepository`, which gives `https://github.com/{owner}/{repo}.git`: the `clone_url` spelling push events carry, so the first ensure and later push-time ensures hash to the same identity.
- **The first ensure needs the watched branch in the grant listing.** `grant-watch` reads `GET /control/grants/self` after the `PUT` and ensures only when that grant's `status.snapshot_status` is `missing`. The ensure advances the branch with `expected_head_commit: ""`.
- **Unwatch without a branch sends `branch=*`** (every branch of the repository).
- **Unwatch with a branch also ends a default-branch watch that resolves to it.** A default-branch watch is held with branch `""`, so `DELETE ?branch=main` does not match it. `revoke-watch` then reads `GET /control/grants/self` and, when a `branch: ""` grant for the repository has `status.resolved_branch` equal to the named branch, sends `DELETE` without a branch.
- **The v1 tombstones follow what the service is known to hold.** The matching v1 entries are tombstoned once the service holds no watch on what was named: `branch=*` answered 200, or, for a named branch, the listing shows no default-branch grant resolving to it (or that grant was deleted too). A v1-only entry, one v1 wrote that was never imported, is therefore tombstoned as well, so neither a rollback to 1.0.1 nor SX-11's import brings the watch back. The entries are kept when that is not known: the listing failed, or a default-branch grant has no `resolved_branch` yet (the repository has no default in the catalog, #199) and GitHub cannot say which branch it is. For such a grant `revoke-watch` reads `default_branch` with `GitHubGetRepository`, because the application ensure that sets the catalog default takes GitHub's: the named branch ends the default-branch watch too, and another branch leaves it alone and tombstones the named entries. When that read fails, the reply says the default-branch watch is still in place and that "stop watching {repo}" ends every branch. "Stopped watching" needs a removed grant or a tombstoned entry.
- **Refusals are outputs, not failures.** `ensure`, `start-indexing` and `get-indexing-status` complete with an `outcome` (`SextantInterpretEnsureResult`'s set, or `not_found`/`rejected`) and a code-shaped `reason` (`^[a-z0-9_]{1,64}$`, else `http_<status>`). A free-text reason from the service is never passed on. The chat shows a service reason only in that shape.
- **`ensure` builds the body itself and has no input for `default_branch`, `forced` or `branch_head_sequence`.** Every body carries `branch_update`:
  - `advance` with `expected_head_commit`, when the caller asks to advance, names a branch, and `ensure` confirms the advance rule below (all zeros becomes `""`);
  - otherwise `none`.

  A user or MCP caller can therefore never set the default flag. SX-10's push flows, which run as `act=application` and need `default_branch`/`forced`, send `SextantPlanRepositoryChange`'s bodies through their own `HttpRequest`, not through this process.
- **`ensure` does not trust its caller.** `internal: true` is not enforced on a direct `/v1/applications/{id}/run` (elevenworks/ProcessStack#3287), so anyone who can run the app can call `ensure` with arguments of their own. Before anything is sent, it refuses (`rejected`, `statusCode` 0, no request) with the first problem it finds:
  - `invalid_repository`: a URL `SextantNormalizeRepository` does not accept (the service's shape rules);
  - `invalid_branch`: a branch the chat parser refuses (`git check-ref-format --branch`), or one that still starts with `refs/heads/`. Every caller drops that prefix first, so it is refused rather than stripped a second time: `refs/heads/x` must not be sent as `x`;
  - `invalid_commit`/`invalid_tree_sha`: not 40 or 64 lower-case hex;
  - `invalid_config_hash`: not `^[0-9a-f]{1,128}$`;
  - `invalid_expected_head`: on an advance, neither `""` nor 40 or 64 lower-case hex (all zeros is still sent as `""`).

  It also enforces the advance rule itself instead of trusting the caller's check. It sends `advance` only when all of these hold: the repository is on github.com; there is no tree or config hash; and `GitHubListBranches` shows the commit is the branch's current head. Anything else is sent as `branch_update: none`, which indexes the commit and moves nothing. A downgrade is not a refusal, the same as `start-indexing`'s rule, so a head that moves between the caller's read and `ensure`'s read gives `none`, not an error. A `ListBranches` failure fails the process: `grant-watch` routes that to its reply, and `start-indexing` already fails its MCP call on a GitHub error. On an advance, the caller and `ensure` therefore both read the heads.

  The body carries the checked URL and branch, never the inputs' own spelling. Pins, each checked by mutation:
  - `ensure-refuses-malformed-cas`, `ensure-refuses-non-hex-commit`, `ensure-refuses-invalid-branch` and `ensure-refuses-heads-prefixed-branch` pin the refusals;
  - `ensure-advance-not-head-sends-none`, `ensure-advance-with-config-sends-none` and `ensure-advance-other-host-sends-none` pin the downgrades.

  **Residual:** the check applies the URL's shape rules, not a host allowlist. A direct run can still ask for any shape-valid https host, though never to advance there. The service's SVC-5 intake policy (`REPOSITORY_HOSTS`, default `github.com`) and the caller's grants (403 `not_granted`) stay the authority, as they are for any user ensure. A direct run can still index any commit of a granted repository without moving a branch, which is what `start-indexing` allows too.
- **Every user ensure stays inside SX-6d's bounds** (`EnsureSnapshotRequest.UserCallerBranchProblem`, #198):
  - an advance always carries `branch_name` plus `expected_head_commit`;
  - everything else is `branch_update: none`, which the service accepts with or without a `branch_name` (only `start-indexing` without a branch sends none);
  - neither `default_branch` nor `branch_head_sequence` is ever sent.

  A 400 from those bounds is the outcome `rejected` with its code (`ensure-user-bounds-refused`).
- **#199: a user's first ensure does not pick the default branch.** On a repository with no default yet, `grant-watch`'s first ensure makes the watched branch the default only when the service confirms that the remote's `HEAD` names it (clone mode). Otherwise the branch is created non-default. The default-branch grant then has no `resolved_branch`, and it reports `snapshot_status: missing` until an application ensure (SX-10's push or reconcile) sets the default. Another watch sends one more first ensure, which only attaches, because its CAS `""` no longer matches.
- **`start-indexing` builds the body from GitHub and the service (#203).** No argument reaches the body in its own spelling:
  - The repository must be on github.com (else `rejected`/`unsupported_host`, before any call) and visible to the workspace's GitHub connection. `repository_remote_url` is `SextantNormalizeRepository` over GitHub's `full_name`, the spelling watches and push events use, so a mixed-case or `.git`-less argument hashes to the same identity.
  - `commitSha`, `treeSha` and `configHash` must be hex (`invalid_commit`/`invalid_tree_sha`/`invalid_config_hash`) and are sent lower-cased. `branchName` goes through the chat parser's rule (`SextantNormalizeRepository`'s `branch` input: a leading `refs/heads/` is dropped, then `git check-ref-format --branch`); an invalid one is `invalid_branch`, before any call (`start-indexing-invalid-branch-refused`).
  - With a branch and no tree or config hash, it reads `GET /control/resolve` first (200 → CAS `commit_sha`, lower-cased, or `""` when it is not a SHA; 404 → CAS `""`; 400 refuses as `rejected`; anything else refuses), then the head with `GitHubListBranches`: the CAS only rejects pushes the service processes after the resolve (the `app-activities.md` rule for reconcile). When the commit is GitHub's head, it advances with GitHub's head as `commit_sha` and the service's head as `expected_head_commit`. The CAS value has to be the service's head: a GitHub head would never match once the service lags.
  - Everything else is `branch_update: none`: any other commit, no branch, or a tree or config hash (a non-default snapshot never moves a branch). In that mode the argument's commit, validated and lower-cased, is what gets indexed; it moves nothing.
  - A `GetRepository` or `ListBranches` failure fails the MCP call, because a process has no `on-error`.

  `start-indexing-body-from-github` and `watch-sha-is-a-branch-name` pin this. The first also fails when `ensure-advance` is changed to send the arguments.
- **A CAS that no longer matches is not an error.** `/control/ensure` attaches with `branch_advanced: false`; it never answers 409 (`head_mismatch` is `/control/branches/retire`'s). Only a published result (complete or partial) carries that decision: a queued or running job has `branch_advanced` null and moves the branch when it publishes, if the guard still holds then. So `start-indexing` adds "the branch was not moved" only to a published result's message.
- **`get-indexing-status`** accepts only a decimal `jobId` (`^[0-9]{1,19}$`) and never builds a URL from anything else. The service's `last_error` is free text (an exception message, or a coverage reason that names projects), so only its first line leaves, with control characters replaced and at most 200 characters, plus the code a `code: message` error starts with as `errorCode` (`get-indexing-status-error-first-line`). The service already shows `last_error` to the same user on `/control/status`, so this is hygiene, not a boundary.
- **Every flow assigns its variables and outputs before reading them.** A run takes every input as a variable, declared or not (an MCP `tools/call` passes every argument through), and a `variables:` default only fills a name that is still unset (elevenworks/ProcessStack#3286). Before this, an extra argument named `expectedHead` on start-indexing's resolve-404 path became the compare-and-swap value. Each flow now starts with an `init` node that assigns every variable and every output that is not an input:
  - `start-indexing`: all 22 variables (including `expectedHead`, `advanceRequested`, `ensurePublished`, `resolveJson`, `githubHead`, `isHead`) and its 9 outputs. `expectedHead` is computed in one node (`expect-head`) for both a 200 and a 404 from resolve.
  - `ensure`: its 14 variables and 12 outputs.
  - `get-indexing-status`: its 3 variables and 8 outputs.
  - `grant-watch`: all 20 variables, which cover its outputs (`granted`, `needsIndex`, `ensureOutcome`, `memoryKeys`, `memoryValue`, `message`).
  - `revoke-watch`: all 12 variables, which cover its outputs.
  - `configure-watched-repos`: all 18 variables, SX-11's 8 `import*` ones included (`stateUpdate` is still `{}` from a `SetVariable` node).
  - `legacy-dual-write`: its 4 variables and 3 outputs.

  Pins, each checked by mutation:
  - `start-indexing-seeded-cas-ignored` fails against the old flow (the seeded value was sent as `expected_head_commit`). It needs both defences removed, the `init` reset and the shared `expect-head` node.
  - `grant-watch-seeded-state-ignored` and `unwatch-seeded-state-ignored` fail when `init` is emptied, which also shows `context.setVariable` works in orchestration scripts.
  - `get-indexing-status-seeded-outputs-ignored` fails the same way, on `errorCode`. The not-found path resets the other outputs itself.
  - `get-indexing-status-seeded-response-ignored` pins a non-JSON 200 as `error`. It passes even without `init`, because the request's empty `json` output overwrites the seed.

  A seeded name that shadows a script global makes the run fail, because inputs are data: a seeded `String` fails `check-inputs` with "Property 'String' of object is not a function" before any request (checked by hand).
- **`legacy-dual-write`** writes the v1 slug and the camelCase value. A remove lists the scope and tombstones (`"null"`) each live entry that matches by value (owner, repo, host from `cloneUrl`, branch) or, when the value does not parse, by key. That covers entries v1 wrote under a non-canonical spelling.

**Tests and CI**
- **`processstack app validate`/`app test` take the app directory as `-p`**, and `app test` needs `--all` (or `-s <name>`). Both load `activities/sextant/`, so CI runs `build.sh` first.
- **The GitHub activities run against `http:` stubs (CLI 1.1.1, PS-19).** A stub on connection `github` answers `GitHubGetRepository` (`GET /repos/{owner}/{repo}`) and `GitHubListBranches` (`GET /repos/{owner}/{repo}/branches`). The bodies use GitHub REST field names (`full_name`, `default_branch`, `private`; `name`, `commit.sha`, `protected`), which Octokit maps onto the activities' outputs. An error status raises Octokit's own exception (404 `NotFoundException`, 403 `ForbiddenException`, 5xx `ApiException`). A bare `connection: github` criterion in `expected.httpCalls` also matches `sextant-control` requests: only GitHub requests carry a bound connection id, and the GitHub connection has no base URL. GitHub assertions are therefore scoped by path (`/repos/**`).
- **A chat scenario passes the prompt envelope as inputs** (`tenantId`, `applicationInstanceId`, `conversationId`), which `app test` needs to route `SendConnectionMessage`. `expected.httpCalls` records requests only when the scenario has an `http:` block, so the no-call scenarios stub `/**` with a 500.
- **The CLI installs from `apps/processstack/`,** whose `nuget.config` maps `ProcessStack.Cli` to the private feed, so it is never resolved from nuget.org. CI pins 1.1.1: 1.1.0 carried PS-6, PS-7, PS-8 and PS-15, and 1.1.1 adds PS-19's GitHub stubs.
- **Build output leaves the app directory.** `app test` reads every `.yaml`/`.yml`/`.json` file under `tests/` as a scenario (PS `TestScenarioFiles`), including the MSTest project's `bin/`/`obj/` JSON. `sextant/Directory.Build.props` therefore sets the artifacts output to `apps/processstack/artifacts/`.
- **For G2c:** `app publish` packs every file under the app directory (PS `ApplicationPacker`), including `src/` and the MSTest project's sources. Build output is no longer among them.

**A fix to SX-13's activities.** In-process, PS binds each resolved input onto the activity's property but leaves `Definition.Parameters` holding the authored expression text (`= prompt || ''`); only an off-host dispatch overlays resolved values (elevenworks/ProcessStack#3269). `ActivityValues.Input` preferred the parameter, so every expression input read its own source text, and the parse always returned `help`. The bound property is now the source of truth. A parameter is used only when it is a literal, not text that may be authored. PS is not consistent about the prefix (`"= "`, `"=${"`, a trimmed `=`), so any text whose trimmed start is `=` counts as authored, as do a `${…}` template and `{{ … }}`, also inside a map or list. A false positive costs nothing: the bound property then holds the same literal. `chat-prompt-expression-reaches-parse` pins the symptom end to end through the real `parse` node.

## Implementation notes (SX-10, as built)

SX-10 adds the GitHub repository-event triggers, the nightly schedule, `on-repository-change`, `reconcile`, `reconcile-target` and `tenant-grant`, and resolves #199 in `SextantPlanReconcile`. The tables above are updated to match. Where the code still differs, the code wins.

**Manifest**
- **Five connection-event triggers and one schedule.** `gh-push`, `gh-delete`, `gh-pr-open`, `gh-pr-sync` and `gh-pr-reopen` on connection `github` start `on-repository-change`; `nightly-reconcile` (`0 3 * * *`, `Etc/UTC`) starts `reconcile`. The triggers map no inputs, because the run receives the normalized metadata as `event`.
- **`configure-watched-repos` stays the default entrypoint** (the sketch named `on-repository-change`). The default is what an unnamed run starts, and the chat is the only flow a user should reach that way.
- **The four new entrypoints are `internal: true`** and not in `mcp.include`. `internal` is not enforced on a direct run (elevenworks/ProcessStack#3287), so each flow assumes a direct run with inputs of the caller's choosing, and each resets every variable and output first (elevenworks/ProcessStack#3286).
- **`permissions: []` stays.** An app reaches its own App State without a permission (PS `docs/APPLICATION_STATE.md`).

**`on-repository-change`**
- **It reads `event.*` only.** `init` copies the fields `SextantPlanRepositoryChange` reads into `ev`, each as a string (an object or null becomes `""`). `senderLogin` is never read, so it is never identity.
- **Every send waits for the tenant grant: push, deleted push, delete event and pull request alike.** A user's direct run runs as that user, with an `event` of its choosing (its pull request commits, its branch to retire). `tenant-grant`'s `granted` (200) and `limit` (409 `grant_limit`) come only from an application caller: `GrantEndpoints` checks the actor before the write, so a user gets 403 `wrong_actor`. Any other answer, including a 5xx or no response, is logged and ends the run. The service's own checks stay behind it, but without the gate a user's run would reach the service as a user ensure with caller-chosen commits.
  - **Only the reconcile repairs a missed push ensure.** A later push carries its own `before` as the CAS, and that fails while the pointer is still behind, so the branch waits for the nightly run (or a run started by the operator). Re-sending a missed ensure under the resolved commit would not be safe: a late, older push would roll the branch back.
  - **A pull request, deleted push or delete event also creates or holds the repository's tenant grant** (a deviation from v1, which enrolled a repository only on a push). The grant is idempotent and names a repository the installation already delivers pushes for; the enrolled row is still written only by a default-branch push.
  - `repo-push-user-run-refused`, `repo-push-deleted-user-run-refused`, `repo-delete-event-user-run-refused` and `repo-pr-user-run-refused` pin that a user's run sends only the refused grant (no ensure, retire or GitHub call), each checked by mutation (a gate that never matches fails all of them).
  - `repo-push-grant-limit` pins that the ensure is still sent at the tenant's limit, without an enrolled row.
- **The enrolled row is written inline** with `SetAppValue` (not through `legacy-dual-write`, which keeps the per-user memory). It is written only when all of these hold:
  - the grant answered `granted` (not `limit`);
  - the branch is the event's default branch;
  - the event's `connectionId` matches `^[A-Za-z0-9_-]{1,128}$` (it is a key segment).

  The key is `enrolled/{connectionId}/{host}/{owner}/{repo}@{branch}` with v1's slug rules, and the value is v1's PascalCase shape with an ISO 8601 `EnrolledAt`. It is written whatever the ensure's outcome, because it mirrors the grant. A failed write is logged.
- **A deleted push** retires under the CAS `before`. The enrolled row is removed (`DeleteAppValue`) only after the service answered the retire with success, whether it retired the branch or found it absent. The service answers 409 `default_branch` for its own default, and 403 to a user.
- **A delete event's retire has no CAS, so GitHub is asked first.** This implements the narrowing `app-activities.md` suggests. `GitHubListBranches` (Octokit pages through every branch) must not list the branch. Otherwise the retire is skipped (`retireSkipped`):
  - `branch_exists`: a push re-created it;
  - `github_unavailable`: the listing failed, or did not return a list;
  - `not_verifiable`: the repository is not on github.com.

  The window that remains is between the listing and the retire. The reconcile later retires a watched branch the check kept, under a CAS.
- **Pull requests publish base, then head, with `branch_update: none`,** after the tenant grant like every other event. Nothing moves. The commits are the event's, checked for shape only, so the grant gate is what keeps a user's direct run from choosing them.
- **A fork's head is not indexed (a deviation from v1).** `SextantPlanRepositoryChange` sends the head only when `headCloneUrl` passes the shape check and has the base repository's SVC-5 canonical key, and then under the base's spelling. Otherwise the reason is `fork_head` and only the base is sent.
  - Why: a fork's head is code its author controls, and anyone can open a pull request against a public repository in the installation. A trigger's ensure is the application's, which the service neither grant-checks nor bounds (SX-6d applies to users). The worker's MSBuild evaluation of a hostile project is not contained (`EvaluationSandbox`, #76), so indexing the fork would run the author's code with the service's credentials.
  - The service's SVC-5 `REPOSITORY_HOSTS`/`REPOSITORY_OWNERS` policy stays a second line of defense; set `REPOSITORY_OWNERS` for production.
  - `repo-pr-fork-head-skipped` and the planner's unit tests pin it.
- **No retry.** Neither `retryAdvised` nor a 503 is retried: every step is idempotent, deliveries are not de-duplicated, and the reconcile heals a missed advance of a watched branch.
- **Outputs:** `action`, `reason`, `grantOutcome`, `ensureOutcome`/`ensureReason`, `headEnsureOutcome`, `retireOutcome`/`retireReason`, `retired`, `retireSkipped`, `enrollment` (`recorded`/`removed`), `enrolledKey` and `enrolledValue`. Reasons are code-shaped (`^[a-z0-9_]{1,64}$`, else `http_<status>`), and no response body is logged.

**`tenant-grant` (internal process)**
- **Input `repository`; outputs `outcome`, `created`, `statusCode` and `reason`.** The outcomes are `granted` (200), `limit` (409 `grant_limit`), `rejected` (other 400/409), `unauthorized` (401/403), `unavailable` (no response, 408, 429, 5xx) and `error`.
- **The URL is checked first** with `SextantNormalizeRepository`. A URL it refuses gives `rejected`/`invalid_repository`, and nothing is sent.
- **The body is only `{repository}`,** the checked URL. The service takes the tenant and principal from the caller assertion, and it refuses a body that names one (400 `principal_in_body`).
- **The body expression is wrapped in parentheses:** `= ({ repository: checkedUrl })`. Jint parses `= { repository: x }` as a block with a label, which sends the bare string. `tenant-grant-granted` pins this.

**`reconcile`**
- **The grants listing comes first.** It is the application-only call, so a user's run stops after one refused `GET`, with `status: refused` (`reconcile-grants-refused`, with seeded enrolled rows). The sketch imported first, which let a user's run send up to 500 refused `PUT`s.
- **The legacy import follows the listing.** Repositories are de-duplicated by URL, lower-cased without a trailing `/` or `.git` (the tenant grant has no branch), sorted, and capped at 500 per run. They are granted one at a time, so the grant limit is counted in order. When the import created a grant, the targets are listed again. A failed second listing keeps the first, and the new grants wait for the next run.
- **Targets** are de-duplicated and sorted by (repository key, branch), and capped at 500 per run (`truncatedTargets`: the rest wait for the next run). They are reconciled four at a time with `continueOnError`. A child run that returned no result counts as `failed`.
- **The reconcile retires, one target at a time.** This follows the planner's default (`app-activities.md`), amending the sketch's "retire is left to delete events". A branch GitHub no longer has is retired under the CAS of the commit the service resolved, and the service's default branch is never retired.
- **Outputs:** `status` (`completed`, `refused` or `unavailable`), `legacyRows`, `legacyGranted`, `targetCount`, `truncatedTargets`, `upToDate`, `ensured`, `retired`, `skipped` and `failed`.

**`reconcile-target` (internal orchestration)**
- **It is an orchestration, not a process,** so a GitHub or network failure routes to a skip instead of failing the run.
- **Inputs `repository` and `branch`; outputs `result`, `reason`, `ensureOutcome` and `retireOutcome`.** `result` is one of `up_to_date`, `ensured`, `retired`, `skipped` or `failed`.
- **It refuses before any call:**
  - `invalid_repository`: a URL `SextantNormalizeRepository` refuses;
  - `not_github`: another host, which the tenant's GitHub connection cannot read;
  - `invalid_branch`: a branch the chat parser refuses, or one still starting with `refs/heads/`, which is refused rather than stripped (as in `ensure`).
- **Order:** `GitHubGetRepository` (the default branch's name), then `GET /control/resolve` with that name (a target with branch `""` is the default), then `GitHubListBranches`, then `SextantPlanReconcile` over this one target. So the planner's limits never bind.
- **Skips:** `not_reachable` (a GitHub read failed), `default_branch_unknown`, `resolve_failed` (the resolve had no response), or the planner's skip reason.

**#199: the reconcile sets the default**
- `SextantPlanReconcile` ensures a target when all of these hold: it is at GitHub's head, it is on GitHub's default branch, and its resolve says `is_default: false`. The ensure carries the CAS of the current commit and `default_branch: true`. Before, such a target was up to date and never promoted.
- The service passes the CAS on the unchanged commit (`BranchHeadMatches`), then `ShouldOwnDefault` and `PromoteSoleDefaultBranch` make the branch the default. No pointer moves (`SnapshotService.ApplyReuseBranchDecision`).
- This is the case SX-9 left open: a user's first watch creates the branch non-default, and the default-branch grant reports `missing` until an application ensure sets the default. The first reconcile now does that without waiting for a push.
- `reconcile-sets-default` and the planner's unit tests pin it.

**Scenarios (as built)**
- **The numbered scenarios map to these files:**
  - 13 and 33: `repo-push-default-branch` (the sender's login is a user id);
  - 14: `repo-push-feature-branch`;
  - 15: `repo-push-branch-create`;
  - 16: `repo-push-deleted`;
  - 17: `repo-push-tag-ignored`;
  - 18: `repo-delete-event-retires`;
  - 19: `repo-pr-opened`;
  - 20: `repo-push-ensure-unavailable`;
  - 21 to 26: `reconcile-noop`, `reconcile-up-to-date`, `reconcile-behind`, `reconcile-missing`, `reconcile-branch-gone` and `reconcile-legacy-sync`.
- **Added:**
  - `repo-delete-event-branch-recreated` and `repo-delete-event-github-unavailable`;
  - `repo-push-user-run-refused` and `repo-push-grant-limit`;
  - `repo-push-deleted-user-run-refused`, `repo-delete-event-user-run-refused` and `repo-pr-user-run-refused`;
  - `repo-pr-fork-head-skipped`;
  - `reconcile-sets-default`, `reconcile-several-targets`, `reconcile-grants-refused` and `reconcile-legacy-already-granted`;
  - `reconcile-target-refuses-heads-prefixed-branch` and `tenant-grant-granted`.
- **Seeded-state pins,** each checked by mutation (removing one name from the flow's `init` fails it): `repo-change-seeded-state-ignored`, `reconcile-seeded-state-ignored`, `reconcile-target-seeded-state-ignored` and `tenant-grant-seeded-outputs-ignored`.
- **The harness exposes no claims for an application run.** `caller: { act: "!absent" }` therefore asserts that no user caller signed the request, which is how 31 and 33 are checked for repository events and the schedule.
- **`app test` supplies the connection-event envelope.** The scenario's `event:` block is merged over its defaults.

## Implementation notes (SX-11, as built)

SX-11 adds `mcp.connectionTools`, the `import-legacy-watches` process, the lazy import in the chat, and the app's `README.md`. Where the code differs from the sections above, the code wins.

**Connection tools**
- **One entry: `sextant-query`, `prefix: ""`, `timeoutSeconds: 120`, 24 tools.** The `include` list is exactly `ServiceApp.RemoteQueryTools` minus `research_codebase` (it calls an LLM on the service's side). `ProcessStackAppManifestTests` (`tests/Sextant.Service.Tests`, which can reference `Sextant.Service.Host`; the app's test project cannot) reads `psapp.yaml` as text and pins that equality in both directions, the absence of the local-only tools (`get_source_context`, `get_daemon_status`, `get_base_snapshot_symbols`) and of `research_codebase`, `sextant-query` as `type: mcp` with no `config:` (a deployment-bound dependency; an inline one fails `CONNECTION_INLINE`), and no name shared with an app entrypoint. A tool added to or dropped from the service surface therefore fails the public build until the app is triaged.
- **Not scenario-tested (deviation).** `app test` has no stub for connection tools: the `mcp:` stubs answer `CallMcpTool` only, so no scenario can list or forward them. The kickoff's connection-tools scenario is replaced by the manifest test and the pooled-client test below.
- **One pooled upstream client is authorized request by request (elevenworks/ProcessStack#3258 item 4).** PS pools one MCP client per (tenant, connection) and shares it across users, with each request carrying its own assertion. The service's `/mcp` is stateless and verifies each POST's assertion (`CallerAssertionGate`); nothing is bound to an MCP session id. `McpClientCompatibilityTests.SdkClient_OnePooledClient_AuthorizesEachRequestByItsOwnCaller` pins it with one SDK client over the delegate token whose handler stamps each request's assertion: `user-1` (granted) reads the repository, `user-2` (no grant) reads nothing, `user-1` still reads afterwards, a request with no assertion is `caller_required`, and six concurrent calls alternating the two users each get their own caller's answer.

**`import-legacy-watches`**
- **It cannot reuse `grant-watch` (deviation).** An MCP tool must be a process, and a process can neither start an orchestration nor route an error, so it repeats `grant-watch`'s gate inline: `GitHubGetRepository` (the workspace's GitHub connection must see the repository), then `SextantNormalizeRepository` over GitHub's `full_name`. The URL sent is GitHub's spelling, never memory text (the row above used to say `value.cloneUrl`). It sends no first ensure, unlike `grant-watch`: one chat turn would otherwise start an index job per v1 watch. An imported watch with no snapshot lists as "not indexed yet" until a push, reconcile or `start-indexing` indexes it. It writes no v1 memory (the entry is already there).
- **Memory is untrusted.** `SextantPlanLegacyImport` (pure) reads each value's `owner`, `repo` and `branch`. The owner and repository must each be one path segment in the SVC-5 shape, a `cloneUrl`, when present, must name the same github.com repository, and the branch must pass `git check-ref-format --branch` without a `refs/heads/` prefix. Anything else is unreadable (counted as skipped). Tombstones (`null`, `""`, `"null"`) and duplicate slugs are not counted.
- **Already imported.** An entry is held when a grant for the same repository key names its branch, or a default-branch grant resolved (`status.resolved_branch`) to it. A v1 watch on GitHub's `default_branch` is granted as a default-branch watch (`{repository}`, no `branch`), as "watch owner/repo" records it; any other branch is named.
- **Failures converge (deviation).** A GitHub 404 or 5xx fails the whole run, because a process has no error route. So before each GitHub check the entry's count in `sextant.app:legacy-import-attempts` (`{slug: count}`) is written; a grant clears it, and a refusal (a normalize refusal, 400, or the 409 watch limit) sets it to 2. The plan checks the fewest-attempted entries first and leaves out an entry at 2 unless `retryUnresolved` is set, so each run gets past the entry the last one stopped at, and N entries GitHub never shows settle within 2N + 1 runs. A 401, 403 or 5xx from the `PUT` is retried on the next run. The attempts map is untrusted too: a non-object or a non-positive count reads as empty, and a count is clamped; a tampered map can only skip or re-check the user's own entries.
- **At most 200 entries per run** (`remaining` counts the rest), inside the process's 300 s MCP bound.
- **Outputs (deviation: more than `imported, skipped, failed`).** `imported` (held after the run, including those held before), `newlyImported`, `skipped` (unreadable, unresolved, or refused for good), `failed` (not granted this run, or at the watch limit), `remaining`, `complete`, `outcome` (`ok`, or `unavailable` when `GET /control/grants/self` fails, which imports nothing) and `message`. The message carries counts only, never a repository name or service text.
- **The process sets the flag itself (deviation).** It writes `sextant.app:legacy-import-v1` (ISO time) only when `complete`: nothing remaining and every attempt mark at 2. An import run over MCP therefore counts too, and one cut short by a failure is picked up again.
- **Every variable and output is reset first** (#3286), pinned by `import-legacy-watches-seeded-state-ignored`: seeded `pending`, counts and marks change nothing.
- **Scenarios:** `import-legacy-watches-process` (#12), `-not-visible` (a failed run leaves its mark), `-unresolved-skipped`, `-retry-unresolved`, `-service-unavailable`, `-refusals` (400/409/503) and `-seeded-state-ignored`. The `PUT`s assert the `act=user` caller claims (#31).

**The lazy import in the chat**
- **The flag alone guards it (deviation: no `isFirstTurn` check).** On an internal channel, each turn reads the flag (`GetMyMemory`; a failed read is logged and skips the import for that turn) and, while it is unset, runs `import-legacy-watches` before the command. It stops once the process sets the flag. Slack is refused before this, so a Slack turn never imports (`slack-watch-refused`).
- **The note is prepended to the reply to the command** (`compose-reply`, then a blank line). It is the process's `message` when `outcome` is not `ok` or anything was newly imported, skipped, failed or left; nothing is said when there was nothing to import or every entry was already held. A failed import is the note "I couldn't finish importing your earlier Sextant watches yet; I'll try again next time." with `_lastErrorType` logged; the command is still answered.
- **Scenarios:** `lazy-legacy-import` (#11: two `PUT`s, the tombstone skipped, the reply starts with the note), `lazy-import-flag-set-skips` (no call with the flag set) and `lazy-import-failed-note`. `unwatch` and `unwatch-two-repositories` seed the flag, because they seed v1 entries.

**Deploy (the README section above is corrected)**
- **`processstack app activate` takes no `--version`** (CLI 1.1.1): it activates the published version. Bindings are set in the WebClient deploy dialog; the CLI sets none. Rollback is `processstack app rollback sextant 1.0.1`, which publishes 1.0.1's content as a new auto-bumped version, then `processstack app activate sextant`. `cutover-runbook.md` is corrected the same way.
- **After a rollback and a re-activation, run `import-legacy-watches` again.** The flag survives the rollback, so watches added under v1 meanwhile are not imported lazily.
- **v2.1 cleanup also deletes the import's `sextant.app` keys** (listed above).
