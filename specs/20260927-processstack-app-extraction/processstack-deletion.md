# ProcessStack deletion plan (PS-11, G3)

> **Approved at G1 (2026-09-27). Tracking: elevenworks/ProcessStack#3159.** This file names Sextant on purpose, so it lives in `jonlipsky/sextant` `specs/20260927-processstack-app-extraction/`, not in ProcessStack specs. The PS-11 PR body links here and to elevenworks/ProcessStack#3159.
> Inventory verified at ProcessStack `origin/main` `0f30c358f`. Re-run the two regeneration commands on the PS-11 base before starting.
> Prod-specific values (`<prod-host>`, `<platform-checkout>`, `<tenant>`, `<rollback_tag>`, `<leaked-key-prefix>`) are placeholders. The operator holds the concrete values.
> Issue and PR numbers: a bare `#NNNN` of 3000 or more is in elevenworks/ProcessStack.
> **Status (as built, 2026-09-28):** not started. PS-11 opens only after the G2c soak; the "As-built refresh" at the end updates the inventory against a newer ProcessStack `main`.

## Preconditions
Each item below must hold before PS-11 is opened for merge:
- **Traffic:** the v2 Sextant app has been active in prod since G2c, it has passed validation, and it has soaked for 48h or more. No traffic hits `/v1/<tenant>/mcp/_sextant` in the Sextant `/control/audit` and platform logs for the last 24h.
- **Migration:** every legacy watch has been imported: the v2 `import-legacy-watches` report shows 0 pending for the owner.
- **Prerequisite PRs:** PS-3 (grant pruning), PS-5 (GitHub repository events) and PS-8 (connection-tool exposure) are merged and deployed.
- **Approval:** the human has given G3 approval.

## 1. Delete whole files and directories

| Path | Notes |
|---|---|
| `src/ProcessStack.Activities.Sextant/` (whole project, 42 files) | Activities, gateway, WatchedRepo, audit, options |
| `src/ProcessStack.Api/Controllers/SextantMcpController.cs`, `SextantGatewayControlController.cs` | `/v1/{tenant}/mcp/_sextant` + control |
| `src/ProcessStack.Api/Mcp/SextantMcpProxy.cs`, `SextantEnrolledRepoSource.cs`, `ISextantEnrolledRepoSource.cs` | |
| `src/ProcessStack.Core/Applications/ReservedApplicationSlugs.cs` | Only member is the gateway segment |
| `src/ProcessStack.Connections.GitHub/RepositoryEvents/` (4 files) | App-shaped publisher, replaced by GitHub repository connection events |
| `src/ProcessStack.Activities.GitHub/GitHubConnectionBranchHeadSource.cs` | Implements an app interface |
| `tests/ProcessStack.Activities.Sextant.Tests/` | |
| `tests/ProcessStack.Api.Tests/Integration/Sextant*.cs` (12) and `tests/ProcessStack.Api.Tests/Mcp/SextantEnrolledRepoSourceTests.cs` | |
| `tests/ProcessStack.Core.Tests/Applications/SextantWatchedRepoSampleTests.cs` | |
| `tests/**/RepositoryEventPublisherTests.cs`, `GitHubRepositoryEventMapperTests.cs`, `GitHubConnectionBranchHeadSourceTests.cs` | |
| `samples/applications/sextant/` | Now lives at `jonlipsky/sextant` `apps/processstack/sextant/` |
| `docs/SEXTANT_INTEGRATION.md`, `docs/SEXTANT_REDEPLOY_RUNBOOK.md` | Delete. A summary (not a copy) is in `jonlipsky/sextant` [`specs/20260922-processstack-query-gateway-archive.md`](../20260922-processstack-query-gateway-archive.md); the durable Sextant docs are `docs/service.md` and `docs/onboarding.md`. The originals stay in ProcessStack git history |
| `specs/20260922-sextant-query-gateway/` | Delete. Summarized in the same archive file (the spec is private, so it is not copied); the original stays in ProcessStack git history |

