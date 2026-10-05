# The standalone Sextant index service (Phase 13)

The **Sextant index service** is a persistent, independently deployable **data plane** that hosts
committed-branch snapshots so a client only has to index its local diff, deduplicates shared submodules
once for everyone, and answers low-latency semantic queries over authenticated HTTP MCP. It is the server
the distributed-indexing initiative was built toward.

It lives in two projects that depend on the core libraries — **never the reverse**:

- **`Sextant.Service`** — the data-plane library: the `SnapshotService` control core, service contracts,
  on-disk volume management, caller identity, repository grants, and the service-only MCP tools
  (`list_repositories`, `search_symbols`). It references `Sextant.Core`, `Sextant.Store`, `Sextant.Indexer`
  and `Sextant.Mcp`.
- **`Sextant.Service.Host`** — the ASP.NET Core composition root: the HTTP surface, auth middleware, and
  MCP transport. It references only `Sextant.Service` (and reaches `Sextant.Mcp` through it). The CLI's
  `sextant service` command references the host.

> **Core stays ProcessStack-agnostic.** `Sextant.Core`, `.Store`, `.Indexer`, `.Daemon`, and `.Mcp` never
> reference the service or ProcessStack, and the service itself uses only generic concepts (a delegate
> token, a signed caller assertion, a tenant, a grant). `ArchitectureBoundaryTests` asserts the dependency
> direction.

