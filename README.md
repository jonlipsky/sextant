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

Sextant can use an LLM to power the `research_codebase` tool. Run the interactive setup wizard:

```bash
sextant config llm
```

This prompts for provider, model, API key, and other settings, then saves to `sextant.json`. You can also configure non-interactively:

```bash
# Set provider and model
sextant config llm set --provider anthropic --model claude-sonnet-4-20250514

# Set which env var holds your API key
sextant config llm set --api-key-env ANTHROPIC_API_KEY

# Or use an OpenAI-compatible provider
sextant config llm set --provider openai-compatible --model gpt-4o --base-url https://api.openai.com/v1

# Enable/disable
sextant config llm set --enabled true
```

Or add the `llm_assist` section to `sextant.json` directly:

```json
{
  "llm_assist": {
    "provider": "anthropic",
    "model": "claude-sonnet-4-20250514",
    "api_key_env": "ANTHROPIC_API_KEY",
    "max_tool_calls": 15,
    "enabled": true
  }
}
```

| Variable | Description |
|---|---|
| `SEXTANT_LLM_API_KEY` | API key (highest priority, overrides all other key sources) |
| `SEXTANT_LLM_API_KEY_ENV` | Name of env var containing the API key |
| `SEXTANT_LLM_PROVIDER` | Provider override (`anthropic` or `openai-compatible`) |
| `SEXTANT_LLM_MODEL` | Model override |
| `SEXTANT_LLM_BASE_URL` | Base URL override |
| `SEXTANT_LLM_MAX_CALLS` | Max tool calls override |

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

### Query Examples

```bash
# Find a symbol by name (fuzzy)
sextant query find-symbol UserService --fuzzy

# Find all references to a method
sextant query find-references "global::App.Services.UserService.GetById(int)"

# Get call hierarchy
sextant query get-call-hierarchy "global::App.Handlers.OrderHandler.Process()" --direction callees --depth 3

# Get type hierarchy
sextant query get-type-hierarchy "global::App.Models.BaseEntity" --direction down

# Semantic search
sextant query semantic-search "authentication" --kind method --max 10

# Project dependencies
sextant query get-dependencies abc123def456gh78 --transitive

# API surface with breaking change detection
sextant query get-api-surface abc123def456gh78 --diff oldcommitsha
```

## Daemon

The daemon watches your repository for file changes and incrementally re-indexes, keeping the SQLite database up to date without manual `sextant index` runs.

### Starting the Daemon

```bash
# Start the daemon (auto-discovers .sln files in repo root)
sextant daemon

# With explicit database path
sextant daemon --db /path/to/sextant.db

# With explicit repo root
sextant daemon --repo-root /path/to/repo
```

On first run (empty or missing database), the daemon performs a full index of all solutions. On subsequent starts, it checks file content hashes and only re-indexes changed files.

### How It Works

1. **Solution discovery** — uses `solutions` from `sextant.json`, or auto-discovers `*.sln` files in the repo root
2. **Initial index** — full Roslyn-based extraction if the database is empty; incremental SHA256 hash comparison otherwise
3. **File watching** — monitors `.cs` and `.csproj` files for changes using `FileSystemWatcher`
4. **Debounced indexing** — batches rapid file changes with a 500ms debounce window before re-indexing
5. **Cascade re-indexing** — when a symbol signature changes, dependent files are automatically queued for re-indexing

### Status and Health

The daemon runs a lightweight HTTP status server on a random port. Connection details are written to `.sextant/daemon.pid`:

```
<process-id>
<status-port>
```

Check daemon health:

```bash
# Read the port from the PID file
PORT=$(tail -1 .sextant/daemon.pid)

# Health check
curl http://localhost:$PORT/health
# → OK

# Detailed status
curl http://localhost:$PORT/status
# → {"state":"idle","queued_files":0,"background_tasks":0,"last_indexed_at":1709567890123}
```

### Checking Daemon Status

```bash
sextant daemon status
# → Daemon is running (pid=12345, port=54321)
# → {
# →   "state": "idle",
# →   "queued_files": 0,
# →   "background_tasks": 0,
# →   "last_indexed_at": 1709567890123
# → }
```

Returns exit code 1 if the daemon is not running.

### Stopping the Daemon

```bash
# Using the CLI
sextant daemon stop

# If running in the foreground, press Ctrl+C

# Or manually via the PID file
kill $(head -1 .sextant/daemon.pid)
```

The daemon cleans up its PID file on graceful shutdown.

### Log Files

Daemon logs are written to `.sextant/logs/daemon.log` with automatic size-based rotation at 10MB. Logs include indexing progress, file change events, and errors.

### MCP Server Auto-Spawn

When the MCP server starts (`sextant serve`), it automatically checks for a running daemon and spawns one if needed. This means AI tools using Sextant via MCP get live, incrementally-updated data without any manual setup.

The auto-spawn behavior:
- Reads `.sextant/daemon.pid` to find an existing daemon
- Verifies the process is alive and the health endpoint responds
- If no daemon is found, spawns `sextant daemon` as a background process
- Logs actions to stderr (safe for stdio transport)

To disable auto-spawn:

```bash
# Via environment variable
SEXTANT_AUTO_SPAWN_DAEMON=false sextant serve --stdio

# Via sextant.json
# { "auto_spawn_daemon": false }
```

### Daemon vs MCP Server

The MCP server and daemon are independent processes:

- **MCP server** (`sextant serve`) — reads the SQLite database and responds to tool calls from AI agents. Works without the daemon, but data may be stale.
- **Daemon** (`sextant daemon`) — watches files and writes to the SQLite database. Keeps data fresh for the MCP server (or any other consumer of the database).

Both can run simultaneously — the daemon writes, the MCP server reads. SQLite WAL mode ensures concurrent access works correctly.

### Auto-Start with launchd (macOS)

Create `~/Library/LaunchAgents/com.sextant.daemon.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.sextant.daemon</string>
    <key>ProgramArguments</key>
    <array>
        <string>/path/to/sextant</string>
        <string>daemon</string>
        <string>--repo-root</string>
        <string>/path/to/your/repo</string>
    </array>
    <key>WorkingDirectory</key>
    <string>/path/to/your/repo</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/tmp/sextant-daemon.out.log</string>
    <key>StandardErrorPath</key>
    <string>/tmp/sextant-daemon.err.log</string>
</dict>
</plist>
```

```bash
# Load (start now and on login)
launchctl load ~/Library/LaunchAgents/com.sextant.daemon.plist

# Unload (stop and remove from login)
launchctl unload ~/Library/LaunchAgents/com.sextant.daemon.plist
```

### Auto-Start with systemd (Linux)

Create `~/.config/systemd/user/sextant-daemon.service`:

```ini
[Unit]
Description=Sextant Daemon
After=network.target

[Service]
Type=simple
ExecStart=/path/to/sextant daemon --repo-root /path/to/your/repo
WorkingDirectory=/path/to/your/repo
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
```

```bash
# Enable and start
systemctl --user daemon-reload
systemctl --user enable sextant-daemon
systemctl --user start sextant-daemon

# Check status
systemctl --user status sextant-daemon

# Stop and disable
systemctl --user stop sextant-daemon
systemctl --user disable sextant-daemon
```

## Architecture

```
Sextant.Core       Shared models, project identity, configuration
Sextant.Store      SQLite index store, migrations, queries
Sextant.Indexer    Roslyn workspace loading, symbol extraction
Sextant.Daemon     File watcher, incremental indexing
Sextant.Mcp        MCP server, tool implementations
Sextant.Cli        CLI entry point
```

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
