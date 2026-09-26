# MCP Tools Reference

Sextant exposes its index through MCP (Model Context Protocol) tools. The MCP server reads from the SQLite database directly and does not depend on the daemon being running.

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

### Ambiguous FQN lookups

Because the display FQN is no longer unique, tools that accept an FQN resolve it through a shared resolver. When the FQN matches exactly one definition the response is unchanged. When it matches **several** definitions (overloads, or the same FQN in multiple projects) the resolver still returns a deterministic best match, but adds ambiguity fields to `meta` so callers can see the collision instead of silently getting one row:

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

These fields are omitted entirely for unambiguous lookups, so existing callers see byte-identical responses. To pin a specific definition, re-issue the lookup with a narrowing `project_id`; `selected_project_id` and each candidate's `project_id` identify which projects collided. The `symbol_key` on each candidate is the stable semantic identity you can record and compare across runs (tools resolve by FQN scoped with `project_id`; there is no query-by-key tool in this phase).

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
  "signature": "public string MyMethod(string input)"
}
```

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

Returns reference locations with `reference_kind` (invocation, type_ref, attribute, inheritance, override, object_creation) and `context_snippet`.

### get_type_members

Members of a type with their signatures.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the type |
| `include_inherited` | bool | no | Include members from base types |

### get_file_symbols

All symbols defined in a source file.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `file_path` | string | yes | Path to the source file |

### get_call_hierarchy

Callers or callees of a method with configurable depth.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the method |
| `direction` | string | yes | `callers` or `callees` |
| `depth` | int | no | Recursion depth (default: configured max, typically 5) |

Uses a recursive CTE on the `call_graph` table. Results are returned as a flat list with a `depth` field rather than a nested tree.

### get_implementors

Types implementing an interface or overriding a virtual member.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the interface or member |

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

Returns the current state of the index: project count, symbol count, reference count, last indexed timestamp, and per-project details. Call this first to see what data is available.

No parameters.

The response also includes an `index` block describing the served generation: the active `profile`
(indexing profile), its `config_hash`, the enabled `features` (the capability names built under that
profile), an `overlay` block when the served generation is a Phase-10 working-tree overlay or a full local
fallback (`is_overlay`, `base_snapshot_id`, `has_working_tree_delta`, `fallback_reason`), and a `storage`
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
are still served, with `completeness: "partial"`, issue #119); `meta.next_cursor` continues paging. When the
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

When `compare_to_commit` is provided, classifies each symbol as added, removed, or changed (breaking vs non-breaking).

### research_codebase

Ask a natural language question about the indexed codebase. An LLM agent researches the answer using the semantic index tools and returns a synthesized response. Requires LLM assist to be configured (`sextant config llm`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `question` | string | yes | The natural language question about the codebase |
| `project_id` | string | no | Project canonical ID to scope the research |
| `scope` | string | no | Scope filter: `file:/path`, `project:canonical_id`, `solution:/path`, or `all` |
| `max_tool_calls` | int | no | Maximum tool calls the research agent can make (default: 15) |
| `detail_level` | string | no | Response detail level: `brief` (default) or `detailed` |

The response includes the synthesized `answer`, a `sources` array with FQNs and file locations, and `meta` with `tool_calls_used`, `model`, and standard freshness fields.

## Additional Tools

The following tools are also exposed as MCP tools (registered via `WithToolsFromAssembly`) and are additionally available to the inner LLM agent behind `research_codebase`, which uses them alongside the tools above to answer natural language questions about the codebase.

### get_namespace_tree

Hierarchical view of namespaces and their types.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `namespace_prefix` | string | no | Namespace prefix to explore (e.g. `global::MyApp.Services`). Omit for top-level. |
| `project_id` | string | no | Filter by project canonical ID |
| `depth` | int | no | How many namespace levels deep to traverse (default: 1) |

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

### find_unreferenced

Finds symbols with no inbound references (potential dead code).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `kind` | string | no | Filter by symbol kind |
| `project_id` | string | no | Filter by project canonical ID |
| `accessibility` | string | no | Filter by accessibility (public, internal, etc.) |

### get_type_dependents

Types that depend on a given type, through fields, parameters, return types, or inheritance.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol_fqn` | string | yes | Fully qualified name of the type |
| `dependency_kind` | string | no | Filter: `inherits`, `implements`, `returns`, `parameter_of`, `instantiates`, or `all` (default: all) |

### find_tests

Finds test methods, optionally filtered to tests that reference a specific production symbol.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `for_symbol` | string | no | FQN of a production symbol to find tests for |
| `framework` | string | no | Test framework filter: `xunit`, `nunit`, `mstest`, or `all` |
| `max_results` | int | no | Limit results (default: 50) |

### find_comments

Finds TODO, HACK, FIXME, BUG, and NOTE comments in the codebase.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tag` | string | no | Filter by tag: `TODO`, `HACK`, `FIXME`, `BUG`, `NOTE`, or `all` |
| `search` | string | no | Search within comment text |
| `project_id` | string | no | Filter by project canonical ID |
| `in_symbol` | string | no | FQN of enclosing symbol |
| `max_results` | int | no | Limit results (default: 50) |

### trace_value

Traces data flow through method calls — what values flow into parameters, or where return values go.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `method_fqn` | string | yes | Fully qualified name of the method |
| `direction` | string | yes | `origins` (what flows IN) or `destinations` (where output goes) |
| `parameter` | string | no | Parameter name or index to trace (for origins) |
| `depth` | int | no | Maximum depth of transitive tracing (default: 2) |

### find_by_signature

Finds methods/properties by signature characteristics.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `return_type` | string | no | Return type to match (partial match) |
| `parameter_type` | string | no | Parameter type to match (partial match) |
| `parameter_count` | int | no | Exact number of parameters |
| `kind` | string | no | Symbol kind filter (default: method) |
| `project_id` | string | no | Filter by project canonical ID |
| `max_results` | int | no | Limit results (default: 50) |

### get_daemon_status

Queries the daemon's HTTP status endpoint for live indexing progress. Also available via CLI (`sextant daemon status`).

No parameters. Returns daemon PID, port, state (idle/indexing), current phase, project progress, and elapsed time.

## Transport

Sextant supports two MCP transport modes:

- **stdio** (primary) — Sextant runs as a child process communicating over stdin/stdout. Used by AI tools like Claude Code.
- **HTTP** — Sextant runs as a standalone HTTP server with the MCP endpoint at `/mcp`. Started with `sextant serve --port <port>`.
