# MCP Tools Reference

Sextant exposes its index through MCP (Model Context Protocol) tools. The MCP server reads from the SQLite database directly and does not depend on the daemon being running.

The local stdio server (and `sextant serve`) registers every tool below. The index service's remote `/mcp` lists
the eight tools agents use: `find_symbol`, `find_references`, `get_call_hierarchy`, `get_implementors`,
`get_type_hierarchy`, `get_type_members`, `get_file_symbols` and `list_repositories` (S12), plus the service-only
`search_symbols`. Nothing the remote surface sends (tool and parameter descriptions, the `initialize` instructions,
warnings, errors, messages) names a tool outside that list; `RemoteToolSurfaceGuardTests` checks that over HTTP and
against the source.

## Response Format

Every tool response includes a `meta` object:

```json
{
  "meta": {
    "queried_at": 1709568000000,
    "index_freshness": 1709567990000,
    "result_count": 5
  },
  "results": [...]
}
```

- `queried_at` — timestamp of the query (Unix epoch ms)
- `index_freshness` — oldest `last_indexed_at` among all results (worst-case staleness)
- `result_count` — number of results returned

### Paged results

The tools that can return hundreds of rows — `find_references`, `find_by_attribute`, `find_by_signature`,
`find_comments`, `find_tests`, `find_unreferenced`, `find_cross_repository_usages`, `get_api_surface` (its rows,
or the `changes` of a comparison), `get_call_hierarchy`, `get_file_symbols`, `get_impact` (its `consumers`),
`get_implementors`, `get_index_status` (its project rows), `get_type_dependents`, `get_type_members` and
`trace_value` — return one page at a time:

| Parameter | Type | Description |
|---|---|---|
| `limit` | int | Rows per page (default 50, at most 200) |
| `cursor` | string | `meta.next_cursor` from the previous page |

```json
{
  "meta": { "result_count": 50, "total": 259, "next_cursor": "eyJ2Ijox…" },
  "summary": { "by_project": { "App.Web": 140, "App.Core": 70 }, "by_file": { "src/App.Web/Orders.cs": 7 } },
  "results": [...]
}
```

- `meta.total` is the size of the whole result; `meta.result_count` the rows on this page.
- `meta.next_cursor` is present only when rows remain. A cursor is bound to the tool, its arguments and the
  snapshot it was issued for; reusing it with other arguments, another tool, or after the index moved to a new
  snapshot is `meta.error.code = "invalid_cursor"` (re-run without a cursor). `limit` and `include_source`,
  which change no row, are not bound: the next page may change either.
- A **truncated first page** leads with a `summary` (counts per project, file, kind, … — the top 10 of each,
  then a `(N more)` entry) before the rows, so an agent can narrow the query (`scope`, `project_id`, …)
  instead of paging. A result that fits its page has no summary.
