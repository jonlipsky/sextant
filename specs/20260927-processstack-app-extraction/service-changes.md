# Service changes (SVC units)

> **Approved at G1 (2026-09-27). Tracking: elevenworks/ProcessStack#3159.** Evidence refers to `jonlipsky/sextant` `origin/main` `9544f76`. **R** = required for the app; **N** = nice-to-have.
> Global constraints for every unit:
> - The core libraries get only generic, default-off hooks, and `ArchitectureBoundaryTests` stays green.
> - The local stdio/CLI/daemon path stays byte-identical.
> - The wire format stays snake_case (`src/Sextant.Service/ServiceJson.cs:9-13`).
> - Migrations are forward-only and additive.
> - Every new control route is audited (`src/Sextant.Store/AuditLogStore.cs:91`, actor hashed via `HashActor` `:218`).

## Summary

| SX | SVC | Unit | R/N | Main files |
|---|---|---|---|---|
| SX-1 | SVC-5 | Repository URL/host policy at intake | R | new `Sextant.Service/RepositoryUrlPolicy.cs`, `ServiceOptions.cs`, `ServiceApp.cs` ensure route |
| SX-2 | SVC-1 | Selector always wired; opt-in fail-closed selection | R | `ServiceApp.cs:61-66`, `Sextant.Mcp/DatabaseProvider.cs`, `FederatedReadContext.cs` |
| SX-3 | SVC-8 | Generic MCP-client compatibility test + comment cleanup | R (verification) | `tests/Sextant.Service.Tests`, `ServiceApp.cs:80-92` comment |
| SX-4 | SVC-2 | Per-call selection through reserved tool args (`repository`, `branch`) | R | `ServiceApp.cs` MCP builder, `DatabaseProvider`, `SnapshotStore` |
| SX-5 | SVC-3 | Caller-assertion verification + delegate tokens | R | new `Sextant.Service/CallerIdentity/*.cs` (verifier, key ring, options, principal), new `Sextant.Service.Host/CallerAssertionGate.cs`, `ServiceOptions.cs`, middleware in `ServiceApp.cs` |
| SX-6 | SVC-4 | Grants: migration 024, `/control/grants*`, grant authorizer, `list_repositories` | R | migration `024`, `Sextant.Store/RepositoryGrantStore.cs`, `Sextant.Service/GrantReadAuthorizer.cs`, `ServiceApp.cs` |
| SX-7 | SVC-F | Federated `search_symbols` MCP tool | R (parity) | new `Sextant.Service/Mcp/SearchSymbolsTool.cs`, `ServiceApp.cs:115-125` |
| SX-8 | SVC-6+7 | Branch-pointer semantics: `expected_head_commit`, `branch_update`, retire, resolve `commit_sha` | R | `ServiceContracts.cs`, `SnapshotService.cs`, `SnapshotStore.cs`, `IndexOrchestrator.cs`, `ServiceApp.cs` |
| SX-12 | SVC-16 | Docs (service.md, onboarding Mode A rewrite, CLAUDE.md drift) | R at cutover | `docs/*`, `CLAUDE.md` |

> **Allow-list mismatch.** PS's default gateway allow-list is `find_usages` / `search_symbols` (PS:`src/ProcessStack.Activities.Sextant/SextantGatewayOptions.cs:145-165`).
> - **`find_usages` does not exist in the service.** The real tool is `find_references` (`src/Sextant.Mcp/Tools/FindReferencesTool.cs:11`).
> - **`search_symbols` was gateway-native.** SVC-F now makes it a real service tool.
> - Any PS-side default must use the names in `ServiceApp.RemoteQueryTools` (`src/Sextant.Service.Host/ServiceApp.cs:115-125`) plus the two tools added here.

---

## SVC-5 (SX-1): repository URL/host policy (R)

**Today:**
- `/control/ensure` passes `repository_remote_url` straight to `EnsureJob`, with no validation (`src/Sextant.Service.Host/ServiceApp.cs:224-251` → `SnapshotService.cs:424`).
- Clone mode allows `https:http:file:ssh:git` for the top level (`src/Sextant.Service/CloningCheckoutProvider.cs:77`).
- The only other checks are a hex commit and no userinfo (`:285-304`).
- The file-transport test flag exists for **submodules only** (`:61-66`).

**Contract.** A new pure class, `RepositoryUrlPolicy.Evaluate(string url) → {ok, canonical, host, owner, repo, reason}`, modeled on `SubmoduleUrlPolicy`.

| Rule | Reject reason |
|---|---|
| Scheme must be `https`. `file` is allowed only under a new internal test-only `RepositoryUrlPolicy.AllowFileTransportForTesting`, which mirrors the submodule flag at `CloningCheckoutProvider.cs:61-66` | `scheme_not_allowed` |
| No userinfo, query, fragment or non-443 port | `url_component_not_allowed` |
| Host must match the DNS-label regex (`SubmoduleUrlPolicy.cs:45`) and have ≥2 labels. No IP literal, no `localhost`, no trailing dot | `host_not_allowed` |
| Host must be in `REPOSITORY_HOSTS` (default `github.com`; `*` = any host that passes the shape rules) | `host_not_allowed` |
| Path must be exactly `/{owner}/{repo}` with an optional `.git`. Segments match `[A-Za-z0-9._-]{1,100}`, must not start with `-`, and must not be `.` or `..` | `path_not_allowed` |
| Optional owner allow-list: `REPOSITORY_OWNERS` = `host/owner` entries, `host/*` allowed | `owner_not_allowed` |

- **Canonical form:** `https://{host}/{owner}/{repo}` folded with `RemoteUrlIdentity.Normalize` (`src/Sextant.Core/RemoteUrlIdentity.cs:39-89`). It is used for policy checks and grant keys (`repository_key`). **Submitted URLs are NOT rewritten**, because identity hashes the raw spelling (`ServiceContracts.cs:86-97`), so rewriting would re-index every repo once. Ensure uses the request URL as-is, and a grant stores its submitted spelling in `remote_url`. Identity canonicalization is SVC-15 (N).
- **Applied at:**
  - `/control/ensure`, before any job row exists;
  - `/control/grants*` (SVC-4);
  - `/control/branches/retire` (SVC-6+7);
  - the `repository` tool arg (SVC-2), canonicalization only.
- **Refusal:** `400 {"status":"rejected","reason":"<code>"}`, audited as `ensure`/`denied`. The URL is never echoed.

| Env (`SEXTANT_SERVICE_…`) | Default | Notes |
|---|---|---|
| `REPOSITORY_HOSTS` | `github.com` | Comma list. A malformed entry fails startup. `*` logs a startup warning |
| `REPOSITORY_OWNERS` | unset (any owner) | Recommended before a second tenant (see `security.md`) |

**Tests:**
- A table-driven policy test covering IPv4/IPv6 literals, `localhost`, single-label hosts, a trailing dot, userinfo, a port, a query, a fragment, a `-` prefix, `..`, `http`, `ssh`, scp-like, `file`, and an off-list host.
- Canonicalization is idempotent.
- The ensure route returns 400 with no job row created.
- HTTP tests that ensure `file://` fixtures set the test flag. `SnapshotService`-direct clone tests bypass the route, so they are unaffected.
- **Migration note:** a deployment in locate mode with non-GitHub remotes must set `REPOSITORY_HOSTS`. This is documented in SX-12.

## SVC-1 (SX-2): selector always wired; fail-closed selection (R)

**Today:**
- `X-Sextant-Repository` is read only when `READ_POLICY` is on (`ServiceApp.cs:61-66`).
- With several repos and no selector, a read is `Unscoped`, meaning across all repos (`src/Sextant.Mcp/FederatedReadContext.cs:83-86,115-121`; `SnapshotStore.GetSelectedSnapshotRow` `:848`).

**Contract:**
- **Always wire `RequestedRepositoryAccessor`** (`ServiceApp.cs:506-513`).
- **New generic hook** `DatabaseProvider.RequireRepositorySelection : Func<bool>`, default `() => false`, so the local path is unchanged. When it returns true and no selector is present, `TryBeginRead` fails with a request-shaped error `repository_required` that names no repository and depends only on the request, so it is not an oracle.
- **Service wiring:** `RequireRepositorySelection = () => options.RequireRepositorySelection || caller != null`. The caller half is wired in SX-5.
- **Legacy `QUERY_TOKEN` with no header** keeps today's `Unscoped` behavior until G3, because the old PS gateway sends no selector.

| Env | Default | Notes |
|---|---|---|
| `SEXTANT_SERVICE_REQUIRE_REPOSITORY_SELECTION` | `false` | Set `true` after G3 to fail closed for everyone |

**Tests:**
- The header is honored with `READ_POLICY` off.
- A legacy token with no header is still Unscoped (a regression pin).
- With the env on, a request with no selector gets `repository_required`.
- Stdio `McpServerSetup` is unaffected.

## SVC-8 (SX-3): generic MCP-client compatibility (R, verification)

**Contract.** No behavior change.
- **Test:** the MCP SDK `McpClient` + `HttpClientTransport` with static `AdditionalHeaders` (bearer). This is the shape PS uses at PS:`src/ProcessStack.Connections.Mcp/McpConnection.cs:431-437`. The test runs initialize → tools/list → tools/call against the stateless `/mcp` in a `WebApplicationFactory`.
- **Cleanup:** rewrite the PS-gateway rationale comment at `ServiceApp.cs:80-92` as "stateless: any proxy or pooled client", and rename the framing of `tests/Sextant.Service.Tests/ServiceHttpTests.cs:219-287`.
- **Extension:** SX-5 adds the case "delegate bearer, no assertion: initialize + tools/list succeed; tools/call → `caller_required`".

## SVC-2 (SX-4): per-call selection through reserved tool args (R)

**Why:**
- PS-8 (F3) forwards `arguments` verbatim over a pooled connection that has only static headers, so per-call selection must travel in args.
- Reads always pin the **default** branch (`SnapshotStore.cs:877`).

