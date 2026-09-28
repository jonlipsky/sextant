# Prod cutover and rollback runbook (Phase 4, G2)

> **Approved at G1 (2026-09-27). Tracking: elevenworks/ProcessStack#3159.** Lives in `jonlipsky/sextant` `specs/20260927-processstack-app-extraction/`.
> Each prod step runs on `<prod-host>` through the operator's approved remote-execution channel, and needs the human's explicit G2 approval for that sub-gate. Long jobs are detached with `setsid`. Secrets are generated on the host and never printed.
> Prod-specific values (`<prod-host>`, `<platform-checkout>`, `<tenant>`, `<sextant-service-addr>`, `<rollback_tag>`, `<leaked-host-key>`, `<v1-version-id>`) are placeholders. The operator holds the concrete values.

## Baseline (record before G2a)

| Item | How |
|---|---|
| Platform image tags / sha | `docker ps --format` + `git -C <platform-checkout> rev-parse HEAD` |
| Sextant service image + schema (expect 23) + repo count (expect 8) | `/control/metrics` |
| Old-path latency baseline | 20× `find_symbol` + 20× `find_references` against elevenworks/processstack through `/v1/<tenant>/mcp/_sextant` → p50/p95 |
| Owner's watched repos (expect 8) | the v1 chat "list" turn, **through the app** |
| Enrolled repos | the v1 app's App State listing, read through the app's own `get-indexing-status`/list surface or the generic App State read API **as the app**; no raw SQL |

## G2a: platform features (additive)
Ships PS-2…PS-8. The old path is untouched.

1. **Tag rollback images:** tag the current `processstack-*` images `<rollback_tag>` (derived from the current sha). The existing earlier rollback tags are kept as well.
2. **Deploy:** `git pull` to the release sha, then `deploy/compose-standalone/deploy.sh up`, detached.
3. **Verify:**
   - Health is green.
   - The old gateway answers the baseline query.
   - v1 chat "list" works.
   - A real push still indexes through the old publisher: a new `/control/audit` ensure entry shows up.
4. **Rollback:** retag `<rollback_tag>` and redeploy.

## G2b: Sextant service (additive and opt-in)
Ships SX-1…SX-8.

1. **Env settings:**
   - `SEXTANT_SERVICE_CALLER_KEYS=<kid>=<key>@<tenantId>`; the key is base64url, generated on the host with `openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n'`, and never echoed.
   - `SEXTANT_SERVICE_CALLER_AUDIENCE=sextant`, `SEXTANT_SERVICE_CALLER_APPS=sextant`, `SEXTANT_SERVICE_CALLER_IDPS=processstack`.
   - `SEXTANT_SERVICE_DELEGATE_TOKENS`.
   - Host policy defaults to `github.com`.
   - The legacy `QUERY_TOKEN` stays Unscoped, so the old gateway keeps working.
2. **Deploy:** move the image tag, keeping the previous tag as the rollback. Migration 024 is additive.
3. **Verify:**
   - `/control/metrics` reports schema 24 and 8 repos.
   - The old gateway query still works.
   - A signed test assertion minted on the host for the owner succeeds, and a tampered one fails with 401.
4. **Rollback:** go back to the previous image tag. Migration 024 is additive, so the old image ignores the new tables.

## G2c: the app
1. **Register connections in tenant `<tenant>`.** Secrets are entered via the UI by the human, or piped from a host file by the operator's remote agent without printing.
   - **`sextant-query`** (`type: mcp`, http): URL `https://<sextant-service-addr>/mcp`, a delegate token header, and `callerIdentity {mode: signed-header, audience: sextant, keyId: <kid>, signingKey: <same key>}`.
   - **`sextant-control`** (`type: http-api`): baseUrl `https://<sextant-service-addr>` (plain `http://` only on a private network: both connections carry a bearer token and a signed caller assertion), bearer CONTROL token, plus the same `callerIdentity`, **with the same explicit `keyId`**. F4 defaults `keyId` to the connection instance id, so if you leave it unset the two connections end up with two different kids.
   - **`github`:** the existing connection, unchanged. Its webhook must include **`push`, `delete` and `pull_request`**; `create` is not used by the app. Check this in the GitHub App settings; if any is missing, the human adds it.
