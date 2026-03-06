# Sextant

Roslyn-based semantic code indexer for .NET projects. Stores symbols, references, call graphs, and relationships in SQLite and exposes them via MCP (Model Context Protocol) for AI agents like Claude Code.

## Installation

### From Source

Requires .NET 10 SDK.

```bash
git clone <repo-url>
cd sextant
./scripts/install-cli.sh
```

This builds the project, packs it as a .NET global tool, and installs it so `sextant` is available on your PATH. Re-run the script at any time to update — it automatically uninstalls the previous version first.

### Usage Without Installing

You can also run directly from source without installing globally:

```bash
dotnet build
dotnet run --project src/Sextant.Cli -- <command> [options]
```

## Quick Start

```bash
# Index a solution
sextant index path/to/Solution.sln

# Query the index
sextant query find-symbol MyClassName --fuzzy
sextant query find-references "global::MyNamespace.MyClass.MyMethod(string)"
sextant query get-call-hierarchy "global::App.Handler.Process()" --direction callees --depth 3

# Start HTTP MCP server
sextant serve --port 3001
```

## AI Tool Integration

Automatically configure Sextant as an MCP server for your AI coding tool:

```bash
# Install for a specific tool
sextant install claude-code    # Creates .mcp.json + CLAUDE.md section + agent + skill
sextant install cursor         # Creates .cursor/mcp.json
sextant install copilot        # Creates .vscode/mcp.json (also: vscode)
sextant install codex          # Creates .codex/config.toml
sextant install opencode       # Creates opencode.json

# With a custom database path
sextant install claude-code --db /path/to/sextant.db

# Remove the configuration
sextant uninstall claude-code
```

All tools use stdio transport — Sextant runs as a child process communicating over stdin/stdout.

### Claude Code Extras

When installing for `claude-code`, Sextant also sets up:

| File | Purpose |
|---|---|
| `CLAUDE.md` section | Tells Claude to prefer sextant tools over Grep/Explore for .NET codebase queries |
| `.claude/agents/sextant-researcher.md` | Custom agent type for delegating codebase exploration through the semantic index |
| `.claude/skills/sextant/SKILL.md` | `/sextant` slash command for user-invoked codebase research |

The **CLAUDE.md section** (wrapped in `<!-- sextant:begin -->` / `<!-- sextant:end -->` markers) is appended to existing `CLAUDE.md` files or creates one if absent. Re-running `install` updates the section in place.

The **sextant-researcher agent** appears as a subagent type, intercepting codebase exploration that would otherwise go through the generic Explore agent. It has access to all sextant MCP tools plus Read/Glob/Grep as fallback.

The **`/sextant` skill** gives users a direct entry point: `/sextant how does authentication work?` routes the question through the appropriate sextant tool.

## Configuration

Create a `sextant.json` file at your repository root:

```json
{
  "db_path": ".sextant/sextant.db",
  "max_call_hierarchy_depth": 5,
  "fts_max_results": 20,
  "solutions": ["src/App.sln"],
  "auto_spawn_daemon": true
}
```

Environment variables override the config file:

| Variable | Description | Default |
|---|---|---|
| `SEXTANT_DB_PATH` | Database file path | `.sextant/sextant.db` |
| `SEXTANT_MAX_DEPTH` | Max call hierarchy depth | `5` |
| `SEXTANT_FTS_MAX` | Max FTS search results | `20` |
| `SEXTANT_AUTO_SPAWN_DAEMON` | Auto-spawn daemon from MCP server | `true` (set `false` or `0` to disable) |

### LLM Assist Configuration

The `research_codebase` tool requires an LLM. Quick setup:

```bash
sextant config llm
```

This runs an interactive wizard to configure provider, model, and API key. See `sextant config llm --help` for non-interactive options.

## MCP Tools

| Tool | Description |
|---|---|
| `find_symbol` | Exact or fuzzy symbol lookup by name |
| `find_references` | All usages of a symbol with context snippets |
| `get_type_members` | Members of a type with signatures |
| `get_file_symbols` | All symbols defined in a source file |
| `get_call_hierarchy` | Callers or callees of a method with configurable depth |
| `get_implementors` | Types implementing an interface |
| `get_type_hierarchy` | Base and derived type chains |
| `semantic_search` | FTS5 full-text search over names and docs |
| `get_index_status` | Current indexing state |
| `get_impact` | Cross-project blast radius analysis |
| `get_project_dependencies` | Direct and transitive dependency graph |
| `get_api_surface` | Public/protected API with breaking change detection |
| `research_codebase` | LLM-powered natural language codebase Q&A |

The `research_codebase` tool delegates to an inner LLM agent that has access to all of the above plus additional specialized tools: `find_by_attribute`, `find_unreferenced`, `get_type_dependents`, `get_namespace_tree`, `get_source_context`, `find_tests`, `find_comments`, `trace_value`, and `find_by_signature`.

## CLI Commands

```
sextant index <solution.sln>                     Index a solution
sextant query <tool> [args...]                   Query the index
sextant serve [--port <port>]                    Start HTTP MCP server
sextant serve --stdio                            Start stdio MCP server
sextant install <tool>                           Install MCP config for a tool
sextant uninstall <tool>                         Remove MCP config for a tool
sextant daemon [start] [--repo-root <path>]      Start file-watching daemon
sextant daemon status                            Check if daemon is running
sextant daemon stop                              Stop the running daemon
sextant profiles                                 List named index profiles
sextant config llm                               Interactive LLM configuration setup
sextant config llm --show                        Show current LLM configuration
sextant config llm set [options]                 Set LLM config non-interactively
```

### Global Options

These options are available on all commands:

| Option | Description |
|---|---|
| `--db <path>` | Path to the SQLite database |
| `--profile <name>`, `-p` | Named index profile (default: 'default') |

See [docs/mcp-tools.md](docs/mcp-tools.md) for full tool parameters and query examples.

## Daemon

The daemon watches for file changes and incrementally re-indexes, keeping the SQLite database fresh without manual `sextant index` runs. The MCP server auto-spawns the daemon when needed, so most users don't need to manage it directly.

```bash
sextant daemon                   # Start (auto-discovers .sln files)
sextant daemon status            # Check if running
sextant daemon stop              # Stop
```

See [docs/daemon.md](docs/daemon.md) for details on how it works, status endpoints, auto-spawn behavior, and launchd/systemd setup.

## Architecture

```
Sextant.Core       Shared models, project identity, configuration
Sextant.Store      SQLite index store, migrations, queries
Sextant.Indexer    Roslyn workspace loading, symbol extraction
Sextant.Daemon     File watcher, incremental indexing
Sextant.Mcp        MCP server, tool implementations
Sextant.Cli        CLI entry point
```

See [docs/architecture.md](docs/architecture.md) for data flow, design principles, and technology stack details.

## Development

```bash
# Run tests
dotnet test

# Run specific test project
dotnet test tests/Sextant.Store.Tests

# Run tests with filter
dotnet test --filter "FullyQualifiedName~StressTest"
```

## License

Functional Source License 1.1 (FSL-1.1-Apache-2.0). Free to use, modify, and distribute for any non-competing purpose. Converts to Apache 2.0 on 2028-03-05. See [LICENSE](LICENSE) for full terms.