> **ProcessStack is a client, through an app.** The ProcessStack integration is the `sextant` ProcessStack
> app, which has moved to a separate private repository. Its chat and GitHub triggers drive the control
> plane, and its MCP surface `/v1/{tenant}/mcp/sextant` forwards the service's query tools with each
> caller's signed identity. The older ProcessStack-embedded `_sextant` gateway is being retired (removed
> from the platform once the app has replaced it).
> See [Production deployment checklist](#production-deployment-checklist-gateways)
> before exposing the service to more than one user.

> **The service is additive, never required.** The local stdio MCP path and standalone local indexing
> remain fully functional with **zero** service dependency (acceptance criterion 6).

## Running it

```bash
# Single-node development on loopback. DB + volumes default under the repo's .sextant/service.
# The service refuses to start without a control token (SX-6d), so set one. Read it from an owner-only
# file (see "Generating and installing secrets" below), never type it inline where shell history keeps it...
SEXTANT_SERVICE_CONTROL_TOKEN="$(cat <secrets-dir>/control-token)" sextant service
# ...or, for a loopback-only dev service, opt out explicitly. INSECURE: /control/* is open, and a loud warning is logged.
SEXTANT_SERVICE_INSECURE_OPEN_CONTROL_PLANE=true sextant service

# Scaled deployment: point volumes at durable storage and require tokens, again read from owner-only
# files. A production service loads them from its owner-only env file instead (see the production
# deployment checklist).
SEXTANT_SERVICE_DB_PATH=/data/sextant/catalog.db \
SEXTANT_SERVICE_DATA_ROOT=/data/sextant/volumes \
SEXTANT_SERVICE_CONTROL_TOKEN="$(cat <secrets-dir>/control-token)" \
SEXTANT_SERVICE_QUERY_TOKEN="$(cat <secrets-dir>/query-token)" \
sextant service
```

The `sextant service` CLI command is additive; every other CLI command (`index`, `query`, `serve` for the
local stdio MCP, `retention`, …) is unchanged and needs no service.

## Configuration (`ServiceOptions`)

All settings bind from `SEXTANT_SERVICE_*` environment variables, falling back to the repo `sextant.json`
for the database path and to `<db-dir>/service` for the volume root, so `sextant service` needs nothing but a
control token (or the explicit dev opt-out below) to start.

| Env var | Purpose | Default |
| --- | --- | --- |
| `SEXTANT_SERVICE_DB_PATH` | Durable catalog + semantic store (the Phase-9 snapshot catalog) | repo `sextant.json` `db_path` |
| `SEXTANT_SERVICE_DATA_ROOT` | Root for the persistent volumes | `<db-dir>/service` |
| `SEXTANT_SERVICE_CHECKOUT_ROOT` | Persistent base-branch checkouts | `<data-root>/checkouts` |
| `SEXTANT_SERVICE_ARTIFACT_ROOT` | Persistent published artifacts | `<data-root>/artifacts` |
| `SEXTANT_SERVICE_CACHE_ROOT` | Bounded local caches (federation pages) | `<data-root>/cache` |
| `SEXTANT_SERVICE_SCRATCH_ROOT` | **Ephemeral** per-job worker scratch | `<data-root>/scratch` |
| `SEXTANT_SERVICE_CHECKOUT_MODE` | How a checkout is obtained: `locate` (index only an already-provisioned checkout) or `clone` (provision it by cloning the requested commit) | `locate` |
| `SEXTANT_SERVICE_CHECKOUT_TOKEN` | Access token for cloning a **private** `https` repo in `clone` mode (sent transiently as an env-scoped `Authorization` header to the repository's own host and same-host submodules only; public repos need none) | none |
| `SEXTANT_SERVICE_SUBMODULE_HOSTS` | In `clone` mode, comma-separated `host[:port]` list of **additional** hosts submodules may be fetched from — **anonymously** (the token is never sent to them). Invalid entries fail startup | none (same host only) |
| `SEXTANT_SERVICE_REPOSITORY_HOSTS` | Comma-separated DNS host names a `POST /control/ensure` repository URL may name (see [Repository URL policy](#repository-url-policy-svc-5)). `*` admits any host that passes the URL shape rules and logs a startup warning. A malformed entry **fails startup** | `github.com` |
| `SEXTANT_SERVICE_REPOSITORY_OWNERS` | Comma-separated `host/owner` (or `host/*`) allow-list for ensure and grant repository URLs; each host must also be allowed by `REPOSITORY_HOSTS`. A malformed entry **fails startup**. Optional for a single-user service, but **required in production whenever a tenant has members besides its owner** (see [Production deployment checklist](#production-deployment-checklist-gateways)) | none (any owner) |
| `SEXTANT_SERVICE_MAX_PROVISIONING_ATTEMPTS` | In `clone` mode, how many times a **transient** clone/provisioning failure is retried across re-ensures before the job settles to terminal `failed` (clamped to 1–100; deterministic failures are never retried). Also bounds the requeue of a checkout whose neutralized `global.json` SDK pin could not be restored (`sdk_pin_restore_failed`, #113), in any checkout mode | `5` |
| `SEXTANT_SERVICE_CONTROL_TOKEN` | Bearer token for `/control/*`. **Required:** the service refuses to start without it (SX-6d, #198). A whitespace-only value also fails startup | none (startup fails) |
| `SEXTANT_SERVICE_INSECURE_OPEN_CONTROL_PLANE` | **INSECURE, dev only.** Lets the service start with no control token, leaving the whole control plane (`/control/*`: ensure, retire, retention, backup, grants, audit) open to anyone who can reach the control port. The service prints and logs a loud `WARNING: INSECURE` line at startup while it is in effect. Ignored when a control token is set. A malformed value **fails startup** | `false` |
| `SEXTANT_SERVICE_QUERY_TOKEN` | Bearer token for `/mcp` + `/query/*` | none (anonymous read) |
| `SEXTANT_SERVICE_CONTRIBUTE_TOKEN` | Least-privilege token for `/control/contribute` only (issue #71); the control token remains a superset that also authorizes it | none (falls back to control token) |
| `SEXTANT_SERVICE_READ_POLICY` | Enforced query-plane read-authorization policy (Phase 17) | disabled (open read) |
| `SEXTANT_SERVICE_REQUIRE_REPOSITORY_SELECTION` | Require every `/mcp` read to name its repository, in the `repository` tool argument or the `X-Sextant-Repository` header; a read without one fails with `repository_required` (see [Repository selection](#repository-selection-on-mcp)). A malformed value fails startup | `false` (a read that names no repository reads the unselected default) |
| `SEXTANT_SERVICE_DELEGATE_TOKENS` | `;`-separated query-plane bearer tokens whose reads are decided per verified caller assertion (see [Caller assertions](#caller-assertions-svc-3)). Requires `CALLER_KEYS`; a token equal to the query, control, contribute or a read-policy token **fails startup** | none |
| `SEXTANT_SERVICE_CALLER_KEYS` | `;`-separated `kid=base64url-key@tenantId` HMAC keys that sign caller assertions. A key shorter than 32 bytes, a duplicate `kid`, or a malformed entry **fails startup** | none (caller assertions off) |
| `SEXTANT_SERVICE_CALLER_AUDIENCE` | The `aud` a caller assertion must carry. Required when `CALLER_KEYS` is set | none |
| `SEXTANT_SERVICE_CALLER_ISSUERS` | Optional comma-separated `iss` allow-list | none (any issuer) |
| `SEXTANT_SERVICE_CALLER_HEADER` | The request header that carries the caller assertion | `X-ProcessStack-Caller` |
| `SEXTANT_SERVICE_CALLER_IDPS` | Comma-separated identity providers whose users may act as `act=user` callers (`[a-z0-9-]{1,32}` each) | `processstack` |
| `SEXTANT_SERVICE_CALLER_APPS` | Optional comma-separated allow-list on the signed `app` claim (exact, case-sensitive match). ProcessStack fills `app` with the calling application's **id** (a ULID such as `01M389V5MBGQKSF18HC2EGC3FY`; see [Production deployment checklist](#production-deployment-checklist-gateways) for how to find it), not its slug or name, so list ids here. A slug makes every assertion-bearing call fail with `caller_not_allowed`, while health, discovery and connection tests still succeed | none (any app) |
| `SEXTANT_SERVICE_MAX_GRANTS_PER_PRINCIPAL` | Most repository grants one user caller may hold in a tenant (see [Repository grants](#repository-grants-and-visibility-svc-4)); creating one more is `409 grant_limit`. The tenant-wide `'*'` grants are not counted. A missing or non-positive value uses the default | `200` |
| `SEXTANT_SERVICE_MAX_GRANTS_PER_TENANT` | Most repository grant rows a tenant may hold, tenant-wide grants included; creating one more is `409 grant_limit` | `5000` |
| `SEXTANT_SERVICE_SEARCH_MAX_WIDTH` | Most snapshots one [`search_symbols`](#search_symbols-svc-f) call reads (round-robin over the tracked ones); the rest are listed in `truncated` and searched on later pages. Values above `100` are clamped to `100` (the most snapshots a search tracks at once, which keeps a cursor within 16 KiB); a missing or non-positive value uses the default | `50` |
| `SEXTANT_SERVICE_SEARCH_MAX_HITS` | Most symbols one [`search_symbols`](#search_symbols-svc-f) call returns in all; the snapshots it reads share it evenly (each still gets at least one row, so the round-robin is unchanged), and what a snapshot did not return is searched on its next turn. Clamped to `100`–`5000`; a missing or non-positive value uses the default | `500` |
| `SEXTANT_SERVICE_MAX_RESPONSE_CHARS` | Character budget of one `/mcp` tool result, measured on the text the caller receives. A paged tool cuts its page at the last row that fits and returns `meta.next_cursor` with `meta.page_truncated_by: "size"`; an unpaged one keeps the leading rows and says to narrow the query (see [mcp-tools.md](mcp-tools.md#response-size-budget)). At least `1000`; a missing or non-positive value uses the default. `search_symbols` applies it too, after its `SEARCH_MAX_HITS` cap | `20000` |
| `SEXTANT_SERVICE_BIND_ADDRESS` | Network interface the HTTP surface binds to | `localhost` |
| `SEXTANT_SERVICE_CONTROL_PORT` | HTTP port | `3011` |
| `SEXTANT_SERVICE_QUERY_PORT` | Optional dedicated query port (shares the control port when unset) | none (shared) |
| `SEXTANT_SERVICE_LEASE_TTL_SECONDS` | Single-writer lease TTL | `30` |
| `SEXTANT_SERVICE_CONTROL_WRITE_WAIT_SECONDS` | How long a `POST /control/ensure?wait=false` registration or a `POST /control/branches/retire` waits for the single writer before answering `202` with the write still queued (issue #158; see [Queued control writes](#queued-control-writes-issue-158)). Keep it well under the client's timeout. A value above `25` is clamped to `25`; a missing, non-numeric or non-positive value uses the default | `5` |
| `SEXTANT_SERVICE_PEERS` | Comma-separated peer base URLs for federation | none |
| `SEXTANT_SERVICE_REMOTE_TIMEOUT_SECONDS` | Per-request remote-fetch timeout | `10` |
| `SEXTANT_SERVICE_CONTRIB_REQUIRE_AUTH` | Require contribution authorization (Phase 16) | `false` (dev-open) |
| `SEXTANT_SERVICE_CONTRIB_REQUIRE_GIT_VERIFY` | Require Git-content verification of uploads (Phase 16) | `false` |
| `SEXTANT_SERVICE_CONTRIB_MAX_ARTIFACT_BYTES` | Max accepted contribution artifact size (Phase 16) | policy default |
| `SEXTANT_SERVICE_SANDBOX_ENABLED` | Enforce the evaluation sandbox (Phase 17) | `true` |
| `SEXTANT_SERVICE_SANDBOX_TIME_BUDGET_SECONDS` | Wall-clock evaluation time budget | policy default |
| `SEXTANT_SERVICE_SANDBOX_MEMORY_BUDGET_BYTES` | Watchdog memory ceiling | policy default |
| `SEXTANT_SERVICE_SANDBOX_ALLOW_NETWORK` | Allow network during evaluation (a best-effort posture, not a network block; it does not gate package restore, see [Package restore](#package-restore-before-the-load)) | `false` |
| `SEXTANT_SERVICE_SANDBOX_SCRUB_SECRETS` | Scrub secrets from the evaluation environment | `true` |
| `SEXTANT_SERVICE_SDK_PIN_OVERRIDE` | Temporarily neutralize a checkout `global.json` SDK pin that no installed SDK satisfies, so the checkout still indexes with an installed SDK (issue #113; see [SDK pins](#repository-globaljson-sdk-pins-issue-113)). `false` leaves such pins alone and the job fails / goes partial with a typed `sdk_resolution_failed` diagnostic. `false` is part of the snapshot identity, so flipping the toggle re-indexes a commit instead of reusing a result built under the other policy. An unparseable value **fails startup** | `true` |
| `SEXTANT_SERVICE_PACKAGE_RESTORE` | Run `dotnet restore` over the selected solutions before the load (see [Package restore](#package-restore-before-the-load)). `false` loads unrestored projects, which compile against their direct project references only, so calls into transitively referenced projects may not bind. `false` is part of the snapshot identity (`restore=off`). An unparseable value **fails startup** | `true` |
| `SEXTANT_SERVICE_PACKAGE_RESTORE_TIMEOUT_SECONDS` | Bound on one job's whole restore step (all solutions), clamped to 3600. On expiry the restore process tree is killed and the load goes ahead with whatever was restored | `300` |

Boolean toggles accept `1/0`, `true/false`, `yes/no`, `on/off` (case-insensitive); any other non-empty
value **fails startup** rather than silently disabling a security-relevant control (fail-closed). The
per-repository/profile `platform_routing` policy and the `retention` policy are read from the repo
`sextant.json` / `SEXTANT_*` config (see [configuration.md](configuration.md)).

`SEXTANT_SERVICE_BIND_ADDRESS` defaults to `localhost`, so out of the box the service listens on loopback
only — safe for single-node dev. A deployment reached from **other hosts or containers** (e.g. a container
on a Docker network) must set it to a routable interface such as `0.0.0.0`. Binding a routable address
widens exposure, so rely on network/firewall scoping plus the control/query tokens
(`SEXTANT_SERVICE_CONTROL_TOKEN` / `SEXTANT_SERVICE_QUERY_TOKEN`) to protect the surface. A blank or
syntactically malformed value is rejected at startup (fail-closed) rather than handed to Kestrel, which
would silently widen it to a bind on all interfaces. Note that Kestrel only pins a **specific** interface
for `localhost` or an IP literal; a non-IP **hostname** binds all interfaces, so use an IP literal (e.g.
`127.0.0.1`) when you need to restrict the service to one interface.

### Repository URL policy (SVC-5)

`POST /control/ensure` evaluates the request's `repository_remote_url` against a pure SSRF policy
(`RepositoryUrlPolicy`) **before any job row exists**, in both checkout modes. A refused URL returns
`400 {"status":"rejected","reason":"<code>"}` (the URL is never echoed), runs no worker, creates no job, and
writes one `ensure`/`denied` audit row whose `detail` is the reason code (the URL itself is never stored).
The 400 waits at most about two seconds for that row. While a running index holds the single writer, the row
is queued and lands once the writer frees up, so a refusal is never delayed behind a long index run.

| Rule | Refusal `reason` |
| --- | --- |
| The scheme is `https` (not `http`, `ssh`, `git`, scp-like `git@host:o/r`, or `file`) | `scheme_not_allowed` |
| No userinfo, query, fragment, or port other than `443`; no whitespace, control characters or `\`; at most 2048 characters | `url_component_not_allowed` |
| The host is a DNS name with at least two labels and no trailing dot. It is not an IP literal (IPv4, IPv6, or a numeric form such as `127.1`/`0x7f.1`) and not `localhost` or `*.localhost` | `host_not_allowed` |
| The host is on `SEXTANT_SERVICE_REPOSITORY_HOSTS` (default `github.com`; `*` = any host passing the shape rule) | `host_not_allowed` |
| The path is exactly `/{owner}/{repo}` with an optional `.git`. Each segment is 1–100 of `[A-Za-z0-9._-]`, does not start with `-`, and is not `.` or `..` | `path_not_allowed` |
| When `SEXTANT_SERVICE_REPOSITORY_OWNERS` is set, `host/owner` (or `host/*`) is on it. A host the list does not name admits no owner | `owner_not_allowed` |

The policy's canonical form, `https://{host}/{owner}/{repo}`, is folded with the same host-aware rule as the
catalog (so `github.com` owners match case-insensitively). It is a **policy key only**: the ensure keeps the
**submitted** URL spelling, because snapshot identity hashes it and rewriting it would re-index every
repository. The shape rules still apply under `*`, but a public DNS name can resolve to an internal address,
so prefer an explicit host list. Direct `SnapshotService` callers (the local CLI/daemon never construct one)
are not subject to the route policy.

> **Migration note.** The default admits only `github.com`. A deployment that ensures repositories on any
> other host must list them in `SEXTANT_SERVICE_REPOSITORY_HOSTS`, **including a `locate`-mode deployment**
> whose checkouts come from non-GitHub remotes. The policy applies in both modes, so without the setting
> those ensures are refused with `host_not_allowed`.

### Checkout provisioning — `locate` vs `clone`

`POST /control/ensure` indexes an on-disk checkout of the requested commit. `SEXTANT_SERVICE_CHECKOUT_MODE`
governs how that checkout is obtained:

- **`locate` (default)** — the node only **locates** a checkout that already exists on its persistent
  checkout volume (under `SEXTANT_SERVICE_CHECKOUT_ROOT`) and selects the solution(s) to index (see
  [Solution selection](#solution-selection--which-solutions-get-indexed) below). If no checkout or solution
  is present the job terminates `unsupported`. No outbound git is performed — behavior is byte-identical to a
  node with no provisioning at all. Use this when an external orchestrator provisions the checkout volume.
- **`clone`** — the node becomes **self-sufficient**: on a locate miss it clones the repository at the
  requested `commit_sha` into the checkout volume, then indexes it. The canonical checkout directory is a
  **durable cache**, so a subsequent ensure for the same repo **at the same commit** hits the locate
  fast-path with no re-clone. A cached checkout at a **different** commit is re-provisioned (the requested
  commit is fetched into a temp directory and atomically swapped in) so a snapshot is never published from
  the wrong revision. A clone lands in a temp directory under the checkout root and is published to the
  canonical directory via an atomic rename, so a partially-cloned tree is never observed as a valid
  checkout; a failed clone leaves no checkout and the job degrades cleanly (a **deterministic** failure to
  terminal `unsupported`/`failed`, a **transient** one to a bounded retry — see below) rather than indexing
  the wrong commit. Concurrent ensures for the same repository are serialized so they clone once.

#### Solution selection — which solution(s) get indexed

A monorepo commonly carries **many** solutions (MAUI/iOS/Android/Mac/Windows/Unity heads, vendored
externals). The worker chooses the set to index **explicitly and deterministically** — never the historical
"first-enumerated `.slnx`/`.sln`", which indexed one arbitrary solution and silently ignored the rest:

- **Configured (authoritative).** A `solutions` list in the checkout's **own** `sextant.json` (repo-relative
  paths) selects that exact set, **in listed order**. All configured solutions are indexed into **one**
  repository snapshot: their projects are unioned and **de-duplicated by project identity** (a project shared
  by several solution heads is indexed **once**, never per solution). A listed entry that is missing, is not
  a `.sln`/`.slnx`, or escapes the checkout is recorded **skipped-with-reason** (it is never silently
  dropped) and makes the snapshot **partial**.
- **Default (no config) — the union of every solution (issue #124).** All solutions under the checkout are
  discovered (build-output/VCS dirs excluded) and **every** one is selected. They are loaded through the
  same multi-solution union path as a configured list, into **one** snapshot, with the same per-identity
  de-duplication: a project declared by N solutions is evaluated and indexed **once per target framework**
  (identity = remote + repo-relative path + TFM). The loader opens one project at a time and the Phase-6
  extraction pipeline keeps one compilation live at a time, so memory stays bounded. The order is
  deterministic and independent of file-system enumeration order: shallowest first, then solutions with a
  Linux-loadable marker in the name (e.g. a `*-no-macos.slnx`-style root), then neutral names, then
  **platform heads last** (iOS/Android/Mac/Windows/WPF/Unity/…), then `.slnx` before `.sln`, then ordinal
  `/`-separated repo-relative path. **Platform-head solutions are included**, not ranked out: their
  loadable projects (shared libraries, and anything that evaluates on this worker) are indexed, and each
  project that cannot load here is recorded **skipped-with-reason** (`project_skipped`, per-project fault
  isolation from #90) and makes the snapshot **partial**. A partly-unloadable union never fails the
  snapshot; only a union in which **no** project loads at all fails the job, so an empty snapshot is never
  published. A checkout whose
  discovered solutions all load is **complete**. With a single discovered solution this is just that
  solution, loaded as before. With several, each project is opened individually (that is what isolates a
  per-project load fault), so no solution sets `$(SolutionDir)`: a project that imports
  `$(SolutionDir)…` with no fallback may be skipped-with-reason on this path. Each selected solution still
    gets its own `solution → project` mapping (the `solution:` query scope): the projects that solution
    declares plus everything they reference, as the single-solution path maps. A `solution:` scope over a
    selected solution none of whose projects loaded returns nothing rather than the whole repository.
    Nothing is "discovered but
  not selected" any more. Before #124 the default
  picked **one** solution and reported the rest as `solution_not_selected`; that kept a monorepo with no
  root solution (issue #119) mostly unindexed.

Because selection is a pure function of the committed checkout tree + committed `sextant.json` (both pinned
by the commit), it is **stable across runs** for a given commit. The selection policy itself is not part of
the snapshot identity, so changing it bumps `IndexConfigurationHash.AnalyzerVersion` (`"3"` for #124): a
narrow pre-#124 snapshot of the same commit is never reused as the union snapshot. The recorded
`coverage.selection_source` is `configured`, `default_union`, or `none` (rows written before #124 may still
say `default_root`).

To narrow the scope (for example, to skip platform heads that cannot load on this worker and avoid the
`partial` verdict they cause), list the wanted solutions in `solutions`.

**Coverage reporting (partial never reported as complete, issue #119).** Before indexing, the worker computes
a durable **coverage** record for the checkout from the selection, the multi-solution load, and a pure
file-system inventory (project files on disk, excluding `obj`/`bin`/`.git`; submodules declared by
`.gitmodules`, recursing through populated ones). The snapshot is **`partial`** when any of these hold:

| Gap | Diagnostic `code` |
| --- | --- |
| a discovered solution was not selected (defensive; the no-config default selects all of them since #124) | `solution_not_selected` |
| a configured solution is missing/invalid/outside the checkout | `solution_skipped` |
| a declared project could not load on this worker (e.g. an iOS/Android/Mac/WPF head on Linux, #90) | `project_skipped` (`sdk_resolution_failed` when its `global.json` pins an SDK this worker lacks and the pin was not overridden, #113) |
| a selected solution declared no readable project, or nothing loaded at all | `solution_no_projects` / `no_projects_loaded` |
| a declared submodule is not populated (no `.git` at its path) | `submodule_unpopulated` |
| code in a loaded project did not bind (see [Binding health](#binding-health)) | (coverage `binding`) |
| part of the tree could not be inspected (unreadable dir, `.gitmodules` entry escaping the checkout) | `coverage_scan_incomplete` |

A project file on disk that no selected solution declares and no loaded project references
(`project_file_unreferenced`, `info`) does **not** make the snapshot partial; it is listed in
`coverage.notes` instead ("N project file(s) outside every selected solution were not indexed: …", naming up
to 10). Under the no-config default every discovered solution is indexed (#124) and every project a solution
reaches through a `ProjectReference` is loaded, so such a file is outside every build of the repository (a
sample, a template, a scratch project): only the code inside it is missing, and the note says exactly which
files. Before this change ten stray samples made every answer for a 188-project repository carry the generic
"some projects or submodules were not indexed" warning, which agents read as "do not trust these results"
and fell back to grep. Under an explicit `solutions` list the same files were already `info`. A **provider**
(submodule) snapshot still goes partial for a stray project in its subtree (#162): the parent's selection
does not scope another repository. Per-item diagnostics are capped at 200 per code
with a summary row; the coverage counts are never capped. Diagnostic paths are checkout-relative.

The coverage record is persisted in `snapshot_coverage` (migration `022`) **in the same transaction that
publishes the snapshot**, and the job verdict is derived from it: a partial snapshot is still **published
and served** (its `snapshots.status` is `complete`, i.e. servable), but the job is `partial`, its `reason`
says why, and every surface carries the `coverage` block:

- `POST /control/ensure` and `GET /control/status/{jobId}` — `status: "partial"`, `reason`, `coverage`.
- `GET /control/resolve` — the snapshot row plus an additive `coverage` object. The row's own `status`
  stays `complete` (servable); read `coverage.verdict` for completeness.
- `GET /query/snapshots/{identityHash}/symbols` — `complete` is `true` only when the snapshot is published
  **and** not coverage-partial; the new `published` flag says whether this node publishes the snapshot;
  `coverage` carries the record. **Wire change:** before #119 `complete` meant "this node knows the
  snapshot" (it was `true` even for a pending or superseded identity with no rows). Current clients follow
  a resumed multi-peer cursor on `published` (falling back to `complete` for a peer that predates it, as
  before), but treat an empty FIRST page as "not published" unless `published` is `true` — so an older
  peer's empty page is never served as an empty complete base and never masks a later peer that does
  publish it. A pre-#119 client paging a current server could only misread an empty partial page, and in
  practice it cannot address one: identity hashes fold in the schema version, so an old (schema ≤ 21)
  client only computes hashes of pre-022 snapshots, which carry no coverage row and keep `complete` =
  published. Still, upgrade peers and their clients together. Empty "not published" pages are never
  cached by `RemoteHttpBaseSnapshotSource` (that state is mutable), so a base published after a probe
  is seen on the next fetch. Multi-peer source affinity (binding a resumed cursor, or an overlay's
  recorded base coverage, to the peer that produced it) is tracked in #122.
- MCP `meta.snapshot.completeness` is `partial` and `meta.snapshot.coverage` is set (on the service's
  `/mcp` the lean `meta.snapshot.coverage` is the string `partial`, plus a `warning`; the full block is in
  `get_index_status` `index.snapshot`); `get_index_status`
  reports `index.coverage` (an overlay reports its committed base's coverage; a baseless remote-base
  overlay records the peer's probed base coverage on its own row at publish, so a local hit that never
  contacts the peer still reports a partial remote base as partial).

A snapshot with **no** coverage row (a local CLI/daemon index, a local-base overlay's own row, a remote-base
overlay over a pre-#119 peer, or a snapshot published before migration `022`) has coverage *not recorded*: its
surfaces omit `coverage` and keep their previous
completeness. Re-selecting an already-published snapshot never backfills coverage. Because migration `022`
bumps the schema version (folded into the snapshot identity), every repository is re-indexed — and gets a
coverage row — on its next ensure. The Phase-10 `snapshots.fallback_reason` column is **not** used for
coverage; the reason lives on the job and in the coverage record.

**Provider snapshots carry their own coverage (issue #162).** A Phase-12 provider snapshot (a populated,
clean submodule published at its pinned commit) is published in the same transaction as its consumer, and it
now gets its own `snapshot_coverage` row in that transaction. A later direct ensure that reuses the provider
identity therefore reports the provider's real verdict on ensure/status/resolve and in MCP
`meta.snapshot.coverage`, instead of reporting "complete" by omission. The record is computed over the
provider's **subtree** (`SnapshotCoverageBuilder.BuildProviders`), not over the whole checkout:

- counts are limited to the subtree: the projects the parent's selection declared, loaded, and skipped
  under the submodule path, the project files on disk there, the solution files there, and the submodules
  nested inside it;
- **partial** when any provider project was skipped; when a provider project on disk was neither declared
  nor loaded (always a gap, even under a configured parent `solutions` scope, because the parent's scope does
  not scope another repository); when the provider has its own solution files and **none** of them was
  selected (the provider was built only from the projects the parent's selection reaches, so its
  solution-scoped view is missing) or only some of them were; when a provider solution declared no readable
  project; when a nested submodule is unpopulated; or when any part of the checkout could not be scanned;
- a provider with no solution files, whose on-disk projects all loaded, is `complete`;
- `selection_source` is the parent's source when one of the provider's own solutions was selected, and
  `parent_selection` otherwise. SDK-pin overrides are kept only when the `global.json` governs the provider
  (inside it, rewritten provider-relative, or an ancestor, shown as `<indexing checkout>/global.json`);
- reasons name provider-relative paths only, so the parent's layout never leaks to a reader of the provider.

The row is **record-if-absent**. A #53 growth republish of an already-published provider keeps its
first-publish verdict. That verdict can only be conservative: `complete` means every on-disk project loaded.
A provider that is rebuilt after a failure, or is re-staged from a non-complete state, has its stale row
deleted first, so re-recording never trips the "recorded twice" guard. A provider reused as already complete
keeps whatever row it has and is never backfilled. So a provider published before #162 still has coverage
*not recorded*, until its identity changes, for example through a schema or `AnalyzerVersion` bump. If the
worker computed no coverage for a provider's submodule path, the provider is recorded `partial` with reason
"coverage was not computed". Provider coverage is not part of `SnapshotIdentity`, so Phase-12 dedup is
unchanged.

Routing platform heads to a native Windows/macOS worker is a separate concern (issue #89); here they are
recorded skipped-with-reason.

#### Package restore before the load

The worker runs `dotnet restore <solution> -p:DesignTimeBuild=true --ignore-failed-sources
--disable-build-servers -nodeReuse:false` over every selected solution, in order and under one deadline
(`SEXTANT_SERVICE_PACKAGE_RESTORE_TIMEOUT_SECONDS`, default 300 s), inside the evaluation sandbox and while
an unsatisfiable `global.json` SDK pin is still neutralized, then loads the solutions. **Why:** without a
restore there is no `obj/project.assets.json`, so the design-time build gets no package compile assets and the
SDK never adds the **transitive** project references the assets file lists. Each project then compiles against
its direct `ProjectReference`s only, every type it reaches through another project is unresolved, and every
call whose signature mentions one fails to bind. Before this change the worker never restored: on a
188-project repository a project with 3 direct references compiled with 2,219 errors (8 references and 0
errors once restored), and `find_references` silently missed most call sites of methods with an optional
parameter of such a type.

A restore never fails the job. `--ignore-failed-sources` lets an unreachable or credential-gated feed (for
example a private feed authenticated by an environment variable the worker does not have) leave the other
packages restored; NuGet then reports the source as the per-project **warning** `NU1801` (not the error
`NU1301`), and the parser records that warning as "a package source was unreachable", even when its message
contains the word "error". `-p:DesignTimeBuild=true` keeps a missing optional workload from failing the restore,
like the load. What it could not do is recorded: per-project `package_restore_incomplete` warnings (codes and
package ids only, never a raw message, which can name a source URL), `coverage.notes` lines such as "Package
restore could not find 2 package(s) (Acme.Auth, Acme.Auth.UI) for 1 project(s); code that uses them may not
bind.", and each affected project's `coverage.binding.projects[].load_issue`. A restore that exits non-zero
without a recognizable error line still leaves a note ("Package restore failed for N solution(s) without a
recognized error code; ..."). A restore problem alone does not make the snapshot partial; the code that then
fails to bind does (below). The process tree is killed on expiry or cancellation, stdin is closed, and
stdout/stderr are drained concurrently with a bounded wait, so a leaked MSBuild node cannot hang the job.

A restore problem is **not retried**: the job publishes with the notes above, and a later ensure of the same
commit attaches to that snapshot. Retrying would rarely help, because each job restores into its own cold
`NUGET_PACKAGES` (a timed-out restore would time out again), and a credential-gated feed never becomes
reachable. The binding-health verdict below is what tells an agent that results may be missing; the next commit
re-indexes.

**Network and credentials.** Restore contacts nuget.org and every package source the repository's
`nuget.config` names, from the worker host. `SEXTANT_SERVICE_SANDBOX_ALLOW_NETWORK` does **not** gate it: that
setting is a best-effort offline posture for evaluation (telemetry and first-run variables) and never blocked
the network, and the repository's design-time targets can already reach it during the load (only
out-of-process worker isolation, #76, contains them). Set `SEXTANT_SERVICE_PACKAGE_RESTORE=false` on a host that
must not make outbound requests on behalf of indexed repositories. The restore inherits the sandbox's scrubbed
environment, and never any `SEXTANT_*` variable, so a `%VAR%` in a repository's `nuget.config` cannot send the
service's own tokens to a source it names. Do not give the service account a user-level `NuGet.Config` with
`packageSourceCredentials`: a repository's `nuget.config` can declare a source with the same key and another
URL, and NuGet would send those credentials to it.

Cost: 15-60 s per job for a large repository with a warm NuGet cache. The sandbox gives each job its own
`NUGET_PACKAGES`, so a cold job downloads every package (about 2.6 GB for a 188-project repository) into job
scratch, which is released with the job. The restore writes `obj/` files (`project.assets.json`,
`*.nuget.g.props`) into the checkout, which `EvaluationFingerprint` hashes; a different commit is a fresh clone,
so they never carry over to another commit. The toggle is part of the snapshot identity: `restore=off` is folded
only when `SEXTANT_SERVICE_PACKAGE_RESTORE=false`, so the default leaves identities unchanged and flipping it
re-indexes. The worker also closes each loaded project's transitive project-reference graph in the workspace
(`TransitiveProjectReferences`), which covers a project restore could not restore at all.

#### Binding health

The extractor counts, per indexed project version, the identifier names it examined, the names that bound to
no symbol, and the invocations that did not bind to one method. A compile-clean project leaves no name
unbound, so these are symptoms of a missing reference or package. A project is **degraded** when at least 25
names, and at least 0.5% of the names it uses, did not bind (`BindingHealthBuilder`); a few stray errors
(generated code, a test fixture that deliberately does not compile) are tolerated. Every degraded project makes
the snapshot **partial**, with a reason naming up to 5 of them ("Code in N project(s) did not fully compile on
the indexer, so references and calls inside them may be missing (src/App/App.csproj: 412 unbound name(s); …).
Calls that failed to bind are kept as candidate matches."). The counts are recorded in `coverage.binding`
(totals, `projects_degraded`, and up to 25 projects with problems, each with `names_examined`,
`unbound_names`, `unbound_invocations`, `candidate_occurrences`, `degraded` and the restore `load_issue`) in
the publish transaction, so `get_index_status` `index.coverage` shows them. Binding health is recorded for
the repository's own snapshot; provider snapshots and local CLI/daemon indexes record none.

**Candidate matches.** When a name or call does not bind but the compiler offers candidates (overload
resolution failed because an argument or optional parameter type is unresolved, or the call is ambiguous), the
reference and the call edge are still stored, against every candidate, with the occurrence flag bit 2
(`0b100`, `ReferenceStore.CandidateFlag`). Roslyn's own Find References reports these as candidate locations.
An exact edge to the same target at the same site wins over a candidate. `find_references` and
`get_call_hierarchy` mark such results `"candidate": true`; an exact result carries no `candidate` field.

#### Submodules — recursive, pinned, credential-scoped (#125)

In `clone` mode the provisioner initializes every submodule **recursively at the commit the parent pins**
(the gitlink), so monorepo projects that live in — or `ProjectReference` into — a submodule are indexed, and
the Phase-12 submodule dedup fires (each populated, clean submodule becomes a **provider snapshot** at its
pinned commit, with a `snapshot_dependencies` edge and cross-repository usage edges from the consumer).

It does **not** run `git submodule update`. It reads `.gitmodules` from the committed tree
(`git config --blob HEAD:.gitmodules`, honouring only each entry's `path` and `url` — `update`, `branch`,
`shallow`, and any other key are ignored), reads the gitlink from `git ls-tree`, and for each submodule runs
the same hardened sequence as the top level: `init` → `remote add origin --end-of-options <clean-url>` →
`fetch --depth 1 --no-tags --end-of-options origin <gitlink>` (a full fetch as fallback when the pin is not
advertised shallowly) → `checkout --detach` → verify `HEAD` equals the gitlink. The child's git dir is then
absorbed into the parent (`.git/modules/<name>`) — after which every populated submodule is re-verified (its
gitdir link must still resolve to its pinned `HEAD`; otherwise the staged provisioning is discarded and
retried) — and nesting recurses (bounded to depth 8 and 256
submodules). Submodule names/paths are validated (no `..`, no absolute or `.git` segments, no escape from the
parent). (Clone mode as a whole requires git ≥ 2.31 — see the credentials paragraph below.)

**URL policy (per `.gitmodules` entry).**

| `url` shape | Handling |
| --- | --- |
| absolute `https://host/…` | fetched when `host` is the repository's host (with the token) or an allowlisted submodule host (anonymously); otherwise refused. Username-only userinfo such as `https://user@github.com/…` is stripped |
| relative `../X.git`, `./X` | resolved against the parent's **clean** origin URL (never climbing above the host), then treated as above |
| `git@host:org/repo.git`, `ssh://[user@]host[:port]/…` | rewritten to `https://<repository-host>/org/repo.git` **only** when `host` is the repository's host; otherwise refused |
| a URL carrying a **password** (`https://user:secret@…`) | refused — credentials come only from the service token |
| `http://`, `git://`, `file://`, `ext::`/helper transports, local paths, a leading `-`, whitespace/control/backslash characters, a query/fragment, dot segments, a URL over 2048 chars | refused |

**Who gets the token.** The token is sent **only** to a submodule on the **top-level repository's own https
host** (the same env-scoped header as the top level). `SEXTANT_SERVICE_SUBMODULE_HOSTS` (comma-separated
`host[:port]` list) additionally allows submodules on other hosts, fetched **anonymously** (the token is never
sent to them). Any other host is refused. When the top-level remote is not `https` (ssh/scp) no host is
authenticated, so only allowlisted-host https submodules are fetched. A token-carrying fetch never follows
a redirect (see the security note below).

**An unfetchable submodule never fails the checkout.** A refused URL, an auth/404/unreachable fetch, a pinned
commit that is not on the remote, an invalid entry, a `.gitmodules` entry with no gitlink, or exceeding the
depth/count bounds leaves that submodule **unpopulated** — its directory is reset to **empty** (a checkout
that failed halfway leaves no partial work-tree files, which could otherwise be loaded into the parent
snapshot; if the directory cannot be emptied the provisioning is discarded and retried) — and records a
token-redacted outcome. Coverage then
reports the snapshot `partial` with a `submodule_unpopulated` diagnostic per submodule whose message and the
job `reason` say why, e.g. `libs/X (url refused: host 'example.com' is not the repository host …)`,
`libs/Y (fetch failed: …)`, `libs/Z (pinned commit not found: …)`, `libs/W (no gitlink: …)`. A
**transient** submodule failure (timeout, connection refused/reset, DNS, 5xx) follows the transient
classification below — the whole staged provisioning is discarded and retried — **except on the job's final
allowed attempt** (`SEXTANT_SERVICE_MAX_PROVISIONING_ATTEMPTS`): there it too is left unpopulated
(`fetch failed: transient failure persisted through the final provisioning attempt: …`), so one persistently
unreachable submodule host degrades the snapshot to `partial` instead of making the whole repository
un-indexable. In tests only, `AllowFileTransportForTesting` permits `file://`
submodule fixtures; production never allows a `file://` submodule from an untrusted `.gitmodules`.

**Checkout layout marker + cached-checkout upgrade.** A provisioned checkout records
`.git/sextant-checkout.json` (layout `2`, with each submodule's outcome). A cached checkout that carries the
marker is reused as-is (its recorded outcomes feed coverage again). A cached checkout from **before** this
change (no marker) that has a declared-but-unpopulated submodule is **re-provisioned into a fresh temp
directory and atomically swapped in** — the same stage-then-rename publish as a commit change, so a reader
never observes a half-upgraded tree (and ensures for one repository are serialized). If the upgrade fails
deterministically the cached tree keeps being served (honestly partial); a transient failure is retried, and
on the final allowed attempt the cached tree is served too. A
pre-change checkout with no submodules is reused untouched. Separately, `AnalyzerVersion` is bumped to `4`,
so a pre-change snapshot indexed without submodules at the same commit is never reused.

#### Repository `global.json` SDK pins (issue #113)

Roslyn's MSBuild BuildHost picks its .NET SDK through hostfxr, which honors the checkout's `global.json`.
If a repository pins an SDK band the worker does not have **and** forbids roll-forward (e.g.
`{"sdk": {"version": "10.0.300", "rollForward": "disable"}}` on an image that only ships 10.0.401), the
load dies before any project evaluates (`hostfxr_resolve_sdk2 … A compatible .NET SDK was not found`),
even though the code builds fine with the installed SDK. No environment variable or MSBuildLocator/
MSBuildWorkspace option makes hostfxr ignore a `global.json`, and copying the checkout to scratch would
cost a full copy per job and break checkout-relative paths. So the service handles it like this:

1. **Detect.** Before loading, the worker asks hostfxr (the same resolver the BuildHost uses) whether the
   nearest `global.json` for each selected solution's directory and each declared project's directory
   resolves. Under the no-config default every discovered solution is selected (#124), so this covers
   every project the union load opens. A project reached only through a `ProjectReference` from outside
   every selected solution is not probed, so its pin is never overridden: it loads, or fails, exactly as
   before #113. Under the no-config default such a project file is in no selected solution, which already
   makes coverage `partial`. A pin that resolves is **never
   touched**, so repositories that work today behave exactly as before. For example, `10.0.100` with
   `latestFeature` rolls forward to 10.0.401 and is left alone.
2. **Neutralize, only for the load.** With `SEXTANT_SERVICE_SDK_PIN_OVERRIDE` on (the default), each
   failing pin inside the checkout has its `sdk` section removed **in place**. Other sections such as
   `msbuild-sdks` are kept. hostfxr then resolves the newest installed SDK, which is re-probed and
   recorded. The committed bytes, last-write time and unix mode are **restored in a `finally` immediately
   after the MSBuild load**, before the coverage scan, the `EvaluationFingerprint`, or any indexing reads
   the checkout. The persistent, reused checkout therefore never diverges from its commit (`git status`
   stays clean). The symlink/junction check is repeated right before the restore writes, so a directory
   swapped for a link during the load is refused (the restore fails closed, as below). This is defense in
   depth, not a security boundary: evaluated repository code already holds the worker's filesystem
   authority (see the evaluation sandbox; OS-hard isolation is #76).
3. **Crash-safe.** Before any file is modified, the original bytes and the checkout's `HEAD` commit are
   journaled atomically (fsynced, with the rename and a newly created journal directory flushed on Unix) to
   `<checkout-root>/.sextant-sdk-pin/<checkout>-<hash>.json`, outside every working tree. For a pin inside a
   populated submodule (issue #171) the entry also records the submodule's work tree, the commit it was
   checked out at and the git directory its `.git` resolved to, and the journal is written as version `2`: a
   binary from before #171 cannot read it, so
   it fails closed instead of replaying it without the submodule check (a journal with no submodule entry
   stays version `1`). Nothing is ever written under `.git` or `.git/modules`. Each rewrite goes
   through a uniquely named sibling temp file created exclusively, so no repository file (whatever its
   name) is ever overwritten or deleted. A restored file (including its mode and mtime) is flushed before
   its journal is deleted, and the deletion is flushed too. Every job replays leftover journals **before** a
   cached checkout is resolved or reused:
   - a file that still holds exactly the neutralized content is restored; one that already holds the
     committed content is left alone;
   - a file that is **missing or holds foreign content** fails closed while the checkout is still at the
     journaled commit: the journal is kept and the checkout is not indexed. Repair it by restoring the
     committed `global.json` (e.g. `git checkout -- global.json`) or by deleting the checkout so it is
     re-cloned; the journal then retires itself;
   - once the checkout has **moved to another commit** (the cloning provider re-provisions a mismatched
     checkout as a whole fresh tree) or is gone, the journal is retired **without writing or deleting
     anything** in the checkout — even a file whose bytes happen to equal the neutralized form, or one at
     the journaled temp-file path, belongs to the new commit. If the
     journaled commit can no longer be confirmed (the checkout's `HEAD` is unreadable), it fails closed;
   - a submodule entry is restored only while that submodule is **still at its journaled commit**, is still
     the innermost submodule containing the file, and still resolves to the same git directory. A
     submodule that has moved to another commit, or is no longer populated and its `global.json` is gone,
     holds nothing of the neutralized tree: its entry is retired without writing anything, and the
     checkout's other entries are still restored. A submodule whose commit cannot be confirmed (its `HEAD` is
     unreadable, its `.git` now points at another git directory or outside the checkout's own git
     metadata, or its `.git` is gone while the `global.json` remains) fails closed like the checkout's own
     unreadable `HEAD`: nothing is written, the journal is kept and the checkout is not indexed. So does a
     checkout-owned entry whose `global.json` now lies inside a populated submodule;
   - a journal is replayed only when it is well formed and confined: it must be the journal of the checkout
     it names, that checkout must be on the checkout volume (the journal directory's parent), and it must
     list at least one entry, each a distinct `global.json` inside it (never under `.git`) whose journaled
     bytes match their checksum and whose recorded commit, timestamp and mode are valid. A submodule entry
     must name an absolute work tree inside the checkout that contains its `global.json`, a commit id, and
     an absolute git directory inside the checkout's git metadata; a version-`2` journal must carry at least
     one submodule entry, and only a version-`2` journal may. Before a checkout that is still at
     the journaled commit is restored, neither the `global.json`, nor the checkout directory, nor any
     directory between them and the checkout volume may be a symlink/junction (checked for every entry
     before any is written). A moved checkout's links belong to the new commit and never block retiring its
     journal. Anything else is logged and left for inspection, and one bad journal never stops the others
     from being replayed;
   - if the journal directory cannot be read (e.g. after a restart under another UID), the service cannot
     rule out a leftover journal, so the checkout is not indexed — an access failure is never read as "no
     journal".

   If a restore fails during a job (even a cancelled one), or a leftover journal cannot be replayed, the
   checkout is not indexed, the journal is kept, and the job is **requeued** with an `sdk_pin_restore_failed`
   diagnostic rather than recorded as a terminal failure. The checkout's state is the problem, not the commit,
   so it must not poison the identity: once the checkout is repaired, the next ensure retries. There is no
   background retry: the next attempt runs when the orchestrator **re-calls `POST /control/ensure`** for the
   same commit (`/control/status/{jobId}` shows the `queued` job and its diagnostic in the meantime). Like a
   transient clone failure, this is bounded by `SEXTANT_SERVICE_MAX_PROVISIONING_ATTEMPTS`. Once the attempts
   are used up the job settles to `failed`, still carrying `sdk_pin_restore_failed` next to
   `provisioning_attempts_exhausted`.
4. **Refusals.** Each failing pin is judged **on its own** (issue #171): a refused pin is reported with its
   own reason and never keeps another pin from being overridden, so a root pin is still overridden when a
   pin in a submodule is refused (that pin's projects then fail per project, below). A pin is left alone
   and reported as not overridden, with the reason, when:
   - the override is disabled;
   - hostfxr failed for a reason **other than a missing SDK version**. Only its "A compatible .NET SDK was
     not found" wording is overridden; the bare `0x8000809B` status is not enough, because hostfxr returns it
     for every resolution failure;
   - the pinned version is not a well-formed SDK version (`major.minor.patch` with a feature band of at
     least 100, e.g. `10.0.300`). hostfxr words a malformed `1.2.0` pin exactly like an absent band, so this
     keeps the override to pins that name a real SDK band;
   - the `global.json` lies outside the checkout, inside git metadata (`.git`, including an absorbed
     submodule's `.git/modules/…`), or is reached through a symlink/junction (including a symlinked checkout
     directory);
   - it cannot be read or parsed, it has no `sdk` section, or its `sdk` section pins no version;
   - the checkout's git `HEAD` commit cannot be read (e.g. a non-git directory placed by an external
     provisioner in `locate` mode). The journaled commit, not the file content, is what lets recovery tell
     the neutralized tree apart from a re-provisioned one that happens to hold the same bytes;
   - git cannot confirm the `global.json` is exactly its **committed** content. `HEAD` must resolve to that
     commit, and the file must be tracked, carry no assume-unchanged/skip-worktree flag, and have no staged
     or unstaged change. The exact bytes to be journaled must also equal, byte for byte, the commit's blob
     as stored or git's checkout rendering of it (eol/`ident`/`working-tree-encoding` conversion). A path
     that goes through a `filter` driver (e.g. LFS) must match the stored blob, because a driver's smudge
     output is the program's, not the blob's. The rendered alternative needs git 2.11 or later
     (`cat-file --filters`); an older git fails closed. This matters because `git status`
     can call an edit clean from cached stat data alone, and clean conversions such as `ident` are
     many-to-one, so hashing the bytes would not be proof. The rule covers an untracked or locally edited
     `global.json` in a `locate`-mode checkout. The journal therefore only ever holds the commit's content, and a
     same-commit recovery can only put the committed file back. git runs with inherited `GIT_*` variables
     dropped, replace refs ignored, repository discovery stopped at the checkout, literal pathspecs,
     `core.fsmonitor` off and optional locks off (it never rewrites `.git/index` or writes objects). If git
     is missing, times out, or refuses the checkout (e.g. its `safe.directory` ownership check), the pin is
     refused;
   - the pin is inside a populated submodule (a directory with its own `.git`, e.g. from #125's recursive
     checkout) and that submodule is not provably the one the checkout's commit pins. A submodule's file
     belongs to the **submodule's** repository (from the superproject it is simply untracked), so it is
     verified there, as above, at the submodule's own `HEAD` — but only after every enclosing submodule,
     outermost first, is shown by `git ls-tree` of its parent's verified commit to be a gitlink (mode
     `160000`) pinned to exactly that `HEAD`. Each submodule's git metadata must be the checkout's own —
     an embedded `.git` directory or an absorbed git dir under a `.git` directory inside the checkout
     (`.git/modules/…`), reached through no symlink/junction — so its `HEAD` describes that work tree and no
     other repository. A submodule checked out away from its gitlink, one whose `HEAD` cannot be read, one
     whose `.git` points elsewhere (another checkout, outside the checkout) or is a symlink, or a nested
     repository the commit does not record as a submodule is refused (e.g.
     `the submodule 'libs/sub' is checked out at <sha>, not the commit <gitlink> that <commit> pins for it`);
   - the journal cannot be written, or is laid out so recovery could never replay it: inside the checkout,
     or in a directory whose parent does not contain the checkout (the service always uses
     `<checkout-root>/.sextant-sdk-pin`);
   - neutralizing it still leaves no resolvable SDK (for example, a parent pin outside the checkout also
     fails). In that case it is restored at once.

Outcomes and diagnostics. All paths are checkout-relative, and every diagnostic names the repo-relative
`global.json`, the requested version and `rollForward`, and the installed SDK(s):

| Situation | Job status | Diagnostic `code` |
| --- | --- | --- |
| pin overridden, checkout loaded | `complete` (unless another coverage gap applies) | `sdk_pin_overridden` (warning; also the substituted SDK) |
| pin not overridden and the **whole** load failed SDK resolution | `failed` with a typed reason (requested vs installed, pin path, why not overridden) — never a bare exception message. This also applies when the load fails with an error that names no hostfxr function (e.g. every project came back empty) while a pin it depends on is known not to resolve; the reason then carries the load error too | `sdk_resolution_failed` (error) |
| pin not overridden, but only **some** solutions/projects are governed by it (#90-style isolation, e.g. a `tools/global.json` in one of several solutions, or a refused pin inside one submodule while the root pin is overridden) | `partial`; the reason names the pin | `sdk_resolution_failed` (warning) for the pin and for each project it kept from loading |
| a neutralized pin could not be restored (or a leftover journal could not be replayed, or its presence ruled out) | `queued` (requeued; HTTP 202 — re-call ensure to retry), nothing indexed; `failed` once `SEXTANT_SERVICE_MAX_PROVISIONING_ATTEMPTS` is exhausted | `sdk_pin_restore_failed` (error) |

An overridden snapshot is **complete**, because the override restored full coverage, but it is never
silent:

- `GET /control/status/{jobId}` lists the `sdk_pin_overridden` diagnostic.
- The snapshot's durable `coverage` block (in ensure/status/resolve, query pages, local MCP
  `meta.snapshot.coverage`, and `get_index_status` `index.snapshot.coverage` on the service's `/mcp`) carries
  `sdk_pin_overrides: [{ global_json_path, requested_version, roll_forward, resolved_sdk_version, installed_sdks }]`.
- The ensure audit row's `detail` gains a suffix, e.g. `job_42;sdk_pin_overridden`. The suffixes are
  `;sdk_resolution_failed` and `;sdk_pin_restore_failed` for the other two outcomes.

**Identity and fingerprints stay deterministic.** The snapshot identity is computed from the request and
the service configuration before the worker runs, and does not read `global.json`.
`EvaluationFingerprint.Compute` does hash `global.json`, but it runs at index time, after the restore. It
therefore records the **committed** checkout's value, the same value a worker that has the pinned SDK would
record. The override needs no `AnalyzerVersion` bump.

**The override policy is part of the snapshot identity.** The toggle decides what a node publishes for a
pinned commit: `complete` with the substituted SDK, or `partial`/`failed` with the override off. An
already-published snapshot is never rebuilt, and a terminal `failed` job is reused as recorded. If the policy
were not in the identity, a commit ensured with the override off would therefore be silently reused after the
operator turned it on, and the other way round. So a node with `SEXTANT_SERVICE_SDK_PIN_OVERRIDE=false` folds
`sdkpin=strict` into every identity it computes and publishes, for the repository snapshot and its submodule
provider snapshots. Both the ensure request identity (`ServiceOptions.SdkPinIdentityComponent`) and the
worker's published identity (`SdkPinGuard.IdentityComponent`) derive it from that one toggle. A mismatch
still fails closed, as any worker/config identity mismatch does. Details:

- **Flipping the toggle (and restarting) re-indexes.** The next ensure of a commit gets a new identity and
  rebuilds it under the new policy. Flipping it back re-attaches to the earlier policy's snapshot or job
  without a rebuild.
- **The default policy (override on) folds nothing.** Its identities stay byte-identical to before #113, so
  enabling this costs no rebuild. The local CLI/daemon path, which never overrides a pin, is unchanged too, as
  are contribution identities and the remote-base addressing a local client uses (#108). That addressing
  assumes the peer's default policy, so an override-off peer's bases are not addressable. The client then
  falls back to a full local build.
- **The worker's installed SDK bands are not in the identity.** Neither is the clone credential. A commit
  that already failed SDK resolution on a node is not retried just because a new SDK band was installed there.
  Pushing a new commit, or an `AnalyzerVersion` bump, produces a new identity that is evaluated afresh.
- **Pre-#113 identities.** A job that failed on a worker from before this feature has the default (unfolded)
  identity. It is only re-evaluated because the same deploy also bumps `AnalyzerVersion`, to `4` for #125.
  Retrying failed identities after an environment change is tracked in #153.

**Operator options.** Install the pinned SDK band in the worker image, which makes the pin resolve so the
override never engages. Alternatively, have the repository relax `rollForward` (e.g. `latestFeature`). Set
`SEXTANT_SERVICE_SDK_PIN_OVERRIDE=false` to prefer an honest `failed`/`partial` over a substituted SDK. The
local CLI/daemon path is unaffected: it evaluates with the developer's own SDK and never rewrites
`global.json`.

#### Transient vs deterministic provisioning failures

Cloning is a **network** operation, so some failures are expected to recover on their own (a DNS blip, a
fetch timeout, a connection reset, a remote 5xx, a partially-received pack). Others are permanent for the
requested identity (the repository or commit genuinely does not exist, authentication is refused, or the
tree is not a git repository and carries no solution). The service classifies them so it never permanently
poisons a recoverable commit while still caching genuinely-hopeless ones:

- **Transient** — the failing ensure does **not** record a suppressing terminal result. The identity is
  requeued, so the orchestrator's natural retry (re-calling `ensure` for the same commit) re-runs the
  worker. This is bounded by `SEXTANT_SERVICE_MAX_PROVISIONING_ATTEMPTS` (default `5`); once exhausted the
  job settles to terminal `failed`. Such an ensure returns **HTTP 202 Accepted** with a non-terminal
  (`queued`) status. `/control/status/{jobId}` shows the queued job and its diagnostics, but nothing
  re-runs it in the background: the next attempt happens when `ensure` is called again.
- **Deterministic** — the job records a terminal `unsupported`/`failed` as before, and every later ensure
  for that identity attaches the cached result **without** re-running the worker (so a hopeless request is
  not re-attempted on every delivery). This is byte-identical to the pre-existing behavior.

An unrecognized git error is treated as **transient** on purpose: a needless bounded retry is cheaper than
permanently poisoning a commit that would have recovered. The attempt counter is **job-wide** (it also
counts cancellation re-attempts and snapshot regenerations for the same identity), so it is a safety ceiling,
not an exact transient-retry budget.

> **Credential-rotation caveat.** `SEXTANT_SERVICE_CHECKOUT_TOKEN` is **not** part of the snapshot identity
> hash, so an `authentication failed` outcome is classified **deterministic** and cached terminal. Rotating
> the token alone will **not** revive that cached job — the token is not in the identity, so the ensure
> re-attaches the terminal result. Advance the branch to a new commit (new identity) to retry with the new
> token. (Folding the token into the identity is deliberately avoided so a token rotation does not
> needlessly fork the snapshot lineage.)

Set `SEXTANT_SERVICE_CHECKOUT_TOKEN` to clone a **private** `https` repository. The token reaches git **only
through the child process's environment**: `GIT_CONFIG_COUNT` / `GIT_CONFIG_KEY_n` / `GIT_CONFIG_VALUE_n`
carry an `http.https://<repository-host>/.extraheader` entry (`AUTHORIZATION: basic` over
`x-access-token:<token>`, preceded by an empty entry that resets any inherited header), **scoped to the
repository's own host** (scheme + host + port). It is therefore never on git's argv, never written to any git
config file (`.git/config`, `.git/modules/**/config`), never in a fetch record, and is redacted — raw and
base64 forms — from every log line and exception. Every service git invocation also resets credential
helpers and askpass (`credential.helper=` / `core.askPass=` through the same channel) and disables implicit
submodule recursion, so a host-level credential helper can neither supply nor **store** a credential for a
service fetch. **Behavior change (#125):** before this, a host credential helper could silently authenticate
a clone; now only `SEXTANT_SERVICE_CHECKOUT_TOKEN` does. Git (and anything it launches — transports,
filters, hooks) never inherits the service's own `SEXTANT_*` variables (including the raw token and the
control/query tokens) or any other variable whose value contains the token. Clone mode requires git ≥ 2.31 — even without a token, since the helper/askpass reset travels the same way (it is
probed once; an older git fails the provisioning closed rather than fetching without its hardening). Public
repositories (and non-`https` remotes) need no token. The header can only be scoped to a plain DNS host name,
so for an `https` remote whose host is not one (an `_`, a trailing `.`, an IPv6 literal) the token is **not
sent** and a warning says so (the fetch proceeds anonymously). A credential embedded directly in the
`repository_remote_url` (any userinfo) is **refused** — supply credentials only via the token.

**Scrub + verify before publish (fail closed).** After the tree (and its submodules) is provisioned, every
git dir in the staged checkout — the top-level `.git`, every absorbed `.git/modules/**` dir, and any embedded
submodule `.git` or gitfile (discovered by walking the work tree, not from recorded paths) — has its
`FETCH_HEAD` removed, and every remaining non-object file (config, refs, logs,
packed-refs, hooks, markers, …) is scanned for the raw, base64 and basic-credential forms of the token. A hit
**fails the provisioning closed**: the staged tree is discarded and nothing is published.

> **Security note.** `clone` mode performs **outbound git fetches** to the repositories it is asked to
> ensure and stores their checkouts on the persistent volume as a cache. The commit id is validated as a
> git object id (never an arbitrary ref/option) and the checked-out `HEAD` is verified against the request
> before indexing; git helper transports are blocked (`GIT_ALLOW_PROTOCOL`; submodules are further limited
> to `https`) and prompting is disabled. The token lives only in the git child's **environment** (not
> persisted, not logged, not on argv), so it is visible only to a process that can already read the service
> process's environment — treat the service host as trusted. **Redirects are disabled for every fetch that
> carries the token** (`http.followRedirects=false`, set both globally and for the exact fetch URL, so an inherited
> URL-scoped `followRedirects=true` cannot win git's most-specific-URL match): git copies `http.extraheader` onto every request,
> including the requests it rebases onto a redirect target after following one, so a redirect on the
> repository host (a path an untrusted `.gitmodules` can choose) would otherwise hand the token to another
> host. An authenticated repository or same-host submodule reachable only through a redirect (e.g. a renamed
> repository) therefore fails **deterministically** (`The requested URL returned error: 301`) — update the
> URL. Anonymous fetches keep git's default redirect policy. Host-level git configuration (system/global
> config, an inherited `GIT_CONFIG_COUNT` block) is **operator-trusted** and still honoured — CA bundles,
> proxies, `url.<base>.insteadOf` mirrors — so it is part of the trusted host: an `insteadOf` there can change
> where a URL is fetched from; the token header is matched against the URL *after* that rewrite, so it is
> still only ever sent to the repository's own host (a mirror on another host is fetched without it).
> Prefer `locate` for nodes that must not reach
> the network; scope network egress and the control token appropriately when enabling `clone`. Any value
> other than `locate`/`clone` **fails startup** (fail-closed).

The **wire format is snake_case** (`ServiceJson.Options` = `SnakeCaseLower` + ignore-null, matching the
rest of Sextant's JSON). Response bodies are serialized with `ServiceJson.Options` explicitly; request
bodies bind through a matching `Http.Json.JsonOptions` registration so control-plane bodies like
`{ "repository_remote_url": ... }` bind to the contract records.

## HTTP surface — control vs query planes

The host deliberately **separates control endpoints from query endpoints**, and health from readiness:

| Endpoint | Plane | Auth | Meaning |
| --- | --- | --- | --- |
| `GET /health` | — | open | Service **AVAILABILITY**: the process is up and the catalog is reachable. |
| `GET /ready` | — | open | Worker **CAPACITY**: `503` when this node has no worker (query-only), so an operator can tell "up" from "can index". |
| `POST /control/ensure` | control | control token | Idempotent ensure-snapshot (criterion 1). Accepts an optional monotonic `branch_head_sequence` for forward-only branch-head advance (Phase 14, issue #84), **or** an `expected_head_commit` head CAS, plus `forced` and `branch_update` (see [Branch-pointer guards](#branch-pointer-guards-head-cas-branch_update-none-retire-svc-67)); the result carries `branch_advanced`. Blocks until terminal (`200`; `202` when transient-requeued) unless `?wait=false`, which returns `202` at once with the job to poll (issue #148). When that registration is still waiting for the writer after `CONTROL_WRITE_WAIT_SECONDS` (another identity is producing), it returns `202 {"job_id":<integer>,"identity_hash":…,"status":"queued","attached":…}` with the id the job is (or will be) registered under, which `/control/status/{job_id}` resolves at once (issue #158, see [Queued control writes](#queued-control-writes-issue-158)). A caller disconnect/timeout **never** cancels production. A repository URL the [repository URL policy](#repository-url-policy-svc-5) refuses, both branch guards together (`conflicting_branch_guards`) or an unknown `branch_update` (`invalid_branch_update`) is `400 {"status":"rejected","reason":"<code>"}` before any job exists (audited `ensure`/`denied`). A user caller (`act=user` assertion) may ensure only a repository it can read: otherwise `403 {"status":"rejected","reason":"not_granted"}` (SVC-4), whatever the body asks. A user caller's ensure is then bounded (SX-6d, see [User callers on the control plane](#user-callers-on-the-control-plane-issue-193)): `default_branch: true`, any `branch_head_sequence`, or an advance with no `expected_head_commit` or no `branch_name` is `400` (`default_branch_not_allowed`, `branch_head_sequence_not_allowed`, `branch_guard_required`, `branch_required`). |
| `POST /control/contribute` | control | control **or** contribute token | Ingest a client/CI semantic contribution (Phase 16); the least-privilege contribute token authorizes this endpoint only. |
| `GET /control/status/{jobId}` | control | control token | Job status + per-project diagnostics (criterion 5) + checkout `coverage` (#119). For a user caller, a job on a repository it cannot read is the same `404` as an unknown id (SVC-4). |
| `GET /control/resolve` | control | control token | Resolve a repository branch (`?branch=`, else the default) to its current published snapshot (+ its `coverage`, #119), plus `commit_sha`, the resolved `branch` name, `is_default` and `head_sequence` (SVC-7; a null `commit_sha`/`head_sequence` is omitted), and `current_identity_hash` + `identity_current`: whether the snapshot is what an ensure of its commit would build on this node now (both omitted when there is no `commit_sha` or the head is a contribution; see [Identity currency](#branch-pointer-guards-head-cas-branch_update-none-retire-svc-67)). For a user caller, a repository it cannot read is the same bare `404` as an absent one (SVC-4). |
| `POST /control/branches/retire` | control | control token | Delete a branch pointer (`{repository, branch, expected_head_commit?}`, SVC-6); its snapshots stay for retention. `200 {"retired":true}`, or `{"retired":false}` for a missing branch (idempotent). The default branch or a head-CAS mismatch is `409 {"status":"rejected","reason":"default_branch"\|"head_mismatch"}`; a refused URL or blank branch is `400`. When the retirement has not applied within `CONTROL_WRITE_WAIT_SECONDS` (a production holds the writer), the answer is `202 {"status":"accepted"}`: it applies once the writer frees, with both guards evaluated then (issue #158, see [Queued control writes](#queued-control-writes-issue-158)). `503 {"status":"unavailable"}` once shutdown began (nothing queued). Audited `retire`. A user caller is refused with `403 {"error":"caller_not_allowed"}` (issue #193, see [User callers on the control plane](#user-callers-on-the-control-plane-issue-193)). |
| `POST /control/retention` | control | control token | Run the service-owned retention/GC pass (`?execute=true` to apply). A user caller is refused (`403 caller_not_allowed`). |
| `PUT`/`DELETE`/`GET /control/grants/self` | control | control token + `act=user` assertion | The caller's own repository grants (see [Repository grants](#repository-grants-and-visibility-svc-4)). |
| `PUT`/`DELETE /control/grants/tenant` | control | control token + `act=application` assertion | The tenant-wide repository grants. |
| `GET /control/grants?scope=tenant` | control | control token + `act=application` assertion | The tenant's distinct reconcile targets, with counts and no user ids. |
| `GET /control/metrics` | control | control token | Observability snapshot (criterion 5); `?format=prometheus` for text exposition, else JSON. A user caller is refused (`403 caller_not_allowed`). |
| `GET /control/audit` | control | control token | Durable audit log (criterion 5); optional `action`/`repository`/`limit` filters. **Operator-only**: a user caller is refused (`403 caller_not_allowed`). |
| `GET /control/pilot` | control | control token | Pilot-readiness gate (criterion 7); `?workload=trusted\|untrusted&hard_isolation=&recent_backup=`. A user caller is refused (`403 caller_not_allowed`). |
| `POST /control/backup` | control | control token | Write a consistent catalog + artifact backup to `?dir=` (criterion 6). A user caller is refused (`403 caller_not_allowed`). |
| `GET /query/snapshots/{identityHash}/symbols` | query | query token (or delegate token + caller assertion) | One immutable page of a snapshot's symbols, cursor-paged (federation, issue #51). |
| `POST /mcp` | query | query token (or delegate token; `tools/call` needs a caller assertion) | Authenticated HTTP MCP semantic queries (criterion 4). |

A null query token allows anonymous reads (single-node development). The control plane has no such default:
the service refuses to start without a control token unless `SEXTANT_SERVICE_INSECURE_OPEN_CONTROL_PLANE=true`
explicitly opens it, with a loud startup warning (SX-6d). Token checks are constant-time. Query
reads use a connection **independent** of the service writer (Phase-9 WAL supports concurrent readers), so
a low-latency query never blocks behind a running index. Control-plane **reads** — `/control/status`,
`/control/resolve` (and its coverage), metrics, audit — do the same (issue #148), so they answer promptly
for the whole duration of a long index.

### Repository selection on `/mcp`

A catalog can hold several repositories. An `/mcp` read names the one it wants either per call, in the
reserved `repository` tool argument, or per connection, in the `X-Sextant-Repository` request header (its
remote URL). The host always honors both, whether or not a read policy is configured. The read then pins
that repository's default-branch snapshot, or a named branch's snapshot when the call also sends the
reserved `branch` argument:

| Request | `REQUIRE_REPOSITORY_SELECTION=false` (default) | `REQUIRE_REPOSITORY_SELECTION=true` |
| --- | --- | --- |
| Names an indexed repository | That repository's default-branch snapshot (subject to the read policy, when one is set) | Same |
| Names a repository and a `branch` | The complete snapshot that branch's pointer targets (branch names match exactly) | Same |
| Names a repository (or branch) with no complete snapshot | Nothing, as a `repository_not_found` tool error (`isError: true`): an actionable message under an open read, the uniform not-found (below) under an enforced read policy. Never widened to other repositories or branches | Same |
| Names a `branch` but no repository | `meta.error.code = repository_required`, with no results | Same |
| Names nothing (or only blank values) | The unselected default: the only repository of a single-repository catalog, or **every** repository of a multi-repository catalog | `meta.error.code = repository_required`, with no results |

The `repository_required` error is an MCP tool error (`isError: true`) and names no catalog repository: for a
request without a verified caller it depends only on the request, so it reveals nothing about what the catalog
holds. For a verified caller its message also lists the repositories that caller's own grants make visible (the
`list_repositories` set, as `owner/repo`), so an agent can retry at once. The default keeps a caller that names no repository (for example a legacy
gateway using the plain query token) working unchanged; turn the requirement on once every caller selects
one. The local stdio MCP server has no header, advertises no reserved arguments, and never requires a
selection.

The **uniform not-found** is the one answer an enforced read policy gives to every read it cannot serve: a
repository the caller may not read, one that does not exist, a branch with no complete snapshot, or an
unprovisioned service. It is a `repository_not_found` tool error (`isError: true`) with one fixed message that
says what to pass instead (a repository you can read, as listed by `list_repositories`, and no `branch` for the
default branch). Every case gets the same code and the same bytes (only `queried_at` differs), and it echoes
nothing the request named, so it is not an existence oracle; what Phase 17 forbids is a distinct denial code. It
is an error, not an empty result, so an agent never reads "you cannot read this" as "no matches" (issue #163).

#### Reserved tool arguments (SVC-2)

A client that forwards tool arguments verbatim over one pooled connection with only static headers selects
per call through two reserved arguments. The service adds them in MCP request filters on its stateless
`/mcp` (`ToolSelectionFilters`); the tools themselves are unchanged.

- **`tools/list`** advertises two string arguments, `repository` and `branch`, on every
  repository-scoped remote tool (every tool on the remote allowlist except `list_repositories`, which reads no
  index, and `search_symbols`, which declares its own `repository`/`branch` narrowing arguments). The
  `repository` description says "Optional" only when omitting it can read something: with
  `REQUIRE_REPOSITORY_SELECTION` on it says "Required", and with delegate tokens configured it states the one case
  in which a verified caller may omit it (implicit selection, below). A tool that already declares an
  argument of the same name keeps its own: `find_cross_repository_usages` and `find_submodule_consumers`
  keep their consumer-filter `branch`, so they get only `repository`.
- **`tools/call`** removes the reserved arguments before the tool binds its own, so a tool never sees them.
  `repository` accepts `https://{host}/{owner}/{repo}[.git]`, `{host}/{owner}/{repo}`, or `{owner}/{repo}`
  when `REPOSITORY_HOSTS` lists exactly one host besides `*`. It must pass the same URL policy as
  `/control/ensure` (`REPOSITORY_HOSTS`/`REPOSITORY_OWNERS`) and is canonicalized with it before selecting.
  A JSON `null` or blank value counts as absent.
- **Precedence:** the `repository` argument wins over the header. When both are sent and name different
  repositories (compared canonically, so `https://github.com/Acme/Widgets.git` and `acme/widgets` are the
  same), the call fails with the tool error `selector_conflict`. A reserved argument that is not a string, or
  a repository the URL policy refuses, fails with `invalid_selector` (the message carries only the policy's
  reason code, never the value). A `branch` argument works with a header-selected repository.
- **Cross-repository tools:** when a selection is required and the call names no repository, the two
  cross-repository tools default the selection to their own `provider_repository_url`. The gate then pins
  the provider's default-branch snapshot, so it serves a provider that has one (for example one also ensured
  directly). A provider-only repository (indexed only as a submodule of its consumers) has no selectable
  snapshot, so such a call gets the uniform not-found. Name a consumer repository you can read in
  `repository` instead. With the requirement off the default does not apply, and these tools keep the
  unselected read.

The filter errors are MCP tool errors (`isError: true`) whose text is the usual JSON error envelope
(`meta.error.code`). The selection of each call is kept in the request's `HttpContext.Items`
(`ToolCallSelection`), where later per-call checks can reuse it.

### Agent-sized output on `/mcp`

The remote tools answer an agent, so every response is shaped to fit its context:

- **Paging.** Every tool that can return an unbounded list takes `limit` (default 50, max 200) and `cursor`,
  and reports `meta.total` and `meta.next_cursor` (see [Paged results](mcp-tools.md#paged-results)). A
  truncated first page leads with a `summary` (counts per project, file or kind) before `results`. A cursor
  is bound to the tool, its query arguments and the served snapshot (so its repository and branch): reused
  anywhere else, or after the branch has moved, it fails with `meta.error.code = invalid_cursor`.
- **Paths.** Every path in a response is repository-relative (`src/App/Foo.cs`); the worker's checkout
  directory never appears (#145). A path outside every checkout (a package's source file) is reduced to its file
  name, including where it keys a summary count. Path inputs (`file_path`,
  `file:` and `solution:` scopes) take the same relative form, and an absolute path is refused with
  `invalid_argument` (the message does not echo the path). `RemoteOutputFilter` (the innermost `tools/call`
  filter) applies this as a post-pass, so the
  local stdio server keeps its absolute paths.
- **Lean `meta.snapshot`.** Each response carries only `{repository, branch, commit, coverage, warning?}`
  (the 12-character commit; `warning` only when the answer may be incomplete: partial coverage, an incompatible
  indexer, or a dirty working tree), plus `repository_selection: "implicit"` when the caller named no repository
  and the service read its one visible repository. A tool error carries the same lean block. `get_index_status`
  reports the full provenance in `index.snapshot`. A partial-coverage warning names what is missing: it is
  `Partial index: ` + the recorded coverage reasons (for example `Code in 1 project(s) did not fully compile on
  the indexer, … (src/App/App.csproj: 412 unbound name(s)). …`), cut
  at a word boundary past 600 characters, + ` Call get_index_status for details.` A partial record with no
  reasons keeps the generic caution. `coverage.notes` (stray project files, restore problems) never produce a
  warning.
- **Server `instructions`.** `initialize` returns a short instruction string telling the agent to pass
  `repository` and to page with `next_cursor`. A gateway that does not forward server instructions, or a client
  that drops them (Copilot CLI keeps them only for an allowlisted server), loses only this hint: the tool
  descriptions stand alone, and the `repository` argument reads `Required: owner/repo` (or
  `Required: host/owner/repo` with several allow-listed hosts) when `REQUIRE_REPOSITORY_SELECTION` is on, and
  `Required unless exactly one is granted: owner/repo` when delegate tokens are configured (a verified caller that
  can read exactly one repository may omit it; see implicit selection below).
- **Compact `tools/list`.** Descriptions are one or two short sentences, the reserved `repository` argument is
  one short line and `branch` carries no description (omitted, it reads the default branch), and an optional
  argument is advertised as its plain type (no
  `["T","null"]` union, no `default: null`; an explicit JSON `null` still means "omitted").

### Caller assertions (SVC-3)

A gateway that serves many callers over one pooled connection authenticates with a **delegate token**
(`SEXTANT_SERVICE_DELEGATE_TOKENS`) and names the caller of each request in a signed **caller assertion**:
a compact HS256 JWS in the `SEXTANT_SERVICE_CALLER_HEADER` header (default `X-ProcessStack-Caller`). The
signing keys are `SEXTANT_SERVICE_CALLER_KEYS`, and each key belongs to exactly one tenant.

**The assertion.** The JOSE header is `{"alg":"HS256","typ":"JWT","kid":"<kid>"}` (`typ` may be omitted).
The header parameters `jku`, `jwk`, `x5u`, `x5c` and `crit` are refused. The payload carries:

| Claim | Meaning |
| --- | --- |
| `iss` | Issuer; checked against `CALLER_ISSUERS` when set |
| `aud` | Must equal `CALLER_AUDIENCE` (a string, or an array containing it) |
| `tid` | Tenant id; **must equal the tenant of the signing `kid`** |
| `tslug` | Tenant slug (recorded) |
| `act` | `user` or `application` |
| `idp`, `sub` | For `act=user` only: the identity provider and the full subject. `idp=processstack` needs a `sub` with no `:`; any other `idp` needs `{idp}:{connectionInstanceId}:{peerId}` |
| `app`, `cid`, `via` | Calling app, connection and surface (`via` is `mcp-surface` or `activity`). ProcessStack sets `app` to the application's id, not its slug |
| `dep` | Optional deployment id, sent only when the calling run is bound to a deployment (recorded). When present it must be a non-empty string |
| `run` | Optional run id |
| `jti`, `iat`, `nbf`, `exp` | Assertion id and times: 60 s skew, at most 300 s from `iat` to `exp` |

A verified `act=user` assertion also needs its `idp` in `CALLER_IDPS`, and, when `CALLER_APPS` is set,
every assertion needs its `app` in it. Because `app` carries the application's id, `CALLER_APPS` lists
application ids, never slugs.

**Where an assertion is accepted.**

| Request | Bearer | Assertion |
| --- | --- | --- |
| `/mcp` discovery (`initialize`, `ping`, `tools/list`, notifications) | delegate | Optional, so a pooled client connects and lists tools once; verified when present |
| `/mcp` `tools/call` | delegate | Required: without one the call is the tool error `caller_required`; a caller refused by `CALLER_IDPS`/`CALLER_APPS` is the tool error `caller_not_allowed` |
| `/query/*` | delegate | Required: `401 {"error":"caller_required"}` |
| `/mcp`, `/query/*` | query token, read-policy principal, or an open plane | Refused: `401 {"error":"assertion_not_allowed"}` |
| `/control/*` except `/control/contribute` | control | Optional; when present it must verify, and the audit actor becomes the caller. A verified user caller reaches only ensure, status, resolve and the grant routes ([below](#user-callers-on-the-control-plane-issue-193)) |
| `/control/contribute` | control or contribute | Refused: `401 {"error":"assertion_not_allowed"}` |

An assertion that fails verification is `401 {"error":"invalid_caller_assertion"}` with
`WWW-Authenticate: Bearer error="invalid_token"`; a caller refused by the idp or app allow-list is
`403 {"error":"caller_not_allowed"}`. Sending the header more than once counts as a failed verification.
With no `CALLER_KEYS`, an assertion on `/control/*` is `assertion_not_allowed`. Each refusal logs one warning
(category `Sextant.Service.Host.CallerAssertion`) with the reason code, the `kid` and the `jti`, and never
the assertion, another claim or a token.

**What a caller may read.** A verified caller must name its repository (as if
`REQUIRE_REPOSITORY_SELECTION` were on for that request), unless implicit selection picks it (below). A
delegate read is decided by the caller's [repository grants](#repository-grants-and-visibility-svc-4): a
repository the caller holds no grant for gets the uniform not-found, and a delegate snapshot-page request for
it is the uniform `404`. A delegate token therefore opens nothing on its own. A request without a delegate
token is unchanged.

**Audit.** A control call that carries a verified assertion is audited as `HashActor("{tid}/{sub}")` for a
user caller or `HashActor("{tid}/app:{app}")` for an application caller, instead of its bearer. Its audit
detail gains the suffix `;idp=…;kid=…;via=…;cid=…;dep=…;jti=…` (SVC-4). Each value is written only when it is
at most 64 characters of `[A-Za-z0-9._:-]`, else as `-`; an application caller has `idp=-`, and an assertion
without `dep` has `dep=-`.

### Repository grants and visibility (SVC-4)

A **grant** makes a repository visible to a caller. Grants live in the catalog (`repository_grants`,
migration `024`), keyed by `(tenant, principal, repository, branch)`:

- **Principal:** the full, exact `sub` of an `act=user` caller, or `'*'` for a tenant-wide grant. The
  principal and tenant come **only** from the verified assertion. A body or query that names one (`tenant_id`,
  `tid`, `principal`, `sub`, `user`, `user_id`, …) is refused with `400 principal_in_body`.
- **Repository:** the canonical `https://{host}/{owner}/{repo}` key, with the first-submitted spelling kept
  for reconcile.
- **Branch:** `''` for the repository's default branch.

**Visibility is repository-level.** A user caller sees a repository when it holds a grant of its own on it
or its tenant holds a `'*'` grant. An application caller sees only the tenant's `'*'` grants. Every indexed
branch of a visible repository is readable; the grant's branch only drives indexing and reconcile. The same
`sub` under another tenant's key sees nothing. Visibility is read once per HTTP request and never cached
across requests, so a revocation takes effect on the next call. Once a request has a verified caller:

- **Reads:** a delegate read of a repository the caller cannot see is indistinguishable from a repository that
  does not exist.
- **Cross-repository tools** list only consumer repositories the caller can see.
- **Implicit selection:** a delegate read that names neither a repository nor a branch reads the caller's
  **one** visible repository with a complete default-branch snapshot, and says so: `meta.snapshot.repository`
  names it and `meta.snapshot.repository_selection` is `implicit`. With none or several, it fails with
  `repository_required` (`isError: true`), whose message lists the caller's visible repositories.
- **`/control/ensure`** by a user caller needs the repository to be visible, otherwise `403 not_granted`
  (audited `ensure`/`denied`), and its body is then bounded (SX-6d, see
  [User callers on the control plane](#user-callers-on-the-control-plane-issue-193)). An application caller
  (a trigger) and an assertion-less call are unchanged.
- **`/control/status/{jobId}`** by a user caller is `404` for a job on a repository it cannot see.
- **`/control/resolve`** by a user caller is the same bare `404` as an absent repository when it cannot see
  the repository (checked before the branch is resolved). An application caller and an assertion-less call
  are unchanged.
- **Every other control route** (retire, retention, backup, metrics, audit, pilot) refuses a user caller with
  `403 caller_not_allowed`, whatever it can see ([issue #193](#user-callers-on-the-control-plane-issue-193)).
- **Failures fail closed:** if the grant catalog cannot be read, the read fails (a tool error or a `5xx`)
  rather than reading as allowed. A grant write, or a user's `not_granted` or SX-6d bound ensure refusal, that cannot be
  recorded (the service lost its writer lease or is stopping) is `503 unavailable` and writes nothing.

**Routes.** Every route needs the control token **and** a verified assertion with the stated `act`.

| Route | `act` | Request | Response |
| --- | --- | --- | --- |
| `PUT /control/grants/self` | user | `{repository, branch?}` | `200 {grant:{repository, branch, source, created_at}, created}` |
| `DELETE /control/grants/self?repository=&branch=` | user | `branch` omitted = the default-branch grant; `branch=*` = every branch | `200 {deleted: n}` |
| `GET /control/grants/self` | user | — | `{grants:[{repository, branch, source, created_at, status}], result_count}` |
| `PUT /control/grants/tenant` | application | `{repository, branch?}` | As for self, principal `'*'` |
| `DELETE /control/grants/tenant?repository=&branch=` | application | As for self | `200 {deleted: n}` |
| `GET /control/grants?scope=tenant` | application | — | `{targets:[{repository, branch, sources, watchers}], result_count}`: the distinct targets to keep indexed, with counts and **no user ids** |

- **`status`** is `{resolved_branch?, snapshot_status, commit_sha?, published_at?, identity_hash?}`.
  `snapshot_status` is `complete`, `partial`, `pending` (an ensure is queued or running) or `missing`.
- **Re-PUT:** re-PUT of an existing grant returns `created: false` and keeps its first spelling and source.

**Refusals:**
- **401:** no verified caller is `401 {"error":"caller_required"}`.
- **403, wrong actor:** `403 {"status":"rejected","reason":"wrong_actor"}`.
- **403, `sub` of `*`:** a user whose `sub` is `*` gets `403 {"error":"caller_not_allowed"}`.
- **400, malformed request:** `invalid_body` (not a JSON object of at most 16 KiB with a string `repository`, or a repeated DELETE parameter), `branch_not_allowed`, `invalid_scope`, or a [URL policy](#repository-url-policy-svc-5) code. A DELETE is accepted for a host or owner no longer on the allow-list, so an old grant can always be revoked.
- **409, limit:** `409 grant_limit` at `MAX_GRANTS_PER_PRINCIPAL` or `MAX_GRANTS_PER_TENANT`.
- **503:** returned when the service cannot record the write (lost writer lease, shutdown).

Every PUT and DELETE, accepted or refused, is audited as action `grant` with a detail like
`put_self;created` or `delete_tenant;deleted_2`; the `GET` routes are not audited.

**`list_repositories`** is an MCP tool on the service `/mcp` only. For the verified caller it returns
`{repositories:[{repository, sources, branches:[{branch, is_default, status, commit_sha?, published_at?}]}], meta}`:
every visible repository, with its catalog branches plus any granted branch not indexed yet. It takes no
arguments and is exempt from the reserved `repository`/`branch` arguments. A request with no verified caller
gets the tool error `caller_required`.

### `search_symbols` (SVC-F)

`search_symbols` is a service-only MCP tool on `/mcp` (the local stdio server does not register it). It searches
symbol names across every repository the verified caller can see under its [grants](#repository-grants-and-visibility-svc-4):
a user caller sees its own grants plus the tenant-wide ones, and an application caller sees the tenant-wide ones only.
A request with no verified caller (a plain query token) gets the tool error `caller_required` and reads nothing.

| Argument | Type | Rule |
|---|---|---|
| `name_prefix` | string, **required** | Case-insensitive (ASCII) literal prefix of the symbol's name (`display_name`); `%`, `_` and `\` match themselves. Trimmed; 1–256 characters |
| `repository` | string? | Search only this repository: `https://{host}/{owner}/{repo}`, `{host}/{owner}/{repo}` or, with one host in `REPOSITORY_HOSTS`, `{owner}/{repo}`, under the [URL policy](#repository-url-policy-svc-5). A repository the caller cannot see is the same `no_visible_repositories` error as one that does not exist |
| `branch` | string? | Search only this branch. Visibility is repository-level, so it can name any indexed branch of a visible repository. Absent = every **granted** branch (a default-branch grant searches the default branch) |
| `kind` | string? | Only symbols of this kind: a lowercase `SymbolKind` name such as `class`, `method` or `typeparameter` |
| `cursor` | string? | The previous page's `next_cursor`, passed back with the same other arguments |
| `limit` | int? | The most symbols read from each snapshot per page; default 50, clamped to 1–200. The call's total is also capped by `SEARCH_MAX_HITS` (default 500), shared by the snapshots the page reads |

The schema has `additionalProperties: false`, so an unknown argument is an error. The tool is exempt from the
[reserved selector arguments](#reserved-tool-arguments-svc-2) and ignores the `X-Sextant-Repository` header: its
own `repository`/`branch` narrow the search, and neither can widen it beyond the caller's grants.

**Result.** A text block and the same JSON as `structuredContent`:

```json
{
  "symbols": [{"repository": "...", "branch": "main", "identity_hash": "...", "symbol_key": "...",
               "name": "TypeA", "fully_qualified_name": "global::App.TypeA", "kind": "class",
               "accessibility": "public", "project": "..."}],
  "next_cursor": "...",
  "pending": [{"repository": "...", "branch": ""}],
  "unavailable": [{"repository": "...", "branch": "main"}],
  "truncated": [{"repository": "...", "branch": "main"}],
  "meta": {"queried_at": 0, "index_freshness": 0, "result_count": 1}
}
```

- `kind` and `accessibility` are **names**, rendered as the local tools render them. (The snapshot page
  `/query/snapshots/{identityHash}/symbols` still emits them as integers.)
- `pending` lists granted branches with no complete snapshot yet (`branch` is `""` when the default branch is
  not known yet). `unavailable` lists branches whose snapshot could not be read on this call; they keep their
  place in the cursor and are retried on a later page (within the same round-robin bound). `truncated` lists
  branches not read on this call because of `SEARCH_MAX_WIDTH`, or left out of a page cut by the response size
  budget; they are searched on later pages.
- Branches that share one snapshot are searched once, labeled with the first (repository, branch) in order.
- `next_cursor` is always present, and is `null` once every snapshot is exhausted.

**Paging.** Targets are deduplicated by snapshot identity hash. Up to 100 snapshots are tracked at a time, in hash
order. Each call reads at most `SEARCH_MAX_WIDTH` of them, round-robin: a page continues in hash order after the
last snapshot the previous page read, and once it reaches the end the next page starts a new round from the lowest
hash. A snapshot that has not been searched yet joins, as tracked ones are exhausted, at the end of a round. So a
snapshot with many matches never keeps the others waiting: with V visible snapshots, each tracked one is read at
least once every ⌈min(V, 100) / `SEARCH_MAX_WIDTH`⌉ pages. Each page reads up to `limit` rows from each snapshot
it reads, and at most `SEARCH_MAX_HITS` rows in all, shared evenly by those snapshots. It orders them by name, then
identity hash, then row. A page can return fewer symbols than it could (with a `kind` filter, even none) and still
have a `next_cursor`: keep paging until it is `null`. While the grants and branch heads do not change,
walking every page returns every match exactly once.

**Size.** A page's text also stays under `SEXTANT_SERVICE_MAX_RESPONSE_CHARS` (20,000 by default; see
[mcp-tools.md](mcp-tools.md#response-size-budget)). A page over it keeps, from each snapshot it read, the first
symbols of that snapshot's own page, so the snapshot resumes at the first one left out; if one symbol from each is
still too much, it keeps as many of the first snapshots of its turn as fit, and the others with matches are listed
in `truncated` and read first on the next page (a snapshot with no match is never held back). Such a page has `meta.page_truncated_by: "size"` and a top-level `message`. A size cut can
make a round take more pages than the bound above (each page then returns fewer snapshots than
`SEARCH_MAX_WIDTH`), but the round-robin order is kept, so no snapshot is starved. The `pending`, `unavailable`
and `truncated` lists and the cursor are never cut, and at least one symbol is always returned.

The cursor is opaque
base64url JSON of at most 16 KiB. It carries each snapshot's position, and a digest binds it to the tenant, the
caller and the query arguments other than `limit`. A tampered or oversized cursor, or one issued to another caller,
tenant or query, gets `invalid_cursor`, and so does a cursor from a Sextant before issue #196 (restart the search).
The digest is not an authorization control. Every call reads the caller's
grants again, so a revoked grant stops being searched on the next page, and a cursor naming a snapshot the caller
cannot see is treated exactly like one naming a snapshot that does not exist.

**Cost.** One call is bounded, whatever the size of the snapshots (issue #196):
- Each snapshot's page seeks the prefix's range in a name index (`ix_symbols_project_name_nocase`, migration
  `025`), so a prefix that matches nothing costs a few index lookups per snapshot, never a scan.
- The call returns at most `SEARCH_MAX_HITS` symbols.
- With a `kind` filter, it examines at most 8192 rows in all.
- The caller's repositories are found by one indexed query over the caller's own grants.
- If the client disconnects or cancels the request, the search stops: before its next snapshot, at the next row it
  reads, or by interrupting the SQLite statement that is running. The read connection is released cleanly.

**Errors** (all tool errors): `caller_required`, `invalid_arguments` (a missing or blank `name_prefix`, a wrong
type, an unknown argument or `kind`), `invalid_selector` (a `repository` the URL policy refuses),
`invalid_cursor`, and `no_visible_repositories` (nothing to search: no grants, or an explicit `repository` or
`branch` that matches nothing visible). A failure to read the grants fails the call; it is never presented as
`no_visible_repositories` or as an unscoped search.

### User callers on the control plane (issue #193)

A control call with a verified `act=user` assertion reaches **only** the routes that apply their own user
rule: `/control/ensure`, `/control/status/{jobId}` and `/control/resolve` (the SVC-4 visibility gates above)
and the grant routes (their `act` rule). Every other control route refuses a user caller with
`403 {"error":"caller_not_allowed"}` (no `WWW-Authenticate` challenge). The rule is default-deny, so a control
route added later refuses user callers too unless it opts in with its own rule (`ControlCallerRules`).

| Route | `act=user` | `act=application` or no assertion |
| --- | --- | --- |
| `POST /control/ensure` | Gated by visibility (SVC-4), then the body is bounded (SX-6d, below) | Unchanged (`default_branch`, sequences and unguarded advances still work) |
| `GET /control/status/{jobId}`, `GET /control/resolve` | Gated by visibility (SVC-4) | Unchanged |
| `/control/grants/self` | Allowed (own grants) | `403 wrong_actor` for an application; `401 caller_required` without an assertion |
| `/control/grants/tenant`, `GET /control/grants?scope=tenant` | `403 wrong_actor` | Allowed for an application; `401 caller_required` without an assertion |
| `POST /control/branches/retire` | `403 caller_not_allowed`, audited `retire`/`denied` | Unchanged |
| `POST /control/retention` | `403 caller_not_allowed`, audited `retention`/`denied` | Unchanged |
| `POST /control/backup` | `403 caller_not_allowed`, audited `backup`/`denied` | Unchanged |
| `GET /control/metrics`, `GET /control/audit`, `GET /control/pilot` | `403 caller_not_allowed` (not audited, like the grant `GET`s) | Unchanged |
| `POST /control/contribute` | Any assertion is `401 assertion_not_allowed` (SVC-3) | Unchanged |
| Any other `/control/*` path | `403 caller_not_allowed` | Unchanged (`404`/`405`) |

- **Order:** the refusal runs after the assertion is verified (a bad assertion is still `401
  invalid_caller_assertion`, and an idp/app allow-list refusal is still the SVC-3 `403`) and before the route
  reads its request or any catalog state. The response is therefore identical whether the named repository
  or branch exists, and it is the same even when the user holds a grant on the repository.
- **Retire is application/operator-only.** Visibility is not enough, because a grant makes a repository
  readable, not retirable: a user retiring a branch of a repository other users watch would be a destructive
  cross-user action. The app retires branches as its application identity (repository delete and reconcile).
- **Audit rows:** a refused retire, retention or backup writes a `denied` row under that action with detail
  `caller_not_allowed` plus the caller suffix (`dep=-` when the assertion has no `dep`), attributed to the
  user caller, with no repository scope (the refusal precedes reading the request).
- **`/control/audit`** is never readable by a user caller, so a user can never read another caller's audit
  rows, and `metrics`/`pilot` (cross-tenant counts and cost) stay operator data.
- An application caller and an assertion-less control call are unchanged, and a deployment without
  `CALLER_KEYS` never has a verified caller on `/control/*` (an assertion there is `401 assertion_not_allowed`).
- **Request targets are normalized before these rules.** On a real Kestrel socket, `%2e%2e`, `./`,
  percent-encoded letters, a case change and an absolute-form target all reach the route they normalize to
  and get the same refusal. `;x`, `//` and an encoded `%2f` match no route: they are `403` under `/control` and
  `404` outside it (`KestrelControlPathTests`, #198).

**User ensure bounds (SX-6d, issue #198).** A grant lets a user read a repository and index a commit of it. It
does not let the user change the branch state every reader of that repository shares. For a user caller only,
after the visibility gate (so an ungranted user sees only `not_granted`, whatever the body), the ensure body is
checked in this order, reading nothing but the body:

| Body | Response (`400 {"status":"rejected","reason":…}`, audited `ensure`/`denied` with the repository scope) |
| --- | --- |
| `default_branch: true` | `default_branch_not_allowed` |
| any `branch_head_sequence` | `branch_head_sequence_not_allowed` |
| `branch_update: none` | allowed (moves no pointer) |
| no `expected_head_commit` | `branch_guard_required` |
| a missing or blank `branch_name` | `branch_required` (an ensure that names no branch claims the default) |

A user's CAS (`expected_head_commit` plus `branch_name`) moves only the branch it names, and only while that
branch still points at the expected commit. A stale CAS attaches without moving anything. `default_branch: false`
and `forced` are allowed. A body that also trips an SVC-6+7 conflict (`conflicting_branch_guards`,
`invalid_branch_update`) keeps that code. The service cannot verify the upstream head, so a user CAS can still
move its branch, the default included, from the head it observed to a commit it names. The calling app is
expected to check the head upstream first.

**No implicit default for a user (issue #199).** A repository's first branch normally becomes its default (the
#104 safety net). For a user caller it does so only when that branch is the one the remote's `HEAD` names. The
service looks that up itself by running `git ls-remote --symref` in clone mode, with the same credential scoping
as a clone and a 30 s timeout. The request never supplies it. The lookup happens only while the repository has no
default and only for an ensure that may move a pointer, never under `branch_update: none`. Lookups are bounded:
- concurrent lookups of one repository share one call;
- an answer is remembered for 60 s;
- at most 4 run at once.

Such an ensure, `?wait=false` included, is registered only after its lookup finishes. The branch is created
non-default when the lookup is unavailable: in locate mode, when every lookup slot is busy, or when the remote is
unreachable or its answer is unparseable. The repository then gets a default from an application
ensure (a push or reconcile) or from a user ensure of the remote's default branch. Application and
assertion-less callers are unchanged.

## The `SnapshotService` data plane

`SnapshotService` owns the durable snapshot catalog + semantic store on a **single writer connection**,
serialized by an async gate (a raw `SqliteConnection` is not thread-safe), and guarded by the
cross-process single-writer lease. On `Start` it, in order: runs migrations **without** recovery,
acquires the writer lease (**fail-closed** — throws if a live writer already holds it), recovers a valid
WAL / sweeps abandoned staging generations, and reconciles orphaned jobs.

### Idempotent ensure (criterion 1)

`EnsureSnapshotAsync` computes the request's Phase-9 `SnapshotIdentity.Hash` — filling an omitted
`config_hash` from the service's `DefaultConfigHash` (the profile's `ConfigurationHash`) so a client that
doesn't send one still lands on the **same** identity the worker publishes under. `EnsureJob` then creates
or attaches to the **one** durable `snapshot_jobs` row for that identity. The caller attaches immediately
**only** when the job is terminal **and** its result is still usable — a `complete` job whose published
snapshot was later reclaimed by retention (issue #46) is **stale**, so it is requeued and regenerated rather
than reported as a phantom-complete. Otherwise production is serialized under the write gate so only **one**
worker runs per identity while concurrent callers attach. The pluggable `ISnapshotWorker` produces the
snapshot under a per-job **scratch** directory; its output is validated — a worker that claims
`complete`/`partial` but published no complete snapshot, **or** published a snapshot whose identity does not
match the requested hash, is **downgraded to `failed`** — and published through the catalog, then a terminal
status + per-project diagnostics are recorded. A job **cancelled** mid-run is requeued (transient), never
recorded as a permanent failure.

### Ensure lifecycle: production outlives the caller (issue #148)

Indexing a large repository takes far longer than a typical HTTP client timeout (ProcessStack's
`HttpClient` defaults to 100 s; proxies and operator `curl`s give up too). So an ensure's production is
owned by the **service**, never by the request:

- **A caller disconnect/timeout never cancels production.** The worker runs on a service-owned lifetime;
  the request only *waits* for it. When the caller goes away, only that wait ends — the job stays
  `running`, the worker finishes, and the snapshot publishes normally (status, diagnostics, coverage,
  branch pointer, audit row).
- **Re-ensures attach to the in-flight run.** In-flight productions are tracked per identity, so an
  ensure for an identity that is already producing attaches at once (without waiting for the writer) and
  the worker runs **once** (criterion 1). A retrying client never restarts an index.
- **Non-blocking ensure.** `POST /control/ensure?wait=false` returns **`202 Accepted`** as soon as the job
  is registered. The body is the same ensure-result shape as a blocking ensure — `job_id`,
  `identity_hash`, `status` (`queued` while waiting for the writer, `running` once the worker holds it),
  `attached` — or the full terminal result when the identity is already settled (`200`). Poll
  `GET /control/status/{job_id}` until `job.status` is terminal. Without the parameter the ensure keeps the
  blocking contract (`200` terminal, `202` transient-requeued).
- **Only shutdown cancels a worker.** On host `ApplicationStopping` (and `SnapshotService.Dispose`) the
  service lifetime is cancelled: each in-flight worker is cancelled and its job **requeued** (never
  failed; even a worker that ignores the cancellation and then reports a failure is requeued, because such a
  failure is usually just the shutdown surfacing). Dispose waits up to `ShutdownDrainTimeout` (30 s) for those requeues to land **before** it
  releases the writer lease. From the moment shutdown begins, every ensure is refused with **`503`**
  (`status: unavailable`), including a waiting one and a `wait=false` attach to a production that is
  winding down. The body claims nothing about the job (it may have been requeued, never registered, or
  already settled), so the client retries against the next instance and that ensure reports the real state.
- **A worker that ignores cancellation never races a successor.** If a production is still running when
  the drain bound expires, Dispose **abandons** the writer lease instead of releasing it. Every write
  probe then fails closed: the straggler's write session aborts at its next batch, and its result is never
  recorded. The lease row is left to **expire by its TTL**, exactly as if the process had crashed, and the
  host leaves the shared catalog open for process exit. A restart within the TTL is refused by the
  fail-closed lease guard until it expires; the next owner's startup reconcile then requeues the
  `running` job.
- **Single writer is unchanged.** Productions of *different* identities are still serialized by the one
  writer, and registering a **new** identity's job needs that writer too. A `wait=false` ensure no longer
  waits for it past a short bound, though: see [Queued control writes](#queued-control-writes-issue-158).
  The ensure survives its caller disconnecting either way: it registers and produces once the writer
  frees up.
- **Lease loss mid-run fails closed.** If the writer lease is stolen during a worker run, the service
  records **nothing**. The job stays `running` for the new owner's startup reconcile to requeue (#38).

**Recommended clients:** use `?wait=false` and poll `/control/status/{job_id}` with backoff, with a
polling budget sized for your largest repository (hours for a monorepo, not minutes). A blocking ensure is
fine for small repositories or operator use. Either way, a client timeout is now harmless: re-issue the
ensure to re-attach, or poll the job. Status and resolve reads never wait for a running index, so a short
per-poll timeout (a few seconds) is appropriate.

### Queued control writes (issue #158)

A production holds the single writer for its whole run, which can take hours for a monorepo. Two event-driven
control writes must not make their caller wait that long: registering a new identity's job
(`POST /control/ensure?wait=false`) and retiring a branch (`POST /control/branches/retire`). Both are
**service-owned** operations that take their turn on the writer when the service admits them. The caller waits
at most `SEXTANT_SERVICE_CONTROL_WRITE_WAIT_SECONDS` (default `5`, at most `25`) for that turn; the write
itself never gives up.

- **Ensure with `wait=false`.** When the job is registered within the bound, the response is unchanged: a
  `202` `queued`/`running` result, or the `200` terminal result. Otherwise the response is `202` with
  `{"job_id": <integer>, "identity_hash": "…", "status": "queued", "attached": <bool>}`.
  - `job_id` is always a JSON integer. It is the identity's durable job id when its row already exists;
    otherwise it is an id **reserved** for the identity, and the row is inserted with exactly that id when the
    ensure's turn comes.
  - `GET /control/status/{job_id}` resolves a reserved id at once, as a `queued` job described by the
    submitted repository, commit and branch, with no diagnostics. It keeps resolving the id once the row
    exists. If the registration fails instead (for example the instance lost the writer lease before its turn),
    the reserved id answers `404`: ensure the identity again.
  - Another ensure of the same identity while the first is queued gets the same `job_id`. The identity is
    registered once and produced once. `attached` is `false` only for the request that reserved the id.
  - The `202` is given without reading the job's state under the writer, so an identity whose job is
    already terminal may be answered `queued` too; polling then reports the terminal `job.status`. In every
    case the ensure's branch decision (the advance, CAS or attach) is made when its turn comes, not at
    submission.
- **Blocking ensure** (no `wait`, or `wait=true`): unchanged. It waits for the result.
- **Retire.** When the retirement applies within the bound, the response is unchanged
  (`200 {"retired":…}` or `409`). Otherwise it is `202 {"status":"accepted"}`, with no `retired` field.
  - The retirement stays queued and applies when the writer frees.
  - The default-branch guard and the `expected_head_commit` CAS are evaluated **when it applies**, against the
    branch as it is then.
  - Its `retire` audit row (`retired`, `absent`, `default_branch` or `head_mismatch`) is written then. To learn
    the outcome, read the audit log, or `GET /control/resolve` the branch.
  - An identical retirement (same repository, branch, `expected_head_commit` and audit actor) submitted while
    one is still queued, with no other write admitted in between, is **coalesced** into it: it shares that
    retirement's result and its single audit row.
- **A caller's disconnect or timeout never drops a queued write.** The request token only bounds that
  caller's own wait.
- **Submission order.** Ensures and retires take their writer turns in the order the service admits them,
  and a production runs on the turn of the ensure that started it. So a push's ensure, the branch's retire
  and its re-creation's ensure apply in that order even when all three are queued behind another
  repository's production. Other control writes (contribution ingest, retention, backup, denied-audit
  rows) queue on the same writer.
- **Graceful shutdown drains.** Once shutdown begins, a new ensure or retire is refused with
  `503 {"status":"unavailable"}` and nothing is queued. Writes queued before that **drain**: Dispose
  cancels and requeues the running production, then waits (up to `ShutdownDrainTimeout`, 30 s) for each
  queued write to take its turn before it releases the lease. A queued retirement applies, and a queued
  registration inserts its job row under its reserved id. That job is not produced: it stays `queued`, and
  the next ensure of the identity produces it.
- **A crash loses queued writes.** Queued registrations and retirements are held in memory only, so a
  crash, or a drain that times out, **loses** them:
  - A reserved `job_id` then answers `404` from the next instance. Re-ensure the identity to register it.
  - A reserved id is never given to another identity. Before it hands out an id with no row yet, the service
    persists an id floor in `<catalog-db>.job-id-floor`, and ids start above it after a restart. If that
    file cannot be written, the ensure keeps waiting for the writer rather than hand out an unrecorded id.
    A malformed floor file **fails startup**.
  - A lost retirement leaves its branch in place.

  Treat a `202` as "accepted, not yet durable", and keep a reconcile as the backstop. The ProcessStack app's
  nightly reconcile re-ensures watched branches and retires branches the host no longer has.

**Forward-only branch-head advance (Phase 14, issue #84).** An ensure request may carry an optional
monotonic `branch_head_sequence`. Because the service ensures *every* delivered commit (including
out-of-order/older ones) to build immutable content-addressed snapshots, the control plane owns commit
ordering and supplies this sequence so Sextant advances the data-plane branch pointer **forward-only**: the
pointer (and stored `branches.head_sequence`) advance only when the supplied sequence is strictly greater
than the stored one; a lower/equal sequence still ensures/attaches the immutable snapshot but leaves the
branch pointer untouched (no transient regression to a stale snapshot). A NULL sequence — the local
CLI/daemon path — advances unconditionally and never writes the column, preserving pre-#84 behavior (a
null-sequence service ensure that *reuses* a published snapshot follows the stricter #162 rule below).

**Re-selecting a superseded snapshot on reset/force-push (issue #85).** A branch advance supersedes the
previous head, so after A@10 → B@20 the snapshot for A is `superseded`. A sequence-bearing ensure of A
(e.g. A@30 after a reset/force-push) re-selects A on the no-worker reuse path, the same way the
orchestrator's `SelectExistingSnapshot` does. In ONE write transaction under the single-writer gate, A is
restored to `complete` and the same forward-only gate runs. A higher sequence re-points the branch back to A
and supersedes B. A lower/equal sequence is declined: the pointer and `head_sequence` stay untouched, but A
stays `complete`, so it can still be resolved by commit, as #84 already specifies for the orchestrator path.
The job verdict comes from A's durable coverage row (`partial` when that row is partial). The row is read and
reported, never rewritten or backfilled.

A superseded snapshot is only reused when it is still **intact**. All of these must hold:
- it was published (`published_at` set);
- it is not an overlay;
- it still owns mapped project-version data;
- every Phase-12 provider it consumes is still published.

The `index_runs` ledger is not consulted. `run_id` is bound when the row is first staged, and a retry
republishes into the same id. Retention also reclaims ledger rows independently of the snapshot's data.

Otherwise it is demoted to `failed` and the ensure falls through to the worker, which rebuilds the identity
into the same snapshot id instead of resurrecting a data-less snapshot. A snapshot whose row retention has
already GC'd is simply regenerated (issue #46). The null-sequence path does not re-select: it still hands a
superseded identity to the worker.

**Null-sequence reuse re-points only when history cannot regress (issue #162).** An ensure with no
`branch_head_sequence` that reuses an already-published snapshot (a Phase-12 provider built by a monorepo
ensure, or any identity built earlier) used to be attach-if-unset. The worker path for the same request
advances unconditionally, so identical requests had different outcomes depending on whether the identity
had already been built. The reuse path now re-points the branch **only** when that cannot move the head to
older history:

- the branch has no target yet (attach, as before);
- the branch's current target is **not usable**: it is no longer `complete` (for example `superseded`), or
  retention reclaimed it (the pointer is null);
- the current target is a **pure identity change at the same commit**: same repository, same non-null
  commit, neither snapshot an overlay, and no working-tree delta. This covers an `AnalyzerVersion`, schema,
  config, or toolchain upgrade, and a flip of the node's SDK-pin override policy (issue #113, now part of the
  identity). The latest such ensure wins, as it does on the worker path, so nodes with different policies
  ensuring the same commit simply alternate the head between two equally current snapshots.

In every other case, and in particular when the current target is at a different commit, the pointer is left
alone: a null sequence carries no ordering, so an older commit must not replace a newer head. When the branch
is re-pointed, the previous target is marked `superseded` only if it was `complete` and **no other branch
still points at it**, so this path never strands another branch on a superseded head (the #128 hazard;
the other advance paths are unchanged). `head_sequence` is never written. Everything runs in the existing
single `BEGIN IMMEDIATE` transaction under the write gate, together with the #104 default-branch
bookkeeping. Reuse also clears the provider-only flag on the repository, on both the null-sequence and the
sequence-bearing path, just as the worker's `EnsureRepository` does. So a repository first seen only as a
provider becomes selectable once it is ensured directly. Sequence-bearing requests stay on the #84/#85
forward-only gate, unchanged.

On the terminal-attach fast path (an already-terminal job), checking that the job's snapshot is still usable
and advancing the branch to it happen in the same write-gate hold. Otherwise a concurrent ensure could
supersede the snapshot between the check and the advance, and the head would end up on a `superseded`
snapshot.

### Branch-pointer guards: head CAS, `branch_update: none`, retire (SVC-6/7)

A coordinator that forwards git push events knows each push's `before` commit but may not have a
monotonic sequence to hand. For that case an ensure can carry a **head compare-and-swap** instead of a
`branch_head_sequence`. The fields are additive, and none of them is part of the snapshot identity, so a
guarded and an unguarded ensure of one commit attach to the same job and snapshot:

- `expected_head_commit` (string): the commit the branch head must currently be at for the pointer to move.
- `forced` (bool): informational only. It marks a force-push and is appended to the `ensure` audit detail as
  `;forced`. It **never** bypasses the CAS.
- `branch_update` (`"advance"`, the default, or `"none"`): `none` builds or attaches the snapshot without
  touching any branch.

The result carries `branch_advanced`: `true` when this ensure moved the requested branch's pointer onto the
snapshot, `false` when the decision declined to move it or it already pointed there. It is absent when there
was no snapshot to decide for (a failed or unsupported job), and when the ensure attached to another
request's production that did not settle terminally (a transient requeue). A caller that attaches to another
request's production that did settle re-runs its **own** branch decision on the terminal-attach path above
and reports that decision.

The service evaluates one precedence table **inside the branch-advance write transaction on every path**:
the worker's `IndexOrchestrator.AdvanceBranchToSnapshot`, both reuse paths (#162 attach-or-upgrade and the
#84 sequence path) and the #85 superseded re-select.

| # | Request | Branch decision |
| --- | --- | --- |
| 0 | `branch_update: "none"` | No pointer moves and **no branch row is created**; any other guard is ignored (so `none` with both guards is not a conflict). A reuse still clears the provider-only flag, like the worker. |
| 1 | both `expected_head_commit` and `branch_head_sequence` | `400 conflicting_branch_guards` at intake, before any job. |
| 2 | `expected_head_commit` only | CAS (below). On a pass the branch advances (created first when absent). On a mismatch the snapshot is **attach-only**: no branch row is created, no default is promoted, and the pointer and `head_sequence` stay as they are. |
| 3 | `branch_head_sequence` only | The #84/#85 forward-only gate, unchanged. |
| 4 | neither | Unchanged: the worker advances unconditionally; a reuse follows #162. |

The CAS **passes** when any of these hold:

- the branch does not exist and `expected_head_commit` is empty or all zeros (git's null object id, as a
  branch-create push reports it);
- the branch exists and its target's commit equals `expected_head_commit` (case-insensitive);
- the branch exists but its target is **unusable**: the pointer is null (retention reclaimed it) or the
  target is not `complete`.

It fails in every other case. In particular it fails when the branch exists at a usable head and the empty or
zero value is sent, when the branch is absent and a real commit is sent, and when the target snapshot has no
recorded commit. A passing CAS supersedes the previous target only if it was `complete` and no other branch
still points at it (the #128 guard), and it never writes `head_sequence`.

A superseded snapshot is re-selected under **any** guard (sequence, CAS or `none`), not only under a
sequence. When intact it is restored to `complete` without the worker, exactly as the orchestrator's
`SelectExistingSnapshot` restores it. The branch is re-pointed at it **only when the guard passes**, so a
reset or force-push whose CAS matches re-points the head and a stale one leaves it alone. An unguarded
ensure still hands a superseded identity to the worker.

**Out-of-order pushes converge on retry.** Pushes `A→B` then `B→C` delivered in reverse order: `B→C`
arrives first and its CAS fails (the head is still A), so it builds C and returns `branch_advanced: false`.
`A→B` then advances the head to B. Re-sending `B→C` (a cheap reuse) advances it to C. A delayed duplicate of
`A→B` is then declined, because the head is C. The client should retry a declined push once the pushes
before it have landed, or let its reconcile catch up. A push for a branch the service has never seen that
names a real `before` commit fails the CAS, so seed the branch first with an ensure that sends
`expected_head_commit: ""`, or with a plain unguarded ensure.

**Retire.** `POST /control/branches/retire` with `{repository, branch, expected_head_commit?}` deletes the
branch row for a branch deleted upstream. The snapshots stay in the catalog, and retention reclaims them once
nothing else protects them. It is application/operator-only: a user caller is refused with
`403 caller_not_allowed` before the body is read, audited `retire`/`denied` (issue #193, see
[User callers on the control plane](#user-callers-on-the-control-plane-issue-193)). The endpoint applies the
[repository URL policy](#repository-url-policy-svc-5)
exactly as ensure intake does. A refused URL (or a blank `branch`) is a `400` whose body carries only the
reason code, audited `retire`/`denied` with no repository scope. Then, in one write transaction under the
single writer:

1. A repository or branch the catalog does not know is `200 {"retired":false}` (idempotent), even under
   a CAS.
2. The repository's default branch is `409 default_branch`: it is never retired.
3. When `expected_head_commit` is present and the CAS above fails, the result is `409 head_mismatch`, so a
   branch that a newer push re-created is never retired.
4. Otherwise the row is deleted: `200 {"retired":true}`.

Every outcome writes a `retire` audit row in the same transaction. The detail is `retired`, `absent` or
the refusal reason, scoped to the request's repository. Retire is a service-owned write that takes its turn on
the single writer in submission order (issue #158). When a running production holds the writer and the
retirement has not applied within `SEXTANT_SERVICE_CONTROL_WRITE_WAIT_SECONDS`, the response is
`202 {"status":"accepted"}`. The steps above then run when the writer frees, against the branch as it is at
that point, and their outcome is recorded only in the audit row. The caller's disconnect or timeout never drops
the retirement. See [Queued control writes](#queued-control-writes-issue-158) for ordering, coalescing and
durability.

Because a failed CAS never creates a branch, a stale push that arrives after its branch was retired cannot
re-create it. A
**sequence**-guarded ensure has no such protection: retire deletes the row together with its
`head_sequence`, so a late sequence-bearing ensure re-creates the branch. Do not mix the two guards on one
branch; the CAS path never writes `head_sequence`, so that value also goes stale.

**Resolve.** `GET /control/resolve` additionally returns the head's `commit_sha`, the `branch` it resolved
through (the default branch's own name when `?branch=` is omitted), `is_default` and `head_sequence` (a null
value is omitted). A reconciler can therefore read the head commit to send as the next push's
`expected_head_commit`.

**Identity currency.** A snapshot's identity hash folds the node's schema, analyzer, toolchain, default profile
and policies, so after an upgrade (an `AnalyzerVersion` or `SnapshotSchemaVersion` bump) or a policy flip
(`SDK_PIN_OVERRIDE`, `PACKAGE_RESTORE`) the snapshot a quiet branch points at is no longer what an ensure of
its commit would build, although its commit still equals the upstream head. `GET /control/resolve` therefore
also returns, next to the row's own `identity_hash`:

| Field | Type | Meaning |
| --- | --- | --- |
| `current_identity_hash` | string | The identity hash an ensure of the head's commit would compute on this node **now**: no `config_hash` (the node's default profile), no tree sha, no capability override, and the node's present schema, analyzer, toolchain, capability, SDK-pin and restore components. |
| `identity_current` | bool | `identity_hash == current_identity_hash`. `false` means the head is not the identity an ensure of its commit builds now. |

The identity also folds the raw repository URL spelling, and one repository is ensured under several (a push
sends GitHub's `clone_url`, `…/repo.git`; a reconcile sends the grant's stored spelling). A spelling variant
alone is never stale: `current_identity_hash` is computed under the spelling of the snapshot's own ensure
(its job row), of the resolve request's `repository`, and of the catalog's repository row, and the first that
reproduces the snapshot's `identity_hash` is returned (`identity_current: true`). When none does, it is the hash
under the request's spelling, which is exactly the `identity_hash` the caller's own ensure of `commit_sha` will
produce. A head built by an ensure that sent a `config_hash` or `tree_sha` reads as not current, since the
default ensure builds another snapshot.

Both fields are **omitted together, never `null`**, when currency is not reported, so a client treats an absent
`identity_current` as current, which is also what an older service implies:

- the head records no commit (`commit_sha` is absent);
- the head was assembled from client contributions (`POST /control/contribute`). A contribution carries its
  contributor's identity (its own tree sha, no toolchain), which no service ensure reproduces, and rebuilding
  it on the server could replace client-built projects (say, Windows-only ones) with a narrower snapshot. Its
  contributor refreshes it.

The fields are additive: old clients ignore them. A head left behind by an upgrade from analyzer `"4"` and schema
24 (hashes shortened):

```json
{
  "id": 41, "repository_id": 3, "commit_id": 17, "run_id": 41,
  "identity_hash": "e722df68…",
  "schema_version": 24, "analyzer_version": "4", "config_hash": "f421bf15…", "toolchain_fingerprint": "5c794fa4…",
  "status": "complete", "created_at": 1791178954837, "published_at": 1791178954849,
  "is_overlay": false, "is_provider": false,
  "commit_sha": "0123456789abcdef0123456789abcdef01234567",
  "branch": "main", "is_default": true,
  "current_identity_hash": "33bdf552…",
  "identity_current": false
}
```

To refresh a stale branch, ensure the same head with a CAS on it: `commit_sha` and `branch` from the resolve,
`expected_head_commit` = `commit_sha`, `branch_update: "advance"`. The CAS passes (the head's commit equals the
expected value), the worker builds the new identity, and on publish the branch moves to the new snapshot (the
previous one is superseded unless another branch still points at it, #128); resolve then reports
`identity_current: true` with the new `id`, and queries serve the new snapshot. A repeat of that ensure is a
reuse (`branch_advanced: false`), never a re-index. Like any ensure, it attaches to a recorded terminal job of
the same identity: if the current identity's job already ended `failed` or `unsupported`, the ensure returns that
result without rebuilding, and `identity_current` stays `false` until the commit or the node's identity changes.
A reconciler should treat that result as settled for the pass rather than retry it in a loop (a `failed` job
needs a fix, a new commit, or an identity change).

The local CLI/daemon path never sets these guards (`SnapshotContext.ExpectedHeadCommit` and
`SuppressBranchUpdate` stay null), so its branch state is byte-identical.

### Restart recovery (criterion 2)

The catalog, published data, branch pointers, and dependency edges are all durable SQLite. A restart
holds a **fresh** lease token, so `ReconcileOrphanedJobs` resets every `running` job **not** owned by the
new token back to `queued` for re-attempt — a job a dead worker abandoned is never stuck `running`, and a
job a still-live writer owns is left alone.

### Structured diagnostics (criterion 5)

A partial / failed / unsupported job records one `snapshot_job_diagnostics` row per affected project with a
machine-parseable `code`, `severity`, and `message`. `GET /control/status/{jobId}` returns them, so a
client learns **which** projects failed and **why** (extending the Phase-9 completeness gate + Phase-8
capability meta) instead of a single opaque failure. A node with no configured worker uses
`UnavailableSnapshotWorker`: it is available for **queries** but reports no capacity, and any ensure
resolves to an `unsupported` job rather than hanging queued forever.

## Platform-specific routing by worker capability (Phase 15)

The ProcessStack server runs on **Linux**, but a C# graph may target Windows (`net8.0-windows`) or Apple
platforms (`net8.0-ios`, `-maccatalyst`, …). Linux evaluates *most* such projects fine with restored
reference packs; only projects whose evaluation Linux genuinely **cannot** complete are routed to a native
worker. Routing keys off **demonstrated evaluation success + policy, NEVER the TFM name alone** — a
`net8.0-windows` class library that Linux loaded cleanly is never needlessly routed.

**The capability model + routing decision are ProcessStack-agnostic and live in `Sextant.Core.Platform`:**

- `WorkerCapability` — a stable, comparable advertisement of what a worker can faithfully evaluate (OS,
  architecture, SDK bands, installed workloads, reference packs, target platforms, custom tools). Its
  deterministic `Fingerprint` (SHA-256) is recorded in snapshot provenance and folded into the Phase-9
  snapshot identity **only when non-null**, so a snapshot built under one capability set is never silently
  reused under an incompatible one (extends the Phase-8 config-hash + Phase-11 read-time compat gate).
- `CapabilityRequirement` — what a project graph needs; a *portable* requirement is satisfied by any
  worker. A requirement derived from the TFM string is only a **hint** (`RequirementSource.Declared`);
  routing escalates only on a `DemonstratedFailure`.
- `CapabilityRouter` — the **pure** decision engine: given each project's discovered requirement, its
  demonstrated Linux outcome, the available worker capabilities, and the policy, it decides per project to
  keep it on Linux, escalate it to the least-specialized compatible native worker, or fail it closed. It
  never executes anything.
- `PlatformRoutingPolicy` — per-repo/profile policy (`platform_routing` in `sextant.json`, env
  `SEXTANT_PLATFORM_ROUTING`): `auto` (default — escalate to any compatible native worker) or `linux_only`
  (never escalate; a non-Linux-evaluable project is marked unsupported). An `allowed_native_operating_systems`
  set can enable one OS while withholding another during capacity provisioning.

**The execution seam is service-side (`Sextant.Service.Placement`), behind the same core contract:**

- `IWorkerPlacement` — advertises a `Capability` and produces a snapshot when selected. `LocalPlacement`
  (the Phase-13 in-process indexer) is the **default (Linux)** placement. The actual native Windows/macOS
  worker EXECUTION is ProcessStack's trusted-placement job (**Phase 14, deferred**) — a Phase-14 placement
  implements this same interface without the core routing contract ever depending on ProcessStack.
- `IPlatformEvaluationProbe` — observes the demonstrated Linux outcome per project. The default
  `AssumeLinuxCapableProbe` reports everything Linux-capable (correct zero-cost behaviour for the common
  portable case); a deployment with native workers substitutes a real probe.
- `CapabilityRoutingSnapshotWorker` — the `ISnapshotWorker` that ties probe → `RouteJob` → fail-closed
  diagnostics **or** execute the selected placement. Routing is **job-granular** in Phase 15 (one worker
  per job; per-project mixed production is a later phase).

**Fail closed (criterion 4).** When no compatible worker exists (or no single worker covers the union of a
job's escalated requirements), the job resolves to `unsupported` with a structured `no_compatible_worker`
diagnostic per affected project (reusing `snapshot_job_diagnostics`) and **no** complete snapshot is
published — never a silent empty success. A routed success additionally records a `routed_to_native_worker`
info diagnostic in provenance.

**Local operation is untouched (CRITICAL 2).** A plain single-node/local run leaves the snapshot
capability null (identity byte-identical to pre-Phase-15) and, with only the default placement registered,
the routing worker never escalates — so a local Linux/Windows/macOS dev box indexes its own platform
exactly as before, with **zero** routing infrastructure required.

Because the native placements are a substitutable seam, the routing **decision** + fail-closed + fingerprint
logic are fully covered on a Linux CI with fake Windows/macOS placements (criterion 6). The env-gated
`PlatformFixtureMatrixTests` (`SEXTANT_RUN_PLATFORM_MATRIX=1`, `[TestCategory("Performance")]`) sweeps the
full fixture matrix without destabilizing the default suite on runners lacking a native toolchain.

## On-disk volumes — scratch is quarantined (criterion 3)

`ServicePaths` materializes four roots and enforces the load-bearing invariant that **worker scratch is
separate from the persistent checkout/artifact/cache volumes**:

- The constructor asserts the scratch root is not nested inside any persistent volume (or vice versa) and
  throws otherwise.
- Scratch is allocated **per job** under the scratch root; `ReleaseScratch` refuses any path that resolves
  outside the scratch root (a persistent volume, the catalog directory, or a `..` escape).

So a botched worker-scratch cleanup can **never** reach — let alone delete — a published snapshot's durable
data. This extends the Phase-8 retention servable guard + Phase-9 `BranchPointerProtection`.

The checkout volume also holds `.sextant-sdk-pin/`, the SDK-pin restore journals (issue #113). It is a
sibling of the checkouts, never inside a working tree, and is empty except while a job has a `global.json`
pin neutralized or after a crash in that window. Keep it on the same persistent volume as the checkouts:
it is what lets the next job put a neutralized `global.json` back.

## Untrusted evaluation sandbox (Phase 17, criterion 2)

MSBuild project evaluation is an **untrusted execution boundary — even for a private repo** (imported
targets, SDK resolvers, inline `UsingTask`/`Exec` tasks run arbitrary code). Every service-worker evaluation
of a checkout therefore runs through `EvaluationSandbox`, which enforces a wall-clock **time budget**, a
watchdog **memory ceiling**, **secret scrubbing** + an **offline/no-telemetry** environment, and
**fail-closed scratch confinement** (it refuses to run if the per-job scratch is not under the scratch root,
so evaluation can never write into a persistent volume).

> ⚠️ **Defense in depth, NOT a hard security boundary.** The untrusted work runs **in-process**, so a
> hostile project can still read/write arbitrary filesystem paths the worker user can reach, spawn child
> processes, open network sockets, and ignore the cooperative cancellation (a tight native loop never
> observes the token). The memory ceiling is a cooperative abort, not an OS hard cap; the offline posture is
> best-effort environment, not a kernel network block.

**Operational rule:** do **not** host untrusted third-party repositories in multi-tenant production on this
in-process tier. It is adequate for local/single-node use and for **explicitly-onboarded, trusted pilot
repositories**. True OS-hard isolation (job object / cgroup + rlimits + network namespace / `sandbox-exec`,
over an **out-of-process** evaluator) is tracked as **issue #76** and is a **documented precondition** for
untrusted multi-tenant production — it will be wired into the security runbook and the pilot exit criteria
(criterion 7), and cross-referenced from the ProcessStack integration (#19). The single-node local CLI/daemon
path does not run this worker, so leaving the sandbox unwired there keeps local operation byte-identical.

## Retention & GC — the service is the lease owner (#46 / #37 / #54 / #38)

Now that the service owns the durable catalog and a `/control/retention` endpoint, it performs the
snapshot-DATA garbage collection Phase 9 deferred:

- **#46 — server-side GC of unreferenced snapshot data.** `RetentionService` computes a **protected set**
  (pending snapshots, snapshots on a non-deletable/servable generation, and branch-pointed snapshots),
  transitively expands it across `base_snapshot_id` + `snapshot_dependencies` (consumer→provider) to a
  fixpoint, and deletes only the orphaned (non-pending, unprotected) snapshot semantic rows. GC is
  **bounded** and protected-set-aware.
- **#37 — bounded, protected-set-aware source-blob prune.** The orphan source-blob prune now honors the
  same protected set instead of being per-row/unbounded.
- **#54 — providers of *retained* consumers are protected.** The protected set includes providers
  referenced by **any retained** consumer generation (not just branch-pointed heads), coupled with the
  Phase-12 published-status gate, so a historical-scope cross-repo usage query over a superseded consumer
  cannot lose provider rows GC'd out from under it.
- **#38 — single-writer lease.** Retention/publish/GC run under the `writer_lease`, so they can never race
  a live daemon/service. The lease is acquired fail-closed at `Start`, heartbeated for the service's
  lifetime, and a stale lease may be stolen so a crash never wedges the DB.

## Remote snapshot federation (#51)

Phase 11 built the **local** federation planner, immutable-snapshot pinning, and a base-snapshot-source
seam. This service provides the **remote** half:

- `GET /query/snapshots/{identityHash}/symbols` serves one immutable page of a snapshot's symbols by its
  portable identity hash, with a stable cursor (result **paging**), from a short-lived per-request read
  connection.
- `RemoteHttpBaseSnapshotSource` is the client seam Phase-11's planner slots into. It is **cache-first**
  (**caching by snapshot id** via `SnapshotPageCache`, a bounded LRU keyed by `{identityHash}:{cursor}:
  {limit}`), enforces a per-request **timeout** (linked CTS), and on timeout / connection failure / 401 /
  403 raises `RemoteSnapshotUnavailableException`. A page already in cache is served **without contacting
  the peer** — the transparent **offline fallback** to a cached base.

## Provider-snapshot growth is immutable (#53)

A provider snapshot that *gains* a project after publish must never mutate an already-complete generation
(the same immutability contract as Phase 9/10/12). A late-referenced provider project produces a **new
pending** provider generation and republishes atomically; `MarkComplete` is a guarded no-op on a snapshot
that is already complete. `ProviderGrowthImmutabilityTests` is the regression.

## Observability (criterion 5)

The service exposes a **clean, dependency-free** observability surface on the **control plane only** — every
signal is operator data, so none of it is reachable with a query token (criterion-1 leakage guard: audit
rows and per-repository cost would otherwise reveal repository/snapshot existence and cross-tenant counts).
For the same reason a user caller (`act=user` assertion) on the control plane is refused with
`403 caller_not_allowed` on metrics, audit and pilot (issue #193), so it can never read other callers' audit
rows.

- **`GET /control/metrics`** returns a point-in-time [`MetricsSnapshot`](../src/Sextant.Service/Observability/MetricsSnapshot.cs):
  indexing latency (worker run time), queue delay (wait before a worker claimed a job), success &
  completeness rates, worker capacity, storage (catalog + artifact + cache + checkout bytes), cache reuse
  (idempotent-ensure attach rate + federation page-cache hit rate), and query latency (p50/p95/max). Add
  `?format=prometheus` for text exposition a scraper/dashboard can ingest directly. Alerts are evaluated
  over the snapshot (`queue_delay_high`, `low_success_rate`, `worker_exhaustion`, `storage_pressure`).
- **`GET /control/audit`** serves the durable [`audit_log`](../src/Sextant.Store/Migrations/020_audit_log.sql)
  (migration 020): who did what to which repository scope, with what outcome and at what worker cost.
  The actor is stored as a **non-reversible hash**, never the raw token; the raw secret never touches the DB.
- **`GET /control/pilot`** evaluates the pilot-readiness gate (see runbooks).
- **Traces:** ensure / retention / backup emit `System.Diagnostics.Activity` spans on the
  `Sextant.Service` `ActivitySource`, so an operator can wire OpenTelemetry without any code change.

Observability is **optional** — the local stdio MCP path and standalone local indexing never construct any
of this and stay byte-identical (criterion 6 / zero service dependency).

## Backup, restore & disaster recovery (criterion 6)

A backup captures the two durable, non-reconstructable stores and **nothing else**:

- the **catalog** database, copied with SQLite's **online backup API** so it is a transactionally
  consistent point-in-time image even while the writer is active (no torn WAL); and
- the immutable **artifact** volume (published snapshot outputs).

Worker scratch (ephemeral) and the checkout/cache volumes (reconstructable from Git / federation) are not
backed up, and **secrets are never written** into a backup — the `manifest.json` records a *credentials
boundary* listing the environment variables an operator re-provides on restore.

```bash
sextant service backup   /backups/2026-09-19   # consistent catalog + artifact copy
sextant service restore  /backups/2026-09-19   # lay catalog + artifacts back down (offline)
sextant service                                # start: migrate → recover → reconcile → RE-ENFORCE authz
```

Restore refuses a backup taken at a **newer** schema than the restoring build (same forward-only guard as
`IndexDatabase.CheckReadiness`). Because a restored service goes through the normal `Start` flow — run
migrations, recover the WAL, reconcile orphaned jobs, and load the read-authorization policy from
configuration — a restored service is a **queryable AUTHORIZED** service, never a policy-stripped one, and
the immutable snapshot it serves is byte-for-byte the one that was backed up. `POST /control/backup` writes
a backup from a running service under its writer gate; restore is offline (it must precede startup). See
[`runbooks.md`](runbooks.md) for the schema-upgrade rehearsal and DR drill.

## Production deployment checklist (gateways)

Use this list for any service that more than one person reaches, whether through the ProcessStack app
(which has moved to a separate private repository) or through another gateway that pools many callers
behind one delegate token.

> **Secret handling.** Generate every secret straight into an owner-only file and never print it. Treat
> terminal scrollback, CI logs and agent transcripts as logs: a value that reaches one has leaked, so the
> commands below print only a length or `set`/`MISSING`, and never put a value on a command line or in shell
> history. Once a secret file is consumed (its value is in the service's env file and the gateway's secret
> store), remove it with `shred -u <file>`; on a copy-on-write or journaling filesystem `shred` cannot
> overwrite the old blocks, so `rm` is the realistic option there. Rotating a secret means overwriting its
> file with a new value, replacing its env entry from that file, and restarting the service (it reads its
> secrets only at startup). The commands are in
> [Generating and installing secrets](#generating-and-installing-secrets).

| Setting | Production value | Why |
| --- | --- | --- |
| `CONTROL_TOKEN` | A long random secret, [generated into an owner-only file](#generating-and-installing-secrets) and held only by the gateway's control connection and operators | Startup fails without it. Never set `INSECURE_OPEN_CONTROL_PLANE` |
| `DELEGATE_TOKENS` | One random token per pooled gateway connection, each [generated into its own owner-only file](#generating-and-installing-secrets) | A delegate token opens nothing on its own: every read is decided by the verified caller's grants |
| `CALLER_KEYS` | `kid=base64url-key@tenantId` entries with keys of at least 32 random bytes, each [generated into an owner-only file](#generating-and-installing-secrets) and never printed. A kid belongs to exactly one tenant: never share a kid or a key across tenants | The verifier binds each kid to its tenant, so an assertion signed with one tenant's key can never name another tenant. The gateway must sign with the same key bytes under a kid the service knows (the ProcessStack app's two connections share one key, so both set the same explicit `keyId`) |
| `CALLER_AUDIENCE` | The audience the gateway signs (the ProcessStack app uses `sextant`) | Required with `CALLER_KEYS` |
| `CALLER_APPS` | The gateway app's **id**: the value ProcessStack signs into the `app` claim, which is the application's id (a ULID such as `01M389V5MBGQKSF18HC2EGC3FY`), not its slug or name (`sextant`). Find it with `processstack app status sextant --json` (the `ApplicationId` field), or as the `id` of the entry named `sextant` in `GET /v1/<tenant>/applications?search=sextant`. The id exists only after the app's first publish | Only that app's assertions are accepted, so another app that is bound to the same connection is refused (`caller_not_allowed`). A slug here refuses **every** assertion-bearing call (the grant routes, delegate `tools/call`, `/query/*`) with `caller_not_allowed` (`403` over HTTP, the tool error on `/mcp`), while `/health`, `tools/list` and the gateway's connection test still succeed |
| `CALLER_IDPS` | `processstack` (the default; set it explicitly) | Only these identity providers' users act as `act=user` callers |
| `REPOSITORY_HOSTS` | An explicit list, never `*` | The ensure/grant SSRF policy ([above](#repository-url-policy-svc-5)) |
| `REPOSITORY_OWNERS` | The `host/owner` entries of the organisations you index, comma-separated (for example `github.com/<org>,github.com/<user>`) | **Required whenever a tenant has members besides its owner** (below) |
| `CHECKOUT_MODE` | `clone` (recommended) | A user's first ensure becomes a repository's default branch only when `git ls-remote` confirms the remote's `HEAD` names it (#199). In `locate` mode an application ensure (a push or the reconcile) has to set the default |
| `BIND_ADDRESS` / TLS | A routable address, with TLS terminated in front of the service | The service serves plain HTTP. Both connections carry a bearer token and a signed assertion on every request, so plain HTTP is acceptable only on a private network |

Before deploying, check that every repository already in the catalog passes the new host and owner lists:
a repository they refuse can no longer be ensured or granted (an old grant can still be revoked).

### Generating and installing secrets

Every random secret you generate for the service (`CONTROL_TOKEN`, `QUERY_TOKEN`, `CONTRIBUTE_TOKEN`, each
`DELEGATE_TOKENS` entry and each `CALLER_KEYS` key) is 48 random bytes, base64url-encoded without padding,
written straight into its own owner-only file:

```bash
install -d -m 700 <secrets-dir>
(umask 077; openssl rand 48 | base64 -w0 | tr '+/' '-_' | tr -d '=' > <secrets-dir>/control-token)
(umask 077; openssl rand 48 | base64 -w0 | tr '+/' '-_' | tr -d '=' > <secrets-dir>/delegate-token)
(umask 077; openssl rand 48 | base64 -w0 | tr '+/' '-_' | tr -d '=' > <secrets-dir>/caller-key)
wc -c < <secrets-dir>/caller-key   # prints only the length (64), never the key
```

The `umask 077` subshell creates each file with mode 600, and nothing reaches the terminal. Use one file per
secret (for example `query-token`, `contribute-token`, and one `delegate-token-<n>` per pooled connection).
A secret issued elsewhere, such as a `CHECKOUT_TOKEN` from your git host, goes into its file without being
echoed: `(umask 077; IFS= read -rs v && printf '%s' "$v" > <secrets-dir>/checkout-token)`.

Add the env entries from the files to the service's owner-only env file, which your service manager loads
(for example systemd `EnvironmentFile=` or `docker run --env-file`):

```bash
(umask 077; touch <service-env-file>) && chmod 600 <service-env-file>
printf 'SEXTANT_SERVICE_CONTROL_TOKEN=%s\n' "$(cat <secrets-dir>/control-token)" >> <service-env-file>
printf 'SEXTANT_SERVICE_DELEGATE_TOKENS=%s\n' "$(cat <secrets-dir>/delegate-token)" >> <service-env-file>
printf 'SEXTANT_SERVICE_CALLER_KEYS=<kid>=%s@<tenant-id>\n' "$(cat <secrets-dir>/caller-key)" >> <service-env-file>
```

`printf` is a bash builtin, so the value is in no process's argv (the only external command, `cat`, receives
just the path) and goes straight into the file, never to stdout. For several delegate tokens or caller keys,
join them with `;` in one format string, with one `"$(cat …)"` per file. Never type a value after `KEY=` or
`export KEY=` at a prompt (shell history keeps it), and never pass one as a command-line argument (other
local users can read argv in the process list). Check that an entry is set without printing it:

```bash
grep -Eq '^SEXTANT_SERVICE_CALLER_KEYS=[^[:space:]]' <service-env-file> && echo set || echo MISSING
```

To replace a value (a rotation, or the query token [below](#after-the-legacy-_sextant-gateway-is-retired-not-before)),
regenerate its file with the same `umask 077` command, delete the old line by its key
(`sed -i '/^SEXTANT_SERVICE_QUERY_TOKEN=/d' <service-env-file>`, which names only the key), append the new
line with `printf`, and restart the service.

The gateway needs the same control token, delegate token and caller key (under the same kid). Move them into
its secret store file to file, or on stdin, never through a chat, a ticket or a terminal.

For an HTTP call that needs a bearer (for example `/control/metrics`, `/control/resolve` or an ensure), keep
the header in an owner-only file and let `curl` read it (`-H @file` needs curl 7.55 or later):

```bash
(umask 077; printf 'Authorization: Bearer %s\n' "$(cat <secrets-dir>/control-token)" > <secrets-dir>/control-token-auth-header)
curl -sS -H @<secrets-dir>/control-token-auth-header <service-url>/control/metrics
```

Never use `curl -H "Authorization: Bearer $TOKEN"`: the shell expands the token into curl's argv. To compute
an HMAC with a caller key (for example to mint a test caller assertion), read the key from its file inside a
small script, such as Python's `hmac` module given the key file's path. Never use `openssl dgst -hmac <key>` or
`-macopt hexkey:<key>`, which put the key in the process list.

### Why `REPOSITORY_OWNERS` is required for a tenant with other members

With only the host list, any tenant member can make the service index any public repository on that host.
The ProcessStack app's watch and import check only that the workspace's GitHub connection can see the
repository, which every public repository passes, and a grant on a repository also lets its holder index any
commit reachable in it, fork-network commits included, with a `branch_update: none` ensure (the app's
`start-indexing`, #209). Indexing runs the repository's MSBuild evaluation on the worker, inside an
in-process sandbox that is defense in depth only (#76, see [Untrusted evaluation
sandbox](#untrusted-evaluation-sandbox-phase-17-criterion-2)). The owner list keeps grants and ensures to
the organisations you trust; it does not stop a fork commit under an allowed base repository (#209), which
only #76 or a commit-reachability check closes.

### Known residuals

- **Workspace-level visibility (#210).** A ProcessStack workspace's members can watch, and so read, every
  repository its GitHub connection can see. Treat everyone in a workspace as trusted with what that
  connection reads.
- **A missed push is repaired only by the nightly reconcile.** Each push ensure carries its own `before` as
  the head CAS. If one push's ensure is lost, the branch pointer stays behind and every later push's CAS
  misses (the ensure indexes but does not advance), until the app's nightly `reconcile` (or an operator's
  run of it) re-ensures the branch under the CAS of the service's current head.
- **A deleted tenant grant comes back.** `DELETE /control/grants/tenant` removes the grant, but two app paths
  create it again: every repository event from the GitHub App installation first creates or holds the
  repository's tenant grant (by design), and until the app's v2.1 the reconcile still imports the legacy
  `enrolled/*` App State rows as tenant grants (a default-branch push keeps that row current). To stop
  indexing a repository for the tenant, remove it from the GitHub App installation; its enrolled row still
  restores the grant on each reconcile until the v2.1 cleanup drops that import and the rows.
- **`internal: true` app flows can be run directly** (elevenworks/ProcessStack#3287). The app assumes any
  flow may run with inputs of the caller's choosing; the service's grant gate, the SX-6d user-ensure bounds and
  the URL policy stay the authority.

### After the legacy `_sextant` gateway is retired, not before

Once no client uses the legacy query token (the ProcessStack-embedded `_sextant` gateway was its only user):

1. Set `REQUIRE_REPOSITORY_SELECTION=true`. A read without a verified caller must then name its repository.
   Delegate reads are unaffected: implicit selection still picks a caller's only visible repository.
2. Retire the legacy `QUERY_TOKEN` by **replacing** it with a fresh random value that no client holds (or by
   configuring a `READ_POLICY`). **Do not simply unset it:** with no query token and no read policy the query
   plane is open, and a request with no bearer can read every repository (the service logs a startup warning
   when delegate tokens are configured on an open query plane). Generate the new value into an owner-only file
   and swap the env entry without printing it (see
   [Generating and installing secrets](#generating-and-installing-secrets)), then restart the service:

   ```bash
   (umask 077; openssl rand 48 | base64 -w0 | tr '+/' '-_' | tr -d '=' > <secrets-dir>/query-token)
   sed -i '/^SEXTANT_SERVICE_QUERY_TOKEN=/d' <service-env-file>
   printf 'SEXTANT_SERVICE_QUERY_TOKEN=%s\n' "$(cat <secrets-dir>/query-token)" >> <service-env-file>
   grep -Eq '^SEXTANT_SERVICE_QUERY_TOKEN=[^[:space:]]' <service-env-file> && echo set || echo MISSING
   ```

   Only a trusted federation peer that pages snapshots from this service over `/query/*` should be given the
   new value, file to file (the peer sets `SEXTANT_PEER_QUERY_TOKEN` from it), never pasted.

## Migration & schema

Migration `016_service_job_catalog.sql` adds `snapshot_jobs`, `snapshot_job_diagnostics`, and
`writer_lease`; `017_snapshot_capability_fingerprint.sql` adds `snapshots.capability_fingerprint`
(Phase 15); `018_client_contributions.sql` and `019_pull_request_retention_roots.sql` are the Phase-17
slice-1/2 additions; `020_audit_log.sql` adds the durable operational + security **audit log** (Phase 17
slice 3, criterion 5); `021_branch_head_sequence.sql` adds `branches.head_sequence` for the forward-only
branch-head advance on the ensure path (Phase 14, issue #84); `022_snapshot_coverage.sql` adds the durable
per-snapshot `snapshot_coverage` record (issue #119); `023_partial_occurrence_source_index.sql` rebuilds
`ix_occ_source` as a partial index over call edges only (issue #160); `024_repository_grants.sql` adds the
`repository_grants` table behind per-caller visibility (SVC-4); `025_symbol_name_prefix_index.sql` adds the
`NOCASE` name and repository-URL indexes that bound `search_symbols` (issue #196); `026_symbol_declaration.sql`
adds `symbols.declaration`, the member declaration the MCP `signature` shows. All are additive/forward-only. See
[`schema.md`](schema.md) for the table definitions. `LatestSchemaVersion` auto-derives from the highest
migration and is **26**. Snapshot identities fold `SnapshotSchemaVersion` instead, which skips identity-neutral
(index-only) migrations such as `025` and is **26**.

> **One-time full re-index when upgrading from schema 23.** Migration `024` moves `SnapshotSchemaVersion`
> from 23 to 24, which changes every snapshot's identity hash. Each repository is therefore re-indexed once,
> on its next ensure (a push, or a manual ensure of its current head), into a new schema-24 snapshot; until
> that snapshot publishes, the branch keeps pointing at the existing one. The ProcessStack app's nightly
> `reconcile` counts a branch already at GitHub's head as up to date (it ensures one only to promote a
> GitHub-default branch the service does not mark default, #199), so a repository that gets no push keeps
> serving its schema-23 snapshot until its head is ensured by hand (`branch_name` and `commit_sha` from
> `/control/resolve`'s `branch` and `commit_sha`, and that commit as `expected_head_commit`).
> Deploy at low traffic, watch CPU, disk and the ensure queue while the re-index runs, and check that queries
> keep answering meanwhile. Migration `025` is identity-neutral, so upgrading from 24 to 25 re-indexes
> nothing. (A `search_symbols` cursor issued by a build before SX-7b answers `invalid_cursor`, because the
> cursor is now v2; the client re-queries.)

> **One-time full re-index when upgrading to schema 26.** Migration `026` stores a new column, and
> `AnalyzerVersion` is `5`, so `SnapshotSchemaVersion` moves from 24 to 26 and every snapshot identity changes
> once, exactly as for `024` above: each repository is re-indexed on its next ensure, and until its new snapshot
> publishes it keeps serving the old one, whose member `signature` is the earlier display
> (`Ns.Type.Member(string, System.DateTime)`, no return type or parameter names). The column is added without a
> default or backfill, so the migration itself only rewrites the schema text. A build older than `026` serves no
> reads from an upgraded catalog (`CheckReadiness`: newer schema); see
> [runbooks.md](runbooks.md#runbook-schema-upgrades-with-rehearsal) for rolling back.

> **Finding the branches an identity change left behind.** `GET /control/resolve` reports
> `identity_current: false` for a head whose snapshot predates the node's present identity (see
> [Identity currency](#branch-pointer-guards-head-cas-branch_update-none-retire-svc-67)). A reconciler that
> ensures such a head (same commit, `expected_head_commit` = that commit) re-indexes quiet repositories too,
> without waiting for a push. This reporting needs no migration and changes no identity.

## Testing

Service tests are fast and hermetic: an **in-process `TestServer`** (no real network), an ephemeral SQLite
catalog, and a `FakeSnapshotWorker`. The suite maps to the acceptance criteria:

| Criterion | Test coverage |
| --- | --- |
| 1 — idempotent ensure | `SnapshotServiceTests` (concurrent ensures attach to one job; worker runs once); `EnsureCallerDisconnectTests` / `EnsureCallerDisconnectHttpTests` (#148: caller disconnect never cancels production, re-ensure attaches to the in-flight run, `wait=false`, prompt status/resolve during a run, shutdown requeues); `QueuedControlWriteHttpTests` / `JobIdReservationsTests` (#158: `wait=false` and retire answer `202` within the bound while a production holds the writer, reserved job ids resolve and are never reused, apply-time retire guards, submission order, coalescing, shutdown drain) |
| 2 — restart recovery | `SnapshotServiceTests` (catalog survives restart; orphaned `running` jobs reconciled) |
| 3 — scratch cannot delete published | `ServicePathsTests` (scratch/persistent separation + `ReleaseScratch` refusal) |
| 4 — query via HTTP MCP without ProcessStack | `ServiceHttpTests` (`/mcp` mapped + auth-gated; `/query` paging); `RepositorySelectionHttpTests` (the `X-Sextant-Repository` header without a read policy, the legacy no-header unscoped read, `repository_required`); `ToolArgumentSelectionHttpTests` + `ToolSelectionFiltersTests` (SVC-2: the reserved `repository`/`branch` arguments listed on repository-scoped tools and stripped on call, argument-over-header precedence, `selector_conflict`/`invalid_selector`, branch pinning, `branch` without a repository, the cross-repository provider default, the unknown-branch uniform not-found); `McpClientCompatibilityTests` (an SDK client selecting through the `repository` argument); `QueryToolInputResolutionHttpTests` (issues #149/#163: symbol arguments without `global::`, `Type.Member`, parameter lists and documentation IDs; exact type lookup and `kind`; ambiguity and not-found as tool errors with candidates; `get_type_members`; implicit selection naming its repository and `repository_required` listing the caller's repositories); `AgentSizedOutputHttpTests` (#145: paging, truncation summary, cursor binding, lean `meta.snapshot`, repo-relative paths in and out, server `instructions`, the `tools/list` size pin); `SubmoduleCheckoutIntegrationTests.RemoteMcp_RepositoryRelativePaths_*` (#145 on a real worker index: repo-relative inputs over `/mcp`, a submodule file included) |
| 5 — structured per-project diagnostics | `SnapshotServiceTests` (partial/failed/unsupported diagnostics) |
| 6 — local-only remains functional | `ArchitectureBoundaryTests` (core assemblies never reference the service; local query without a service) |

Plus store-level regressions: `RetentionSnapshotGcTests` (#46/#37/#54), `WriterLeaseTests` (#38),
`ProviderGrowthImmutabilityTests` (#53), and `RemoteFederationTests` (#51 paging/caching/offline/timeout/
auth). Coverage integrity (#119): `SnapshotCoverageBuilderTests`, `CheckoutInventoryTests`,
`SnapshotCoverageStoreTests`, `OrchestratorCoverageTests`, `SnapshotServiceCoverageTests`, and the
`CoverageRegressionFixtureTests` clone of a two-solution repo with a refused-host submodule. Null-sequence
reuse and provider coverage (#162): `NullSequenceReusePointerTests`, `EnsureNullSequenceReuseTests`,
`ProviderCoverageBuilderTests`, `OrchestratorProviderCoverageTests`, and the
`ServiceHttpTests` direct-ensure-reusing-a-provider test. Identity currency on `/control/resolve`:
`ResolveIdentityCurrencyHttpTests` (a policy flip and a simulated analyzer/schema upgrade each read as stale, a
same-commit CAS ensure re-points the branch through the real orchestrator and `/mcp` serves the new snapshot, a
URL spelling variant is never stale, and both fields are omitted for a head with no commit or a contributed head). Submodule
provisioning (#125): `CloningCheckoutProviderSubmoduleTests` (local `file://` fixtures, no network:
absolute/relative/nested pins, unfetchable → partial with reasons, sentinel-token non-persistence across every
git dir, pre-change cache upgrade, transient failure retried then degraded on the final attempt),
`SubmoduleUrlPolicyTests`, `SubmoduleProvisioningHelperTests`, and the
`SubmoduleCheckoutIntegrationTests` end-to-end Phase-12 provider-snapshot test. SDK pins
(#113): `HostFxrSdkResolutionErrorTests` (classifying the hostfxr SDK-not-found error), `SdkPinGuardTests`
(detect/neutralize/restore/journal recovery/refusals over a fake hostfxr probe),
`GitCheckoutContentVerifierTests` (the committed-content check over real git: edits, including ones
`git status` cannot see from stat data, through an `ident` clean filter, or through a `filter` driver;
staged changes; untracked/ignored files; index flags; CRLF and stored-form checkouts; a replace ref; a moved
or unborn `HEAD`; a timeout; no discovery above the checkout; and no index writes), `SdkPinSurfaceTests`
(diagnostics, coverage provenance, audit suffix, status, and the override policy's identity component),
`SnapshotIdentityTests` (the `sdkpin` fold leaves a null-policy identity byte-identical), and the real-MSBuild
`SdkPinOverrideIntegrationTests`. The integration tests cover a `10.0.999` + `disable` pin that is
overridden (with and without the sandboxed worker; the checkout bytes, mtime, `git status` and
`EvaluationFingerprint` are unchanged afterwards), the override disabled (typed failure), a resolvable pin
(untouched), an uncommitted local pin (refused, typed failure, left as found), crash recovery, and
multi-solution repos where one pin yields `partial` (override off) or `complete` (override on). One of those
repos pins a solution's directory; the other uses the #124 default union, with the pin in a nested project's
directory. Through a real `SnapshotService` over one shared catalog, a commit ensured with the override off
(`failed`, or `partial` for the multi-solution repo) is rebuilt with complete coverage under a new identity
once the override is on, instead of being reused. A re-ensure under the unchanged policy then attaches without
a rebuild. Per-pin and submodule verification (#171): `SdkPinGuardTests` (one unverifiable pin never
suppresses another's override; submodule pins verified through every gitlink, with git metadata confined to
the checkout's own, journal `version 2`, and submodule-entry recovery: restored, moved, unpopulated,
unconfirmed, repointed, malformed), `GitCheckoutContentVerifierTests`
(`GitlinkProblem` and the guard over a real absorbed submodule: both pins overridden, `.git`/`.git/modules`
byte-identical, both trees clean), and two `SdkPinOverrideIntegrationTests` over a real absorbed submodule under
the default union: a root pin plus a submodule pin both overridden (`complete`), and a submodule checked out away
from its gitlink refused with its own reason while the root pin is still overridden (`partial`).

### Phase 15 — platform routing tests

| Criterion | Test coverage |
| --- | --- |
| 1 — portable graphs stay on Linux | `CapabilityRouterTests`, `CapabilityRoutingWorkerTests` (default placement, no route) |
| 2 — Windows project routes to a Windows worker | `CapabilityRoutingWorkerTests` (fake Windows placement publishes) |
| 3 — Apple project routes to a macOS worker | `CapabilityRoutingWorkerTests` (fake macOS placement publishes) |
| 4 — no compatible worker → fail closed | `CapabilityRouterTests`, `CapabilityRoutingWorkerTests` (`unsupported` + `no_compatible_worker`, no complete snapshot) |
| 5 — capability fingerprint gates reuse | `SnapshotIdentityTests`, `SnapshotCapabilityStoreTests`, `FederatedReadContextTests` (identity fold + read-time compat) |
| 6 — matrix validated / env-gated; core stays agnostic | `PlatformFixtureMatrixTests` (`SEXTANT_RUN_PLATFORM_MATRIX`), `ArchitectureBoundaryTests` (`WorkerCapability`/`CapabilityRouter` on the core side) |

The demonstrated-not-TFM rule is unit-tested by `LinuxEvaluationAnalyzerTests`; criteria 2/3 native legs run
through a substitutable fake placement on Linux (real native execution is Phase 14).