**Keep, per the history exemption:** the four `Migration_3_70_0_SextantEnrolledRepos` / `Migration_3_71_0_RemoveSextantEnrolledRepos` files (Postgres and SQLite). The 3.71 migration already drops the table. No new migration is added.

## 2. Edit these files

| File | Edit |
|---|---|
| `ProcessStack.slnx:22,89`, `no-macos.slnx:28,94` | Remove the two project entries |
| `src/ProcessStack.Api/ProcessStack.Api.csproj:61` | Remove the ProjectReference |
| `src/ProcessStack.Composition/ProcessStack.Composition.csproj:44` | Remove the ProjectReference |
| `src/ProcessStack.Composition/PlatformComponentCatalog.cs:14,77,137` | Remove the using, the activity-assembly entry and the DI registration |
| `src/ProcessStack.Activities.GitHub/ProcessStack.Activities.GitHub.csproj:24` | Remove the ProjectReference |
| `src/ProcessStack.Activities.GitHub/ServiceCollectionExtensions.cs` | Remove the branch-head-source registration |
| `src/ProcessStack.Connections.GitHub/ServiceCollectionExtensions.cs` | Remove the publisher registration |
| `src/ProcessStack.Connections.GitHub/GitHubConnectionProvider.cs:240-252` | Remove the publisher call; the generic GitHub repository-event mapping stays |
| `src/ProcessStack.Api/Program.cs:32,489-503` | Remove the using and the gateway options/proxy/source registrations |
| `src/ProcessStack.Api/Authorization/PermissionRegistration.cs:123-128` | Remove the `mcp:sextant:read` registration |
| `src/ProcessStack.Api.Contracts/Permissions/PermissionPresets.cs:59-61` | Remove the "Sextant search" preset |
| `src/ProcessStack.Core/Applications/ApplicationManifestValidator.cs:222-225` | Remove the reserved-slug check |
| `src/ProcessStack.Api/Controllers/ApplicationAssetsController.cs:1013`, `VersioningEndpoints.cs:422` | Remove the reserved-slug checks |
| `src/ProcessStack.Api/Services/PlatformEventDispatcherService.cs:393`, `PlatformEventTriggerRegistry.cs:198`, `ApplicationDeploymentsController.cs:~639-644` | Remove the app-named comments; PS-9 should already have done this |
| `src/ProcessStack.Cli/Commands/Application/TestApplicationRuntimeFactory.cs:98-163`, `TestHarnessApplicationDeploymentRepository.cs` | Remove the app-specific harness wiring; scenario connection stubs replace it |
| `tests/ProcessStack.Api.Tests/*.csproj`, `tests/ProcessStack.Core.Tests/*.csproj` | Remove the ProjectReferences |
| Tests that assert on removed items (`PermissionResolverTests`, `PermissionSetTests`, `ApplicationManifestValidatorTests`, `ApplicationMcpExposureResolverTests`, `SampleApplicationTests`, `PlatformEventDispatcherServiceTests`, `ApplicationDeploymentsControllerTests`, `ApplicationLifecycleTests`) | Delete the app-specific cases; keep the generic ones |
| `docs/prompt-trigger.md` | Replace the app example with a neutral one |
| `tests/ProcessStack.Conventions.Tests/PlatformAppBoundaryAllowList.txt` | Remove every Sextant entry and lower `MaxAllowListEntries` by the same count. Sextant is added to a `StrictApplicationNames` set that the allow-list may never contain |

After the change, both of these must return nothing except the four exempt migration files:
- `git grep -il sextant -- src`
- `git ls-files | grep -i sextant | grep -v '^specs/archived'`

## 3. Grant cleanup (generic)
Run these after the PS-11 image is deployed, with prod dispatch done through the operator's approved remote-execution channel under G3.

1. **Dry run:**

       processstack permissions prune-unregistered --only mcp:sextant:read

   Expect exactly the known holders: the preset-derived roles, if any, and snapshot keys such as the gateway key. Record the list.
