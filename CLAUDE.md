# CLAUDE.md

Sextant is a Roslyn-based semantic code indexer for .NET projects. It extracts symbols, references, call graphs, and relationships into SQLite and exposes them via MCP for AI agents.

## Build & Test

```bash
dotnet build Sextant.slnx
dotnet test Sextant.slnx --no-build
dotnet test --no-build --filter "FullyQualifiedName~SomeTest"
```

## Project Structure

```
Sextant.Core        — models, project identity, configuration
  ← Sextant.Store   — SQLite storage, migrations, queries
    ← Sextant.Indexer — Roslyn workspace loading, symbol extraction
      ← Sextant.Daemon — file watcher, incremental indexing
    ← Sextant.Mcp   — MCP server, tool implementations
      ← Sextant.Cli — CLI entry point
```

## Critical Rules

- **MSBuildLocator.RegisterDefaults()** must be called before any Roslyn type loads (first call in `Program.cs`).
- **Roslyn symbols must be semantic.** Use `ISymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)` for FQNs.
- **SQLite only, no ORMs.** Raw parameterized SQL via `Microsoft.Data.Sqlite`. WAL mode.
- **Project identity is `(git_remote_url, repo_relative_path)`.** Never disk paths.
- **Generated code excluded:** `IsImplicitlyDeclared`, `*.g.cs`, `*.designer.cs`, `obj/`.
- **MCP tool responses include `meta` object:** `queried_at`, `index_freshness`, `result_count`.
- **MCP server reads SQLite directly** — does not depend on daemon running.
- **JSON serialization uses `SnakeCaseLower` naming policy.**
- **FTS5 for text search** — `symbols_fts` virtual table, kept in sync via SQL triggers.
- **Migrations:** hand-written SQL in `src/Sextant.Store/Migrations/`, numbered `001_`, `002_`, etc.

## Configuration

- **Per-repo:** `sextant.json` at repo root — solutions, db_path, profile, daemon settings. Env vars (`SEXTANT_DB_PATH`, `SEXTANT_MAX_DEPTH`, `SEXTANT_FTS_MAX`) override.
- **Global:** `~/.sextant/sextant.json` — LLM assist config (provider, model, API key). Env vars (`SEXTANT_LLM_API_KEY`, `SEXTANT_LLM_PROVIDER`, `SEXTANT_LLM_MODEL`) override.
- **Runtime output:** `.sextant/` directory in repo root (gitignored).
