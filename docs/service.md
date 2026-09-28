# The standalone Sextant index service (Phase 13)

The **Sextant index service** is a persistent, independently deployable **data plane** that hosts
committed-branch snapshots so a client only has to index its local diff, deduplicates shared submodules
once for everyone, and answers low-latency semantic queries over authenticated HTTP MCP. It is the server
the distributed-indexing initiative was built toward.

It lives in two new projects that depend on the core libraries — **never the reverse**:

- **`Sextant.Service`** — the data-plane library: the `SnapshotService` control core, service contracts,
  on-disk volume management, and the base-snapshot federation sources. It depends on `Sextant.Store`,
  `Sextant.Indexer`, and `Sextant.Core`.
- **`Sextant.Service.Host`** — the ASP.NET Core composition root: the HTTP surface, auth middleware, and
  MCP transport. It depends on `Sextant.Service` and `Sextant.Mcp`.

> **Core stays ProcessStack-agnostic.** `Sextant.Core`, `.Store`, `.Indexer`, `.Daemon`, and `.Mcp` never
> reference the service or ProcessStack. Phase 14 will integrate ProcessStack as an orchestrator *over*
> this service's APIs; the dependency must not invert. `ArchitectureBoundaryTests` asserts this.

> **The service is additive, never required.** The local stdio MCP path and standalone local indexing
> remain fully functional with **zero** service dependency (acceptance criterion 6).

## Running it

```bash
# Single-node development: zero config. DB + volumes default under the repo's .sextant/service.
sextant service

# Scaled deployment: point volumes at durable storage and require tokens.
SEXTANT_SERVICE_DB_PATH=/data/sextant/catalog.db \
SEXTANT_SERVICE_DATA_ROOT=/data/sextant/volumes \
SEXTANT_SERVICE_CONTROL_TOKEN=... \
SEXTANT_SERVICE_QUERY_TOKEN=... \
sextant service
```

The `sextant service` CLI command is additive; every other CLI command (`index`, `query`, `serve` for the
local stdio MCP, `retention`, …) is unchanged and needs no service.

## Configuration (`ServiceOptions`)