- `group_by` (`find_references`) groups the rows of the page; `meta.result_count` counts those rows.
- `find_tests`, `find_comments` and `find_by_signature` replaced their former `max_results` with `limit`.
- A page also ends early when its rows would not fit the [response budget](#response-size-budget).

### Response size budget

Every tool result stays under a character budget (default **20,000** characters of the text the client receives;
`max_response_chars` in `sextant.json` / `SEXTANT_MAX_RESPONSE_CHARS` locally, `SEXTANT_SERVICE_MAX_RESPONSE_CHARS`
on the service; at least 1,000). MCP clients cap what they accept from a tool — Claude Code refuses a result over
`MAX_MCP_OUTPUT_TOKENS` (25,000 by default, estimated from the character count); the 57,953-character result it
refused counted as more than 25,000 tokens, so 20,000 characters stays well inside the cap even at a pessimistic
1.5 characters per token.

- A **paged** tool fills its page until the next row would not fit, then stops with `meta.next_cursor` exactly as
  if it had reached `limit` (`limit` is an upper bound). The page says so:

  ```json
  "meta": { "result_count": 37, "total": 259, "next_cursor": "eyJ2Ijox…", "page_truncated_by": "size" },
  "message": "Page cut to 37 rows to stay under 20,000 characters. Pass meta.next_cursor for the next rows, or narrow the query."
  ```

  `meta.total` stays exact, the next page resumes at the first row left out, and the cursor is bound like any
  other (tool, arguments, snapshot). A first page cut this way also leads with its `summary`.
- An **unpaged** tool (`find_symbol` lists, `semantic_search`, `get_type_hierarchy`, `get_namespace_tree`,
  `get_project_dependencies`, `find_submodule_consumers`) keeps the leading rows that fit, sets `meta.total` and
  `meta.page_truncated_by: "size"`, and its `message` says to narrow the query; it has no cursor. A result that
  fits is unchanged.
- `get_base_snapshot_symbols` (local only) pages by the symbol id: a page cut by size returns the id of its last
  row as `meta.next_cursor`, with `meta.page_truncated_by: "size"` (it reports no `meta.total`).
- At least one row is always returned, even if that row alone is over the budget.
- `search_symbols` (service only) applies the budget after its hit cap (`SEARCH_MAX_HITS`). A page over it first
  keeps fewer symbols from each repository read (each resumes at its first symbol left out), and if one symbol from
  each is still too much it returns only as many of the first repositories of its turn as fit: the others with
  matches are listed in `truncated` and are the first ones read on the next page (a repository with no match is
  never held back). The page then has `meta.page_truncated_by: "size"` and a top-level
  `message` ("…Pass next_cursor for the rest, or narrow with repository, branch or kind."). Its `pending`,
  `unavailable` and `truncated` lists and its cursor are never cut, so a caller with very many repositories can
  still get a page over the budget; narrow with `repository`.

Result text is written without HTML escaping: `Task<int>`, not `Task\u003Cint\u003E`.

### Paths

Every `file_path` (and every path in `scope`) is repository-relative on the service's remote `/mcp`
(issue #145): a file in a submodule reads under its path in the parent checkout (`external/lib/src/…`), and no
response names a worker checkout directory. Path inputs — `get_file_symbols`'s `file_path` and the `file:`/
`solution:` scopes — take the repository-relative form there; an absolute path is refused with
`meta.error.code = "invalid_argument"` (the message does not echo the path). The local stdio/CLI surface keeps
reporting paths as indexed and accepts both forms.

### Remote meta

On the remote `/mcp`, `meta.snapshot` is lean — what an agent needs to trust the answer:

```json
"snapshot": { "repository": "github.com/org/app", "branch": "main", "commit": "0123456789ab", "coverage": "complete" }
```

`coverage` is `complete` or `partial`; a `warning` is added only when the answer may be incomplete (a partial
index, an incompatible indexer, or a dirty working tree). A partial warning counts what is missing and names no
project or tool, in at most 160 characters, for example
`Partial index: 3 of 40 projects did not load or compile, 1 of 2 submodules were not checked out, so results may be
incomplete.` (a gap that does not fit is folded into `and other gaps`; a partial record that counts no gap gets
`Partial index: some projects or submodules were not indexed, so results may be incomplete.`). The full
provenance and the coverage record (its reasons, its `binding` health per project and its informational `notes`)
are not on the remote surface; an operator reads them from `get_index_status` (`index.snapshot`,
`index.coverage`) on a local server over the same catalog, or from `/control/resolve`. The server's `initialize`
result carries short `instructions` (when to use the tools, the `repository` argument, paging) for clients that
surface them.

### Symbol arguments

Every tool that takes a symbol (`symbol_fqn`, `method_fqn`, `for_symbol`, `in_symbol`, `find_symbol`'s `name`, and
`find_cross_repository_usages`' provider symbol) resolves it through one resolver that accepts the spellings a C#
developer types, with or without Roslyn's `global::` alias:

| Form | Example |
| --- | --- |
| A type, with or without its namespace | `MyApp.Services.UserService`, `global::MyApp.Services.UserService`, `UserService` |
| A member qualified by its type (and optionally its namespace) | `UserService.GetById`, `MyApp.Services.UserService.GetById` |
| A member with its parameter list (C# keywords or framework names) | `UserService.GetById(int)`, `global::MyApp.Services.UserService.GetById(System.Int32)` |
| A Roslyn documentation ID (the stored `symbol_key`) | `M:MyApp.Services.UserService.GetById(System.Int32)`, `T:MyApp.Services.UserService` |

Ranking prefers an exact symbol key, then a full path over a path suffix over a bare name, then a name spelled
without type arguments for a non-generic symbol over a generic namesake (`Result` is `Result`, not `Result<T>`;
write `Result<T>` or ``Result`1`` for the generic one), and, when the caller asked for no `kind`, a type over a
member or type parameter of the same name, so a class is never shadowed by its constructors or by a field elsewhere
that shares its name. `find_symbol` honours `kind` on its exact path too. It is a query-time resolution over the
stored keys and signatures: no re-index is needed. An argument longer than 2048 characters, or with generic
arguments or tuples nested more than 32 deep, is an `invalid_argument` error.

The `fully_qualified_name` a tool prints for a symbol is in one of those accepted forms (a type's
`global::Ns.Type`, a method's `global::Ns.Type.Method(int)`, a field's `global::Ns.Type.Field`, else its
documentation ID), so any name a tool returns can be passed back to another tool as is.

### Tool errors

A call that cannot be answered as asked is an MCP tool error (`isError: true`), never an empty success. Its text is
the JSON envelope with `meta.error.code` and a top-level `message` saying what to pass instead:

| `meta.error.code` | When | Extra metadata |
| --- | --- | --- |
| `symbol_not_found` | The symbol argument matches nothing (in scope, of the kinds the tool accepts) | `meta.candidates`: up to five closest symbols, each named in an accepted form |
| `ambiguous_symbol` | A tool that needs one symbol was given a name that several different symbols match equally well (e.g. a bare method name, or an overloaded method without its parameter list). The tool never picks one | `meta.ambiguous`, `meta.ambiguous_match_count`, and `meta.candidates` (the first ten) |
| `invalid_argument` | An argument is malformed or names nothing the tool accepts (an unknown `project_id`, `scope`, `kind`, `accessibility`, namespace or commit; on the remote `/mcp`, an absolute path) | |
| `invalid_cursor` | A `cursor` that was issued for another tool, other arguments or an older snapshot, or is malformed. Re-run without it | |
| `repository_required` (service) | The call must name a repository; for a verified caller the message lists the repositories it can read | |
| `repository_not_found` (service) | The named repository or branch serves nothing to this caller: not indexed, no complete snapshot on that branch, or not readable by it. Under a read policy every such case gets the same bytes, so it never tells an existing repository from an absent one | |

`find_symbol` is a search, so several equally good matches are an answer there: it lists them (best first) and its
`message` says how to narrow. A valid query whose answer is empty (a type with no implementors, a method with no
references) is not an error: the result says so in `message`.

When one symbol is declared in several project versions (the same `symbol_key` in several projects or target
frameworks), the tool answers for one of them and discloses the others in `meta`:

```json
{
  "meta": {
    "queried_at": 1709568000000,
    "index_freshness": 1709567990000,
    "result_count": 3,
    "ambiguous": true,
    "ambiguous_match_count": 2,
    "selected_project_id": "a1b2c3d4e5f6g7h8",
    "selected_symbol_key": "M:MyNamespace.MyClass.MyMethod(System.String)",
    "candidates": [
      { "project_id": "a1b2c3d4e5f6g7h8", "symbol_key": "M:MyNamespace.MyClass.MyMethod(System.String)", "fully_qualified_name": "global::MyNamespace.MyClass.MyMethod(string)", "kind": "method", "file_path": "src/A/MyClass.cs", "line_start": 42 },
      { "project_id": "99887766aabbccdd", "symbol_key": "M:MyNamespace.MyClass.MyMethod(System.String)", "fully_qualified_name": "global::MyNamespace.MyClass.MyMethod(string)", "kind": "method", "file_path": "src/B/MyClass.cs", "line_start": 42 }
    ]
  },
  "results": [...]
}
```

These fields are omitted when the symbol is declared once. To pin a specific project version, re-issue the call with
a narrowing `project_id`.

### Feature availability (Phase 8)

Under the `core` indexing profile some optional data (documentation search, comments, tests, dataflow) is
not built. When a capability-aware tool needs data the **active** [indexing profile](configuration.md#index-profiles)
did not build, it returns a structured `feature_unavailable` block in `meta` — naming the missing feature,
the active profile, and the minimum profile that would provide it — instead of crashing or returning a
silently-empty result:

```json
{
  "meta": {
    "queried_at": 1709568000000,
    "index_freshness": 0,
    "result_count": 0,
    "feature_unavailable": {
      "feature": "dataflow",
      "required_profile": "deep",
      "active_profile": "standard",
      "message": "Feature 'dataflow' (profile 'standard') did not build. Re-index with the 'deep' profile or higher to enable it."
    }
  },
  "results": []
}
```

### Snapshot provenance (Phases 11–12)

When a query is served from a snapshot generation (a committed base, a Phase-10 working-tree overlay, or a
federated read), `meta` carries a `snapshot` block describing the provenance of the served data. It is
**omitted** for a pure legacy/direct-seed database, so pre-snapshot responses are byte-identical:

```json
{
  "meta": {
    "queried_at": 1709568000000,
    "index_freshness": 1709567990000,
    "result_count": 5,
    "snapshot": {
      "base_snapshot_id": 42,
      "base_commit": "a1b2c3d",
      "overlay_generation": 57,
      "is_overlay": true,
      "completeness": "complete",
      "scope": "local",
      "dirty": true,
      "fallback_reason": null,
      "compatible": true,
      "incompatibilities": null,
      "freshness": 1709567990000
    }
  },
  "results": [...]
}
```

- `base_snapshot_id` / `base_commit` — the committed base snapshot the read rests on, and its commit.
- `overlay_generation` / `is_overlay` — the Phase-10 overlay generation layered on the base, when the read
  includes uncommitted working-tree changes.
- `completeness` — `complete` or `partial` for the served generation. A generation whose committed base
  has a recorded **partial** checkout coverage (issue #119) is `partial` even though it is published.
- `coverage` — the committed base's durable checkout coverage (issue #119): `verdict` (`complete` /
  `partial`), `reasons`, `selection_source`, and the solution / project / submodule counts behind the
  verdict (`solutions_not_selected`, `projects_skipped`, `project_files_unreferenced`,
  `submodules_unpopulated`, `scan_errors`, …). Omitted when no coverage was recorded (a local CLI/daemon
  index or a pre-022 snapshot). `get_index_status` reports the same object as `index.coverage`.
- `scope` — the federation partition the results came from (e.g. `local`, `committed`).
- `dirty` — whether the working tree had uncommitted changes.
- `fallback_reason` — set when a full local fallback could not reuse a committed base.
- `compatible` / `incompatibilities` — the read-time compatibility verdict; each incompatibility names the
  `dimension`, `expected`, and `actual` value (issue #41).
- `freshness` — the served generation's freshness timestamp.
- `origin` — where the served base snapshot's rows came from: `local` (the local catalog) or `remote` (a
  configured peer served a base snapshot the local catalog lacked). Omitted on the pure-local path (issue
  #60).
- `base_identity_hash` — the immutable identity hash of the base snapshot a remote federation fetch
  resolved (issue #60).

The transparent offline fallback still works — once a remote page is cached, it keeps answering with
`origin=remote` even when the peer is later unreachable. There is no separate `offline_cache` flag: the
remote source is cache-first (it serves a warm page without probing the peer), so "served because offline"
is indistinguishable from "served because warm" and no honest signal can be produced.

When a result set is cursor-paged (e.g. `get_base_snapshot_symbols`), `meta.next_cursor` carries the
opaque cursor to pass back to fetch the next page; it is omitted on the final page.

A read that fails a hard compatibility check surfaces an `error` block in `meta` (`code` + `message`)
rather than serving mismatched data (Phase 11).

## Symbol Result Object

When a tool returns symbols, each includes at minimum:

```json
{
  "fully_qualified_name": "global::MyNamespace.MyClass.MyMethod(string)",
  "display_name": "MyMethod",
  "kind": "method",
  "project_canonical_id": "a1b2c3d4e5f6g7h8",
  "file_path": "src/MyProject/MyClass.cs",
  "line_start": 42,
  "line_end": 55,
  "accessibility": "public",
  "signature": "string MyMethod(string input, int retries = 3)"
}
```

`signature` is the member's C# declaration: return (or property/field/event) type, name, type parameters,
parameter modifiers (`this`, `params`, `ref`, `out`, `in`), parameter names and default values, and for a property
its accessors, with type names written as in source (`Task<ChannelRecord> CreateAsync(string tenantId, DateTime
installedAt, CancellationToken cancellationToken)`). It comes from the index (migration 026, analyzer version 5):
a snapshot indexed before that keeps the earlier display, `Ns.Type.Member(string, System.DateTime)`, until it is
re-indexed. Fields, events and delegates now have a `signature` too; other types have none. `fully_qualified_name` is unchanged and is the name to pass back to a tool.

The `fully_qualified_name` is display/query data and is **not** unique — overloads and same-named members share one. The stable per-definition identity (`symbol_key`, a Roslyn documentation ID or a version-scoped fallback) is surfaced in the ambiguity `candidates` above when an FQN collides, so callers can tell the definitions apart.

## Tools

### find_symbol

Exact or fuzzy symbol lookup by name.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Symbol name to search for |
| `kind` | string | no | Filter by symbol kind |
| `project_id` | string | no | Filter by project canonical ID |
| `fuzzy` | bool | no | Use FTS5 fuzzy search (default: false) |

When `fuzzy` is false, matches exactly on `fully_qualified_name`. When true, uses the FTS5 `symbols_fts` table to search `display_name`, ranked by relevance.

### find_references

All usages of a symbol across the codebase.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the symbol |
| `include_projects` | string[] | no | Limit to specific projects |
| `group_by` | string | no | Group results by `project`, `file`, or `kind` |
| `include_source` | bool | no | Include source code lines in results |
| `scope` | string | no | `file:<relative path>`, `project:<canonical_id>`, `solution:<relative path>` or `all` |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

Returns reference locations with `reference_kind` (invocation, type_ref, attribute, inheritance, override, object_creation) and `context_snippet`. A location whose code did not bind exactly (overload resolution failed, typically because an argument or parameter type is unresolved on the indexer, or the call is ambiguous) is still returned, against each compiler candidate, with `"candidate": true`, like a candidate location in Roslyn's Find References. Exact locations carry no `candidate` field.

### get_type_members

Members of a type with their signatures.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the type |
| `include_inherited` | bool | no | Include members from base types |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)); declared members in source order, then inherited ones |

### get_file_symbols

All symbols defined in a source file.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `file_path` | string | yes | Repository-relative path (an absolute path also works locally) |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### get_call_hierarchy

Callers or callees of a method with configurable depth.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the method |
| `direction` | string | yes | `callers` or `callees` |
| `depth` | int | no | Recursion depth (default: configured max, typically 5) |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

Uses a recursive CTE on the `call_graph` table. Results are returned as a flat list with a `depth` field rather than a nested tree. An edge recorded from a call that did not bind exactly is marked `"candidate": true` (see `find_references`).

### get_implementors

Types implementing an interface or overriding a virtual member.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the interface or member |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### get_type_hierarchy

Base and derived type chains.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the type |
| `direction` | string | no | `up` (bases), `down` (derived), or `both` (default: both) |

### semantic_search

FTS5 full-text search over symbol names and documentation comments.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `query` | string | yes | Search query |
| `kind` | string | no | Filter by symbol kind |
| `max_results` | int | no | Limit results (default: configured max, typically 20) |

### get_index_status

Returns the current state of the index: a summary of the whole index, and one page of per-project rows
(`canonical_id`, `git_remote_url`, `repo_relative_path`, `assembly_name`, `is_test_project`, `last_indexed_at`,
`symbol_count`, `reference_count`) ordered by `repo_relative_path`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `limit` / `cursor` | int / string | no | Pages the project rows (see [Paged results](#paged-results)) |

The response also includes an `index` block describing the served generation: `totals` over every project
(`projects`, `test_projects`, `symbols`, `references`, whichever page is shown), the active `profile`
(indexing profile), its `config_hash`, the enabled `features` (the capability names built under that
profile), an `overlay` block when the served generation is a Phase-10 working-tree overlay or a full local
fallback (`is_overlay`, `base_snapshot_id`, `has_working_tree_delta`, `fallback_reason`), a `snapshot` block
with the served snapshot's full provenance (the `meta.snapshot` fields described under
[Snapshot provenance](#snapshot-provenance-phases-1112); omitted for a legacy, pre-snapshot index), and a `storage`
block (`database_bytes`, `api_snapshot_count`, `file_version_count`, `complete_generation_count`,
`total_run_count`). Under an enforced multi-tenant read policy the run metadata is scoped to the caller's
selected snapshot and the DB-wide `storage` block is omitted (Phase 17). If the database needs a rebuild or
a Sextant upgrade, the status surfaces an actionable readiness message instead (see
[schema.md](schema.md#migrations)).

### get_base_snapshot_symbols

Pages the symbols of a **base snapshot** addressed by its immutable identity hash (Phase-9
`SnapshotIdentity.Hash`). Serves the snapshot from the local catalog when present; otherwise, when remote
peers are configured (`peers` in `sextant.json` / `SEXTANT_PEERS`), it transparently federates the fetch to
a peer that publishes that snapshot — the marquee cross-repo path where a base snapshot lives in another
repository's service (issue #60). Falls back to a cached page when a warmed peer later goes offline.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `identity_hash` | string | yes | Immutable identity hash of the base snapshot to page |
| `cursor` | string | no | Resume cursor from a previous page's `meta.next_cursor` |
| `limit` | int | no | Max symbols per page (default 500, clamped 1..5000) |

`meta.snapshot.origin` (`local`/`remote`) and `base_identity_hash` record where the rows came from;
`meta.snapshot.completeness` / `coverage` report the snapshot's checkout coverage (a partial snapshot's rows
are still served, with `completeness: "partial"`, issue #119); `meta.next_cursor` continues paging, and a page
over the [response size budget](#response-size-budget) ends early with `meta.page_truncated_by: "size"`. When the
snapshot is neither local nor served by any peer, the response is an empty result with an explanatory
`message` (never a silent zero-symbol answer).

> **Local planner tool only.** This tool takes a caller-supplied `identity_hash` that is not bound to the
> repository scope the read gate authorizes, so it is exposed on the local single-tenant MCP surface (stdio
> and the local HTTP host) but **excluded** from the multi-tenant service's remote MCP allowlist. Service-to-
> service snapshot federation uses the per-hash-authorized `/query/snapshots/{identityHash}/symbols` HTTP
> endpoint instead (issue #60).



Cross-project blast radius analysis for a symbol.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the symbol |
| `limit` / `cursor` | int / string | no | Pages `consumers` (see [Paged results](#paged-results)) |

Returns all projects that consume the symbol, reference counts, whether changes are breaking, and submodule pin status for cross-repo references.

### get_project_dependencies

Direct and transitive project dependency graph.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `project_id` | string | yes | Project canonical ID |
| `transitive` | bool | no | Include transitive dependencies (default: false) |

### get_api_surface

Public and protected API surface of a project, with optional breaking change detection.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `project_id` | string | yes | Project canonical ID |
| `compare_to_commit` | string | no | Git commit SHA to diff against |
| `limit` / `cursor` | int / string | no | Paging in list mode (see [Paged results](#paged-results)) |

When `compare_to_commit` is provided, classifies each symbol as added, removed, or changed (breaking vs non-breaking).

## Additional Tools

The following tools are also exposed as MCP tools (registered via `WithToolsFromAssembly`).

### get_namespace_tree

Hierarchical view of namespaces and their types.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `namespace_prefix` | string | no | Namespace prefix to explore (e.g. `global::MyApp.Services`). Omit for top-level. |
| `project_id` | string | no | Filter by project canonical ID |
| `depth` | int | no | How many namespace levels deep to traverse (default: 1) |

The result is one row (`namespace`, `child_namespaces`, `symbols`); `meta.result_count` counts its child
namespaces plus its symbols, the rows the [response size budget](#response-size-budget) keeps in that order.

### get_source_context

Retrieves source code lines around a given location.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `file_path` | string | yes | Path to the source file |
| `line` | int | yes | Center line number |
| `context_lines` | int | no | Number of lines above and below to include (default: 5) |

### find_by_attribute

Symbols decorated with a given attribute.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `attribute_fqn` | string | yes | Fully qualified name of the attribute |
| `kind` | string | no | Filter by symbol kind |
| `scope` | string | no | Scope filter |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### find_unreferenced

Finds symbols with no inbound references (potential dead code).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `kind` | string | no | Filter by symbol kind |
| `project_id` | string | no | Filter by project canonical ID |
| `accessibility` | string | no | Filter by accessibility (public, internal, etc.) |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### get_type_dependents

Types that depend on a given type, through fields, parameters, return types, or inheritance.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the type |
| `dependency_kind` | string | no | Filter: `inherits`, `implements`, `returns`, `parameter_of`, `instantiates`, or `all` (default: all) |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### find_tests

Finds test methods, optionally filtered to tests that reference a specific production symbol.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `for_symbol` | string | no | FQN of a production symbol to find tests for |
| `framework` | string | no | Test framework filter: `xunit`, `nunit`, `mstest`, or `all` |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### find_comments

Finds TODO, HACK, FIXME, BUG, and NOTE comments in the codebase.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tag` | string | no | Filter by tag: `TODO`, `HACK`, `FIXME`, `BUG`, `NOTE`, or `all` |
| `search` | string | no | Search within comment text |
| `project_id` | string | no | Filter by project canonical ID |
| `in_symbol` | string | no | FQN of enclosing symbol |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### trace_value

Traces data flow through method calls — what values flow into parameters, or where return values go.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `method_fqn` | string | yes | Fully qualified name of the method |
| `direction` | string | yes | `origins` (what flows IN) or `destinations` (where output goes) |
| `parameter` | string | no | Parameter name or index to trace (for origins) |
| `depth` | int | no | Maximum depth of transitive tracing (default: 2) |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### find_by_signature

Finds methods/properties by signature characteristics.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `return_type` | string | no | Return type to match (case-insensitive substring of the declared type, e.g. `Task` matches `Task<int>`) |
| `parameter_type` | string | no | Parameter type to match (case-insensitive substring of any parameter's type) |
| `parameter_count` | int | no | Exact number of parameters |

The filters read the member's declaration (see [Symbol Result Object](#symbol-result-object)). A snapshot indexed
before analyzer version 5 has no return types, so `return_type` matches nothing there until it is re-indexed.
| `kind` | string | no | Symbol kind filter (default: method) |
| `project_id` | string | no | Filter by project canonical ID |
| `limit` / `cursor` | int / string | no | Paging (see [Paged results](#paged-results)) |

### get_daemon_status

Queries the daemon's HTTP status endpoint for live indexing progress. Also available via CLI (`sextant daemon status`).

No parameters. Returns daemon PID, port, state (idle/indexing), current phase, project progress, and elapsed time.

## Transport

Sextant supports two MCP transport modes:

- **stdio** (primary) — Sextant runs as a child process communicating over stdin/stdout. Used by AI tools like Claude Code.
- **HTTP** — Sextant runs as a standalone HTTP server with the MCP endpoint at `/mcp` (Streamable HTTP). Started with `sextant serve --port <port>`. The legacy HTTP+SSE endpoints (`/mcp/sse`, `/mcp/message`) are not served; the MCP SDK disables them by default.