2. **Publish and activate v2.0.0 from the sextant repo:**
   - Commands: `processstack app validate|test|publish apps/processstack/sextant`, then bind the connections (github, sextant-query, sextant-control) in the WebClient deploy dialog (the CLI sets no bindings) and run `processstack app activate sextant`.
   - The same app name makes this a new version of the existing asset. **Note:** activation replaces v1.0.1 as the active version, so the chat and triggers move to v2. The old *gateway* stays live because it is platform code that reads the legacy stores, and v2 keeps writing those stores (dual-write).
3. **Migrate:**
   - **Watches.** The owner runs `import-legacy-watches`, which executes as the owner, reads the owner's own legacy user memory, and writes grants through the app. Expect 8 imported.
   - **Enrolled repos.** Fire the app's `nightly-reconcile` schedule immediately with the generic trigger run-now endpoint, `POST /v1/<tenant>/triggers/run/{triggerId}` (`TriggersController.cs:399-416`).
     - Run-now re-enqueues the exact recurring-job definition (`HangfireSchedulerService.RunNowCore`), so it runs as `act=application`.
     - Its v2.0.x legacy enrollment import (after the grants listing, which proves the caller) converts the legacy `enrolled/*` rows into tenant grants. Expect N (the run's log line "N of N enrolled repositories are tenant grants").
     - The app needs no extra schedule for this.
   - **Check:** the Sextant grants list for the owner shows 8 repos.
4. **Keys.** The human mints a live-bounded key: `processstack api-key create --name sextant-agents --app sextant`. The human also rotates the leaked `<leaked-host-key>` host key into a live-bounded replacement and revokes the old one.

## Validation (all must pass)

| Check | Expected |
|---|---|
| Chat "list" | 8 repos |
| `search_symbols` (federated) on the new path | Returns results. The old gateway's `sextant.search_symbols` may already be broken against the real service because of a `kind` int/string DTO mismatch, so there is no latency baseline for it |
| Chat "watch" on a new repo, then "unwatch" | Works, and the new repo shows up in Sextant grants and in the legacy memory |
| A real push to `elevenworks/processstack` main | Within 5 min, a new ensure in Sextant `/control/audit` with the push's `after` sha; exactly one, no duplicates |
| PR opened | Head and base ensures |
| Agent query with the new key: `tools/list` on `/v1/<tenant>/mcp/sextant` | Only allow-listed tools |
| `find_symbol` + `find_references` on elevenworks/processstack | Correct results; p50/p95 recorded next to the baseline |
| A repo the owner has no grant for | Excluded or rejected; not leaked |
| Old key without `mcp:run:sextant` | 403 with a diagnosable body; no elevenworks/ProcessStack#3138-style silent stale key |
| Nightly reconcile (next run) | Completes; re-ensures stale heads only |

**Soak:** at least 48h with both paths live, including at least one real push and at least one agent session.

## Rollback (any time before G3)
1. **App:** `processstack app rollback sextant 1.0.1`, then `processstack app activate sextant`. `app activate` has no `--version`; the rollback publishes 1.0.1's content (version id `<v1-version-id>`) as a new, auto-bumped version. Because v2 dual-writes, the legacy memory and App State are current, so v1 resumes with the same data. After a later re-activation of v2, run `import-legacy-watches` again: its flag survives the rollback.
2. **Connections:** disable `sextant-query` and `sextant-control`, or leave them; v1 doesn't use them.
3. **Service:** the previous image tag. **Platform:** the `<rollback_tag>` tags.
4. **Keys:** the old snapshot key stays valid until G3, so agents can fall back to `_sextant`.

## Overlap and downtime
- **Target: zero downtime.** The old gateway, v1 data and old keys stay live through G3.
- **Only user-visible change at G2c activation:** chat turns are served by v2. It has the same commands, and the prompt-trigger per-turn version pickup applies at the next turn.
