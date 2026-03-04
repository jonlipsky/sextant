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

Returns the current state of the index: project count, symbol count, reference count, last indexed timestamp, and per-project details.

No parameters.

### get_impact

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

## Research-Only Tools

The following tools are not directly exposed as MCP tools. They are available to the inner LLM agent behind `research_codebase`, which uses them to answer natural language questions about the codebase. The research agent also has access to all of the direct tools listed above.

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

Queries the daemon's HTTP status endpoint for live indexing progress. Available via CLI (`sextant daemon status`) but not exposed as an MCP tool.

No parameters. Returns daemon PID, port, state (idle/indexing), current phase, project progress, and elapsed time.

## Transport

Sextant supports two MCP transport modes:

- **stdio** (primary) — Sextant runs as a child process communicating over stdin/stdout. Used by AI tools like Claude Code.
- **HTTP** — Sextant runs as a standalone HTTP server with the MCP endpoint at `/mcp`. Started with `sextant serve --port <port>`.
