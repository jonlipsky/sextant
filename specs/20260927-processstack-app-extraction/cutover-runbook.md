# Prod cutover and rollback runbook (Phase 4, G2)

> **Approved at G1 (2026-09-27). Tracking: elevenworks/ProcessStack#3159.** Lives in `jonlipsky/sextant` `specs/20260927-processstack-app-extraction/`.
> Each prod step runs on `<prod-host>` through the operator's approved remote-execution channel, and needs the human's explicit G2 approval for that sub-gate. Long jobs are detached with `setsid`. Secrets are generated on the host and never printed.
> Prod-specific values (`<prod-host>`, `<platform-checkout>`, `<tenant>`, `<baseline-repo>`, `<sextant-service-url>`, `<rollback_tag>`, `<leaked-key-prefix>`, `<v1.0.1-version-id>`, `<org>`, `<owner>`) are placeholders. The operator holds the concrete values.
> Issue and PR numbers: a bare `#NNNN` of 3000 or more is in elevenworks/ProcessStack; a lower bare number, or one written `jonlipsky/sextant#NNN`, is in this repository.

## Status (as built, 2026-09-28)

| Gate | Code | Prod |
|---|---|---|
| G1 | Approved 2026-09-27 | — |
| G2a (platform) | The PS units and fixes named under G2a are merged, as noted there by PR number | Not run |
| G2b (service) | Every Sextant service unit is merged: SX-1…SX-8, SX-U, SX-5b, SX-6c, SX-6d and SX-7b (per-unit PRs in [`pr-plan.md`](pr-plan.md), "As-built status") | Not run |
| G2c (app) | The app v2.0.0 (SX-9, SX-10, SX-11, and its activities bundle SX-13) is merged at `apps/processstack/sextant/`. A dev-stack rehearsal confirms the deploy mechanics in step 2 first | Not run |
| G3 (PS-11) | Planned in [`processstack-deletion.md`](processstack-deletion.md) | Not run |

## Baseline (record before G2a)

| Item | How |
|---|---|
| Platform image tags / sha | `docker ps --format` + `git -C <platform-checkout> rev-parse HEAD` |
| Sextant service image + schema (expect 23) + repo count (expect 8) | `/control/metrics` |
| Old-path latency baseline | 20× `find_symbol` + 20× `find_references` against `<baseline-repo>` through `/v1/<tenant>/mcp/_sextant` → p50/p95 |
| Owner's watched repos (expect 8) | the v1 chat "list" turn, **through the app** |
| **Which identity keys those watches** | v1 stores a watch under the id of the chat that created it. Run "list" from the **web chat or CLI**, which use the platform user id, and check that it returns all 8. Under the G1 decision (`CALLER_IDPS=processstack`), `import-legacy-watches` imports only memory keyed by the platform user id. Slack-keyed watches are **not** imported, and v2 refuses watch commands on Slack. As built in PS-7, a Slack *channel* turn is even signed `act=application`. If any of the 8 were created from Slack, **stop and ask the human** before G2c: re-watch them from the web chat, or revisit the Slack decision. |
| Enrolled repos | the v1 app's App State listing, read through the app's own `get-indexing-status`/list surface or the generic App State read API **as the app**; no raw SQL |

## G2a: platform features (additive)
Ships PS-2…PS-8, PS-15 and the #3216 security fix. PS-14 is the SDK package only and needs no deploy. The old path is untouched.