All settings bind from `SEXTANT_SERVICE_*` environment variables, falling back to the repo `sextant.json`
for the database path and to `<db-dir>/service` for the volume root, so a bare `sextant service` works out
of the box.

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
| `SEXTANT_SERVICE_REPOSITORY_OWNERS` | Optional comma-separated `host/owner` (or `host/*`) allow-list for ensure repository URLs; each host must also be allowed by `REPOSITORY_HOSTS`. A malformed entry **fails startup** | none (any owner) |
| `SEXTANT_SERVICE_MAX_PROVISIONING_ATTEMPTS` | In `clone` mode, how many times a **transient** clone/provisioning failure is retried across re-ensures before the job settles to terminal `failed` (clamped to 1–100; deterministic failures are never retried). Also bounds the requeue of a checkout whose neutralized `global.json` SDK pin could not be restored (`sdk_pin_restore_failed`, #113), in any checkout mode | `5` |
| `SEXTANT_SERVICE_CONTROL_TOKEN` | Bearer token for `/control/*` | none (open, dev only) |
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
| `SEXTANT_SERVICE_CALLER_APPS` | Optional comma-separated allow-list on the signed `app` claim | none (any app) |
| `SEXTANT_SERVICE_MAX_GRANTS_PER_PRINCIPAL` | Most repository grants one user caller may hold in a tenant (see [Repository grants](#repository-grants-and-visibility-svc-4)); creating one more is `409 grant_limit`. The tenant-wide `'*'` grants are not counted. A missing or non-positive value uses the default | `200` |
| `SEXTANT_SERVICE_MAX_GRANTS_PER_TENANT` | Most repository grant rows a tenant may hold, tenant-wide grants included; creating one more is `409 grant_limit` | `5000` |
| `SEXTANT_SERVICE_BIND_ADDRESS` | Network interface the HTTP surface binds to | `localhost` |
| `SEXTANT_SERVICE_CONTROL_PORT` | HTTP port | `3011` |
| `SEXTANT_SERVICE_QUERY_PORT` | Optional dedicated query port (shares the control port when unset) | none (shared) |
| `SEXTANT_SERVICE_LEASE_TTL_SECONDS` | Single-writer lease TTL | `30` |
| `SEXTANT_SERVICE_PEERS` | Comma-separated peer base URLs for federation | none |
| `SEXTANT_SERVICE_REMOTE_TIMEOUT_SECONDS` | Per-request remote-fetch timeout | `10` |
| `SEXTANT_SERVICE_CONTRIB_REQUIRE_AUTH` | Require contribution authorization (Phase 16) | `false` (dev-open) |
| `SEXTANT_SERVICE_CONTRIB_REQUIRE_GIT_VERIFY` | Require Git-content verification of uploads (Phase 16) | `false` |
| `SEXTANT_SERVICE_CONTRIB_MAX_ARTIFACT_BYTES` | Max accepted contribution artifact size (Phase 16) | policy default |
| `SEXTANT_SERVICE_SANDBOX_ENABLED` | Enforce the evaluation sandbox (Phase 17) | `true` |
| `SEXTANT_SERVICE_SANDBOX_TIME_BUDGET_SECONDS` | Wall-clock evaluation time budget | policy default |
| `SEXTANT_SERVICE_SANDBOX_MEMORY_BUDGET_BYTES` | Watchdog memory ceiling | policy default |
| `SEXTANT_SERVICE_SANDBOX_ALLOW_NETWORK` | Allow network during evaluation | `false` |
| `SEXTANT_SERVICE_SANDBOX_SCRUB_SECRETS` | Scrub secrets from the evaluation environment | `true` |
| `SEXTANT_SERVICE_SDK_PIN_OVERRIDE` | Temporarily neutralize a checkout `global.json` SDK pin that no installed SDK satisfies, so the checkout still indexes with an installed SDK (issue #113; see [SDK pins](#repository-globaljson-sdk-pins-issue-113)). `false` leaves such pins alone and the job fails / goes partial with a typed `sdk_resolution_failed` diagnostic. `false` is part of the snapshot identity, so flipping the toggle re-indexes a commit instead of reusing a result built under the other policy. An unparseable value **fails startup** | `true` |

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
| a project file on disk is in no selected solution and was not pulled in by a `ProjectReference` | `project_file_unreferenced` |
| part of the tree could not be inspected (unreadable dir, `.gitmodules` entry escaping the checkout) | `coverage_scan_incomplete` |

Under an explicit `solutions` list, project files outside that scope are reported as `info` and do **not**
make the snapshot partial (the operator chose the scope). Per-item diagnostics are capped at 200 per code
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
- MCP `meta.snapshot.completeness` is `partial` and `meta.snapshot.coverage` is set; `get_index_status`
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
- The snapshot's durable `coverage` block (in ensure/status/resolve, query pages, and MCP
  `meta.snapshot.coverage`) carries
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
| `POST /control/ensure` | control | control token | Idempotent ensure-snapshot (criterion 1). Accepts an optional monotonic `branch_head_sequence` for forward-only branch-head advance (Phase 14, issue #84), **or** an `expected_head_commit` head CAS, plus `forced` and `branch_update` (see [Branch-pointer guards](#branch-pointer-guards-head-cas-branch_update-none-retire-svc-67)); the result carries `branch_advanced`. Blocks until terminal (`200`; `202` when transient-requeued) unless `?wait=false`, which returns `202` at once with the job to poll (issue #148). A caller disconnect/timeout **never** cancels production. A repository URL the [repository URL policy](#repository-url-policy-svc-5) refuses, both branch guards together (`conflicting_branch_guards`) or an unknown `branch_update` (`invalid_branch_update`) is `400 {"status":"rejected","reason":"<code>"}` before any job exists (audited `ensure`/`denied`). A user caller (`act=user` assertion) may ensure only a repository it can read: otherwise `403 {"status":"rejected","reason":"not_granted"}` (SVC-4). |
| `POST /control/contribute` | control | control **or** contribute token | Ingest a client/CI semantic contribution (Phase 16); the least-privilege contribute token authorizes this endpoint only. |
| `GET /control/status/{jobId}` | control | control token | Job status + per-project diagnostics (criterion 5) + checkout `coverage` (#119). For a user caller, a job on a repository it cannot read is the same `404` as an unknown id (SVC-4). |
| `GET /control/resolve` | control | control token | Resolve a repository branch (`?branch=`, else the default) to its current published snapshot (+ its `coverage`, #119), plus `commit_sha`, the resolved `branch` name, `is_default` and `head_sequence` (SVC-7; a null `commit_sha`/`head_sequence` is omitted). |
| `POST /control/branches/retire` | control | control token | Delete a branch pointer (`{repository, branch, expected_head_commit?}`, SVC-6); its snapshots stay for retention. `200 {"retired":true}`, or `{"retired":false}` for a missing branch (idempotent). The default branch or a head-CAS mismatch is `409 {"status":"rejected","reason":"default_branch"\|"head_mismatch"}`; a refused URL or blank branch is `400`. Audited `retire`. |
| `POST /control/retention` | control | control token | Run the service-owned retention/GC pass (`?execute=true` to apply). |
| `PUT`/`DELETE`/`GET /control/grants/self` | control | control token + `act=user` assertion | The caller's own repository grants (see [Repository grants](#repository-grants-and-visibility-svc-4)). |
| `PUT`/`DELETE /control/grants/tenant` | control | control token + `act=application` assertion | The tenant-wide repository grants. |
| `GET /control/grants?scope=tenant` | control | control token + `act=application` assertion | The tenant's distinct reconcile targets, with counts and no user ids. |
| `GET /control/metrics` | control | control token | Observability snapshot (criterion 5); `?format=prometheus` for text exposition, else JSON. |
| `GET /control/audit` | control | control token | Durable audit log (criterion 5); optional `action`/`repository`/`limit` filters. **Operator-only.** |
| `GET /control/pilot` | control | control token | Pilot-readiness gate (criterion 7); `?workload=trusted\|untrusted&hard_isolation=&recent_backup=`. |
| `POST /control/backup` | control | control token | Write a consistent catalog + artifact backup to `?dir=` (criterion 6). |
| `GET /query/snapshots/{identityHash}/symbols` | query | query token (or delegate token + caller assertion) | One immutable page of a snapshot's symbols, cursor-paged (federation, issue #51). |
| `POST /mcp` | query | query token (or delegate token; `tools/call` needs a caller assertion) | Authenticated HTTP MCP semantic queries (criterion 4). |

A null token disables that plane's auth (single-node development). Token checks are constant-time. Query
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
| Names a repository (or branch) with no complete snapshot | Nothing: an empty result with an actionable message under an open read, the uniform not-found under an enforced read policy. Never widened to other repositories or branches | Same |
| Names a `branch` but no repository | `meta.error.code = repository_required`, with no results | Same |
| Names nothing (or only blank values) | The unselected default: the only repository of a single-repository catalog, or **every** repository of a multi-repository catalog | `meta.error.code = repository_required`, with no results |

The `repository_required` error names no repository and depends only on the request, so it reveals nothing
about what the catalog holds. The default keeps a caller that names no repository (for example a legacy
gateway using the plain query token) working unchanged; turn the requirement on once every caller selects
one. The local stdio MCP server has no header, advertises no reserved arguments, and never requires a
selection.

#### Reserved tool arguments (SVC-2)

A client that forwards tool arguments verbatim over one pooled connection with only static headers selects
per call through two reserved arguments. The service adds them in MCP request filters on its stateless
`/mcp` (`ToolSelectionFilters`); the tools themselves are unchanged.

- **`tools/list`** advertises two optional string arguments, `repository` and `branch`, on every
  repository-scoped remote tool (every tool on the remote allowlist except `list_repositories`, which reads no
  index). A tool that already declares an
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
| `app`, `cid`, `via` | Calling app, connection and surface (`via` is `mcp-surface` or `activity`) |
| `dep` | Optional deployment id, sent only when the calling run is bound to a deployment (recorded). When present it must be a non-empty string |
| `run` | Optional run id |
| `jti`, `iat`, `nbf`, `exp` | Assertion id and times: 60 s skew, at most 300 s from `iat` to `exp` |

A verified `act=user` assertion also needs its `idp` in `CALLER_IDPS`, and, when `CALLER_APPS` is set,
every assertion needs its `app` in it.

**Where an assertion is accepted.**

| Request | Bearer | Assertion |
| --- | --- | --- |
| `/mcp` discovery (`initialize`, `ping`, `tools/list`, notifications) | delegate | Optional, so a pooled client connects and lists tools once; verified when present |
| `/mcp` `tools/call` | delegate | Required: without one the call is the tool error `caller_required`; a caller refused by `CALLER_IDPS`/`CALLER_APPS` is the tool error `caller_not_allowed` |
| `/query/*` | delegate | Required: `401 {"error":"caller_required"}` |
| `/mcp`, `/query/*` | query token, read-policy principal, or an open plane | Refused: `401 {"error":"assertion_not_allowed"}` |
| `/control/*` except `/control/contribute` | control | Optional; when present it must verify, and the audit actor becomes the caller |
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
at most 64 characters of `[A-Za-z0-9._:-]`, else as `-`; an application caller has `idp=-`.

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
  **one** visible repository with a complete default-branch snapshot. With none or several, it fails with
  `repository_required`.
- **`/control/ensure`** by a user caller needs the repository to be visible, otherwise `403 not_granted`
  (audited `ensure`/`denied`). An application caller (a trigger) and an assertion-less call are unchanged.
- **`/control/status/{jobId}`** by a user caller is `404` for a job on a repository it cannot see.

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
  writer. Registering a **new** identity's job needs that writer too, so even a `wait=false` ensure for a
  new identity waits while another identity is producing. The ensure still survives its caller
  disconnecting: it registers and produces once the writer frees up. Making this registration
  gate-free is tracked in #158.
- **Lease loss mid-run fails closed.** If the writer lease is stolen during a worker run, the service
  records **nothing**. The job stays `running` for the new owner's startup reconcile to requeue (#38).

**Recommended clients:** use `?wait=false` and poll `/control/status/{job_id}` with backoff, with a
polling budget sized for your largest repository (hours for a monorepo, not minutes). A blocking ensure is
fine for small repositories or operator use. Either way, a client timeout is now harmless: re-issue the
ensure to re-attach, or poll the job. Status and resolve reads never wait for a running index, so a short
per-poll timeout (a few seconds) is appropriate.

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
nothing else protects them. The endpoint applies the [repository URL policy](#repository-url-policy-svc-5)
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
the refusal reason, scoped to the request's repository. Retire waits for the single writer as retention does,
so it queues behind a running production, and the caller's timeout bounds only that wait. Because a failed
CAS never creates a branch, a stale push that arrives after its branch was retired cannot re-create it. A
**sequence**-guarded ensure has no such protection: retire deletes the row together with its
`head_sequence`, so a late sequence-bearing ensure re-creates the branch. Do not mix the two guards on one
branch; the CAS path never writes `head_sequence`, so that value also goes stale.

**Resolve.** `GET /control/resolve` additionally returns the head's `commit_sha`, the `branch` it resolved
through (the default branch's own name when `?branch=` is omitted), `is_default` and `head_sequence` (a null
value is omitted). A reconciler can therefore read the head commit to send as the next push's
`expected_head_commit`.

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

## Migration & schema

Migration `016_service_job_catalog.sql` adds `snapshot_jobs`, `snapshot_job_diagnostics`, and
`writer_lease`; `017_snapshot_capability_fingerprint.sql` adds `snapshots.capability_fingerprint`
(Phase 15); `018_client_contributions.sql` and `019_pull_request_retention_roots.sql` are the Phase-17
slice-1/2 additions; `020_audit_log.sql` adds the durable operational + security **audit log** (Phase 17
slice 3, criterion 5); `021_branch_head_sequence.sql` adds `branches.head_sequence` for the forward-only
branch-head advance on the ensure path (Phase 14, issue #84); `022_snapshot_coverage.sql` adds the durable
per-snapshot `snapshot_coverage` record (issue #119); `023_partial_occurrence_source_index.sql` rebuilds
`ix_occ_source` as a partial index over call edges only (issue #160); `024_repository_grants.sql` adds the
`repository_grants` table behind per-caller visibility (SVC-4). All are additive/forward-only. See
[`schema.md`](schema.md) for the table definitions. `LatestSchemaVersion` auto-derives from the highest
migration and is **24**.

## Testing

Service tests are fast and hermetic: an **in-process `TestServer`** (no real network), an ephemeral SQLite
catalog, and a `FakeSnapshotWorker`. The suite maps to the acceptance criteria:

| Criterion | Test coverage |
| --- | --- |
| 1 — idempotent ensure | `SnapshotServiceTests` (concurrent ensures attach to one job; worker runs once); `EnsureCallerDisconnectTests` / `EnsureCallerDisconnectHttpTests` (#148: caller disconnect never cancels production, re-ensure attaches to the in-flight run, `wait=false`, prompt status/resolve during a run, shutdown requeues) |
| 2 — restart recovery | `SnapshotServiceTests` (catalog survives restart; orphaned `running` jobs reconciled) |
| 3 — scratch cannot delete published | `ServicePathsTests` (scratch/persistent separation + `ReleaseScratch` refusal) |
| 4 — query via HTTP MCP without ProcessStack | `ServiceHttpTests` (`/mcp` mapped + auth-gated; `/query` paging); `RepositorySelectionHttpTests` (the `X-Sextant-Repository` header without a read policy, the legacy no-header unscoped read, `repository_required`); `ToolArgumentSelectionHttpTests` + `ToolSelectionFiltersTests` (SVC-2: the reserved `repository`/`branch` arguments listed on repository-scoped tools and stripped on call, argument-over-header precedence, `selector_conflict`/`invalid_selector`, branch pinning, `branch` without a repository, the cross-repository provider default, the unknown-branch uniform not-found); `McpClientCompatibilityTests` (an SDK client selecting through the `repository` argument) |
| 5 — structured per-project diagnostics | `SnapshotServiceTests` (partial/failed/unsupported diagnostics) |
| 6 — local-only remains functional | `ArchitectureBoundaryTests` (core assemblies never reference the service; local query without a service) |

Plus store-level regressions: `RetentionSnapshotGcTests` (#46/#37/#54), `WriterLeaseTests` (#38),
`ProviderGrowthImmutabilityTests` (#53), and `RemoteFederationTests` (#51 paging/caching/offline/timeout/
auth). Coverage integrity (#119): `SnapshotCoverageBuilderTests`, `CheckoutInventoryTests`,
`SnapshotCoverageStoreTests`, `OrchestratorCoverageTests`, `SnapshotServiceCoverageTests`, and the
`CoverageRegressionFixtureTests` clone of a two-solution repo with a refused-host submodule. Null-sequence
reuse and provider coverage (#162): `NullSequenceReusePointerTests`, `EnsureNullSequenceReuseTests`,
`ProviderCoverageBuilderTests`, `OrchestratorProviderCoverageTests`, and the
`ServiceHttpTests` direct-ensure-reusing-a-provider test. Submodule
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