2. **Check the list.** Confirm that no other grant appears. `--only` guarantees this.
3. **Execute:**

       processstack permissions prune-unregistered --only mcp:sextant:read --execute

   This emits audit events. Re-run the dry run and expect it to be empty.
4. **Retire the old gateway key(s)** through the normal API-key revoke endpoint, as the human owner. Separately, rotate the leaked host key `<leaked-key-prefix>`: mint a live-bounded replacement and revoke the old key.

## 4. Prod compose
- On `<prod-host>`, remove the uncommitted `SextantGateway__*` environment block from `<platform-checkout>/docker-compose.yml` (a local edit).
- Take a backup copy first, `docker-compose.yml.bak-<date>`. That copy is the rollback.

## 5. Redeploy and rollback
1. **Tag rollback images:** `docker tag processstack-<svc>:latest processstack-<svc>:<rollback_tag>` (derived from the pre-PS-11 sha).
2. **Deploy:** `deploy/compose-standalone/deploy.sh up`, detached via `setsid`.
3. **Verify:**
   - The v2 app still works: chat list, one query, one push.
   - `/v1/<tenant>/mcp/_sextant` now returns 404.
   - The health checks pass.
4. **Rollback:** retag `<rollback_tag>` → `latest`, restore the compose backup, and redeploy. v2 keeps working on the old platform image too, because PS-11 only deletes things.

## 6. Post-delete
- The app ships v2.1: it stops dual-writing the legacy stores, and it cleans the legacy user-memory and App State keys, including the service URL/token that v1 kept in App State, **through the app**.
- PS-12 (enforce): close elevenworks/ProcessStack#3159 and elevenworks/ProcessStack#3138 with evidence.

## As-built refresh (2026-09-28, against ProcessStack `origin/main` `8db9546f6`)
- **Counts:**
  - 51 Sextant-named `src` files and 46 Sextant-named test files, unchanged;
  - 32 other `src` files mention Sextant, up from 24. The increase is mostly comments added by earlier Sextant-era work plus the PS-5 wiring comments. PS-11 re-runs the scan at its base and treats that result as authoritative.
- **`RepositoryEvents/` is safe to delete whole.**
  - PS-5's generic repository connection events (#3210) are produced by `GitHubEventHandler.Map`, not by `RepositoryEvents/`.
  - In `GitHubConnectionProvider.ProcessWebhookAsync`, remove the `GitHubRepositoryEventMapper.Handles` side-effect branch, `PublishRepositoryEventAsync`, and the `IRepositoryEventPublisher` resolution. Keep the `GitHubEventHandler.Map` return unchanged.
  - Delete `tests/ProcessStack.Connections.GitHub.Tests/{GitHubRepositoryEventMapperTests,RepositoryEventPublisherTests}.cs`.
  - Keep and pass PS-5's `repo-activity-log` tests; they prove the generic path is intact.
- **Comment-only edits the list above misses:**
  - `src/ProcessStack.Composition/PlatformServiceCollectionExtensions.cs:53`, the comment explaining that `IRepositoryEventPublisher` depends on `IPlatformEventPublisher`. Keep the registration if other consumers need it, and reword the comment generically.
  - `src/ProcessStack.Silo/Program.cs:323`, the same kind of comment.
- **Deploy vars:** also remove the `SEXTANT_*` / `SextantGateway__*` api env vars that #3202 committed to `deploy/compose-standalone`. That is a separate PR, so first check that it merged.
- **Expected side effect:** #3145, where `DateTimeUtcConventionTests` is red because of Sextant `DateTimeOffset` members, should go green. Verify it, and close #3145 with a `Fixes`.
- **Boundary test:** after the deletion the allow-list must have **zero** `sextant` entries (only the 4 permanent historical-migration exemptions remain), and `sextant` moves into `StrictApplicationNames` in PS-12.