> **As-built additions (2026-09-28):**
> - **#3216 (HIGH) is a security fix. It is merged as #3237 → `a62267571`.** Consider shipping it as its own G2a-0 hotfix, ahead of the rest. After the deploy, a platform admin audits prod keys through the key-listing API (`GET /v1/admin/apikeys` or the AdminPortal API Keys page), with no raw SQL, and the human reviews the list. Look for:
>   - system-tenant keys with `admin:*`, and keys with cross-tenant grants, minted by non-admins (#3216);
>   - `admin:*` keys minted by an admin who has since been demoted or deactivated (#3240, open: the admin role is still read from the JWT claim);
>   - child keys minted by an API key rather than a session. These can outlive their parent key's expiry and revocation (#3249, open).
> - **PS-4 (#3220 → `e299c3a24`)** adds live-bounded keys. Existing keys are unchanged: they stay snapshot keys.
> - **PS-15 adds a worker-side piece.** Release a `runner-v*` image containing the isolated app-activity loader, then redeploy the dispatcher/worker on the worker host with that runner image. The API/silo deploy alone is not enough.
> - **Plan gate.** Check that the prod tenant's plan has `CustomActivitiesEnabled`; it defaults to false in `DefaultPlanSeeder`. If it's false, the human enables it through the Admin Portal plans page (an audited admin action). Without it, v2.0.0 publish fails with `custom_activities_not_enabled`.
> - **#3202** commits the live `SEXTANT_*` → `SextantGateway__*` api env vars to `deploy/compose-standalone`. Keep them through G3; PS-11 removes them.
> - **Data migrations in this deploy.**
>   - **PS-10 (#3253 → `5f589373f`, migration 3.73.0).** Built-in Owner roles gain every missing tenant catalog grant, notably `artifacts:*` and `content:*`. Built-in Admin roles gain the same two wildcards, but only if they still hold the original seeded Admin default; Admin roles an operator has narrowed are skipped. Member, Viewer and custom roles are untouched. The migration is forward-only and idempotent. Rolling back the images does **not** revoke the new grants. If that's needed, a human removes them through the Roles UI/API; never with raw SQL.
>   - **PS-15 (#3252 → `8c17b48b3`, migration 3.72.0)** adds the `ps.application_activity` table and the `applications:activities:read` permission. Owner and Admin hold it through `applications:*`, so no role migration is needed. The worker execution token carries it for staging. It needs a `runner-v*` image that includes `--app-activity`, plus a dispatcher and worker redeploy. Old runners and workers fail closed on app activities.
>     - **The E2E passed on the local dev stack** (2026-09-28, origin/main `af96815ee`, after a test-only fix, #3292). The path it exercised: silo `ApplicationActivityProxy` → NATS → dispatcher, a per-file SHA-256 check, then `docker run --network processstack-isolated --cap-drop ALL --security-opt no-new-privileges ... processstack-runner:alpine activity exec … --app-activity …`.
>     - The plan flag was set through `PUT /v1/admin/plans/{id}`, the admin API the portal uses. **Negative checks held.** With the flag off, publish returns 422 `custom_activities_not_enabled`. An already-published app's run fails at the silo with the same code, before reaching the dispatcher, because the entitlement is re-checked at every dispatch.
>     - **Topology (dev compose):** the **dispatcher**, which mounts `docker.sock`, stages the bundle, runs the hash check and launches the runner. The `processstack-worker` container isn't involved. So for prod, the dispatcher is the service that needs the new runner tag.
>     - **New HIGH, pre-existing (#3291):** `RunShellCommand` is Full trust and runs a process in-process on the silo. That is one more reason to minimise what the prod silo can reach (docker.sock, host env).
>     - **Caveat for the G2a verify step (#3290):** `GET /v1/applications/{id}/instances/{instanceId}` omits outputs and error while the grain is resident. Read outputs through the orchestration instance endpoint instead.
>   - **Record the applied migration versions before and after** (from the health or admin surface).
> - **PS-8 (#3256 → `a4b3ffd2f`)** adds `mcp.connectionTools`. It's inert until an app manifest uses it, and v1 doesn't.
> - **Security fixes to ship with G2a:**
>   - PS-15's `_tenantId` fix is in `8c17b48b3`.
>   - Ship these too if they're merged by then: PS-17 (#3255 HIGH, tenant connection `${ENV}` expansion from the host environment; **merged ef1e96478**); PS-20 (#3263, RunContainer argument injection, CRITICAL where the silo mounts `docker.sock`; **merged af96815ee**). Operator-visible: a RunContainer `network:` other than `none` now fails unless listed in `Execution:Container:AllowedNetworks`, which is empty by default; `host` and `container:*` are always denied. Also `entrypoint`, env keys, `image`, `cpus` and `memory` are validated, so check that no prod app uses `network:`. The samples and docs don't. Residual: the container itself isn't isolated (#3278, #3277); PS-18 (#3236 HIGH, confirmed: definition `package:` overrides the run tenant, and #3233; **merged 85deec753**); and PS-16 (#3257, in-run spoofing of reserved identity variables).
>   - **Before G2a, and as a possible immediate mitigation (needs approval):** check whether the prod silo container mounts `/var/run/docker.sock`. Read the compose file on the host; don't print its env values. If it does, removing the mount from the silo closes #3263's host-root path until PS-20 ships. PS-20 closes the flag-injection path, but any author can still start an arbitrary image as a sibling container on the host daemon (#3277, #3278), so removing docker.sock from the silo is still worthwhile defence in depth. First confirm nothing on the silo legitimately needs Docker; RunContainer nodes with compute requirements go to workers. Rollback: restore the mount line from a backup of the compose file and recreate the silo.
>   - **Operator-visible change from PS-18 (#3276, merged `85deec753`):** definitions that give a reserved identity variable a default now have it dropped at Orleans start, with a warning that names the variable only. `app validate`, WebClient publish and `/validate` reject them with `DEFINITION_RESERVED_VARIABLE_DEFAULT`. Bare declarations stay legal. `/orchestrations/{name}/start` and its status and control paths always use the caller's tenant.
>   - As built, only a **verbatim-JSON** definition with a PascalCase `"Package"` key was ever misrouted. `/orchestrations` and `/orchestrations/yaml` normalise to camelCase, so the `package: X` samples were never affected, and nothing relied on package→tenant.
>   - **Read-only pre-deploy check:** list prod definitions through the API as an admin, with no raw SQL, and look for any PascalCase `"Package"` JSON or reserved-name defaults. Also look for in-flight runs whose grain tenant differs from the authenticated tenant. The grain context is persisted, so pre-deploy poisoned runs keep it (#3273 comment). If there are any, report them to the human; don't fix them in the data.
>     - As built (#3281 → `e94298f48`), a run misrouted before the fix leaves the caller's top-level `execution_instance` stuck at `Running` with no events. Its tasks, child rows and events live under the package-named tenant. Nothing is re-keyed or migrated. If the package string is a real tenant id, that tenant can terminate the run through its own API; otherwise the run is inert unless it holds timers. Treat long-`Running`, event-less rows as the symptom, and report them without cleaning them up.
>   - **Operator-visible change from PS-17 (#3268):** the API, Silo and Worker no longer expand `${VAR}` in a tenant MCP connection's `headers` or stdio `environment`. Only the CLI does.
>     - An existing prod MCP connection that relies on `${...}` sends the literal text instead, which probably yields a 401.
>     - Before the deploy, list the MCP connections through the API as an admin and look for `${` in their headers or environment. Move any such value into the connection's secret field, entered by the human.
>     - After the deploy, host logs name any connection that still has a placeholder, by field name, never by value.
>     - An MCP `image` that doesn't match the image-reference pattern (for example, one with surrounding whitespace, or flag-shaped) now fails to build.

1. **Tag rollback images:** tag the current `processstack-*` images `<rollback_tag>` (derived from the current sha). The existing earlier rollback tags are kept as well. Also tag the current runner image and record the dispatcher's configured runner tag.
   - **The runner release is itself a G2a step; do not cut it earlier.** Pushing a `runner-v*` tag (`publish-runner-image.yml`) also moves the **floating** GHCR tags `processstack-runner:alpine` and `:debian`, as well as adding `alpine-<sha>` and `alpine-<version>`. If the prod dispatcher references a floating tag and pulls, publishing is already a prod change.
   - Before tagging, record the dispatcher's runner reference and the current floating tag's digest.
   - Pin the dispatcher to the immutable `alpine-<version>` tag.
   - Roll back to the recorded digest or the previous `alpine-<sha>`.
2. **Deploy:** `git pull` to the release sha, then `deploy/compose-standalone/deploy.sh up`, detached. Then roll the worker host's dispatcher to the new `runner-v*` image.
3. **Verify:**
   - Health is green.
   - The old gateway answers the baseline query.
   - v1 chat "list" works.
   - A real push still indexes through the old publisher: a new `/control/audit` ensure entry shows up.
   - The neutral `custom-activity-demo` sample publishes and runs in a scratch tenant, which proves the runner loads app activities. Then deactivate it.
4. **Rollback:** retag `<rollback_tag>` and redeploy; point the dispatcher back at the previous runner tag.

## G2b: Sextant service (additive and opt-in)
Ships SX-1…SX-8, plus SX-U (MCP SDK 2.2.0) and the hardening follow-ups SX-5b, SX-6c, SX-6d and SX-7b (all merged; step 5 lists the ones this image must contain).

1. **Env settings:**
   - `SEXTANT_SERVICE_CALLER_KEYS=<kid>=<key>@<tenantId>` (multiple entries separated by `;`; kid matches `[A-Za-z0-9._-]{1,64}`; key is **base64url** (padding tolerated), ≥ 32 bytes decoded). Generate the key on the host and never echo it: `openssl rand 48 | base64 -w0 | tr '+/' '-_' | tr -d '='`. The PS connection's `callerIdentity.signingKey` must decode to the **same bytes**. As built, PS-7 (#3219) accepts base64url or base64 decoding to at least 32 bytes, so the same base64url string works on both sides. PS redacts `callerIdentity` on every read, so you can't check it by reading it back; the G2b signed-assertion test is the check. One kid per tenant; never share a kid or key across tenants. The Sextant verifier binds kid → tenant; PS doesn't enforce cross-tenant kid uniqueness (#3239).
   - As built, `dep` is optional in assertions (SX-5b); `tslug`, `app` and `cid` are required, and PS omits the header on runs with no application.
   - `SEXTANT_SERVICE_CALLER_AUDIENCE=sextant`, `SEXTANT_SERVICE_CALLER_APPS=sextant`, `SEXTANT_SERVICE_CALLER_IDPS=processstack`.
   - `SEXTANT_SERVICE_DELEGATE_TOKENS`.
   - Host policy defaults to `github.com`.
   - **`SEXTANT_SERVICE_REPOSITORY_OWNERS`** (**required** by the SX-10 security review whenever the tenant has members other than the owner; `docs/service.md`, "Production deployment checklist"): set it to the `github.com/<owner>` entries of the repositories you actually index, e.g. `github.com/<org>,github.com/<owner>`. Entries are **comma**-separated (a `;` list fails startup), and each entry's host must be on `REPOSITORY_HOSTS`. Without it, a tenant member can make the service index any public repository, or a fork commit under the base URL, which runs untrusted MSBuild on the worker (see also jonlipsky/sextant#209). Only jonlipsky/sextant#76 closes that fully. Check the existing 8 repositories' owners against the list before deploying, so none of them is refused.
   - The legacy `QUERY_TOKEN` stays Unscoped, so the old gateway keeps working. Keep it set: an empty query token with no read policy opens the query plane to anonymous reads.
2. **Deploy:** move the image tag, keeping the previous tag as the rollback. Migration 024 is additive.
3. **Verify:**
   - `/control/metrics` reports 8 repos. The DB schema is **25**, from migrations 024 and 025. Snapshot identity schema stays **24**, since 025 is identity-neutral.
   - **Expect a full re-index.** As built in SX-6 (jonlipsky/sextant#191), the schema bump to 24 changes every snapshot identity hash, so each of the 8 repositories re-indexes on its next ensure of a commit: its next push, or a manual ensure of its current head. The G2c reconcile does **not** trigger it in general: a branch already at GitHub's head counts as up to date (the one exception is a GitHub-default branch the service does not yet mark default, which it ensures to promote, #199). Deploy G2b at low traffic and watch CPU, disk and the ensure queue while the re-index runs. To finish it without waiting for pushes, ensure each repository's current head once (`POST /control/ensure` with the `branch` (as `branch_name`) and `commit_sha` that `/control/resolve` returns, and that same commit as `expected_head_commit`; without the branch the service assumes `main`, the CAS misses and the pointer stays), one at a time. During the re-index, check that queries still return results from the existing snapshots. If they don't, that's a rollback trigger.
   - The old gateway query still works.
   - A signed test assertion minted on the host for the owner succeeds, and a tampered one fails with 401.
4. **Rollback:** go back to the previous image tag. Migration 024 is additive, so the old image ignores the new tables.
5. **Service hardening that must be in this G2b image** (confirm each is merged first):
   - jonlipsky/sextant#193: `act=user` refused on retire and the operational control routes. **Merged** as #197 → `bc1751c`.
   - #198: a user's ensure can't set `default_branch` / `branch_head_sequence` or advance unguarded, and startup fails without a control token (SX-6d, jonlipsky/sextant#202, **merged** `8aee3c03`). **Before deploy, check that the prod env sets the control token** (it should, since the v1 app uses it). Check only that it's non-empty; never print it. Since SX-6d, a missing or whitespace-only token makes the service exit 1 at startup instead of running with an open control plane. **Never** set `SEXTANT_SERVICE_INSECURE_OPEN_CONTROL_PLANE` in prod. After deploy, confirm the container is running and `/control/metrics` answers with the token.
   - #199 (SX-6d): a user ensure gets the first-default branch only when the service's own `git ls-remote --symref` confirms that branch is the remote HEAD. That needs **clone mode** plus outbound git to github.com. Before G2c, confirm prod runs clone mode, not locate mode. In locate mode a user's first ensure never becomes the default, so the app's application-caller ensure or the G2c reconcile has to set it. The 8 existing repositories already have defaults and aren't affected.
   - #196: `search_symbols` per-call cost bounds (SX-7b, jonlipsky/sextant#200, **merged** `aec5d4ac`). Migration 025 only adds indexes and is identity-neutral: snapshots fold `SnapshotSchemaVersion` = 24 and `LatestSchemaVersion` = 25. So there's **no second re-index**, but `/control/metrics` / readiness should report DB schema **25** after deploy. Cursor v2 means any `search_symbols` cursor held across the deploy returns `invalid_cursor`, and the client re-queries. New optional env `SEXTANT_SERVICE_SEARCH_MAX_HITS` defaults to 500, so leave it unset.

## G2c: the app
1. **Register connections in tenant `<tenant>`.** Secrets are entered via the UI by the human, or piped from a host file by the operator's remote agent without printing. Name the two Sextant connections **exactly** `sextant-query` and `sextant-control`: a deployment that does not bind them resolves them by name (lazy binding, step 2).
   - **`sextant-query`** (`type: mcp`, http): URL `<sextant-service-url>/mcp`, a delegate token header, and `callerIdentity {mode: signed-header, audience: sextant, keyId: <kid>, signingKey: <same key>}`.
   - **`sextant-control`** (`type: http-api`): baseUrl `<sextant-service-url>`, bearer CONTROL token, plus the same `callerIdentity`, **with the same explicit `keyId`**. F4 defaults `keyId` to the connection instance id, so if you leave it unset the two connections end up with two different kids.
   - Use an `https://` `<sextant-service-url>` unless the service is reachable only on a private network: both connections carry a bearer and a signed caller assertion on every request, and the service itself serves plain HTTP (terminate TLS in front of it).
   - **`github`:** the existing connection, unchanged. Its webhook must include **`push`, `delete` and `pull_request`**; `create` is not used by the app. Check this in the GitHub App settings; if any is missing, the human adds it.
2. **Publish v2.0.0 from the sextant repo, then move the existing deployment onto it.** v2.0.0 bundles its own activities (G1-A). Build them first with `apps/processstack/sextant/build.sh`. Publish needs PS-15 on prod, the runner image and `CustomActivitiesEnabled` from G2a.
   > **Deployment mechanics** (verified in PS code at `e1fbd1590`; the dev-stack rehearsal must confirm them before G2c):
   > - **v2's `github` dependency is required**, because its connection-event triggers name it (`ConnectionDependencyEnumerator`). As a result, **`processstack app activate sextant` refuses** (`DeploymentBindingRequiredException`). It only ever manages the empty-binding tenant-global deployment `app:{appId}`. Never run `app activate` for this app.
   > - The app runs through a **bound deployment**, which the WebClient deploy dialog or `POST .../deployments` creates. `sextant-query` and `sextant-control` are optional dependencies. When a deployment doesn't bind them, they resolve **by name** to tenant connections with exactly those names (`ConnectionDefinitionResolver`, "lazy binding").
   > - **Publish moves latest-mode deployments to v2.0.0 immediately**, with their existing bindings (`UpgradeLatestModeDeploymentsOnPublishAsync`). Pinned deployments stay where they are.
   > - The surfaces that read the asset's **active version** switch at publish, whatever mode the deployment is in. These are the MCP surface, the chat provisioner and per-turn prompt pickup, and connection-trigger queries. Schedule and connection-event registrations follow the **deployment's** version.
   - **2a. Record the current deployment.** Run `processstack app deployment list --id <app-id>`, then `app deployment get --id <app-id> --deployment <id>`. Save the deployment id, `applicationVersion` (expected `1.0.1`), version mode, bindings and enabled flag to an operator file. These are connection ids, not secrets.
   - **2b. If the deployment is `latest`, pin it first:** `processstack app deployment set --id <app-id> --deployment <id> --mode pinned`. It stays on 1.0.1.
   - **2c. Validate, test, pack-check and publish.** Run `processstack app validate|test|publish apps/processstack/sextant`. The active version becomes 2.0.0, so chat and MCP now use v2, while the triggers stay on v1 until 2d.
   - **2d. Move the deployment onto 2.0.0 straight away:** run `processstack app deployment upgrade --id <app-id> --deployment <id>`, or use the WebClient deploy dialog. Confirm its bindings: `github` → the existing GitHub connection, and `sextant-query`/`sextant-control` bound or resolvable by name. Then restore the recorded version mode with `--mode latest` if it was latest.
   - **2e. Check.** `app deployment get` shows `applicationVersion 2.0.0`, the registrations list the 5 GitHub triggers plus `nightly-reconcile`, and there is **exactly one** enabled deployment. No `app:{appId}` tenant-global deployment should exist, because a second deployment double-registers the schedule.
   - **Pack from a clean checkout.** `ApplicationPacker` zips *every* file under the app directory, with no exclusions (#3284): sources, tests, `bin/`/`obj/`, and any local `.env`. Publish from a fresh clone of the merged tag, not a dev worktree. Before publishing, `app pack` and list the zip entries: no `.env*`, `*.user`, `appsettings.Development.json` or tokens, and only the expected activity assemblies. SX-9 builds `bin/`/`obj/` into `apps/processstack/artifacts/`, which is outside the app dir. The activity bundle itself lives in `activities/sextant/` (build.sh), which *is* in the bundle.
   - The same app name makes this a new version of the existing asset. The old *gateway* stays live because it is platform code that reads the legacy stores, and v2 keeps writing those stores (dual-write).
3. **Migrate:**
   - **Watches.** The owner runs `import-legacy-watches`, which executes as the owner, reads the owner's own legacy user memory, and writes grants through the app. Expect 8 imported.
   - **Enrolled repos.** Fire the app's `nightly-reconcile` schedule immediately with the generic trigger run-now endpoint, `POST /v1/<tenant>/triggers/run/{triggerId}` (`TriggersController.cs:399-416`).
     - Run-now re-enqueues the exact recurring-job definition (`HangfireSchedulerService.RunNowCore`), so it runs as `act=application`.
     - Its legacy import step converts the legacy `enrolled/*` rows into tenant grants. Expect N.
     - The app needs no extra schedule for this.
   - **Check:** the Sextant grants list for the owner shows 8 repos.
4. **Keys.** The human mints a live-bounded key: `processstack api-key create --name sextant-agents --app sextant`. The human also rotates the leaked host key (`<leaked-key-prefix>`) into a live-bounded replacement and revokes the old one.

## Validation (all must pass)

| Check | Expected |
|---|---|
| Chat "list" (web chat or CLI, not Slack) | 8 repos |
| Chat "watch"/"list" on Slack | "Watch management isn't enabled for this channel" reply; no service call (app.md scenario 32) |
| `search_symbols` (federated) on the new path | Returns results. The old gateway's `sextant.search_symbols` may already be broken against the real service because of a `kind` int/string DTO mismatch, so there is no latency baseline for it |
| Chat "watch" on a new repo, then "unwatch" | Works, and the new repo shows up in Sextant grants and in the legacy memory |
| A real push to `<baseline-repo>` main | Within 5 min, a new ensure in Sextant `/control/audit` with the push's `after` sha; exactly one, no duplicates |
| PR opened | Head and base ensures |
| Agent query with the new key: `tools/list` on `/v1/<tenant>/mcp/sextant` | Only allow-listed tools |
| `find_symbol` + `find_references` on `<baseline-repo>` | Correct results; p50/p95 recorded next to the baseline |
| A repo the owner has no grant for | Excluded or rejected; not leaked |
| Old key without `mcp:run:sextant` | 403 with a diagnosable body; no #3138-style silent stale key |
| Nightly reconcile (next run) | Completes; re-ensures stale heads only |

**Soak:** at least 48h with both paths live, including at least one real push and at least one agent session.

## Rollback (any time before G3)
1. **App.** CLI 1.1.1 has no `activate --version`, and **never use `app version rollback --id … --to …`**: it publishes a manifest-only version that drops the bundle and the triggers (#3306).
   - **1a.** Run `processstack app rollback sextant 1.0.1`. This calls `POST /v1/{tenant}/applications/{id}/rollback`, which copies version id `<v1.0.1-version-id>`'s content, files and bundled definitions into a new, auto-bumped version (for example `2.0.1`) and makes it active. Chat and MCP switch to v1 content at once.
   - **1b.** Rollback does **not** re-point deployments, so run `processstack app deployment upgrade --id <app-id> --deployment <id>` to move the deployment onto the new version. Alternatively, `PUT .../deployments/{id}` with `{applicationVersion, bindings}` restores the bindings recorded in 2a exactly. Check it with `app deployment get`.
   - Because v2 dual-writes, the legacy memory and App State are current, so v1 resumes with the same data. v1.0.1 bundles no activities, so rollback doesn't depend on the runner image.
   - **Re-cutover later** repeats steps 2a–2e (pin a latest-mode deployment before publishing), publishing a version above the rollback version at 2c, for example `app publish --version 2.0.2`.
2. **Connections:** disable `sextant-query` and `sextant-control`, or leave them; v1 doesn't use them.
3. **Service:** the previous image tag. **Platform:** the `<rollback_tag>` tags.
4. **Keys:** the old snapshot key stays valid until G3, so agents can fall back to `_sextant`.

## Overlap and downtime
- **Target: zero downtime.** The old gateway, v1 data and old keys stay live through G3.
- **Only user-visible change at G2c publish (step 2c):** chat turns are served by v2. It has the same commands, and the prompt-trigger per-turn version pickup applies at the next turn.

## After G3 (ops; not part of G2)
Run these only once PS-11 is deployed and nothing uses the old gateway:
- **Require a repository selection:** set `SEXTANT_SERVICE_REQUIRE_REPOSITORY_SELECTION=true`. A read with no `repository` argument and no `X-Sextant-Repository` header then fails with `repository_required`. Delegate callers are unaffected: a verified caller already needs a selection, and implicit selection (the caller's one visible repository) still applies.
- **Retire the legacy query token by replacing it, never by unsetting it.** With no `SEXTANT_SERVICE_QUERY_TOKEN` and no `SEXTANT_SERVICE_READ_POLICY`, the query plane is open to anonymous reads (the service only logs a startup warning). Set a fresh random value that only a trusted federation peer, if any, is given, or configure a read policy. See `docs/service.md`, "After the cutover (G3), not before".

