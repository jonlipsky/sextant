# Daemon

The daemon is a long-running background process that watches for file changes and triggers incremental re-indexing, keeping the SQLite index fresh without manual intervention.

## How It Works

### Startup Sequence

1. Loads configuration from `sextant.json` or defaults.
2. Discovers solution files (from config or by scanning for `*.sln` in the repo root).
3. Checks whether the index database exists:
   - **No database:** performs a full initial index of all solutions.
   - **Database exists:** compares `file_index` content hashes against current files and queues changed files.
4. Starts the file watcher, background work queue, and HTTP status server.

### File Watching

Uses `FileSystemWatcher` to monitor the repository:

- **Watch patterns:** `*.cs`, `*.csproj`
- **Ignore patterns:** `**/obj/**`, `**/bin/**`, `**/.git/**`
- **Events:** Created, Changed, Deleted, Renamed

### Debouncing

File change events are collected in a sliding 500ms window. After the window closes, file paths are deduplicated and the unique set is passed to the index builder. This prevents thrashing on rapid successive saves.

### Work Queue

Two priority levels:

1. **Immediate:** file-level re-indexing triggered by the file watcher.
2. **Background:** cross-file reference re-resolution when a symbol's signature changes.

The queue is in-memory. If the daemon is killed, background work is lost but will be re-detected on next startup via content hash comparison.

### Cascade Re-indexing

When a symbol's signature changes, the daemon automatically queues dependent files for re-indexing. This ensures that references, call graph edges, and relationship data stay consistent.

## Status Endpoint

The daemon runs a lightweight HTTP status server on a random port. Connection details are written to `.sextant/daemon.pid`:

```
<process-id>
<status-port>
```

### Endpoints

| Endpoint | Response |
|---|---|
| `GET /health` | `200 OK` if alive |
| `GET /status` | JSON status object |

### Status Object

```json
{
  "state": "indexing",
  "queued_files": 3,
  "background_tasks": 1,
  "last_indexed_at": 1709567890123,
  "phase": "extracting_references",
  "current_project": "Sextant.Store",
  "project_index": 2,
  "project_count": 6,
  "indexing_started_at": 1709567880000,
  "elapsed_ms": 10123
}
```

- `state` — `idle` or `indexing`
- `phase` — current indexing phase (registering_projects, extracting_symbols, extracting_relationships, extracting_references, extracting_call_graph, recording_dependencies, capturing_api_surface, complete)
- `current_project` — name of the project currently being processed
- `project_index` / `project_count` — progress through the project list
- `elapsed_ms` — milliseconds since indexing started

## Graceful Shutdown

On SIGTERM/SIGINT:

1. Stops the file watcher.
2. Finishes any in-progress file re-index (does not interrupt mid-write).
3. Discards queued background work (will be re-detected on next start).
4. Closes database connections.
5. Removes `.sextant/daemon.pid`.

## MCP Server Auto-Spawn

When the MCP server starts (`sextant serve`), it automatically checks for a running daemon and spawns one if needed:

1. Reads `.sextant/daemon.pid` to find an existing daemon.
2. Verifies the process is alive and the health endpoint responds.
3. If no daemon is found, spawns `sextant daemon` as a background process.
4. Logs actions to stderr (safe for stdio transport).

To disable auto-spawn, set `SEXTANT_AUTO_SPAWN_DAEMON=false` or add `"auto_spawn_daemon": false` to `sextant.json`.

## Daemon vs MCP Server

The daemon and MCP server are independent processes:

- **MCP server** (`sextant serve`) — reads the SQLite database and responds to tool calls. Works without the daemon, but data may be stale.
- **Daemon** (`sextant daemon`) — watches files and writes to the SQLite database.

Both can run simultaneously. SQLite WAL mode ensures concurrent access works correctly.

## Log Files

Daemon logs are written to `.sextant/logs/daemon.log` with automatic size-based rotation at 10MB. Logs include indexing progress, file change events, and errors.

## Implementation

| Class | Responsibility |
|---|---|
| `DaemonHost` | Top-level orchestrator, lifecycle management |
| `FileWatcherService` | File system watching with debounce |
| `IndexingQueue` | Priority work queue |
| `StatusServer` | HTTP status endpoint |
