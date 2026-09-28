# Security

> **Approved at G1 (2026-09-27). Tracking: elevenworks/ProcessStack#3159.** This covers the service-side security model for the app extraction. PS-side signing is PS-7 (F4), ProcessStack-side spec `20260927-outbound-caller-identity`. PS stamps an immutable `RunCaller` at run start. It is never derived from the `senderId` variable, which connection-event runs set to the external peer id (e.g. a GitHub login that could equal a PS user ULID).
> Prod-specific values (addresses, ids) appear as `<placeholders>`. The operator holds the concrete values.

## Trust boundaries and credentials

| Credential | Held by | Presented on | Grants |
|---|---|---|---|
| Control token (`SEXTANT_SERVICE_CONTROL_TOKEN`) | PS secret store (`sextant-control` connection); service env | `/control/*` | Full control plane. **Required:** the service refuses to start without it unless the explicit, loudly logged `SEXTANT_SERVICE_INSECURE_OPEN_CONTROL_PLANE=true` dev opt-out is set (SX-6d, #198). **Assertion-less calls stay full-power** until SVC-17 (N now; **R before a second app or tenant**) |
| Legacy query token (`QUERY_TOKEN`) | Old PS gateway (until G3) | `/mcp`, `/query/*` | **Unscoped** read of every repository (today's behavior, kept until G3). After G3 it is **replaced** with a value no client holds (or a `READ_POLICY` is set), never unset: with no query token and no read policy the query plane is open to anonymous reads (`docs/service.md`, "After the cutover (G3), not before") |
| Delegate token (`DELEGATE_TOKENS`, new) | PS secret store (`sextant-query` connection) | `/mcp`, `/query/*` | **Nothing by itself.** Reads need a valid assertion, and data visibility = grants |
| Caller key (`CALLER_KEYS`, new) | PS secret store (`callerIdentity.signingKey` on both connections); service env | Signs/verifies `X-ProcessStack-Caller` | Binds a caller to **one tenant** |
| Checkout token (`CHECKOUT_TOKEN`) | Service env only | GitHub (clone) | Read on every repo the PAT reaches (#111) |

**Transport.** The PS↔service hop is the internal compose network (`<sextant-service-addr>`, plain HTTP). Tokens and assertions are bearer material and must not leave that network. TLS or mTLS is required before any cross-host deployment (N).

## SSRF / clone host policy (SVC-5)

| Control | Where | Evidence / status |
|---|---|---|
| Top-level URL: https only; host on allow-list (default `github.com`); no IP literal, localhost, single label, trailing dot, userinfo, port, query or fragment; `/owner/repo[.git]` only | `RepositoryUrlPolicy`, at ensure/grant/retire intake and the `repository` tool arg | Exists (SX-1, #186): intake refuses the URL before any job row exists. Git's own transport list is unchanged (`AllowedGitProtocols` in `CloningCheckoutProvider.cs`), so the intake policy is the gate |
| Commit is a hex object id; URL has no embedded credentials | Clone provisioning | Exists: `CloningCheckoutProvider.cs:285-304` |
| Submodules: `.gitmodules` `path`/`url` only; https / relative / same-host ssh→https; allow-listed extra hosts are anonymous | `SubmoduleUrlPolicy` | Exists: `src/Sextant.Service/SubmoduleUrlPolicy.cs`; `SEXTANT_SERVICE_SUBMODULE_HOSTS` |
| The token never reaches argv/files; it is sent only to the repository's own host; redirects are not followed | Git env config | Exists (#125; `CLAUDE.md`, clone-mode rule) |
| Owner allow-list `REPOSITORY_OWNERS` (comma-separated `host/owner`) | `RepositoryUrlPolicy` | Exists (SX-1). **R in production whenever a tenant has members besides its owner** (SX-10 security review): without it any member can index any public repository on an allowed host, and indexing runs that repository's MSBuild on the worker. It does not stop a fork commit under an allowed base URL (#209); only #76 closes that |
| Which Sextant service PS calls | Operator-set connection URL | Replaces PS `SextantServiceHostRouter` (deleted with PS-11). Nothing is caller-controlled |

**Rationale.** The service is the only component that fetches, so policy lives there. The app does a *pre-check* (`GetRepository`) for UX and #111, but this is not a security boundary.

## Caller-assertion verification (SVC-3). This is a checklist, and each item needs a test.

| # | Check | Failure |
|---|---|---|
| 1 | The header appears at most once, is ≤ 8 KiB, and has 3 base64url segments | 401 |
| 2 | `alg` is exactly `HS256`. Reject `none`, any other HS, any asymmetric alg, and any `jku`/`jwk`/`x5u`/`crit` header | 401 |
| 3 | `kid` is in `CALLER_KEYS` (the unknown kid is not echoed) | 401 |
| 4 | HMAC-SHA256 is verified with `CryptographicOperations.FixedTimeEquals`, before any claim is trusted | 401 |
| 5 | `aud` equals `CALLER_AUDIENCE`; `iss` is in `CALLER_ISSUERS` if configured | 401 |
| 6 | `nbf - 60 ≤ now ≤ exp + 60`; `iat ≤ now + 60`; `exp - iat ≤ 300` | 401 |
| 7 | **`tid` == the tenant bound to `kid`** | 401, and the kid is logged as a possible key misuse |
| 8 | `act ∈ {user, application}`. `idp` and `sub` are both present iff `user`. `jti` is present; `via` is known | 401 |
| 8a | **`sub` namespace matches `idp`.** `processstack` ⇒ `sub` has no `:`. Any other `idp` ⇒ `sub = {idp}:{connectionInstanceId}:{peerId}` | 401 |
| 8b | `act=user` ⇒ `idp ∈ CALLER_IDPS` (default `processstack`) | 403 `caller_not_allowed` |
| 8c | `app ∈ CALLER_APPS` when set (prod: `sextant`). Applies to every assertion-bearing request, so another app bound to the same connections can use neither grant routes nor delegate reads | 403 `caller_not_allowed` |
| 9 | The assertion is accepted only with a delegate or control bearer. Presented with a legacy query token or a `READ_POLICY` principal → 401 | 401 |
| 10 | Principal fields come **only** from verified claims. Bodies or args naming `tenant_id`/`principal`/`sub` → 400 | 400 |

**Logging and errors:**
- Logs record the reason code, `kid` and `jti` only. The raw token, signature and key are never logged.
- The response body is the generic `invalid_caller_assertion`.
- Startup fails closed on: a key shorter than 32 bytes, a duplicate kid, a missing audience, delegate tokens without keys, a delegate token equal to another token, or a malformed `CALLER_IDPS`/`CALLER_APPS` entry.

**Rotation:**
1. Add `kid2=<key2>@<tid>` to `CALLER_KEYS` and restart.
2. Update both PS connections to `keyId: kid2` / `signingKey: key2`.
3. Remove `kid1` after `ttl + skew` (≤ 6 min).

**Replay.** A captured (delegate bearer, assertion) pair is replayable within ≤ 6 min, only on the internal network. `jti` single-use (SVC-18, N) closes this. PS-7 mints one assertion per request, so this is safe to add.

## Tenant binding

| Rule | Why |
|---|---|
| One `kid` maps to exactly one `tid`; several kids per tenant are allowed | A leaked key can only impersonate users of its own tenant |
| Every grant row carries `tenant_id` from the verified `tid`. Lookups always filter by it | The same user id or repository in another tenant is invisible |
| Application-actor calls touch only `principal='*'` rows of their own tenant | A trigger cannot read or write a user's personal grants |
| `GET /control/grants?scope=tenant` returns counts and sources, never user ids | Reconcile needs targets, not people |
| A future second PS tenant gets its **own** kid and key | Isolation relies on the kid→tid binding, not on the shared delegate token |
| Connection-event, schedule, run-now, webhook and platform-event runs are `act=application` (PS-7 `RunCaller`) | A GitHub push whose sender login equals a PS user id can never act as that user |

## Identity providers (`idp`)

| Rule | Detail |
|---|---|
| Grants key on the full `sub` | `processstack` users key on the platform user id. External peers key on `{idp}:{connectionInstanceId}:{peerId}`, so there is no collision (check 8a) and no cross-idp linking |
| Default `CALLER_IDPS=processstack` | Only PS-authenticated users can hold per-user grants or read as themselves. An external-idp user call → 403 |
| **Decided at G1: Slack** | v1 keyed Slack-chat watches by the Slack id. **Decision: `processstack` only in v2.0.** Slack-chat watch commands are refused by the app (`app.md`), and Slack-keyed v1 memory is not imported. The rejected alternative was to add `slack`: Slack users could watch, but those grants would be visible only to Slack-originated calls, **not** to the same person's PS-key agents |

## Visibility rules (SVC-4)

| Rule | Detail |
|---|---|
| Visible set | `grants(tid, sub) ∪ grants(tid, '*')` for `act=user` (exact full-`sub` match); `grants(tid, '*')` for `act=application` |
| Granularity | **Repository-level.** Any indexed branch of a visible repo is readable; `branch` in a grant only drives indexing |
| Denial shape | The uniform not-found already used by `TryBeginRead` (`src/Sextant.Mcp/DatabaseProvider.cs:110-157`), with no "exists but denied" distinction. `/control/status/{id}` → 404 |
| Cross-repo tools | Rows are filtered per consumer repo via `IReadAuthorizer.AuthorizeRepository` (`src/Sextant.Mcp/SnapshotProvenance.cs:150`) |
| `search_symbols` cursor | Re-authorized on every page. Hashes not in the visible set are dropped, so no HMAC is needed |
| `/query/snapshots/{hash}/symbols` | Delegate callers are authorized by grants (uniform 404) |
| Ensure with `act=user` | The repository must be visible (403 `not_granted`); this blocks "index anything" via `start-indexing`. A visible repository's ensure is then bounded so a user cannot change shared branch state (SX-6d, see "Control routes by caller") |
| Revocation | Effective on the next request, with no caching of the grant set across requests |
| Legacy token | Unscoped until G3. Then set `REQUIRE_REPOSITORY_SELECTION=true` and delete the legacy token |

## Control routes by caller (SVC-3/SVC-4, #193)

A control call needs the control token (or the contribute token on `/control/contribute`). A verified caller
assertion then decides what the call may do. The `act=user` rule is **default-deny**: a user caller reaches only
the routes that apply their own user rule, and every other control route, including one added later, is
403 `caller_not_allowed` (`ControlCallerRules`). The refusal runs after the assertion is verified (a bad one
is still 401) and before the route reads its request or the catalog, so it is not an existence oracle.

| Route | `act=user` | `act=application` | No assertion (control bearer) |
|---|---|---|---|
| `POST /control/ensure` | Repository must be visible, else 403 `not_granted` (for every body). Then the body is bounded (SX-6d, #198): 400 `default_branch_not_allowed`, `branch_head_sequence_not_allowed`, `branch_guard_required` (no `expected_head_commit` and not `branch_update: none`) or `branch_required` (a CAS without a `branch_name`); audited `ensure`/`denied` | Unchanged (trigger) | Unchanged |
| `GET /control/status/{id}` | 404 unless the job's repository is visible | Unchanged | Unchanged |
| `GET /control/resolve` | Bare 404 unless the repository is visible | Unchanged | Unchanged |
| `/control/grants/self` | Own grants only | 403 `wrong_actor` | 401 `caller_required` |
| `/control/grants/tenant`, `GET /control/grants?scope=tenant` | 403 `wrong_actor` | Own tenant's `'*'` grants and targets | 401 `caller_required` |
| `POST /control/branches/retire` | **403 `caller_not_allowed`**, even for a visible repository (a destructive cross-user action); audited `retire`/`denied` | Unchanged (repository delete, reconcile) | Unchanged |
| `POST /control/retention`, `POST /control/backup` | 403 `caller_not_allowed`; audited `denied` | Unchanged | Unchanged |
| `GET /control/metrics`, `/control/audit`, `/control/pilot` | 403 `caller_not_allowed` (cross-tenant operator data) | Unchanged | Unchanged |
| `POST /control/contribute` | Any assertion → 401 `assertion_not_allowed` | Same | Unchanged (contribute or control token) |
| Any other `/control/*` path | 403 `caller_not_allowed` | Unchanged (404/405) | Unchanged (404/405) |

Assertion-less calls stay full-power until SVC-17 (see "Trust boundaries").

**User ensure bounds (SX-6d, #198).**
- **The problem.** A grant makes a repository readable. It must not let a user redirect branch state that every reader of that repository shares.
- **What a user may send.** A user ensure must be a CAS (`expected_head_commit` plus a `branch_name`) or `branch_update: none`.
- **What a user may not send.**
  - `default_branch: true`, which would re-point the repository's sole default.
  - A `branch_head_sequence`, because a maximal one would block every later sequenced advance.
  - An unguarded advance.
- **Order.** The bounds are checked after visibility, so an ungranted user sees only `not_granted` whatever the body asks. They read nothing but the body.
- **No implicit default (#199).** On a repository with no default branch yet, the #104 first-branch safety net makes a user's branch the default only if the service confirms, with its own `git ls-remote --symref`, that the remote's `HEAD` names that branch. The branch name is never taken from the request. The check fails closed: with no resolver (locate mode), when the bounded lookup (shared per repository, remembered 60 s, at most 4 at once) has no free slot, or when the lookup fails, the branch is created non-default. Application callers are unchanged.
- **Residual risk.** A user CAS still moves the branch it names, the default included, from the head the user observed to a commit the user names. The service cannot verify the upstream head. The app checks it against GitHub first (`start-indexing`, `grant-watch`), so closing this fully needs server-side head verification (N).

**Kestrel request targets (#198 item 4).** `KestrelControlPathTests` sends raw targets to a real Kestrel socket. `%2e%2e`, `./`, `%63ontrol`, a case change and the absolute form normalize onto the route and get the same user refusal. `;x`, `//` and `%2f` match no route, so they are 403 under `/control` and 404 outside it. Without a token they are 401 or 404. The #193 default-deny and the control token therefore hold however the path is spelled.

## Self-service watch implications (#111)

**Risk.**
- Clone mode uses **one static org-wide PAT** (#111).
- Any app user can grant themselves **any repository that PAT can read**, including private repos their own GitHub account cannot see, and then query its code.
- The v1 app had the same property.

| Mitigation | R/N | Where |
|---|---|---|
| The watch flow checks `GetRepository` through the tenant's `github` connection (installation scope) before `PUT /control/grants/self` | **R** | `app.md`, configure-watched-repos |
| Only the app holds the control token; users cannot call `/control/grants/self` directly | **R** | Operator: restrict who can bind `sextant-control`. PS has no generic "restrict connection binding to app X" (a generic PS issue tracks it), so the service sets **`CALLER_APPS=sextant`** (SVC-3, R): another app bound to the same connections cannot use grant routes or delegate reads. Assertion-less control calls remain full-power until SVC-17 (**R before a second app or tenant**) |
| `REPOSITORY_OWNERS` allow-list on the service | **R** whenever a tenant has members besides its owner (see the SSRF table) | SVC-5; `docs/service.md`, "Production deployment checklist" |
| Per-request installation token instead of the static PAT | N (R before a 2nd tenant) | #111 / SVC-11 |
| Per-user GitHub permission check (the user's own OAuth) | N | Requires a PS user-GitHub identity; out of scope |

The app's pre-check is **UX plus defense in depth, not a boundary**. The durable fix is #111.

## Pre-multi-tenant hardening (open issues)

| Issue | Risk | Recommendation |
|---|---|---|
| #145 | The query surface emits absolute worker checkout paths and keys `solution:` scope by absolute path | Fix before a second tenant (information disclosure) |
| #134 | An unresolvable `project:`/`solution:` scope silently becomes an unfiltered query | Fix before a second tenant. The grant gate still bounds it to the pinned snapshot, but fail-open is the wrong default |
| #77 | Provenance/overlay expose catalog-global integer ids | Makes `/control/status/{id}` enumerable (mitigated by the 404-unless-visible rule) |
| #76 | No hard OS isolation for untrusted MSBuild evaluation | Required before indexing repos from mutually untrusted tenants. Also the only full fix for #209 |
| #111 | Static org-wide checkout PAT | See above |
| #209 | A user ensure with `branch_update: none` (the app's `start-indexing`) can index any commit reachable in a granted repository, fork-network commits included, so untrusted code runs through MSBuild evaluation under a trusted base URL | `REPOSITORY_OWNERS` narrows the base repositories but does not close it; #76 (or a commit-reachability check) does |
| #210 | The app's visibility gate is workspace-level: the watch flow and the legacy import check the repository with the **workspace's** GitHub connection, so any workspace member can watch (and so read) every repository that connection sees | Treat every workspace member as trusted with what the connection reads (documented in the app README, "Tenant trust") |

## Accepted residuals (as built, SX-10/SX-11)

- **A missed push is repaired only by the nightly reconcile.** Each push ensure carries its own `before` as the head CAS, so after one lost push ensure every later push's CAS misses (it indexes but does not advance the branch). The app's nightly `reconcile` re-ensures the branch under the CAS of the service's current head and repairs it.
- **A tenant grant deleted with `DELETE /control/grants/tenant` comes back.** Every repository event from the GitHub App installation creates or holds the repository's tenant grant (by design), and until the app's v2.1 cleanup the reconcile also imports the legacy `enrolled/*` App State rows as tenant grants. To stop indexing a repository for a tenant, remove it from the GitHub App installation; its enrolled row still restores the grant on each reconcile until v2.1 drops that import (`pr-plan.md`, post-G3 APP v2.1).
- **`internal: true` flows are runnable directly** (elevenworks/ProcessStack#3287). The app treats any flow's inputs as caller-chosen; the service's grant gate, the SX-6d user-ensure bounds and the URL policy stay the authority.

## Audit

| Event | Actor | Detail |
|---|---|---|
| ensure / resolve / retire / grant / query | `HashActor("{tid}/{sub}")` (`sub` is idp-namespaced) or `HashActor("{tid}/app:{app}")` (SHA-256 with a domain prefix, `src/Sextant.Store/AuditLogStore.cs:218-224`) | `idp, kid, via, cid, dep, jti` (`dep=-` when the assertion carries none; `dep` is optional since 2026-09-28), outcome, and the repository scope (canonical URL) |

- Grant rows necessarily store `tenant_id` and the full `sub` in clear, because lookups need them. They are PS ULIDs (or namespaced peer ids if another idp is allowed), not names or emails.
- `/control/audit` stays control-token only, and a user caller (`act=user`) is refused there (403 `caller_not_allowed`, #193), so a user can never read another caller's audit rows. Application callers and assertion-less operator calls read it as before.
