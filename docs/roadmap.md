# Feature Roadmap

This document describes potential enhancements to Sextant, prioritized by their benefit to AI coding agents.

## Current Capabilities

Sextant provides 14 MCP tools covering the core semantic navigation features found in IDEs like Visual Studio and Rider:

- **Symbol lookup** — exact and fuzzy search, file symbols, type members
- **References** — find all usages with context snippets, find by attribute, find unreferenced symbols
- **Call graph** — caller/callee hierarchy with configurable depth
- **Type graph** — type hierarchy (up/down/both), interface implementors
- **Dependencies** — project dependency graph, cross-project blast radius analysis
- **API surface** — public/protected API with breaking change detection against git commits

## Planned Enhancements

### High Priority

These features would significantly improve an agent's ability to understand and modify code.

#### Find Symbols by Signature / Type Constraints

Query symbols by return type, parameter types, or parameter count. An agent fixing a bug often needs "show me all methods that accept an `HttpClient`" or "what returns `IEnumerable<Order>`?"

#### Grouped/Categorized References

Group `find_references` results by project, file, or reference kind. When a symbol has 200 references, grouping lets the agent quickly assess blast radius without processing a flat list.

#### Namespace Browsing

Explore the codebase by namespace hierarchy. An agent orienting itself in an unfamiliar codebase needs "what namespaces exist?" and "what's in `Company.Core.Services`?" without guessing fully qualified names.

#### Read/Write Usage Classification

Distinguish read vs write access for properties and fields. An agent refactoring a property needs to know which callsites read it vs mutate it.

#### Dependent Types

Answer "which types have a field of type `ILogger`?" or "which classes compose a `DbContext`?" by tracking field-of and property-type-of relationships.

#### Code Context / Source Preview

Include actual source code lines in tool responses. Currently tools return file paths and line numbers, requiring a separate file read. Inline source snippets for call hierarchy and symbol lookups would eliminate round-trips.

#### Simplified Dataflow Tracking

Track parameter-to-argument flow across call edges. A simplified version of Rider's "Value Origin" / "Value Destination" for cross-method data flow.

### Medium Priority

Useful for specific scenarios but not needed in every task.

#### Test Discovery & Association

Find tests for a given class or method by combining attribute search (`[Fact]`, `[Test]`) with reference analysis. Essential for "add tests for X" and "which tests cover this change?" workflows.

#### Code Metrics

Compute cyclomatic complexity, method line count, and parameter count at index time. Enables "find the most complex methods" queries for targeted refactoring.

#### TODO/Comment Search

Index inline comments (`// TODO`, `// HACK`, `// FIXME`) via Roslyn's `SyntaxTrivia` for task discovery.

#### Scope/Filter Presets

Add `scope` parameters (file, project, solution) to search tools for efficient monorepo workflows.

### Lower Priority

#### Live Diagnostics

Expose Roslyn compilation errors and warnings via `Compilation.GetDiagnostics()`.

#### Rename Impact Preview

Combine references, overrides, and interface implementations into a single view showing every location that would change in a rename operation.

#### Structural/Pattern Search

A simplified version of Rider's Structural Search and Replace — find code matching patterns like "methods with >N parameters" or "catch blocks that swallow exceptions."

#### Diff-Aware Symbol Changes

Compare the current index against a git commit's snapshot to show added, removed, and modified symbols.
