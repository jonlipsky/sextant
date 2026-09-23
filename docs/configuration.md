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

### Full `sextant.json` field reference

| Field | Type | Default | Purpose |
|---|---|---|---|
| `db_path` | string | `.sextant/profiles/default/sextant.db` | SQLite database path for the active index slot. |
| `profile` | string | `default` | On-disk **index slot** name — selects `.sextant/profiles/<name>/sextant.db`. Distinct from the semantic `indexing_profile`; see [Index Profiles](#index-profiles). Also settable via `--profile`/`-p` or `SEXTANT_PROFILE`. |
| `solutions` | string[] | `[]` | Solutions/projects to index. |
| `max_call_hierarchy_depth` | int | `5` | Max call-hierarchy traversal depth. |
| `fts_max_results` | int | `20` | Max FTS search results. |
| `auto_spawn_daemon` | bool | `true` | Auto-spawn the daemon from the MCP server. |
| `daemon_socket` | string | (auto) | Override the daemon IPC socket/pipe path. |
| `document_extractor` | bool | `true` | Phase 5 document-oriented extractor (see above). |
| `max_parallelism` | int | `0` (auto) | Phase 6 extraction worker cap. |
| `extraction_queue_capacity` | int | `0` (auto) | Phase 6 bounded writer-channel capacity. |
| `write_batch_size` | int | `10000` | Row count that forces a mid-project commit, bounding transaction/WAL growth (Phase 3). |
| `wal_autocheckpoint_pages` | int | `1000` | `PRAGMA wal_autocheckpoint` in pages — bounds the WAL during a run (Phase 3). |
| `journal_size_limit_bytes` | int | `67108864` (64 MiB) | `PRAGMA journal_size_limit` — caps the WAL left on disk after a checkpoint (Phase 3). |
| `reconcile_interval_seconds` | int | `30` | How often the daemon runs an authoritative git reconciliation pass that refreshes the working-tree overlay (Phase 10); `0` disables the periodic pass (startup reconciliation still runs). |
| `indexing_profile` | string | `standard` | Semantic-depth profile: `core`, `standard`, or `deep` (see [Index Profiles](#index-profiles)). |
| `generated_source_policy` | string | `exclude` | Generated-source handling; currently always `exclude` (Phase 8). |
| `platform_routing` | string | `auto` | Service-side platform routing: `auto` or `linux_only` (Phase 15; see [Platform Routing](#platform-routing)). |
| `peers` | string[] | `[]` | Remote peer base URLs the live MCP query planner federates to for a base snapshot the local catalog lacks (issue #60; see [Remote Federation](#remote-federation)). Empty keeps pure-local behavior. |
| `remote_fetch_timeout_seconds` | int | `10` | Per-request timeout for a remote federation fetch before falling back to the cached base or reporting the peer unavailable (issue #60). |
| `peer_query_token` | string | (none) | Shared Bearer query token presented to every configured peer's query plane (issue #60). Null presents no token. |
| `retention` | object | — | Retention limits for superseded generations, API history, and source blobs (see [Retention](#retention)). |
| `llm_assist` | object | — | LLM assist config (see [LLM Assist Configuration](#llm-assist-configuration)). |

> The standalone index service (`sextant service`) is configured separately via `SEXTANT_SERVICE_*`
> environment variables — see [service.md](service.md). It is additive and never required for local
> indexing.

### Environment Variable Overrides

Environment variables take precedence over `sextant.json`:

| Variable | Description | Default |
|---|---|---|
| `SEXTANT_DB_PATH` | Database file path | `.sextant/sextant.db` |
| `SEXTANT_PROFILE` | Index slot name (selects `.sextant/profiles/<name>/sextant.db`) | `default` |
| `SEXTANT_MAX_DEPTH` | Max call hierarchy depth | `5` |
| `SEXTANT_FTS_MAX` | Max FTS search results | `20` |
| `SEXTANT_DAEMON_SOCKET` | Daemon IPC socket/pipe path | (auto) |
| `SEXTANT_AUTO_SPAWN_DAEMON` | Auto-spawn daemon from MCP server | `true` (set `false` or `0` to disable) |
| `SEXTANT_DOCUMENT_EXTRACTOR` | Use the Phase 5 document-oriented extractor | `true` (set `false`/`0` for the legacy fallback) |
| `SEXTANT_MAX_PARALLELISM` | Document-extractor analysis worker cap | `0` (auto: `min(cores, 8)`) |
| `SEXTANT_EXTRACTION_QUEUE_CAPACITY` | Bounded writer-channel capacity (contribution sets) | `0` (auto: small multiple of parallelism) |
| `SEXTANT_WRITE_BATCH_SIZE` | Rows before a forced mid-project commit (Phase 3) | `10000` |
| `SEXTANT_WAL_AUTOCHECKPOINT` | `PRAGMA wal_autocheckpoint` pages (Phase 3) | `1000` |
| `SEXTANT_JOURNAL_SIZE_LIMIT` | `PRAGMA journal_size_limit` bytes (Phase 3) | `67108864` (64 MiB) |
| `SEXTANT_RECONCILE_INTERVAL` | Daemon git-reconciliation interval, seconds (Phase 10) | `30` |
| `SEXTANT_INDEXING_PROFILE` | Semantic-depth profile: `core`/`standard`/`deep` (Phase 8) | `standard` |
| `SEXTANT_GENERATED_SOURCE_POLICY` | Generated-source policy (Phase 8) | `exclude` |
| `SEXTANT_PLATFORM_ROUTING` | Service platform routing: `auto`/`linux_only` (Phase 15) | `auto` |
| `SEXTANT_PEERS` | Comma-separated remote peer base URLs for live MCP federation (issue #60) | (empty) |
| `SEXTANT_REMOTE_FETCH_TIMEOUT` | Remote federation fetch timeout, seconds (issue #60) | `10` |
| `SEXTANT_PEER_QUERY_TOKEN` | Shared Bearer query token for configured peers (issue #60) | (none) |
| `SEXTANT_RETENTION_KEEP_GENERATIONS` | Complete generations to retain (Phase 8) | `3` |
| `SEXTANT_RETENTION_API_KEEP_COMMITS` | API-surface snapshot commits to retain (Phase 8) | `10` |
| `SEXTANT_RETENTION_PRUNE_SOURCE_BLOBS` | Prune superseded source blobs (Phase 8) | `true` (set `false`/`0`/`no`/`off` to keep) |

### Runtime Output

Sextant stores its database and logs in the `.sextant/` directory at the repo root. This directory is automatically added to `.gitignore` by `sextant install`.

## Index Profiles

Sextant has **two independent** "profile" concepts. Keep them distinct:

- **Index slot** (`profile` / `--profile`/`-p` / `SEXTANT_PROFILE`, default `default`) — names an on-disk
  database slot at `.sextant/profiles/<name>/sextant.db`. Use it to keep multiple independent indexes side
  by side (for example, one per solution or per experiment). It does **not** change *what* is indexed.
- **Indexing profile** (`indexing_profile` / `SEXTANT_INDEXING_PROFILE`, default `standard`) — selects the
  **semantic depth** a repository indexes for (Phase 8). It resolves to a fixed feature set and is folded
  into the configuration hash.

### Semantic-depth profiles (`indexing_profile`)

| Profile | Builds |
|---|---|
| `core` | Definitions, occurrences, calls, type relationships, project dependencies. |
| `standard` *(default)* | `core` plus documentation search (FTS), doc comments, and test indexing. |
| `deep` | `standard` plus detailed dataflow (argument/return flow) and extended evidence. |

Select it in `sextant.json`:

```json
{ "indexing_profile": "deep" }
```

or via the `SEXTANT_INDEXING_PROFILE` environment variable. An unrecognized value degrades to `standard`
(a typo never crashes a run). Each index run and snapshot records the canonical profile name, the resolved
feature set, and a stable **configuration hash** (`IndexConfigurationHash`) computed over the profile,
features, generated-source policy, and the `document_extractor` toggle.

**Query-time behavior.** The configuration hash gates snapshot reuse: an index built under one profile/hash
is never silently reused under an incompatible one, so flipping the profile (or the `document_extractor`
flag) forces a rebuild on the next full run. A capability-aware query tool that needs data the active
profile did not build returns a structured `feature_unavailable` block in its response `meta` (naming the
missing feature, the active profile, and the minimum profile that would provide it) rather than crashing or
returning a silently-empty result — see [mcp-tools.md](mcp-tools.md#feature-availability-phase-8). Inspect
the active profile, its enabled features, and the configuration hash with `sextant profiles` or the
`get_index_status` MCP tool.

`generated_source_policy` (`SEXTANT_GENERATED_SOURCE_POLICY`, default `exclude`) is recorded alongside the
profile and folded into the hash. Generated code (`*.g.cs`, `*.designer.cs`, `obj/`, implicitly-declared
symbols) is always excluded today; the recorded value makes a future include-generated mode an explicit,
rebuild-forcing change rather than a silent one.

## Retention

The `retention` object bounds how much superseded history the local index keeps (Phase 8):

```json
{
  "retention": {
    "keep_complete_generations": 3,
    "api_snapshot_keep_commits": 10,
    "prune_superseded_source_blobs": true
  }
}
```

| Field | Env var | Default | Purpose |
|---|---|---|---|
| `keep_complete_generations` | `SEXTANT_RETENTION_KEEP_GENERATIONS` | `3` | Complete index generations to retain before older ones are eligible for reclamation. |
| `api_snapshot_keep_commits` | `SEXTANT_RETENTION_API_KEEP_COMMITS` | `10` | Number of per-commit API-surface snapshots to keep for breaking-change history. |
| `prune_superseded_source_blobs` | `SEXTANT_RETENTION_PRUNE_SOURCE_BLOBS` | `true` | Whether to prune source blobs no longer referenced by a retained generation. |

Retention never deletes data still referenced by a protected/default branch, an open pull request, a
submodule pin, or an active overlay base. Report and apply it with the `sextant retention` CLI (dry-run by
default; add `--execute` to apply) — see [runbooks.md](runbooks.md). The standalone service performs its own
service-owned retention/GC over the same policy via `POST /control/retention`; see
[service.md](service.md#retention--gc--the-service-is-the-lease-owner-46--37--54--38).

## Platform Routing

`platform_routing` (`SEXTANT_PLATFORM_ROUTING`, default `auto`) is a **service-side** orchestration choice
(Phase 15) governing how the standalone index service routes platform-specific project graphs across worker
capabilities. A local/single-node index ignores it and always evaluates in-process, so it is deliberately
**not** folded into the configuration hash.

| Value | Behavior |
|---|---|
| `auto` *(default)* | Escalate a project to a native (Windows/macOS) worker only when Linux evaluation is demonstrably insufficient **and** a compatible worker exists. |
| `linux_only` | Never escalate; a project Linux cannot evaluate is marked unsupported rather than routed. |

The routing policy also carries an `allowed_native_operating_systems` set that can enable one native OS
family while withholding another during capacity provisioning (empty = any compatible native worker). See
[service.md](service.md#platform-specific-routing-by-worker-capability-phase-15) for the full routing model.

## Remote Federation

`peers` (`SEXTANT_PEERS`, default empty) wires the **live** MCP query planner to reach a configured remote
peer for a base snapshot the local catalog lacks (issue #60, completing the #51 remote-federation transport).
This unblocks the cross-repo workflow: a query in one repo can resolve symbols that live in another repo's
snapshot served by a peer, without cloning it.

| Field | Env | Default | Behavior |
|---|---|---|---|
| `peers` | `SEXTANT_PEERS` (comma-separated) | `[]` | Peer service base URLs, e.g. `https://sextant-peer.internal:3011`, each exposing `GET /query/snapshots/{identityHash}/symbols`. Empty = pure-local: no remote source is constructed and the query path never touches the network (byte-identical to before). |
| `remote_fetch_timeout_seconds` | `SEXTANT_REMOTE_FETCH_TIMEOUT` | `10` | Per-request fetch timeout before falling back to a cached page or reporting the peer unavailable. |
| `peer_query_token` | `SEXTANT_PEER_QUERY_TOKEN` | (none) | Shared Bearer token presented to every peer's query plane. A remote fetch never widens local authorization — the peer authorizes this token against its **own** read policy, so a local caller reads only what the peer already grants. |

When peers are configured, the `get_base_snapshot_symbols` MCP tool serves a base snapshot from the **local**
catalog when present, and otherwise **transparently federates** the fetch to a peer that publishes it. A
warmed page is cached by immutable snapshot identity, so once fetched a base keeps answering (with
`origin=remote`) even when the peer later goes offline (transparent offline fallback). Response provenance
(`meta.snapshot.origin` = `local`/`remote`, `base_identity_hash`) records where the rows came from. A single
shared token is used for all peers today; per-peer tokens are a tracked follow-up.

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
sextant contribute <solution.sln> [options]      Produce/upload a deterministic contribution (see contributions.md)
sextant query <tool> [args...]                   Query the index
sextant serve [--port <port>]                    Start HTTP MCP server
sextant serve --stdio                            Start stdio MCP server
sextant service                                  Run the standalone index service (see service.md)
sextant service backup <directory>              Write a consistent catalog + artifact backup
sextant service restore <directory>             Restore a service backup, then start the service
sextant install <tool>                           Install MCP config for a tool
sextant uninstall <tool>                         Remove MCP config for a tool
sextant daemon [start] [--repo-root <path>]      Start file-watching daemon
sextant daemon status                            Check if daemon is running
sextant daemon stop                              Stop the running daemon
sextant profiles                                 List index slots + the active indexing profile/features/hash
sextant retention [--execute]                    Report (dry-run) or apply local retention (see runbooks.md)
sextant config llm                               Interactive LLM configuration setup
sextant config llm --show                        Show current LLM configuration
sextant config llm set [options]                 Set LLM config non-interactively
```

`sextant contribute` (Phase 16) produces a deterministic semantic contribution for the committed source and
optionally uploads it to an index service; its options (`--service`, `--out`/`-o`, `--token`, `--tenant`,
`--branch`, `--default-branch`, `--allow-dirty`, `--require`, `--no-finalize`) are documented in
[contributions.md](contributions.md). `sextant service` and its `backup`/`restore` subcommands are
documented in [service.md](service.md); `sextant retention` in [runbooks.md](runbooks.md).

### Global Options

These options are available on all commands:

| Option | Description |
|---|---|
| `--db <path>` | Path to the SQLite database |
| `--profile <name>`, `-p` | Named **index slot** (default: `default`) — the on-disk DB slot, not the semantic `indexing_profile`. See [Index Profiles](#index-profiles). |

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
