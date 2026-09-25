# Feature Roadmap

This document describes potential enhancements to Sextant, prioritized by their benefit to AI coding agents.

> New here? Start with the [Onboarding guide](onboarding.md) to get Sextant running on your machine in
> either the thin/server-backed or hybrid/local-overlay mode.

## Current Capabilities

Sextant provides 25 MCP tools covering the semantic navigation features found in IDEs like Visual Studio and Rider, plus cross-repository analysis and index inspection:

- **Symbol lookup & discovery** — exact and fuzzy search (`find_symbol`), full-text search over names and doc comments (`semantic_search`), file symbols (`get_file_symbols`), type members (`get_type_members`), namespace browsing (`get_namespace_tree`), signature/type-constraint search (`find_by_signature`), attribute search (`find_by_attribute`), and unreferenced/dead-code detection (`find_unreferenced`)
- **References & usages** — find all usages with grouping (by project/file/kind), read/write access classification, and inline source (`find_references`); cross-project blast radius (`get_impact`); dependent types (`get_type_dependents`)
- **Call graph & dataflow** — caller/callee hierarchy with configurable depth (`get_call_hierarchy`); simplified value/dataflow tracing (`trace_value`)
- **Type graph** — inheritance chains up/down/both (`get_type_hierarchy`); interface implementors and member overrides (`get_implementors`)
- **Dependencies** — direct and transitive project dependency graph (`get_project_dependencies`)
- **Cross-repository** — usages across repositories (`find_cross_repository_usages`) and submodule consumers (`find_submodule_consumers`)
- **API surface** — public/protected API with breaking-change detection against git commits (`get_api_surface`)
- **Source context** — code preview around a location (`get_source_context`)
- **Test discovery** — find tests, optionally for a specific production symbol (`find_tests`)
- **Comment discovery** — TODO/HACK/FIXME/BUG/NOTE search (`find_comments`)
- **Research** — natural-language Q&A over the index via LLM assist (`research_codebase`)
- **Index inspection** — index status and enabled capabilities (`get_index_status`) and live daemon progress (`get_daemon_status`)

Many search tools also accept a `scope` filter (file, project, solution, or all) for efficient monorepo workflows. See the [MCP Tools Reference](mcp-tools.md) for the full per-tool documentation.

## Recently Shipped

These capabilities were previously planned and have since shipped as MCP tools:

- **Find Symbols by Signature / Type Constraints** — `find_by_signature` (query by return type, parameter type, or parameter count)
- **Grouped/Categorized References** — `group_by` (project/file/kind) on `find_references`
- **Namespace Browsing** — `get_namespace_tree`
- **Read/Write Usage Classification** — `access_kind` (read/write/readwrite) on `find_references`
- **Dependent Types** — `get_type_dependents`
- **Code Context / Source Preview** — `get_source_context`, plus `include_source` on `find_references`
- **Simplified Dataflow Tracking** — `trace_value` (value origins and destinations)
- **Test Discovery & Association** — `find_tests`
- **TODO/Comment Search** — `find_comments`
- **Scope/Filter Presets** — `scope` filter on `find_symbol`, `semantic_search`, `find_references`, and `find_by_attribute`

## Planned Enhancements

### Medium Priority

#### Code Metrics

Compute cyclomatic complexity, method line count, and parameter count at index time. Enables "find the most complex methods" queries for targeted refactoring.

### Lower Priority

#### Live Diagnostics

Expose Roslyn compilation errors and warnings via `Compilation.GetDiagnostics()`.

#### Rename Impact Preview

Combine references, overrides, and interface implementations into a single view showing every location that would change in a rename operation.

#### Structural/Pattern Search

A simplified version of Rider's Structural Search and Replace — find code matching patterns like "methods with >N parameters" or "catch blocks that swallow exceptions."

#### Diff-Aware Symbol Changes

Compare the current index against a git commit's snapshot to show added, removed, and modified symbols across the full symbol set (today `get_api_surface` diffs only the public/protected API surface).
