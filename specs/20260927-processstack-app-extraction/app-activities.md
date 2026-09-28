# App activities: `Sextant.ProcessStack.Activities` (G1-A amendment)

> G1-A amendment (2026-09-27), authored by the orchestrator while the user was unavailable; flagged for the user's review. It depends on PS-14 (activity SDK package) and PS-15 (server-side app-bundled activities); see `ProcessStack:specs/20260927-application-bundled-activities/`. It amends `app.md`: the v2.0.0 app is no longer YAML-only.

## Why
The finished extraction must prove that a complex app is built **on top of** ProcessStack:
- it brings its **own activities**, compiled in this repository against the published SDK;
- it depends on **its own assemblies** (`Sextant.Core`), bundled with it;
- it depends on **an external process** (the Sextant service, through connections) and on a **CLI** (the `processstack` CLI to validate, test, publish and activate it).

None of this may require anything Sextant-specific in ProcessStack.

## Layout
```
apps/processstack/
  nuget.config              # nuget.org + the private feed (no credentials; see README.md)
  README.md
apps/processstack/sextant/
  psapp.yaml … (as app.md)
  src/Sextant.ProcessStack.Activities/     # net10.0 class library
    Sextant.ProcessStack.Activities.csproj  # PackageReference ProcessStack.Abstractions (private feed); ProjectReference ../../../../../src/Sextant.Core
  tests/Sextant.ProcessStack.Activities.Tests/   # MSTest, no mocking frameworks; dual direct-property + Definition.Parameters tests
  build.ps1 / build.sh      # dotnet publish -c Release -o activities/sextant/  (output not committed; .gitignore)
  Sextant.ProcessStack.slnx # NOT part of Sextant.slnx: the public build must not need the private feed
```

## Activities
All of them are pure computation: deterministic, no network, no secrets. Authenticated I/O stays on the platform's `HttpRequest` with `connectionId: sextant-control`, which keeps F4 caller identity and credentials out of app code (PS-15 recommended pattern).