**Contract.** The mechanism is MCP SDK 2.2.0 request filters: `WithRequestFilters(f => f.AddListToolsFilter(…).AddCallToolFilter(…))` on the service's `AddMcpServer()` builder, package `ModelContextProtocol` 2.2.0 at `src/Sextant.Mcp/Sextant.Mcp.csproj:18`. Each filter is a `McpRequestFilter<ListToolsRequestParams, ListToolsResult>` / `McpRequestFilter<CallToolRequestParams, CallToolResult>` (`next => async (context, ct) => …`); `context.Params.Arguments` is a mutable `IDictionary<string, JsonElement>`, and `context.Services` / `context.User` are the request's. The service `/mcp` is stateless, so the filters run on every request with no session state. The filters live in Service.Host only; stdio is unchanged.

| Aspect | Rule |
|---|---|
| `tools/list` | Adds `repository` (string: `https://host/owner/repo[.git]`, `host/owner/repo`, or `owner/repo` when `REPOSITORY_HOSTS` has exactly one non-`*` host) and `branch` (string) to the `inputSchema` of every repo-scoped tool in `RemoteQueryTools`. Not added to `list_repositories` or `search_symbols`, which declare their own |
| `tools/call` | Removes `repository`/`branch` from `arguments`, canonicalizes with SVC-5, and stores them in `HttpContext.Items`. Precedence: args > `X-Sextant-Repository` header. If both are present and differ: tool error `selector_conflict` |
| Cross-repo tools | `find_cross_repository_usages` / `find_submodule_consumers` take `provider_repository_url` and call `TryBeginRead` with no selector (`src/Sextant.Mcp/Tools/FindCrossRepositoryUsagesTool.cs:19,29,36`). The selector defaults to `provider_repository_url` |
| Branch | New generic `DatabaseProvider.RequestedBranch : Func<string?>` (default null) plus `SnapshotStore.GetSelectedSnapshotRowForRepositoryBranch(url, branch)`, which reads the branch pointer's complete snapshot, next to `:865-895`. `branch` without `repository` gives `repository_required` |
| Miss | Unknown branch or no complete snapshot → the existing uniform not-found (enforcing) or the actionable message (local) |

**Tests:**
- The list schema contains both args.
- A call strips the args and pins the repository; `branch` pins that branch's snapshot.
- Conflict handling.
- The cross-repo default.
- Stdio `tools/list` is byte-identical.

**As implemented (SX-4).** `src/Sextant.Service.Host/ToolSelectionFilters.cs`; the tests are `ToolArgumentSelectionHttpTests`, `ToolSelectionFiltersTests`, the `McpClientCompatibilityTests` argument case, `RepositorySelectionTests` (Mcp) and `BranchSnapshotSelectionTests` (Store). These decisions refine the contract:
- **Per-tool reserved set.** A reserved name the tool already declares is neither advertised nor stripped. The cross-repo tools already take a `branch` argument, which filters consumers, so they get only `repository` and keep their own `branch`. `SelectionExemptTools` (empty today) is where `list_repositories` and `search_symbols` go when they land.
- **Cross-repo default only under the requirement.** The `provider_repository_url` default applies only when `RequireRepositorySelection()` holds and neither the arg nor the header names a repository. With the requirement off, these tools keep today's unselected read, so the default never regresses them.
  - **Known limitation:** the gate pins the provider's default-branch snapshot through `GetSelectedSnapshotRowForRepository`, which excludes provider-only repositories (`is_provider = 1`). So a provider indexed only as a submodule gets the uniform not-found. A caller can still name a readable consumer in `repository`, and a provider ensured directly is served.
- **`invalid_selector`.** A reserved arg that is not a string, or a `repository` refused by the SVC-5 policy, gives the tool error `invalid_selector`. The message carries only the policy reason code, never the value. The `owner/repo` short form without exactly one explicit host is refused as `host_required`. JSON `null` or blank means absent.
- **Where each error comes from.** `selector_conflict` and `invalid_selector` come from the filter as MCP tool errors (`isError: true`, carrying the usual `meta.error` JSON). `repository_required` for a `branch` without a repository comes from `TryBeginRead`, so it applies wherever `RequestedBranch` is wired, whatever the requirement setting. A `branch` arg with a header-selected repository is allowed.
- **Conflict comparison.** The conflict check compares `RemoteUrlIdentity.Normalize(header)` with the canonical arg. A header-only selection is passed through as sent, as before SX-4.
- **Blank branch, and a branch miss.** A blank `branch` means the default branch. A permissive branch miss says "No complete snapshot is available for the requested repository branch."; an enforcing one is the uniform not-found.

## SVC-3 (SX-5): caller-assertion verification (R)

The contract is **PS-7 (F4)** (ProcessStack-side spec `20260927-outbound-caller-identity`). It is implemented in Service.Host, with no PS types. PS stamps an immutable `RunCaller` at run start, so the service never sees a caller derived from a run variable.

| Env (`SEXTANT_SERVICE_…`) | Format | Startup validation (fail closed) |
|---|---|---|
| `DELEGATE_TOKENS` | `tok1;tok2` | Requires `CALLER_KEYS`. A delegate token must not equal the query, control or contribute token |
| `CALLER_KEYS` | `kid=base64url@tenantId;…` | kid matches `[A-Za-z0-9._-]{1,64}` and is unique. The key decodes to ≥32 bytes. `tenantId` is non-empty. Several kids may map to one tenant (rotation); one kid maps to exactly one tenant |
| `CALLER_AUDIENCE` | string (prod: `sextant`) | Required when `CALLER_KEYS` is set |
| `CALLER_ISSUERS` | comma list | Optional. When set, `iss` must be one of them (PS `ApiBaseUrl`) |
| `CALLER_HEADER` | default `X-ProcessStack-Caller` | Must match the connection's `callerIdentity.header` |
| `CALLER_IDPS` | comma list, default `processstack` | Optional. The `idp` values allowed to act as `act=user`. Each entry matches `[a-z0-9-]{1,32}`. **v2.0: leave the default** (decided at G1, see below) |
| `CALLER_APPS` | comma list (prod: `sextant`) | Optional; **R** to set in prod. An allow-list on the signed `app` claim. Unset = any app. The cheap mitigation for "another app bound to the same connection" (PS has no generic binding restriction; a generic PS issue tracks it) |

**Verification, in this order.** Any failure in steps 1-9 → 401 `{"error":"invalid_caller_assertion"}` with `WWW-Authenticate: Bearer error="invalid_token"`. The reason is only logged, never with token material.
1. Compact JWS with 3 base64url segments, ≤ 8 KiB.
2. The JOSE header has `alg == "HS256"` exactly (no `none`, `HS512` or `RS*`), `typ` absent or `JWT`, and a `kid` that is known.
3. HMAC-SHA256 over `b64(header).b64(payload)`, compared with `CryptographicOperations.FixedTimeEquals`.
4. `aud` (a string, or an array containing it) equals `CALLER_AUDIENCE`, and `iss` passes when `CALLER_ISSUERS` is set.
5. Timing, with `now` in UTC seconds and 60s skew:
   - `nbf - 60 ≤ now ≤ exp + 60`;
   - `iat ≤ now + 60`;
   - `exp - iat ≤ 300`.
6. **`tid == CALLER_KEYS[kid].tenantId`.** This is the multi-tenant guard.
7. `act ∈ {user, application}`, with the claims tied to it:
   - `act=user` ⇒ `idp` and `sub` are both non-empty;
   - `act=application` ⇒ `idp` and `sub` are both absent.
8. **`sub` shape matches `idp`.** This is a namespace guard: grants key on the full `sub`, so a platform id must never collide with an external peer id.
   - `idp == processstack` ⇒ `sub` contains no `:` (a platform user id);
   - any other `idp` ⇒ `sub` matches `{idp}:{connectionInstanceId}:{peerId}` (the prefix equals `idp`; both later segments are non-empty).
9. `jti` is non-empty. `via ∈ {mcp-surface, activity}`. `tslug`, `app` and `cid` are strings (recorded; `app` is also checked in step 11). `dep` is **optional**: absent, or a non-empty string (recorded). Unknown claims are ignored.

   > **Amended 2026-09-28: `dep` optional (present iff the calling run is bound to a deployment); orchestrator decision after the signer review.** The signer knows the deployment id only for a run bound to a deployment, and cannot supply it on many legitimate user paths (for example direct starts). `dep` is audit-only (it plays no part in steps 10-11), so an absent `dep` verifies with `CallerPrincipal.Deployment = null`, and the audit suffix renders it as `dep=-`. A present `dep` that is JSON `null` or not a string still fails step 9 (401 `invalid_caller_assertion`), and so does an empty `dep`, which is a tightening: before the amendment an empty `dep` was accepted, so a signer must omit an unknown `dep` rather than send `""`. `tslug`, `app` and `cid` stay required.

**Policy checks** run after a valid assertion. A failure → 403 `{"error":"caller_not_allowed"}` (for MCP `tools/call`, the tool error `caller_not_allowed`). The reason (`idp` or `app`) is only logged.

10. `act=user` ⇒ `idp ∈ CALLER_IDPS`.
11. If `CALLER_APPS` is set ⇒ `app ∈ CALLER_APPS`. This applies to every assertion-bearing request (delegate reads and grant routes), so another app bound to the same connections gets nothing. **Assertion-less control calls stay full-power until SVC-17.**

**Output:** `CallerPrincipal {TenantId, Actor, Idp?, UserId?, App, Deployment?, Connection, Via, Run?, KeyId, Jti}` in `HttpContext.Items`, exposed as `Func<CallerPrincipal?>`, like `PrincipalTokenAccessor` (`ServiceApp.cs:494`). `UserId` is the full `sub` string. `Deployment` is null when the assertion carries no `dep`.

> **Decided at G1: which idps act as users?**
> - **Decision: `processstack` only in v2.0.** Then only PS-authenticated users (agents, CLI, web/api/cli chat) can hold per-user grants.
> - **Consequence:** v1 keyed Slack-chat watches by the Slack id, and these would become `slack:{cid}:{peer}` grants. They are **not visible** to that person's PS-key agents, since that key's `sub` is the platform user id. So v2.0 refuses Slack-chat watch commands (`app.md`) and does not import Slack-keyed legacy memory.
> - **Rejected alternative:** add `slack` to `CALLER_IDPS`. Slack users could then watch, but those grants would be visible only to Slack-originated calls. There is no cross-idp linking, which would be a separate PS identity-linking feature.

