# Sextant

Semantic code index for .NET projects. Gives AI coding agents instant access to symbols, references, call graphs, type hierarchies, and dependency analysis — powered by Roslyn and exposed via MCP.

## Getting Started

### 1. Install Sextant

Requires .NET 10 SDK.

```bash
git clone <repo-url>
cd sextant
./scripts/install-cli.sh
```

### 2. Index Your Codebase

```bash
cd /path/to/your/dotnet/repo
sextant index path/to/Solution.sln
```

### 3. Connect Your AI Tool

```bash
sextant install claude-code   # or: cursor, copilot, vscode, codex, opencode
```

### 4. Start Coding

Your AI agent now has access to 13+ semantic tools. It can find symbols, trace references, walk call hierarchies, analyze dependencies, and assess change impact — all from the pre-built index, without reading files.

### Optional: Enable Natural Language Research

The `research_codebase` tool lets your agent ask freeform questions about the codebase. It requires an LLM:

```bash
sextant config llm
```

## What Your Agent Gets

| Tool | What it does |
|---|---|
| `find_symbol` | Look up any symbol by name (exact or fuzzy) |
| `find_references` | Every usage of a symbol across the solution |
| `get_call_hierarchy` | Who calls this method? What does it call? |
| `get_type_hierarchy` | Inheritance chains up and down |
| `get_implementors` | All implementations of an interface |
| `get_type_members` | Methods, properties, fields of a type |
| `get_file_symbols` | Everything defined in a file |
| `get_project_dependencies` | Project dependency graph |
| `get_api_surface` | Public API with breaking change detection |
| `get_impact` | Blast radius analysis before refactoring |
| `semantic_search` | Full-text search over symbol names and docs |
| `get_index_status` | What's indexed and how fresh |
| `research_codebase` | Ask questions in plain English (requires LLM) |

## How It Works

Sextant uses Roslyn to extract a complete semantic graph from your .NET solution and stores it in SQLite. The MCP server queries the database directly — responses are instant (< 5ms for exact lookups).

A background daemon watches for file changes and incrementally re-indexes, so the data stays fresh as you code.

```
Source Files → [Daemon] → [Indexer] → [SQLite] → [MCP Server] → AI Agent
```

## Documentation

| Doc | Contents |
|---|---|
| [Configuration](docs/configuration.md) | `sextant.json`, env vars, LLM setup, CLI reference, AI tool install details |
| [MCP Tools](docs/mcp-tools.md) | Full tool parameters, response formats, query examples |
| [Daemon](docs/daemon.md) | File watching, incremental indexing, status endpoints, auto-start setup |
| [Architecture](docs/architecture.md) | System design, data flow, technology stack |
| [Indexing](docs/indexing.md) | Roslyn extraction pipeline, incremental indexing, project identity |
| [Schema](docs/schema.md) | SQLite tables, migrations, FTS5 |

## Development

```bash
dotnet build Sextant.slnx
dotnet test Sextant.slnx
```

## License

Functional Source License 1.1 (FSL-1.1-Apache-2.0). Free to use, modify, and distribute for any non-competing purpose. Converts to Apache 2.0 on 2028-03-05. See [LICENSE](LICENSE) for full terms.