| Name | Inputs → outputs | Replaces (app.md) |
|---|---|---|
| `SextantNormalizeRepository` | `cloneUrl` / `htmlUrl` / `owner` / `repo` → `remoteUrl`, `repositoryKey`, `host`, `accepted` (host shape only; the service's SVC-5 policy stays authoritative) | the URL `SetVariable`/JS in the flows; uses `Sextant.Core.GitRemoteNormalizer` / `RemoteUrlIdentity`, the same code the service uses |
| `SextantPlanRepositoryChange` | the event metadata (`kind`: push/delete/pr, plus PS-5 keys) → `action` (`ensure`\|`retire`\|`ignore`), `ensureBody` (`commit_sha`, `branch_name`, `default_branch`, `expected_head_commit` with all-zeros → `""`, `branch_update`, `forced`), `retireBody`, `reason` | the `ensure` steps 1–2 and the `on-repository-change` decision chain; enforces **CAS mode only** (SX-8 note) |
| `SextantInterpretEnsureResult` | `statusCode`, `body` → `outcome` (`ok`\|`attached`\|`rejected`\|`unavailable`\|`head_mismatch`\|`not_granted`), `jobId`, `snapshotId`, `branchAdvanced`, `retryAdvised` | the `ensure` step 3 decision table, including the SX-8 out-of-order convergence (`branch_advanced:false` → `retryAdvised`) |
| `SextantPlanReconcile` | `githubBranches[]`, `serviceTargets[]` (from `?scope=tenant` / resolve), limits → `ensures[]`, `retires[]`, `truncated` | the reconcile diff JS |
| `SextantParseWatchCommand` | `prompt` → `verb` (`watch`\|`unwatch`\|`list`\|`help`), `repositories[]` (normalized), `errors[]` | the `configure-watched-repos` parser |

Names are unique to this app and do not collide with built-ins; PS-15 rejects any collision at publish.

## Tests
- **Unit tests** of each activity: direct-property plus `Definition.Parameters`, covering every row of the SX-8 precedence table in `SextantPlanRepositoryChange`, and all status codes in `SextantInterpretEnsureResult`.
- **Scenario tests** (`tests/*.scenario.yaml`) run the real activities in-process through `processstack app test`, with the PS-6 HTTP/MCP stubs for the service.

## CI (`.github/workflows/processstack-app.yml`, amends app.md)
Checkout → setup-dotnet → add the private feed (`PROCESSSTACK_PACKAGES_TOKEN`) → `dotnet test apps/processstack/sextant/Sextant.ProcessStack.slnx` → `build.sh` → `processstack app validate` → `processstack app test`.

SX-13 adds the workflow through `build.sh`; SX-9 adds the `processstack app validate` and `processstack app test` steps. Until the `PROCESSSTACK_PACKAGES_TOKEN` secret exists, the job emits a notice, skips its steps and succeeds.

## Unit changes
- **SX-13** (this amendment's unit) adds the activities project, its tests, the build scripts and the CI workflow. It needs PS-14 (the package on the feed).
- **SX-9** builds the app skeleton (`psapp.yaml`, flows) and extends the SX-13 workflow with `processstack app validate` and `app test`. It needs PS-15 merged, since local `app test` loading already works but publish validation needs PS-15.
- **SX-10** flows use `SextantPlanRepositoryChange`, `SextantInterpretEnsureResult` and `SextantPlanReconcile`.
- **SX-11** uses `SextantParseWatchCommand`.

## G2 impact
- **Prerequisites:** prod needs the PS-15 API/silo, a runner image (`runner-v*`) containing the isolated loader, and the dispatcher redeployed (the worker host). The tenant's plan must also have `CustomActivitiesEnabled`.
- **Rollback is unchanged:** reactivate v1.0.1, which bundles no activities.

## Implementation notes
SX-13 follows the service code where it differs from the table above. Each activity's `[ActivityInput]`/`[ActivityOutput]` descriptions document its full contract.

**The wire contract (the code wins)**
- **The ensure body names the default flag `default_branch`**, not `is_default_branch`: it is `EnsureSnapshotRequest.IsDefaultBranch`'s JSON name. The table above is corrected.
- **The bodies never carry `branch_head_sequence`.** CAS mode only: an ensure is either CAS-guarded (`expected_head_commit`) or `branch_update: none`. The tests deserialize every emitted body with the service's own `ServiceJson.Options` and check `EnsureSnapshotRequest.BranchGuardProblem()` is null.
- **`forced` is informational.** It is sent for the audit and never bypasses the CAS.

**`SextantNormalizeRepository`**
- **`accepted` covers the whole SVC-5 shape**, not only the host: https only; no userinfo, query, fragment or non-443 port; a DNS host of at least two labels (no IP literal, no `localhost`, no trailing dot); a path of exactly `/{owner}/{repo}[.git]`.
  - The service's `REPOSITORY_HOSTS`/`REPOSITORY_OWNERS` allow-lists stay authoritative.
  - A refusal reports the service's reason code (`scheme_not_allowed`, `url_component_not_allowed`, `host_not_allowed`, `path_not_allowed`), or `missing_repository`.
  - The shape check is a regex-free mirror of `RepositoryUrlPolicy` (which lives in `Sextant.Service`, so the bundle cannot reference it). A parity test pins it to the service's policy with a `*` host list, case by case.
- **`remoteUrl` is the submitted spelling**, because identity hashes it.
  - An https `cloneUrl` is sent verbatim. An ssh remote becomes https through `GitRemoteNormalizer`.
  - `htmlUrl` gets `.git` appended, and `owner`/`repo` build `https://{defaultHost}/{owner}/{repo}.git`.
  - `repositoryKey` is the service's policy key, `https://{host}/{owner}/{repo}` folded by `RemoteUrlIdentity.Normalize`.
- **Extra inputs and outputs:** `defaultHost` (default `github.com`), `repositoryOwner`, `repositoryName` and `reason`. When the URL is not accepted, every URL output is `""`. The log names only the reason, never the input, which could carry a credential.

**`SextantPlanRepositoryChange`**
- **The PS-5 metadata map is the `metadata` input.** Individual inputs override its keys.
  - The pull request action is the `eventAction` input (metadata key `action`), because an input and an output cannot share a name.
  - For the same reason, the branch output is `branchName` (the input is `branch`).
- **`ensureBodies` lists every body**, and `ensureBody` is its first entry. A pull request yields a base and a head body.
- **Push:** an ensure with `branch_update: advance` and `expected_head_commit: before`, where all zeros becomes `""`.
  - A push with no usable `before` cannot be CAS-guarded. It still publishes, but with `branch_update: none` and `reason: missing_before`, so it never moves a pointer.
  - A tag is ignored with `tag`, and a create event with `handled_by_push`.
- **Delete:** a deleted push retires with the CAS `before`. It retires without a CAS (and `missing_before`) when `before` is unusable. A branch delete event retires without a CAS (`app.md` scenario 18).
  - **The no-CAS delete-event retire is a deliberate trade-off,** flagged for review. It removes a deleted branch whose delete push failed its CAS because the pointer was still behind `before`. That happens when the last push's index failed, or when the retire timed out waiting behind that index. The reconcile never covers an unwatched branch, so without this retire such a branch would stay in the service for good.
  - **The cost is the reverse race:** a delete event processed after a push that re-created the branch removes it again. Every later push then fails the CAS until a reconcile, which also covers only watched branches. A flow can narrow the window by checking that GitHub still lacks the branch before it sends the retire.
- **Pull requests** (opened, synchronize, reopened) publish base and head with `branch_update: none`. The head uses `headCloneUrl`; when that is empty, the head repository was deleted and only the base is sent.
  - A side naming the same repository and commit as an earlier one is sent once, because identity ignores the branch.
  - Closed and edited pull requests are ignored.
- **A `cloneUrl` that fails the SVC-5 shape is ignored** with the service's reason code, so the flow never sends a request the service would refuse.
- **Every ignore reason is a constant** on the class: `missing_repository`, `unsupported_event`, `unsupported_action`, `tag`, `not_a_branch`, `missing_branch`, `invalid_branch`, `invalid_after`, `handled_by_push`, `pull_request_closed`, `head_repository_deleted`, `invalid_sha`.

**`SextantInterpretEnsureResult`**
- **The input is the control call's status and body.** `statusCode` is 0 or empty when no response came back. `body` is JSON text, a parsed object or a map.
  - `branchUpdate` is the mode the ensure sent.
  - `operation: retire` interprets a `/control/branches/retire` response, filling `retired`.
- **Two outcomes are added** to the table's six:
  - `unauthorized` covers 401 and a 403 that is not `not_granted`, including the caller-assertion errors `caller_required`/`caller_not_allowed`.
  - `error` covers 404, 405, 415, 422, a 3xx, and an invalid 2xx body.
- **The status mapping:**
  - 2xx gives `ok`, or `attached` when the body says `attached`.
  - 400 gives `rejected`, with the body's reason: an SVC-5 code, a branch-guard code, or `branch_required`.
  - 409 gives `head_mismatch` when the reason is `head_mismatch`. Any other 409 (`default_branch`) is `rejected`.
  - 0 (no response), 408, 429 and 5xx (including the shutdown 503) give `unavailable`.
- **`not_granted` is accepted** as either the body's `reason` or its `error`, because the service does not emit it yet (SVC-4 is unmerged).
- **`reason`** is the body's `reason`, else its `error`, else `no_response`/`http_<status>`. For a 2xx ensure it is the job's recorded reason.
- **Extra outputs:** `identityHash`, `jobStatus`, `terminal`, `published`, `attached` and `retired`. The job statuses mirror `SnapshotJobStatus`, and a test pins them.
- **`retryAdvised` is true only for an `advance` ensure that reported `branch_advanced:false`.** It is never true for `none`.
  - `false` also means the pointer was already there, so the flow must bound its retries (one re-send is a cheap reuse).

**`SextantPlanReconcile`**
- **The input contract:**
  - `serviceTargets` is the watched `(repository, branch)` list. `branch: ""` means the default branch. Each target carries its `/control/resolve` result (`resolveStatusCode`, `resolve`).
  - `githubBranches` is either per repository `{repository, defaultBranch, branches:[{name, sha}], truncated?}` or flat `{repository, name, sha, isDefault?}` (`commit.sha` is also read).
  - **Every resolve happens before the GitHub branch listing,** which amends the order of `app.md` steps 3b and 3c. The order is: `GetRepository` (for the default branch's name), then the resolve, then `ListBranches`.
    - The CAS only guards against pushes the service processes after the resolve.
    - With the listing first, a push processed between the listing and the resolve passes the CAS. The ensure would then roll the branch back to the listing's head, or the retire would remove a branch the push had just re-created.
    - A default target (`branch: ""`) is resolved with GitHub's default branch name from `GetRepository`, not without a branch. A resolve without a branch answers for the service's default, so after GitHub's default changes, that target would be skipped on every run. Resolving with GitHub's name gets a 404 for a new default, which is then ensured and promoted under an empty CAS.
    - A resolve that answers for a branch other than the listing's default is skipped with `resolve_branch_mismatch`. Under this order, that only happens when the default changes between `GetRepository` and `ListBranches`.
- **A branch GitHub has at a different head** is ensured under the CAS of the service's resolved commit. A 404 resolve is ensured under an empty CAS, which creates the pointer.
- **Retires (amending `app.md` step 3b, "retire is left to delete events").** A branch GitHub no longer has is retired under the CAS of the service's resolved commit. A branch re-created after the listing has had its pointer moved by its push, so it fails the CAS and is kept.
  - The default branch is never retired (`default_branch`, `default_branch_absent`).
  - A listing marked `truncated:true` or `complete:false` (either form) is never retired from (`branch_listing_incomplete`).
  - A repository with no `branches` list is treated as unreachable.
  - A flow that wants `app.md`'s behaviour passes `maxRetires: 0`: every absent branch is then skipped with `retire_limit` and `truncated` is true.
- **Limits:**

  | Limit | Default | Maximum |
  |---|---|---|
  | `maxTargets` | 1000 | 10000 |
  | `maxEnsures` | 100 | 1000 |
  | `maxRetires` | 20 | 1000 |

  A negative limit means the default. A cut-short plan sets `truncated`, and the next run continues.
- **Extra outputs:** `truncatedTargets`, `upToDate`, `skipped` and `skippedTargets` (`{repository, branch, reason}`; the reasons are constants on the class).
- **The plan is deterministic:** targets are ordered by `(repositoryKey, branch, repository)`, so input order never changes the plan.

**`SextantParseWatchCommand`**
- **Verbs:** watch (also `track`, `start watching`), unwatch (also `untrack`, `stop watching`, `stop tracking`), list and help.
  - `list` is `app.md`'s set (`what am i watching…`, `list watched…`, `show my watched…`) plus a few variants such as `what am i tracking…` and `show watched…`.
  - Anything else, including an empty prompt, is `help`.
  - Leading mentions (`<@U…>`, `@name`) and trailing sentence punctuation are ignored.
- **Repositories:** `owner/repo`, `host/owner/repo`, https and ssh URLs, `<url>` and Slack `<url|label>` links. They are normalized as in `SextantNormalizeRepository`, de-duplicated by `repositoryKey`, and capped by `maxRepositories` (default 10, at most 50).
  - A Slack link gives its URL, unless the URL is only `http://` or `https://` plus the label. Slack auto-links a typed `github.com/owner/repo` as `<http://github.com/owner/repo|github.com/owner/repo>`, so the label is what was typed.
  - `&amp;` (Slack's escaped `&`) separates repositories, like `&`, `,` and `and`.
- **Branch:** the extra `branch` output comes from `on <branch>` or `on branch <branch>` (the last spaced `on`). `""` means the default branch.
- **Errors** are human-readable and capped at 10.
  - An echoed reference is truncated to 64 characters.
  - A reference containing `@` is never echoed, since it may carry a credential.

**Packaging, tests and CI**
- **The bundle ships no SDK assemblies.** The activities project references `ProcessStack.Abstractions` 1.0.0 with `ExcludeAssets="runtime" PrivateAssets="all"`, which also keeps its transitive `Microsoft.Extensions.*` runtime assets out.
  - The publish output is `Sextant.Core.dll/.pdb`, `Sextant.ProcessStack.Activities.dll/.pdb` and `.deps.json`.
  - `build.sh`/`build.ps1` fail if any `ProcessStack.*.dll` is present, or if either Sextant assembly is missing.
- **The test project references `Sextant.Service`** (test-only, never the bundle), so the parity tests compare against the service's own policy, contracts, JSON options and job statuses.
- **The workflow also triggers on `src/Sextant.Service/**` and `src/Sextant.Store/**`**, where those parity sources live.
- **`ArchitectureBoundaryTests` guards the public build.** It fails if anything outside `apps/processstack/` references the SDK packages or the app's projects, or if `Sextant.slnx` includes the app tree.