| Request | Bearer | Assertion |
|---|---|---|
| `/mcp` `initialize`, `notifications/*`, `ping`, `tools/list` | delegate | Optional (pool connect, discovery); if present, it must verify |
| `/mcp` `tools/call` | delegate | **Required**: the call-tool filter returns tool error `caller_required` |
| `/query/*` | delegate | **Required** (401 in middleware) |
| `/mcp`, `/query/*` | legacy `QUERY_TOKEN` or `READ_POLICY` principal | **Must be absent**; if present → 401 `assertion_not_allowed` |
| `/control/grants*` | control | **Required** (SVC-4) |
| other `/control/*` | control | Optional; if present it must verify, and it drives audit and the `act=user` checks (SVC-4). A verified `act=user` caller reaches only ensure, status, resolve and the grant routes; every other control route is 403 `caller_not_allowed` (#193, see §SVC-4 "As implemented") |

- **Interim state:** in SX-5 alone, delegate reads use a deny-all authorizer, because there is no visibility source until SX-6. SX-5 and SX-6 deploy together at G2b.
- **Audit actor:** `HashActor("{tid}/{sub}")` (`sub` is already idp-namespaced) or `HashActor("{tid}/app:{app}")`. The detail suffix is `idp,kid,via,cid,dep,jti`; an absent `dep` is written as `dep=-`.

**Tests:**
- Positive vectors: a user caller (`idp=processstack`); an application caller; two kids for one tenant; `idp=slack` with a namespaced `sub` when `CALLER_IDPS=processstack,slack`; a user and an application caller without `dep` (amended 2026-09-28).
- Negative vectors:
  - tampered payload; tampered signature;
  - `alg` `none`, `HS512` and `RS256`;
  - an unknown kid; a wrong `aud` or `iss`;
  - expired beyond skew; `nbf` in the future; ttl above 300;
  - **`tid` ≠ the kid's tenant**;
  - `user` without `sub`; `user` without `idp`; `application` with `sub`; `application` with `idp`;
  - `idp=processstack` with a `sub` containing `:`; `idp=slack` with an un-namespaced `sub` or a wrong prefix;
  - `dep` present as `null`, `1` or `""` (amended 2026-09-28);
  - oversized; a malformed segment.
- Policy: `idp=slack` under the default `CALLER_IDPS` → 403 `caller_not_allowed`; `app=other` with `CALLER_APPS=sextant` → 403 on reads and grant routes; an assertion-less control call is unaffected.
- The pool-connect case (SVC-8 extension).
- A header sent with a legacy token → 401.
- 20 parallel calls with distinct `sub`s each resolve their own caller.
- Startup failures for a short key, a duplicate kid, a missing audience, and a malformed `CALLER_IDPS` entry.
- Log redaction.

### As implemented (SX-5)

The contract above is implemented as written. These are the places where the code is more specific than the spec, or deliberately differs from it.

**Placement.**
- The pure pieces live in `Sextant.Service/CallerIdentity/`: `CallerAssertionVerifier` (steps 1-11), `CallerKeyRing`, `CallerAssertionOptions` and `CallerPrincipal`. They have no ASP.NET dependency, so they are unit-tested without a host.
- The HTTP pieces live in `Sextant.Service.Host/CallerAssertionGate.cs`: the middleware admission, the `tools/call` filter, the `Func<CallerPrincipal?>` accessor, and the interim `DenyAllReadAuthorizer` behind a `CallerReadAuthorizer` router.
- `CallerPrincipal` also carries `TenantSlug` (the signed `tslug`).

**JWS and claims (stricter than the spec).**
- Each segment must be canonical unpadded base64url. The header and payload must be JSON objects with no duplicate member names, and every string (member names included, at any depth) must decode. A lone-surrogate escape or an invalid UTF-8 byte is `bad_header`/`bad_payload`, never a server error.
- The JOSE header also refuses `jku`, `jwk`, `x5u`, `x5c` and `crit`, so a key or critical extension can never ride in the header. `typ`, when present, must be exactly `JWT` (case-sensitive).
- `tslug`, `app` and `cid` must be present as strings (empty is allowed). `dep` is optional, but when present it must be a non-empty string (amended 2026-09-28, see step 9; before the amendment it was required and could be empty). `run` is an optional string. `iat`, `nbf` and `exp` must be present as integers in `[0, 253402300799]`.
- A `tenantId` in `CALLER_KEYS` is restricted to `[A-Za-z0-9._-]{1,128}`.

**Startup validation (fail closed, in addition to the table).**
- `CALLER_APPS` entries match `[A-Za-z0-9._-]{1,64}`, and `CALLER_ISSUERS` entries must be non-blank.
- `CALLER_HEADER` must match `[A-Za-z0-9-]{1,64}`. It may not be `Authorization`, `Proxy-Authorization`, `Cookie` or `X-Sextant-Repository` (case-insensitive).
- A delegate token must also differ from every `READ_POLICY` principal token, as well as from the query, control and contribute tokens.
- `DELEGATE_TOKENS` with no non-blank entry fails startup, and so does `DELEGATE_TOKENS` without `CALLER_KEYS`.
- Validation runs in `ServiceOptions.FromEnvironment` and again in `ServiceApp.RegisterServices`, so directly built options are checked too. Error messages name only the entry index and the kid, never a key or token.
- `CALLER_KEYS` without `DELEGATE_TOKENS` is allowed: control calls can carry an assertion (for the audit actor) while no delegate read path exists.

**Request matrix details.**
- `/mcp` policy failures (steps 10-11) are not refused in the middleware. The request is admitted, and the `tools/call` filter returns `caller_not_allowed`. Discovery (`initialize`, `ping`, `tools/list`) with a verified but policy-failing assertion therefore still succeeds, like discovery with no assertion. Every other plane refuses a policy failure with 403.
- `/query/*` with a delegate token and no assertion is 401 `caller_required`, with `WWW-Authenticate: Bearer error="invalid_request"`. `assertion_not_allowed` uses the same challenge. A failed verification uses `error="invalid_token"`, and a 403 carries no challenge.
- More than one instance of the assertion header is refused with 401 `invalid_caller_assertion` (reason `duplicate_header`).
- The caller filter is registered before the SVC-2 selection filter, so a delegate call without a caller gets `caller_required` before its selection arguments are read.
- A verified caller always requires a repository selection (`RequireRepositorySelection` holds whenever `Func<CallerPrincipal?>` returns a principal), because a caller's reads are scoped to its repositories.
- `/query/snapshots/{identityHash}/symbols` routes a delegate request through the per-caller authorizer, so it is the uniform 404 in SX-5.
- `/control/contribute` never accepts an assertion (401 `assertion_not_allowed`): its identity is its bearer.
- With no `CALLER_KEYS`, an assertion header on `/control/*` is 401 `assertion_not_allowed`. An assertion header with a non-delegate bearer on `/mcp` or `/query/*` is 401 `assertion_not_allowed`, whether or not the feature is configured.
- Delegate tokens are compared as SHA-256 digests in constant time, against every configured token with no early exit.

**Deferred.**
- The audit detail suffix (`idp,kid,via,cid,dep,jti`) is not written yet. The audit actor is `HashActor("{tid}/{sub}")` or `HashActor("{tid}/app:{app}")` for ensure (including a denied ensure), branch retire (including a denied retire), retention and backup. Contribute keeps its bearer.
- Security checklist item 10 (`principal_in_body`) belongs to SX-6, which adds the routes that take a principal.

**Logging.** A refusal logs one warning under the `Sextant.Service.Host.CallerAssertion` category, with the plane, the reason code, the configured `kid` and the signed `jti`. The `jti` is logged only when it is at most 64 characters of `[A-Za-z0-9._:-]`, otherwise as `-`. Nothing else from the assertion or any token is logged.

## SVC-4 (SX-6): grants, visibility and `list_repositories` (R)

**Migration `024_repository_grants.sql`.** Additive; `LatestSchemaVersion` auto-derives to 24.

```sql
CREATE TABLE repository_grants (
    id INTEGER PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    principal TEXT NOT NULL,          -- the verified full `sub` (PS user id, or `{idp}:{cid}:{peer}`), or '*' = tenant-wide enrollment
    repository_key TEXT NOT NULL,     -- RemoteUrlIdentity.Normalize(url): uniqueness + visibility match
    remote_url TEXT NOT NULL,         -- first-submitted spelling (validated; e.g. GitHub clone_url), kept on
                                      -- re-PUT so reconcile ensures hit the same snapshot identity
    branch TEXT NOT NULL DEFAULT '',  -- '' = repository default branch
    source TEXT NOT NULL,             -- 'self' | 'tenant' | 'import'
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    UNIQUE (tenant_id, principal, repository_key, branch)
);
CREATE INDEX ix_grants_tenant_repo ON repository_grants(tenant_id, repository_key);
```

**Schema decisions:**
- **No foreign key to `repositories`.** Watching usually happens before the first index. Creating `repositories` rows at grant time would add catalog rows with no snapshots and perturb the single-repository rule of `GetSelectedSnapshotRow` (`SnapshotStore.cs:848`).
- **Sentinels `''` / `'*'` instead of NULL**, because SQLite `UNIQUE` treats NULLs as distinct.

**Control API.** Every route needs the control bearer **and** a verified assertion. The principal comes **only** from the assertion. A body or query that contains `tenant_id`, `principal`, `sub` or `user` → 400 `principal_in_body`.

| Route | `act` | Request | Response |
|---|---|---|---|
| `PUT /control/grants/self` | user | `{repository, branch?}` | `200 {grant:{repository, branch, source, created_at}, created}` |
| `DELETE /control/grants/self?repository=&branch=` | user | `branch` omitted = the default-branch grant; `branch=*` = all branches | `200 {deleted: n}` |
| `GET /control/grants/self` | user | — | `{grants:[{repository, branch, source, created_at, status}], result_count}` |
| `PUT /control/grants/tenant` | application | `{repository, branch?}` | as for self (`principal='*'`) |
| `DELETE /control/grants/tenant?repository=&branch=` | application | — | `{deleted: n}` |
| `GET /control/grants?scope=tenant` | application | — | `{targets:[{repository, branch, sources:[self\|tenant\|import], watchers}], result_count}`. The **distinct** (repository, branch) set for reconcile, with counts only and **no user ids** |

- **`targets[].repository`** is the `remote_url` of the oldest `'*'` grant for that key, or else of the oldest grant. `branch` is `''` for the default branch.
- **`status`:** `{resolved_branch, snapshot_status: complete|partial|pending|missing, commit_sha?, published_at?, identity_hash?}`, read through `ReadCatalog` (never the writer), as `ResolveBranch` does (`SnapshotService.cs:1240-1257`).
- **Error mapping:**
  - 400 `rejected` (SVC-5 reason);
  - 401 (no or invalid assertion);
  - 403 `caller_not_allowed` (SVC-3 steps 10-11: idp or app not allowed);
  - 403 `wrong_actor`;
  - 409 `grant_limit` (`MAX_GRANTS_PER_PRINCIPAL`, default 200; `MAX_GRANTS_PER_TENANT`, default 5000).
- **Audit:** a new `AuditActions.Grant` with outcome `accepted` or `denied`.

**Visibility.** A repository is visible to a caller iff a grant exists for `(tid, sub, key)` or `(tid, '*', key)`, where `sub` is compared as the full, exact string (no idp stripping or cross-idp linking). For `act=application`, only `'*'` counts. Visibility is **repository-level**: any indexed branch of a visible repository is readable, and the `branch` column only drives indexing and reconcile targets.

| Component | Rule |
|---|---|
| `GrantReadAuthorizer : IReadAuthorizer` (`src/Sextant.Mcp/SnapshotProvenance.cs:125-150`) | Lives in `Sextant.Service` and is built from delegates, like `PolicyReadAuthorizer` (`src/Sextant.Mcp/PolicyReadAuthorizer.cs:1-79`). `Authorize(selected)` and `AuthorizeRepository(id, url)` both apply the rule above, so the cross-repo tools inherit it |
| Per-request composite (Service.Host) | Delegate caller → grants (`IsEnforcing=true`); `READ_POLICY` principal → `PolicyReadAuthorizer`; legacy query token → `AllowAll`. `IsEnforcing` is evaluated per call at `DatabaseProvider.cs:119`, so a per-request value works |
| Implicit selection | A delegate request with no selector and exactly **one** visible repository that has a complete default-branch snapshot → select it; otherwise `repository_required` |
| `/query/snapshots/{hash}/symbols` | For a delegate caller, authorized by grants in place of `AuthorizedForSnapshot`'s policy check (`ServiceApp.cs:422-433`), with the same uniform 404 |
| `/control/ensure` with `act=user` | The repository must be visible → otherwise 403 `not_granted`. A visible repository's ensure is then bounded (SX-6d, see "As implemented (SX-6d)" below): no `default_branch: true`, no `branch_head_sequence`, and either `expected_head_commit` with a `branch_name` or `branch_update: none`. Trigger (`act=application`) and assertion-less control calls are unchanged |
| `/control/status/{id}` with `act=user` | 404 unless the job's repository is visible (#77 opaque ids stays N) |
| `/control/resolve` with `act=user` | The same bare 404 as an absent repository unless the repository is visible. Checked before the branch is resolved. Trigger (`act=application`) and assertion-less control calls are unchanged |

**`list_repositories` MCP tool** (in `Sextant.Service`, added to `RemoteQueryTools`):
- Input: `{}`.
- Output: `{repositories:[{repository, sources, branches:[{branch, is_default, status, commit_sha?, published_at?}]}], meta}`.
- A request with no caller gets `caller_required`.

**Tests:**
- Migration up plus the schema version.
- Actor rules for every route; `principal_in_body`.
- Union visibility.
- **Cross-tenant:** with the same user id under a different kid/tenant, nothing is visible.
- Revocation takes effect on the next call.
- Cross-repo tools are filtered.
- An ungranted repository gets a uniform not-found.
- Implicit selection.
- The limits; the ensure/status/resolve `act=user` gates; audit rows.
- Reconcile targets contain no user ids.

### As implemented (SX-6)

The contract above is implemented as written. These are the places where the code is more specific than the spec, or deliberately differs from it.

**Placement.**
- The storage is `Sextant.Store/RepositoryGrantStore.cs` over migration `024_repository_grants.sql`, exactly as the schema above.
- The service logic is `Sextant.Service/SnapshotService.Grants.cs`. The pure pieces are in `Sextant.Service/Grants/`: `RepositoryGrantKey`, `GrantReadAuthorizer`, `ListRepositoriesTool`, `CallerContext` and the contracts.
- The routes and the per-request visibility cache (`CallerVisibility`) are in `Sextant.Service.Host/GrantEndpoints.cs`. `ServiceApp.cs` gains only the authorizer install, the ensure, status and resolve gates, implicit selection and one `GrantEndpoints.Map` call.
- `GrantReadAuthorizer` is built over a `Func<IReadOnlySet<string>?>` (the caller's visible keys) and the catalog's id → URL resolver. `GrantReadAuthorizer.IsVisible(url)` is public so a repository-set filter (SX-7) can reuse the same rule.

**Repository key (differs from the schema comment).**
- `repository_key` is `RepositoryGrantKey.Of(url)`, the SVC-5 canonical `https://{host}/{owner}/{repo}` form (folded by `RemoteUrlIdentity.Normalize`), not a bare `Normalize`.
- The key is computed under the policy's shape rules only, ignoring the host and owner allow-lists, so a catalog repository keeps its key when an operator narrows them.
- A URL outside the shape rules (a test `file://` fixture, for example) falls back to `Normalize`. Grants, catalog repositories and job rows are all compared through this one function.

**Admission.** The spec is silent on a missing or non-matching caller, so the grant routes use these responses:
- **No verified caller:** 401 `{"error":"caller_required"}` with `WWW-Authenticate: Bearer error="invalid_request"`, like `/query/*` in SX-5.
- **Wrong `act`:** 403 `{"status":"rejected","reason":"wrong_actor"}`.
- **`sub` is `*`:** a user whose `sub` is exactly `*` (the reserved tenant-wide principal) gets 403 `{"error":"caller_not_allowed"}`, so a user can never read or write the tenant's grants.
- **Unchanged from SX-5:** the control token is still required first, and SVC-3's steps 10-11 policy failure is still 403 `caller_not_allowed` from the gate.

**`principal_in_body` (security checklist item 10).**
- A request is refused with 400 `principal_in_body` when a query key or a top-level body member is one of `tenant_id`, `tenantid`, `tid`, `principal`, `sub`, `user`, `user_id` or `userid`, matched case-insensitively.
- The check runs before the URL is read, so a request that names a principal always gets this reason.

**Request validation (beyond the table).**
- **PUT body:** a JSON object of at most 16 KiB. `repository` must be a string. `branch` is optional and may be a string or null. A duplicate member, a non-string value, or a body that is not an object → 400 `invalid_body`.
- **DELETE query:** a repeated `repository` or `branch` parameter → 400 `invalid_body`.
- **Branch:** a branch longer than 255 characters, or one containing whitespace, a control character or `*` → 400 `branch_not_allowed`. The exception is `branch=*` on DELETE, which means all branches.
- **Scope:** `GET /control/grants` without exactly `scope=tenant` (case-sensitive) → 400 `invalid_scope`.
- **URL:** a refused URL is `400 {"status":"rejected","reason":<SVC-5 code>}`. It is audited with no repository scope and without echoing the URL.
- **Revocation:** a DELETE is still accepted when the URL is refused only because its host or owner is no longer allow-listed (`RepositoryGrantKey.EvaluateForRevocation`), so a grant made before the allow-list was narrowed can always be revoked.
- **Re-PUT:** a re-PUT of an existing grant returns `created:false`. It refreshes `updated_at` and keeps the first-submitted `remote_url` and `source`.
- **Deleting nothing** is `{deleted:0}`, not an error.
- **`resolved_branch`** in `GET /control/grants/self` is omitted while the default branch is unknown.

**Limits.**
- `SEXTANT_SERVICE_MAX_GRANTS_PER_PRINCIPAL` (default 200) counts one user's grants in a tenant and does not apply to the `'*'` principal.
- `SEXTANT_SERVICE_MAX_GRANTS_PER_TENANT` (default 5000) counts every grant row of the tenant, `'*'` included.
- Both limits are checked only when a PUT would create a row, inside the write transaction, so a refresh of an existing grant is never refused. A refusal is `409 {"status":"rejected","reason":"grant_limit"}`.

**Writes.**
- A grant write uses its own catalog connection (`BEGIN IMMEDIATE`, `busy_timeout` 30 s), not the service write gate, so it never waits behind a long-running index production.
- The writer lease is checked before the transaction and again before `COMMIT`. A service that lost the lease writes nothing and answers 503 `unavailable`, and so does a disposed service.
- The accepted audit row is written in the same transaction as the grant.

**Audit.**
- Every grant write, accepted or denied, is an `AuditActions.Grant` row:
  - accepted details: `put_self;created|updated`, `put_tenant;…`, `delete_self;deleted_<n>` and `delete_tenant;…`;
  - denied details: `<operation>;<reason>`;
  - the repository scope is the key, or null when the URL was refused.
- A refusal before admission (401 `caller_required`) is audited with the bearer as actor. `GET` routes are not audited.
- A user's `not_granted` ensure is an `ensure`/`denied` row with detail `not_granted` and the repository key as scope.
- **The audit-detail suffix SX-5 deferred** is appended to every caller-attributed control action (ensure, branch retire, retention, backup and grant): `;idp=…;kid=…;via=…;cid=…;dep=…;jti=…`.
  - A value is written only when it is at most 64 characters of `[A-Za-z0-9._:-]`, else as `-`, so a claim can never inject a separator, a control character or an unbounded value.
  - An application caller has no `idp`, so its suffix has `idp=-`. A caller whose assertion omits `dep` (SX-5b) has `dep=-`.
  - A call with no assertion (bearer actor) has no suffix.

**Visibility and selection.**
- **Caching:** the caller's visible keys are read from the catalog at most once per HTTP request and cached in `HttpContext.Items`, never across requests. The stateless `/mcp` carries one JSON-RPC message per request, so a revocation takes effect on the caller's next call.
- **Composite:** `CallerReadAuthorizer` routes a delegate-token request to `GrantReadAuthorizer` (`IsEnforcing=true`; a request with no verified caller sees nothing). Every other request keeps the configured authorizer: `PolicyReadAuthorizer` under `READ_POLICY`, else `AllowAll`. The composite is installed only when `DELEGATE_TOKENS` is configured.
- **Implicit selection** applies only to a delegate request that names neither a repository nor a branch, whether by header or by reserved argument. A branch without a repository stays `repository_required`, as in SVC-2.
- **Cross-repo tools** with a verified caller default the selection to `provider_repository_url` (SX-5). The provider must therefore be visible and have a complete default-branch snapshot, and each listed consumer repository is then filtered by `AuthorizeRepository`.
- **Ensure gate order:** the URL policy (400) is checked first, then `not_granted` (403), then the branch guards (400), then, for a user caller only, the SX-6d body bounds (400, see "As implemented (SX-6d)").
- **Status gate:** a job on a repository the user caller cannot see is the same bare 404 as an unknown id.
- **Resolve gate** (added in review): `GET /control/resolve` by a user caller checks visibility before resolving the branch, so an ungranted repository is the same bare 404 as an absent repository or branch. Application and assertion-less callers are unchanged.
- **Every other control route refuses user callers** (SX-6c, #193). A verified `act=user` caller reaches only the routes that apply their own user rule: ensure, status and resolve (the gates above) and the grant routes (their `act` rule). Every other control route returns 403 `{"error":"caller_not_allowed"}`. That covers branch retire, retention, backup, metrics, audit, pilot and any route added later, because the rule is default-deny (`ControlCallerRules`: a route opts in with `.DecidesUserCallers()`, and a route-inventory test pins the admitted set).
  - **Order:** the refusal runs in the control middleware after the assertion is verified (a bad assertion is still 401, and an SVC-3 idp/app refusal is still its own 403) and before the route binds its request or reads any catalog state. The response is identical whether the named repository or branch exists.
  - **Retire:** refused even when the user can see the repository. "Require visibility" was rejected: a grant makes a repository readable, and a user retiring a branch of a repository other users watch would be a destructive cross-user action. The app's retire flows (repository delete, reconcile) run as `act=application`.
  - **Audit:** a refused retire, retention or backup writes a `<action>`/`denied` row with detail `caller_not_allowed` plus the caller suffix, attributed to the user caller and with no repository scope (the refusal precedes reading the request). The `GET` routes (metrics, audit, pilot) are not audited, like the grant `GET`s.
  - **`/control/audit`** is therefore never readable by a user caller, so a user cannot read another caller's rows.
  - Application and assertion-less callers are unchanged on every route. An unknown `/control/*` path is also 403 for a user caller (uniform, no route oracle), and still 404/405 otherwise.
- **Header selection:** an `X-Sextant-Repository` header is authorized like a `repository` argument; it never bypasses the grant. When both are sent and name different repositories, the call fails with `selector_conflict` before any grant or catalog lookup.
- **Failure:** a visibility read that fails (the grant catalog cannot be read) propagates out of the authorizer, so the call fails (a tool error, or a 5xx on HTTP) and never reads as allowed. A grant write that cannot be recorded (lease lost, service disposed) is 503 `unavailable`, and so is a user's ensure whose `not_granted` row cannot be recorded; neither writes a row.

**`list_repositories`.**
- It is served only to a verified caller. A request with no verified caller (a legacy query token, for example) gets the `caller_required` tool error.
- It lists every catalog branch of each visible repository, plus any granted branch the catalog does not know yet (status `missing` or `pending`).
- `meta.index_freshness` is the newest `published_at` among the listed branches.
- It is in `ToolSelectionFilters.SelectionExemptTools`, so `tools/list` does not add the reserved `repository`/`branch` arguments to it.

### As implemented (SX-6d)

Issue #198. A grant made a user caller's ensure body count in full, so a user could change branch state that every reader of the repository shares. SX-6d bounds that body and refuses to start the service with an open control plane.

**User ensure bounds.** `EnsureSnapshotRequest.UserCallerBranchProblem()` (`Sextant.Service/ServiceContracts.cs`) applies to a verified `act=user` caller only. Application callers and assertion-less control calls are byte-identical to before. The checks read the body only, never the catalog, in this order:

| # | Body | Response |
|---|---|---|
| 1 | `default_branch: true` (it would make the branch the sole default every reader resolves) | 400 `default_branch_not_allowed` |
| 2 | Any `branch_head_sequence` (a maximal one would move the pointer and block every later sequenced advance) | 400 `branch_head_sequence_not_allowed` |
| 3 | `branch_update: none` | allowed: it moves no pointer, so rules 4 and 5 do not apply |
| 4 | No `expected_head_commit` (an unguarded advance) | 400 `branch_guard_required` |
| 5 | A missing or blank `branch_name` | 400 `branch_required` (reused from SVC-6+7) |

- **Why rule 5.** An ensure that names no branch claims the default by the legacy rule (`default_branch ?? branch_name is null`) and is filed under `main`. Without rule 5, a user CAS with `expected_head_commit: ""` against an absent `main` would create `main` as the sole default.
- **Allowed.** `default_branch: false` is allowed. So is `forced` (informational only). A user CAS moves only the branch it names, and a stale CAS attaches without moving anything (SVC-6+7).
- **Response shape.** The response is `400 {"status":"rejected","reason":<code>}`, the same shape as the other ensure intake refusals, and it comes before any job row exists.
- **Audit.** The refusal is audited `ensure`/`denied`, with detail `<code>` plus the caller suffix and the repository key as scope, exactly like `not_granted`. It is written on the grant connection, so it never waits behind a production. When it cannot be recorded, the response is 503 `unavailable` and no row is written.

**Order (no oracle).** URL policy (400) → `not_granted` (403) → the SVC-6+7 guard conflicts (`conflicting_branch_guards`/`invalid_branch_update`, 400, every caller) → the user bounds (400).
- Visibility is decided first. A user who cannot see the repository gets the same `not_granted` for every body shape, so a bound refusal never reveals anything about a repository the caller cannot see.
- The bounds read nothing else, so their answer is the same whether the visible repository is indexed or not.
- A body that trips both an SVC-6+7 conflict and a user bound keeps the existing conflict code.

**Residual risk.**
- A user CAS still moves the branch it names, the default branch included, from the head the user observed to any commit the user names, because the service cannot verify the upstream head. The app checks the head against GitHub before it sends the CAS (`start-indexing`, `grant-watch`). Closing this fully needs server-side head verification, which is out of scope.
- A user with a grant can still create the repository's first branch. It no longer becomes the default unless the remote names it (#199, next).

**No implicit default for a user (issue #199).** The #104 safety net makes a repository's first branch its default. For a user caller that would pick the branch every reader resolves, so the service grants it only for the branch the remote itself names as its default. The service determines that branch itself and never reads it from the request.
- **Marking.** After the user bounds pass, the route sets `EnsureSnapshotRequest.RestrictsImplicitDefault` on a user caller's request. The flag is `[JsonIgnore]`, so it is never bound from the wire, and it is not part of the identity. Application and assertion-less requests are never marked, so their behavior is byte-identical.
- **Lookup.** `SnapshotService` does the lookup at the start of every restricted ensure, before the write gate:
  - It first discards any `VerifiedRemoteDefaultBranch` a direct caller preset.
  - It then looks the default up only when the answer can matter: the repository has no default branch yet (once a default exists the safety net cannot apply), and the ensure may move a pointer (`branch_update: none` never does).
  - The lookup runs off the request thread, bounded by the service lifetime (a shutdown during it is the ensure's usual 503).
  - A restricted ensure that does look the default up waits for it before it is registered, `?wait=false` included. The wait is bounded by the resolver's timeout.
- **Bounds (`RemoteDefaultBranchLookup`).** Because every such user ensure would otherwise spawn git, the lookups are bounded:
  - Concurrent lookups of one repository (keyed by `RepositoryGrantKey.Of`) share one call.
  - An answer, `null` included, is remembered for 60 s.
  - At most 4 calls run at once. A lookup of another repository beyond that answers `null` at once, without a call and without remembering it, so it fails closed.
  - The remembered table is pruned at 1024 repositories.
- **Resolver.** In clone mode the resolver is `CloningCheckoutProvider`:
  - It runs `git ls-remote --symref --end-of-options <url> HEAD` in a fresh `git init` temp directory under the checkout root, which it deletes afterwards.
  - It uses the same hardened environment and credential scoping as a clone, with a 30 s timeout.
  - `RemoteDefaultBranch.ParseSymref` accepts only exactly one `ref: refs/heads/<name>\tHEAD` line whose name has no whitespace or control characters and is at most 255 characters.
- **Where it applies.** Every branch-advance path passes the result as `allowImplicitDefault` to `SnapshotStore.ShouldOwnDefault`:
  - the worker path, where the orchestrator's `AdvanceBranchToSnapshot` reads it via `SnapshotContext.AllowImplicitDefault`;
  - on reuse, the CAS guard, the sequence path and the null-sequence attach.
  
  A restricted request gets the first-branch default only when its branch equals the verified name (ordinal). The explicit `default_branch: true` rule and "already the default" are unchanged, and a user cannot send `default_branch: true` anyway.
- **Fail closed.** The branch is created non-default when there is no resolver (locate mode has no outbound git), when every lookup slot is busy, or when the lookup fails, times out or yields nothing parseable. The repository then has no default until something sets one:
  - an application ensure (a push or reconcile with `default_branch: true`, or its own first branch);
  - a user ensure of the verified remote default.
  
  In locate mode, therefore, a user never makes a default implicitly.

**App contract.** Every user-caller ensure the app sends (SX-9 `start-indexing`, and `grant-watch` step 3 in `configure-watched-repos`) must be either a CAS with a `branch_name` or `branch_update: none`. It must never carry `default_branch: true` or a sequence.
- `start-indexing` without a `branchName` must send `branch_update: none`.
- `grant-watch` must resolve the default branch name (`GetRepository.defaultBranch`) for `watch {repo}`. On a repository with no default yet, that branch becomes the default only because the service independently confirms that the remote's `HEAD` names it (#199). A first `watch {repo} on {branch}` of another branch leaves the repository without a default until a trigger sets one.
- The trigger flows (push, pr, reconcile) run as `act=application` and keep `default_branch`.

**Control token required at startup.**
- `ServiceOptions.ValidateControlPlane()` fails startup when `SEXTANT_SERVICE_CONTROL_TOKEN` is unset. It runs in `ServiceHostRunner.RunAsync` before the catalog opens and in `ServiceApp.RegisterServices`.
- The only way to run without a token is the explicit opt-out `SEXTANT_SERVICE_INSECURE_OPEN_CONTROL_PLANE=true`, meant for a loopback-bound dev service or tests. It is off by default, it is ignored when a token is set, and a malformed value fails startup.
- While it is in effect, the host prints and logs `ServiceOptions.OpenControlPlaneWarning`.
- A whitespace-only control token also fails startup, even with the opt-out, because no bearer can ever match it.
- The offline `backup`/`restore` commands serve nothing and do not run the guard.

**Tests.**
- `UserEnsureBoundsHttpTests` covers each refused body, the not-granted-first order, the CAS and `none` paths for a user, application and assertion-less callers unchanged, and the startup guard with its opt-out.
- `ServiceOptionsEnvTests` covers the env binding.
- `ImplicitDefaultBranchTests` (#199) covers each case through a real orchestrator on both the worker path and the reuse path:
  - a user's first branch that the remote does not name is not the default;
  - a user's first branch that the remote names is the default;
  - an application's first branch is the default, with no lookup;
  - an application `default_branch: true` still moves the default;
  - an unknown, throwing or missing resolver fails closed;
  - there is no lookup once a default exists, or under `branch_update: none`;
  - a preset verified branch is discarded.
  
  It also covers the HTTP marking, the wire contract, the worker's context mapping, the store rule and `ParseSymref`. `RemoteDefaultBranchLookupTests` covers the bounds: a shared call, the 60 s memory, the concurrency cap failing closed, a throwing resolver, and the table bound. `CloningCheckoutProviderTests.ResolveDefaultBranch_*` covers the resolver against real `file://` remotes.
- `KestrelControlPathTests` (issue #198 item 4) sends raw request targets to a real Kestrel socket. Targets it normalizes onto `/control/branches/retire` (`%2e%2e`, `./`, `%63ontrol`, case, absolute-form) get the user refusal and are audited. Targets that match no route (`;x`, `//`, `%2f`) are 403 under `/control` or 404 outside it. The same targets without a token are 401 or 404, and none retires the branch.

**Not done.** #198 item 3 (rate-limiting denied-audit writes) is deferred. So is item 4's second gap (the route-inventory test still covers only `/control/*` patterns).

## SVC-F (SX-7): federated `search_symbols` (R, parity)

**Replaces** PS's gateway-native `sextant.search_symbols` and `sextant.list_watched`; `list_watched` becomes `list_repositories`.

**Semantic reference:**
- Targets, auth and cursor handling: PS:`src/ProcessStack.Api/Mcp/SextantMcpProxy.cs:390-452`.
- Result shape: PS `:553-600`.
- Input schema: PS `:740-778`.
- Page defaults: PS `:505-509` (50/200).
- Field names follow the service page, because PS's DTO does not match it (overview finding 2).
- **Renames.** Input `repo` → `repository` (consistent with SVC-2). Output is snake_case (`next_cursor`, not `nextCursor`).

| Input | Type | Rule |
|---|---|---|
| `repository` | string? | Narrows to that repository's visible grants. `owner/repo` or a URL, canonicalized by SVC-5 |
| `branch` | string? | Narrows to grants on that branch (ordinal; a `''` grant matches its resolved default branch name). **Absent = every granted branch** of each target, as in PS (`:406-414`) |
| `name_prefix` | string? | **N, additive.** Case-insensitive prefix on `display_name` (PS had no text filter) |
| `cursor` | string? | Opaque; a non-string is rejected |
| `limit` | int? | Per snapshot; default 50, clamped to 1…200. Since SX-7b also within the per-call hit cap (`SEARCH_MAX_HITS`) |

`additionalProperties: false`.

**Algorithm:**
1. Targets = the caller's visible grants, narrowed by `repository`/`branch`, → branch pointer → a complete snapshot.
   - An empty target set gives the tool error `no_visible_repositories`. This error is uniform: an ungranted `repository` looks the same as no grants (PS: "you have no watched repos to search", `:416-423`).
2. Dedup by `identity_hash` and order by it (ordinal).
3. Cap the width at `SEARCH_MAX_WIDTH` (default 50); overflow is deferred into the cursor.
4. Fetch each snapshot's page with `LocalBaseSnapshotSource` (`src/Sextant.Store/BaseSnapshotSource.cs`; cursor = `symbols.id`, `SnapshotPageRequest.CursorId` `:91-100`).
5. Merge by `display_name` (ordinal) → `identity_hash` → cursor.
6. A snapshot that fails to read goes into `unavailable` and keeps its resume position.
7. A granted target that has no complete snapshot goes into `pending`.

**Cursor:** `base64url(JSON {v:1, s:{hash: cursorId|"done"}, d:[deferred hashes]})`, at most 16 KiB. On resume, **only hashes still in the caller's visible set are honored**, so a forged or stale hash is dropped. It needs no HMAC because it is re-authorized on every call.

**Output:** `{symbols:[{repository, branch, identity_hash, symbol_key, name, fully_qualified_name, kind, accessibility, project}], next_cursor, pending:[{repository, branch}], unavailable:[{repository, branch}], meta}`. `kind` and `accessibility` are rendered as enum **names**. Delivered as a text block plus `structuredContent`.

**Tests:**
- Two-repository merge order.
- Resume across pages.
- Width-cap deferral.
- A grant revoked mid-pagination drops its hash.
- A forged hash is ignored.
- `pending`; `unavailable`; the branch filter; narrowing; the limit clamp; a non-string cursor.

### As implemented (SX-7)

The contract above is implemented, with the differences below. The user docs are in `docs/service.md` ("`search_symbols` (SVC-F)").

**Placement.**
- The tool is `Sextant.Service/Search/SearchSymbolsTool.cs`, a service-only tool like `list_repositories`. It is last in `ServiceApp.RemoteQueryTools`, so the local stdio server never registers it and its `tools/list` is unchanged.
- The search is `Sextant.Service/SnapshotService.Search.cs`. The cursor codec is `Search/SymbolSearchCursor.cs`, and the DTOs are in `Search/SymbolSearchModels.cs`.
- `ServiceApp.cs` gains only the tool type and one `tools/list` filter (`SearchSymbolsTool.ListToolsFilter`), which advertises the tool's own strict schema.
- `RepositoryUrlPolicy.EvaluateSelector` (moved from `ToolSelectionFilters`) parses the `repository` argument the same way SVC-2 parses the reserved one.

**Inputs (differs from the table).**
- **`name_prefix` is required** (1-256 characters, trimmed). A required prefix keeps every call a filtered read, and a search without one is what `list_repositories` plus the per-repository tools already cover.
- The prefix is a literal: `%`, `_` and `\` are escaped in the `LIKE` pattern. SQLite's `LIKE` folds ASCII letters only, so case-insensitivity is ASCII-only. Since SX-7b the page seeks the prefix's `NOCASE` key range, which folds exactly the same letters, and the `LIKE` stays as the exact residual.
- **`kind` (N, additive):** an optional lowercase `SymbolKind` name, advertised as an `enum`.
- The tool parses its raw arguments itself, so a wrong type, an unknown argument or an unknown `kind` is the tool error `invalid_arguments`. A `repository` the URL policy refuses is `invalid_selector`.
- `limit` is clamped, never refused, and it is not bound to the cursor, so it may change between pages.

**Callers and selection.**
- There is no assertion-less mode. A request with no verified caller (a plain query token, or a delegate token without an assertion) gets `caller_required` and reads nothing. The tool did not exist on the remote surface before, so nothing is narrowed or widened.
- Visibility is `RepositoryGrantStore.ListVisible` over the SX-6 subject (a user's `sub` plus `'*'`; an application's `'*'` only), read inside the same `ReadCatalog` transaction as the branch pointers and every page. A failed read propagates, so the call fails, and it is never an empty result or an unscoped read.
- The tool is in `ToolSelectionFilters.SelectionExemptTools`, and it **ignores the `X-Sextant-Repository` header**: only its own arguments narrow the search.
- **Branch.** With no `branch`, each granted branch of each visible repository is searched, and a `''` grant is the default branch. An explicit `branch` matches a granted branch (or a `''` grant whose default branch has that name) **or any indexed branch of a visible repository**, because SVC-4 visibility is repository-level.
- Branches whose heads point at the same snapshot are searched once, and the rows are labeled with the first (repository key, branch) in order.

**Algorithm (differs from step 4).**
- The pages are read with one constant, parameterized SQL statement over `snapshot_projects` → `symbols`, not `LocalBaseSnapshotSource`, which has no name filter. As shipped in SX-7 it walked each snapshot in id order (`id > @after`, `display_name LIKE @pattern ESCAPE '\'`), a scan of the snapshot's symbols; SX-7b (below) replaced it with an index seek.
- Each snapshot reads one row more than it may return to decide whether it has more. A `SqliteException` for one snapshot makes it `unavailable`, and it keeps its position. The whole call is one read transaction.
- **`truncated` (N, additive):** the tracked targets not read on this call because of the width cap, plus the ones not tracked yet. A persistently unavailable snapshot keeps `next_cursor` non-null.
- `SEARCH_MAX_WIDTH` (`SEXTANT_SERVICE_SEARCH_MAX_WIDTH`, default 50) is clamped to 100.
- **Fairness (issue #176, point 2).** Up to 100 snapshots (`ServiceOptions.SearchMaxWidthCeiling`) are tracked at a time, in hash order. Each call reads at most `SEARCH_MAX_WIDTH` of them **round-robin in rounds**: a page reads, in hash order, only tracked snapshots after the cursor's rotation point `r` (the previous page's last read) and never wraps past the end, so the last page of a round can read fewer than the width. Once no tracked snapshot is after `r`, the next page starts a new round from the lowest hash. A not-yet-searched snapshot is admitted only while no tracked snapshot is waiting at or before `r` for the next round, so it joins at the tail of a round. It never jumps ahead of a long snapshot that is waiting. With V visible snapshots, each tracked one is read at least once every ⌈min(V, 100) / width⌉ pages (strictly, ⌈N / width⌉, where N is the tracked count on the last page before its read on which admission was allowed) (`DeferredSnapshot_IsSearchedWithinABoundedNumberOfPages`, and `LongSnapshot_IsNotStarvedByAdmissions_BeyondTheTrackedCapacity` with 151 visible snapshots). An `unavailable` snapshot keeps its position and is retried within the same bound, not necessarily on the next page. Beyond 100 visible snapshots, the rest wait for tracked ones to finish: a bounded cursor cannot carry a position for every one of an unbounded set. The cursor holds at most 100 positions, so it always fits in 16 KiB.
- **Resolve fan-out (issue #176, point 1).** The gateway's concern was one HTTP resolve per target. In the service, resolution is in-process indexed lookups (the branch pointer and snapshot row per target, and since SX-7b the repository ids in one batched query over the caller's keys) in the same read transaction, and the target count is bounded by `MAX_GRANTS_PER_PRINCIPAL` plus the tenant's `'*'` grants (at most `MAX_GRANTS_PER_TENANT`). The per-snapshot pages are bounded by the width and, since SX-7b, by the call's hit budget. Resolution is not batched across pages, because the dedup by identity hash must see every target to guarantee no duplicates.

**Cursor (differs from `{v, s, d}`).**
- The cursor is `base64url(JSON {"v":2, "a":[[hash, afterId], ...], "w":watermark|null, "r":rotation|null, "b":digest})` (`v` was 1 before SX-7b; see below).
  - `a` holds the snapshots still being paged.
  - `w` is the greatest identity hash admitted so far. Any visible hash after it has not been searched yet, which replaces the deferred list.
  - `r` is the last snapshot the page read, where the next page's round-robin starts. It is only a comparison point (at most `w`, and only with a `w`), so a forged value reads nothing.
  - `b` is an unkeyed SHA-256 over the state and the binding: the tenant, `user:{sub}` or `app:{app}`, `name_prefix`, `kind`, the repository key and `branch`.
- A tampered, malformed or oversized cursor, or one issued to another caller, tenant or query, is `invalid_cursor`.
- The digest is not authorization. Only positions whose hash is still visible are honored, so a revoked, forged or unknown hash is dropped silently, and none of them is an oracle.
- A snapshot superseded mid-pagination is dropped. Its replacement is searched only if its hash sorts after the watermark.

**Output.**
- `kind` is the lowercase `SymbolKind` name and `accessibility` is `SymbolStore.FormatAccessibility`, which is how the local tools render them. The snapshot page `/query/snapshots/{hash}/symbols` still emits integers and is unchanged.
- `next_cursor` is always present (null at the end). `project` is the project's canonical id.
- `meta.index_freshness` is the newest `published_at` among the snapshots read on this call.
- Every error, including `caller_required` and `no_visible_repositories`, is an `isError` tool result. `list_repositories`'s `caller_required` is not.

### As implemented (SX-7b): bounded per-call cost (issue #196)

A security review of SX-7 found no access-control issue, but it found four ways one authenticated call could do unbounded or O(total symbols) work. SX-7b bounds all four. The access rules above are unchanged: grants are re-read on every call, cursor positions are intersected with the fresh visible set before any SQL, an ungranted `repository` is byte-identical to an absent one, and a failed grants read is an `isError` failure.

**Prefix index (migration `025_symbol_name_prefix_index`).**
- The migration adds `ix_symbols_project_name_nocase ON symbols(project_id, display_name COLLATE NOCASE)`. The row id is the index's implicit last column, so the index orders by (project, folded name, id).
- `NOCASE` folds ASCII letters only, exactly like `LIKE`. `NoCasePrefixRange` computes the prefix's key range in C#. `lo` is the prefix with its ASCII letters lower-cased, which is how `NOCASE` compares. `hi` is `lo` with its last code point replaced by the next one in `NOCASE` order, or null when no successor exists. The `LIKE` stays as the exact residual.
- `SearchPageSql` forces its plan with `CROSS JOIN` and `INDEXED BY`: each of the snapshot's projects is one seek into `[lo, hi)`, stopping after the rows it may return. There is no scan of the snapshot and no sort. `SearchSymbolsCostTests` pins the plans with `EXPLAIN QUERY PLAN`, with and without `ANALYZE` statistics, asserting a `SEARCH` step and no `SCAN` or `TEMP B-TREE` for:
  - the page query through `ix_symbols_project_name_nocase`, bounded and unbounded (`PageQuery_SeeksThePrefixRange_WithoutAScanOrASort`);
  - the resume lookup by primary key (`ResumeQuery_IsAPointLookup`);
  - the repository lookup (`RepositoryQuery_SeeksEachRange_WithoutReadingTheCatalog`).
- **Traversal order.** A snapshot is now paged in index order, (project id, `NOCASE` name, id), instead of id order. The merge order of a page's output is unchanged: (name ordinal, identity hash, row id).
- **Cursor v2.** A position's `afterId` is still the last row the snapshot returned (or, with a `kind`, examined). The page resumes after that row's (project, folded name, id), which `SearchResumeSql` looks up by primary key inside the snapshot. A forged `afterId` naming a row of another snapshot ends that snapshot's paging, and one naming a row outside the prefix's range restarts at the range's edge, so it still reads only rows of the visible snapshot. Because the traversal order changed, the cursor version is now `2` and a v1 cursor is `invalid_cursor`, so the client restarts the search.
- **Identity.** An index changes no row, so migration `025` is **identity-neutral** and forces no re-index. `IndexDatabase.LatestSchemaVersion` (the readiness gate, which runs the migration) is 25. Every snapshot identity folds `IndexDatabase.SnapshotSchemaVersion` instead: the highest migration that is not in `IndexDatabase.IdentityNeutralMigrations` (= {25}), which is still 24. So the one full re-index at the schema-24 cutover stays the only one. `SymbolNamePrefixIndexMigrationTests` asserts:
  - the indexes exist;
  - an upgrade from 24 keeps `index_runs`;
  - `SnapshotSchemaVersion` is 24;
  - an identity-neutral migration only creates or drops indexes.
  A service test asserts that the ensure identity folds 24.
- **Upgrade cost.** `CREATE INDEX` over an existing `symbols` table runs once, at the first open after the upgrade.

**Per-call hit cap.**
- `SEARCH_MAX_HITS` (`SEXTANT_SERVICE_SEARCH_MAX_HITS`, `ServiceOptions.SearchMaxHits`) defaults to 500 and is clamped to 100…5000, like the width. `limit` stays per snapshot within that budget.
- The snapshots of a page share the budget evenly, in turn order. Each gets `min(limit, remaining / snapshots left)`, and what one leaves unused carries to the next.
- The floor (100) is at least the width ceiling (100), so every snapshot in a page's turn gets at least one row. The round-robin turns, the fairness bound and `truncated` are therefore exactly as above. A snapshot that returned fewer rows than it has keeps its position and resumes on its next turn, so paging to the end returns every hit once, with no gap (`SearchSymbolsCostTests`).
- **`kind` examine bound.** A kind is filtered as rows are read, so a rare kind could otherwise walk every match of a broad prefix. With a `kind`, one call examines at most 8192 rows in all. The page's snapshots share them evenly, like the hit budget, so each examines at least ⌊8192 / width⌋ rows. A snapshot that runs out of its share resumes after the last row it examined, so a page can return fewer hits, or none, with a non-null `next_cursor` (`KindFilter_BoundsTheRowsExaminedPerCall_AcrossEverySnapshotItReads`).

**Repository lookup.**
- The caller's repository ids come from ONE query over the caller's own keys (`SearchRepositoriesSql`). It seeks each key's `NOCASE` range through `ix_repositories_remote_url_nocase` (also migration `025`), which also covers the key with the default `:443` port, and keeps a row only when its `RepositoryGrantKey` is exactly the key. So it no longer builds the whole-catalog map.
- A spelling outside those ranges is not found, so its target reads as `pending` and never as another repository. Such a spelling has leading whitespace or a non-ASCII letter in another case, and the SVC-5 intake refuses both.
- The branch pointer and snapshot row of each target are still point lookups. Their count is bounded by the grant limits, and they run in the same read transaction.
- `list_repositories`, the grants list, `/control/status` and implicit selection still use the whole-catalog `GrantCatalogReader.RepositoryId` map. They are outside #196's scope.

**Cancellation.**
- `SearchSymbolsTool` passes the MCP request's `CancellationToken` to `SnapshotService.SearchSymbols`. The call checks it before resolving, before each snapshot and as each row of a page arrives, and `ReadCatalogCancellable` (a separate name, so the other control-plane reads keep their no-cancellation contract) registers `sqlite3_interrupt` on the read connection for the length of the call, so a statement that is running stops even in a step that yields no row. The row check matters because `CancellationTokenSource` marks the token cancelled before it runs the interrupt callback: a step that completes in that window must not return its row.
- An interrupted statement surfaces as `OperationCanceledException`, never as an `unavailable` snapshot. The read transaction is committed only if it is still open (an interrupt can roll it back), and the connection is disposed as usual, so the next call reads normally (tests: a cancelled call reads no remaining snapshot; the next call returns every hit).
- Over HTTP, the MCP SDK's stateless `/mcp` disposes the request's session when the client disconnects, which cancels the handler's token. `SearchSymbolsHttpTests` asserts that an aborted POST stops the running page.

**Measured** (`SearchSymbolsPerformanceTests`, gated by `SEXTANT_RUN_PERF=1` and never run by CI; median ms per call, SX-7 → SX-7b):

| Shape | SX-7 | SX-7b |
|---|---|---|
| 1 snapshot × 500k symbols, no match | 68 | 0.17 |
| 1 × 500k, narrow prefix | 97 | 0.15 |
| 1 × 500k, broad prefix, `limit` 50 | 75 | 0.20 |
| 1 × 500k, broad prefix, `limit` 200 | 90 | 0.41 |
| 1 × 500k, broad prefix, `kind` = delegate | 84 | 16 |
| 20 snapshots × 25k, no match | 83 | 1.3 |
| 20 × 25k, broad prefix, `limit` 200 | 87 | 4.1 (capped at 500 hits; was 4000) |

## SVC-6+7 (SX-8): branch-pointer semantics (R)

**Today:**
- The forward-only guard is `branch_head_sequence` (#84): `SnapshotStore.AdvanceBranchPointerForwardOnly` `:624-638`. A **null** sequence advances unconditionally on the worker path (`src/Sextant.Indexer/IndexOrchestrator.cs:1690-1707`).
- The reuse paths are `SnapshotService.cs:1033-1053` (#162 attach-or-upgrade) and `:1070-1107` (sequence; `AdvanceOrAttachBranchPointer` / `…Core` `:1085`).
- There is no retire.
- `/control/resolve` has no `commit_sha`.

**`EnsureSnapshotRequest` additions** (`src/Sextant.Service/ServiceContracts.cs:14-98`). None of them enter `ToIdentity`.

| Field | Type | Meaning |
|---|---|---|
| `expected_head_commit` | string? | Push `before`. `""` or an all-zero SHA = "the branch must have no pointer" (branch create) |
| `forced` | bool? | Informational: audited, and **does not bypass the CAS** |
| `branch_update` | `"advance"` (default) \| `"none"` | `none` = publish or attach the snapshot, and create or move **no** branch pointer (PR head/base, historical commits) |

**Result addition:** `branch_advanced: bool?`.

**Precedence** (evaluated inside the existing branch-advance write transaction on every path: the worker `AdvanceBranchToSnapshot`, both reuse paths, and #85 re-select):

| # | Request carries | Decision |
|---|---|---|
| 0 | `branch_update: none` | No pointer change (the other guards are ignored) |
| 1 | `expected_head_commit` + `branch_head_sequence` | 400 `conflicting_branch_guards` |
| 2 | `expected_head_commit` | **CAS.** Advance iff the pointer is unset and the expected value is `""`, **or** the pointer's commit (`GetCommitSha`, `SnapshotStore.cs:454`) equals the expected value, **or** the pointer's target is unusable (not complete, or reclaimed). Otherwise attach only. The #85 re-select is allowed only when the CAS passes |
| 3 | `branch_head_sequence` | Unchanged #84/#85 |
| 4 | neither (NULL) | Unchanged: the worker advances unconditionally; reuse is #162 attach-or-upgrade |

- **Plumbing:** `SnapshotContext` gains the generic nullable fields `ExpectedHeadCommit` and `SuppressBranchUpdate`. The local path leaves them null, so it is byte-identical. A new store method is `SnapshotStore.AdvanceBranchPointerIfHeadMatches(branchId, snapshotId, expected, now)`.
- **Divergence from PS (decided at G1):** PS `BranchHeadGuard` made `forced` attach-only. **Here `forced` does not bypass the CAS:** the CAS on `before` already orders events, so a force-push whose `before` matches advances immediately instead of waiting for the nightly reconcile. Parity would have been "forced ⇒ attach only".

**Retire.** `POST /control/branches/retire`, body `{repository, branch, expected_head_commit?}`:
- Deletes the branch row; the snapshots remain for retention.
- Idempotent: a missing branch returns `200 {retired:false}`.
- A CAS mismatch → 409 `head_mismatch`, because a newer push re-created the branch.
- The default branch → 409 `default_branch`.
- SVC-5 policy applies; audited.

**`/control/resolve` additions** (`ServiceApp.cs:261-273`): `commit_sha`, `branch` (the resolved name), `is_default`, `head_sequence`.

**Tests:**
- CAS:
  - a match advances; a stale `before` attaches only;
  - branch create with no pointer advances; with a pointer it attaches;
  - forced with a match advances;
  - two pushes out of order converge on the newer one;
  - the worker path and the pre-built reuse path converge.
- Guards:
  - `none` never creates a branch row;
  - both guards → 400.
- Retire: with and without CAS; the default branch; idempotence.
- `resolve` returns `commit_sha`.
- The local CLI's branch state is byte-identical.

**As built (SX-8).** The table above is implemented as written. Where it left something open, the
implementation settled it as follows (see `docs/service.md`, "Branch-pointer guards"):
- **Absent branch.** "The pointer is unset" covers a branch that does not exist yet, and also an existing
  row whose pointer is null (retention reclaimed it). The null-pointer case is also "unusable", so it passes
  whatever the expected value is. The absent case passes only for `""` or all zeros. A non-empty `before` for
  a branch the service has never seen therefore attaches only; the client seeds the branch with `""` or a
  plain ensure.
- **Commit comparison.** The comparison is case-insensitive. A target with no recorded commit never matches,
  not even `""`, unless that target is unusable.
- **Attach only.** On a mismatch no branch row is created and no default is promoted. As a result, a stale
  push can never re-create a retired branch. A passing CAS supersedes the previous target only when it was
  `complete` and no other branch points at it (the #128 guard). It never writes `head_sequence`.
- **`none` (row 0) wins over the other guards,** including both together, so `none` plus both guards is not
  a 400. `none` still clears the provider-only flag on a reuse, matching the worker's `EnsureRepository`.
  Any `branch_update` value other than `advance` or `none` (including `""`) is `400 invalid_branch_update`,
  and so is the row-1 conflict. Both refusals are audited `ensure`/`denied` before any job exists.
- **The #85 re-select under the CAS.** Any guarded request (sequence, CAS or `none`) restores an intact
  superseded snapshot to `complete` without the worker, and re-points the branch only when its guard passes.
  This is exactly what the orchestrator's `SelectExistingSnapshot` does on the worker path (restore, then the
  guarded advance), so the two paths converge. An unguarded ensure still hands a superseded identity to the
  worker.
- **`branch_advanced`.** It is `true` when this request's decision moved the pointer onto the snapshot, and
  `false` when the decision declined or the pointer was already there. It is absent when there is no snapshot,
  or when the request shared a non-terminal outcome of another request's production.
- **Out-of-order convergence needs a retry.** A declined CAS returns `branch_advanced: false`. The newer push
  lands when it is re-sent after the older one (a cheap reuse), or when the reconcile catches up.
- **Retire.**
  - Order of checks: absent (`200 {retired:false}`, even under a CAS), then the default branch (409), then
    the CAS (409), then the delete. The CAS uses the same match rule as ensure.
  - A refused URL or a blank `branch` is a `400` whose body carries only the reason code, audited
    `retire`/`denied` with no repository scope.
  - The decision, the delete and the audit row (`retired`/`absent`/reason) commit in one write transaction
    under the single writer. Retire therefore waits behind a running production, as retention does.
  - Deleting the row also drops `head_sequence`, so a late **sequence**-guarded ensure can re-create a
    retired branch. The CAS path cannot. Mixing the two guards on one branch is unsupported.
  - **Caller (SX-6c, #193).** Retire is application/operator-only. A verified `act=user` caller is refused
    with 403 `caller_not_allowed` before the body is read, so the response is the same for an existing, an
    absent or a refused repository or branch, and the same even when the user holds a grant on the
    repository. "Require visibility" was rejected because a user retiring a branch of a repository other
    users watch would be a destructive cross-user action. The refusal is audited `retire`/`denied` (detail
    `caller_not_allowed` plus the caller suffix, no repository scope). Application and assertion-less callers
    are unchanged.
- **Not changed.** Overlays (local-only) and the contribution ingest's branch advance.
- **No migration.** The CAS reads the existing `branches`/`snapshots`/`commits` columns.
- **No format validation of `expected_head_commit`** beyond the empty/all-zero check.

---

## N units (not needed for the cutover)

| SVC | Unit | Note |
|---|---|---|
| 9 | Bounded background retry of transient requeues | #155 |
| 10 | Service-side reconcile over grants (`ls-remote`) | Needs #111 for private repos. The app covers this in the meantime |
| 11 | Per-request checkout token on ensure | #111; **R before a second org/tenant** (see `security.md`) |
| 12 | `GET /control/repositories`, `/control/snapshots` | Operator UX |
| 13 | MCP control tools | Not needed; the app exposes processes |
| 14 | #145 absolute paths, #134 scope fails open, #77 opaque ids | **Strongly advised before multi-tenant** |
| 15 | Identity-hash URL canonicalization | Needs a re-index plan |
| 17 | Scoped app control token (`APP_CONTROL_TOKENS`: ensure, status, resolve, grants, branches only; no retention, backup, pilot or contribute) | Least privilege for `sextant-control`. Until it lands, **assertion-less control calls stay full-power**: `CALLER_APPS` gates only assertion-bearing requests. N for the single-app, single-tenant cutover; **R before a second app or tenant** |
| 18 | `jti` replay cache (bounded, until `exp`) | PS-7 mints one assertion per HTTP request, so this is safe to add |
| 19 | HTTP routes for PR retention roots (`RegisterPullRequestSnapshot` / `ClosePullRequestSnapshot`, `SnapshotService.cs:1407,1447`) + the app on `pull_request.closed` | Keeps open-PR snapshots from being GC'd |
| 20 | `commit` selector arg (SVC-2 extension) | Lets agents query PR-head snapshots created with `branch_update: none` |

## PR order and conflicts

`ServiceApp.cs` is the hotspot:
- the DI block `:51-71`;
- the MCP builder `:93-102`;
- `RemoteQueryTools` `:115-125`;
- the middleware `:138-165`;
- the control maps `:216-362`.

| SX | Touches in `ServiceApp.cs` | Stack |
|---|---|---|
| SX-1 | the ensure route | Independent; land first |
| SX-3 | a comment at `:80-92` | Parallel; trivial rebase |
| SX-2 → SX-4 → SX-5 → SX-6 → SX-7 | DI, MCP filters, middleware, control maps, tool list | **Stacked** (same regions) |
| SX-8 | resolve route, new retire route | Parallel with the chain. Rebase after SX-6 (both add control maps). Also touches `SnapshotService`/`IndexOrchestrator`, which the chain does not |
| SX-12 | docs | Last |
