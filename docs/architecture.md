# Architecture

## Overview

Sextant is a build-time semantic indexer for .NET codebases. It uses Roslyn to extract a typed, queryable graph of symbols, references, call chains, and relationships from C# source code, stores them in SQLite, and exposes them via MCP (Model Context Protocol) for AI coding agents.

Rather than treating codebases as text search problems, Sextant provides structured semantic answers — callers, implementors, type hierarchies, blast-radius analysis — resolved by Roslyn's compiler infrastructure.

## System Architecture

Four loosely coupled components form a pipeline from source code to agent-queryable index:

```
Source Files --> [Daemon] --> [Index Builder] --> [Index Store] --> [MCP Server] --> Agent
                   |                                  |                 |
             file watcher                    SQLite (sextant.db)    stdio/HTTP
```

| Component | Project | Responsibility |
|---|---|---|
| **Core** | `Sextant.Core` | Shared models, project identity, configuration |
| **Index Store** | `Sextant.Store` | SQLite database: schema, migrations, parameterized queries |
| **Index Builder** | `Sextant.Indexer` | Roslyn workspace loading, symbol/reference/call-graph extraction |
| **Daemon** | `Sextant.Daemon` | File watcher, debounced change detection, incremental re-indexing |
| **MCP Server** | `Sextant.Mcp` | MCP tool implementations, reads from store directly |
| **CLI** | `Sextant.Cli` | Command-line entry point |

## Project Dependency Chain

```
Sextant.Core        -- shared models, project identity, configuration
  <- Sextant.Store   -- SQLite storage, migrations, queries (Microsoft.Data.Sqlite)
    <- Sextant.Indexer -- Roslyn workspace loading, symbol/reference/call-graph extraction
      <- Sextant.Daemon -- file watcher, incremental indexing orchestrator
    <- Sextant.Mcp   -- MCP server, tool implementations (ModelContextProtocol SDK)
      <- Sextant.Cli -- CLI entry point (also references Daemon and Indexer)
```

## Data Flow

### Indexing Path

1. Developer saves a `.cs` or `.csproj` file.
2. The daemon detects the change (debounced at 500ms) and invokes the index builder for affected files.
3. The index builder loads the Roslyn workspace and extracts symbols, references, and call-graph edges.
4. Changed entries are written to the SQLite store with `last_indexed_at` timestamps.

### Query Path

1. An AI agent invokes an MCP tool (e.g., `find_references`).
2. The MCP server reads the last committed generation through its own WAL reader connection. It never waits for the daemon or indexer's writer gate, and it caches readiness per published generation.
3. MCP calls execute concurrently within reserved read slots that indexing does not consume. The default is `max(4, cores / 2)`; a read that cannot get a slot or reader connection within the configured bound returns the retryable `index_busy` tool error.
4. A structured JSON response is returned, including results and a `meta` object with freshness information.

Indexing uses bounded extraction and compilation prefetch parallelism, defaulting to at most `min(max(1, cores - 1), 8)` workers, leaving a core available for query work on ordinary multi-core hosts. The writer remains single-threaded; read admission is separate from write coordination.

## Technology Stack

| Concern | Choice |
|---|---|
| Language | C# / .NET 10 |
| Semantic analysis | Roslyn (`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.MSBuild`) |
| MSBuild discovery | `Microsoft.Build.Locator` |
| Storage | SQLite via `Microsoft.Data.Sqlite` |
| Text search | SQLite FTS5 (built-in) |
| MCP protocol | `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` NuGet packages (2.2.0) |
| Transport | stdio (primary), HTTP (secondary) |

## Design Principles

- **Roslyn-native semantics.** All symbol information comes from the Roslyn compiler — never text-parsed from source.
- **SQLite only, no ORMs.** Raw parameterized SQL via `Microsoft.Data.Sqlite`. WAL mode enables concurrent read/write.
- **Stable project identity.** Projects are identified by `(git_remote_url, repo_relative_path)`, not disk paths. This ensures indexes are portable across machines.
- **Always queryable.** The MCP server reads SQLite directly and does not depend on the daemon. The index is queryable even if the daemon is stopped.
- **Generated code excluded.** Files matching `*.g.cs`, `*.designer.cs`, or in `obj/` directories are skipped. `IsImplicitlyDeclared` symbols are excluded.

## Performance Targets

| Metric | Target |
|---|---|
| MCP query (exact lookup) | < 5ms p99 |
| MCP query (FTS) | < 20ms p99 |
| Incremental re-index (single file) | < 2s |
| Full index (100k LOC) | < 5 min |
| Index size (100k LOC) | < 200MB |
| Daemon idle CPU | < 1% |
| Cross-solution impact query | < 500ms p99 |
