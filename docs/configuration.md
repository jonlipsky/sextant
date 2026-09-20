# Configuration

## Per-Repository Configuration

Create a `sextant.json` file at your repository root to customize behavior:

```json
{
  "db_path": ".sextant/sextant.db",
  "max_call_hierarchy_depth": 5,
  "fts_max_results": 20,
  "solutions": ["src/App.sln"],
  "auto_spawn_daemon": true,
  "document_extractor": true,
  "max_parallelism": 0,
  "extraction_queue_capacity": 0
}
```

All fields are optional — Sextant uses sensible defaults.

`document_extractor` (default `true`) selects the extraction engine. When `true` (the default), indexing uses the Phase 5 **document-oriented** extractor (a single usage-site pass per document) instead of the legacy declaration-driven `FindReferencesAsync` path. Set it to `false` to fall back to the legacy extractor, which is retained as an emergency fallback; see [indexing.md](indexing.md#document-oriented-extractor-phase-5-feature-flagged).

`max_parallelism` and `extraction_queue_capacity` (Phase 6) tune the document extractor's bounded parallel pipeline. Per-document analysis runs across up to `max_parallelism` workers; the completed per-project contribution sets flow to the single SQLite writer through a bounded channel of capacity `extraction_queue_capacity`, which applies backpressure and bounds outstanding contribution memory. Both default to `0` (auto): `max_parallelism` resolves to `min(processorCount, 8)` and the queue capacity to a small multiple of the resolved parallelism. A positive `max_parallelism` is honored but clamped to the processor count so a misconfiguration cannot oversubscribe the CPU. The pipeline preserves deterministic output — the canonical index is byte-for-byte identical to a single-threaded run regardless of the parallelism level. These knobs only affect the document extractor; the legacy fallback is unaffected.

### Environment Variable Overrides

Environment variables take precedence over `sextant.json`:

| Variable | Description | Default |
|---|---|---|
| `SEXTANT_DB_PATH` | Database file path | `.sextant/sextant.db` |
| `SEXTANT_MAX_DEPTH` | Max call hierarchy depth | `5` |
| `SEXTANT_FTS_MAX` | Max FTS search results | `20` |
| `SEXTANT_AUTO_SPAWN_DAEMON` | Auto-spawn daemon from MCP server | `true` (set `false` or `0` to disable) |
| `SEXTANT_DOCUMENT_EXTRACTOR` | Use the Phase 5 document-oriented extractor | `true` (set `false`/`0` for the legacy fallback) |
| `SEXTANT_MAX_PARALLELISM` | Document-extractor analysis worker cap | `0` (auto: `min(cores, 8)`) |
| `SEXTANT_EXTRACTION_QUEUE_CAPACITY` | Bounded writer-channel capacity (contribution sets) | `0` (auto: small multiple of parallelism) |

### Runtime Output

Sextant stores its database and logs in the `.sextant/` directory at the repo root. This directory is automatically added to `.gitignore` by `sextant install`.

## LLM Assist Configuration

The `research_codebase` tool requires an LLM to synthesize answers. Run the interactive setup wizard:

```bash
sextant config llm
```

This prompts for provider, model, API key, and other settings, then saves to `sextant.json`.

### Non-Interactive Setup

```bash
# Set provider and model
sextant config llm set --provider anthropic --model claude-sonnet-4-20250514

# Set which env var holds your API key
sextant config llm set --api-key-env ANTHROPIC_API_KEY

# Or use an OpenAI-compatible provider
sextant config llm set --provider openai-compatible --model gpt-4o --base-url https://api.openai.com/v1

# Enable/disable
sextant config llm set --enabled true

# Show current configuration
sextant config llm --show
```

### Direct JSON Configuration

Add the `llm_assist` section to `sextant.json`:

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

### LLM Environment Variables

| Variable | Description |
|---|---|
| `SEXTANT_LLM_API_KEY` | API key (highest priority, overrides all other key sources) |
| `SEXTANT_LLM_API_KEY_ENV` | Name of env var containing the API key |
| `SEXTANT_LLM_PROVIDER` | Provider override (`anthropic` or `openai-compatible`) |
| `SEXTANT_LLM_MODEL` | Model override |
| `SEXTANT_LLM_BASE_URL` | Base URL override |
| `SEXTANT_LLM_MAX_CALLS` | Max tool calls override |

## CLI Reference

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

## AI Tool Installation Details

Supported tools and their config file locations:

| Tool | Config File | Root Key |
|---|---|---|
| `claude-code` | `.mcp.json` | `mcpServers` |
| `cursor` | `.cursor/mcp.json` | `mcpServers` |
| `copilot` / `vscode` | `.vscode/mcp.json` | `servers` |
| `codex` | `.codex/config.toml` | `[mcp_servers]` |
| `opencode` | `opencode.json` | `mcp.mcpServers` |

### Claude Code Extras

When installing for `claude-code`, Sextant also sets up:

| File | Purpose |
|---|---|
| `CLAUDE.md` section | Tells Claude to prefer sextant tools over Grep/Explore for .NET codebase queries |
| `.claude/agents/sextant-researcher.md` | Custom agent type for delegating codebase exploration through the semantic index |
| `.claude/skills/sextant/SKILL.md` | `/sextant` slash command for user-invoked codebase research |

The **CLAUDE.md section** is wrapped in `<!-- sextant:begin -->` / `<!-- sextant:end -->` markers. It is appended to existing `CLAUDE.md` files or creates one if absent. Re-running `install` updates the section in place.

The **sextant-researcher agent** appears as a subagent type, routing codebase exploration through the Sextant semantic index instead of generic file reading. It has access to all sextant MCP tools plus Read/Glob/Grep as fallback.

The **`/sextant` skill** gives users a direct entry point: `/sextant how does authentication work?` routes the question through the appropriate sextant tool.
