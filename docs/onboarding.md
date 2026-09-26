# Onboarding: Get Sextant Running on Your Machine

This guide takes a developer from zero to their first successful Sextant query. Sextant is a
Roslyn-based semantic index for .NET (solution → SQLite → MCP) that your AI coding agent queries for
symbols, references, call graphs, type hierarchies, and impact analysis instead of grepping and reading
files.

There are **two supported modes**. Pick one:

| | Mode A — Thin / server-backed | Mode B — Hybrid / local overlay |
|---|---|---|
| **You index locally?** | No | Only your **uncommitted** changes |
| **Where the index lives** | The shared ProcessStack Sextant gateway | Local SQLite (committed base + a working-tree overlay); optional peer federation for cross-repo base snapshots |
| **Best for** | Large repos, the monorepo, "just query it" | Working on a branch with dirty changes you want reflected in queries |
| **What you install** | An MCP endpoint config + an API key | The `sextant` CLI + `sextant.json` (+ optional peer config) |
| **Network dependency** | Always (every query hits the gateway) | Local queries need no network; the `get_base_snapshot_symbols` federation tool fetches remote base snapshots and **falls back to a cached base when offline** |

**Which should I use?**

- **Start with Mode A.** It is the recommended default for large repositories and the monorepo — there is
  no local indexing, so there is nothing to build or keep fresh. Your agent talks to the central,
  org-shared index.
- **Add Mode B** when you are actively editing code and need queries to reflect your **uncommitted**
  working-tree changes. You index the committed state once locally; the daemon then re-indexes only what
  you change and keeps a small **working-tree overlay** layered over that committed base.

> The local single-node CLI + daemon path is fully functional with **zero** server dependency. Mode B's
> peer federation is purely additive — with no peers configured, the query path never touches the network
> and behaves byte-identically to a standalone local index.

---

## Prerequisites

- **.NET 10 SDK** (required for the CLI and for local indexing in Mode B).
- Your AI coding tool of choice. Sextant ships MCP install support for `claude-code`, `cursor`,
  `copilot`, `vscode`, `codex`, and `opencode`.
- For Mode A: a scoped API key for the ProcessStack Sextant gateway (see below).

---

## Mode A — Thin / server-backed (recommended)

In this mode your agent speaks MCP to the **ProcessStack Sextant gateway**. No local indexing, no
`sextant.json`, no daemon — just an MCP endpoint and a key.

### A1. Get a scoped API key

Obtain an API key for the gateway with the **`mcp:sextant:read`** scope. The key is presented on every
request via the `X-Api-Key` header. Keep it out of source control — reference it through an input prompt
or environment variable in your MCP config (examples below), never a literal.

### A2. Configure the MCP endpoint

The gateway exposes an HTTP MCP endpoint:

```
POST https://standalone.processstack.dev/api/v1/{tenant}/mcp/_sextant
```

For the shared org index the tenant is **`processstack`**, so the full URL is:

```
https://standalone.processstack.dev/api/v1/processstack/mcp/_sextant
```

Point your agent at it as an HTTP MCP server. For **Copilot / VS Code**, add to `.vscode/mcp.json`
(the `servers` key is what Sextant's own installer uses for these tools):

```jsonc
{
  "inputs": [
    { "id": "sextant_api_key", "type": "promptString", "description": "Sextant gateway API key", "password": true }
  ],
  "servers": {
    "sextant": {
      "type": "http",
      "url": "https://standalone.processstack.dev/api/v1/processstack/mcp/_sextant",
      "headers": {
        "X-Api-Key": "${input:sextant_api_key}"
      }
    }
  }
}
```

Other tools use the same URL and header, only the config file and root key differ:

| Tool | Config file | Root key |
|---|---|---|
| `claude-code` | `.mcp.json` | `mcpServers` |
| `cursor` | `.cursor/mcp.json` | `mcpServers` |
| `copilot` / `vscode` | `.vscode/mcp.json` | `servers` |
| `codex` | `.codex/config.toml` | `[mcp_servers]` |
| `opencode` | `opencode.json` | `mcp.mcpServers` |

### A3. Watch the repositories you need

The gateway's query gate is **deny-by-default per principal**: a repository returns data **only if** the
principal behind your key has **watched** it, or the repository is connection-enrolled. This is why a
freshly-issued key returns empty results until you watch something — it is a safety property, not a bug.

Watch a repository through the **Sextant chat assistant**:

- `watch owner/repo on <branch>` — start serving that repo/branch to your principal.
- `what am I watching` — list your current watches.
- `stop watching owner/repo` — remove a watch.

### A4. Run your first query

Ask your agent something that routes to a Sextant tool, or call one directly. Good first calls:

- `get_index_status` — confirms the index is reachable and shows what is available. **Call this first.**
- `find_symbol` — look up any symbol by name (exact or fuzzy).
- `find_references` — every usage of a symbol.

Every response carries a `meta` object (`queried_at`, `index_freshness`, `result_count`) so you can see
how fresh the served data is. If `get_index_status` succeeds and `find_symbol` returns rows for a repo you
have watched, Mode A is working.

---

## Mode B — Hybrid / local overlay (for uncommitted work)

Use Mode B when you want queries to reflect **local, uncommitted** changes. You index the committed state
of your repo locally **once**; from then on the daemon re-indexes only the files you change and maintains a
small **working-tree overlay** layered over that committed base, so queries stay in sync with your
uncommitted edits without re-indexing the whole repo on every save.

The overlay always layers over a **local** committed base snapshot (produced by your initial local index).
Separately, configuring `peers` / `SEXTANT_PEERS` wires **remote base-snapshot federation** for the
`get_base_snapshot_symbols` MCP tool: when a base snapshot addressed by its identity hash is not in your
local catalog, Sextant transparently fetches it from a configured peer (for example the server), so you can
page symbols from a base that lives in **another repository's** service without cloning it. Ordinary
queries (`find_symbol`, `find_references`, …) read your local index; federation is specifically that
cross-repo base-snapshot path.

### B1. Install the CLI

The tool command is **`sextant`**. From source (works today, requires the .NET 10 SDK):

```bash
git clone https://github.com/jonlipsky/sextant
cd sextant
./scripts/install-cli.sh
sextant --help    # verify
```

Or, once the package is published, install it as a global .NET tool:

```bash
dotnet tool install -g Sextant.Cli
```

> Packaging/publishing of the global tool is being finalized in
> [#94](https://github.com/jonlipsky/sextant/issues/94); the command name is `sextant`, and the package id
> (`Sextant.Cli` today) may be finalized there. Until then, the `scripts/install-cli.sh` build-from-source
> path is the reliable option.

### B2. Create `sextant.json`

At the root of the repository you are working in, create a `sextant.json` describing what to index and
(optionally) where to reach remote base snapshots:

```json
{
  "solutions": ["src/App.sln"],
  "peers": ["https://standalone.processstack.dev"],
  "remote_fetch_timeout_seconds": 10
}
```

- `solutions` — the solution file(s) (`.sln`/`.slnx`) to index locally. This is the committed base your
  working-tree overlay is layered over.
- `peers` — remote peer base URLs the `get_base_snapshot_symbols` tool federates to for a base snapshot the
  local catalog lacks. Each peer must expose `GET /query/snapshots/{identityHash}/symbols`. Empty (the
  default) keeps pure-local behavior and never touches the network.
- `remote_fetch_timeout_seconds` — per-request timeout (default `10`) before falling back to a cached base
  page or reporting the peer unavailable.

If the peer's query plane requires a token, add `"peer_query_token": "<token>"`. A remote fetch never
widens local authorization — the peer authorizes that token against its **own** read policy, so you read
only what the peer already grants.

Environment variables override `sextant.json` and are handy for keeping secrets and endpoints out of a
committed file:

| Variable | Maps to | Notes |
|---|---|---|
| `SEXTANT_PEERS` | `peers` | **Comma-separated** list of peer base URLs |
| `SEXTANT_REMOTE_FETCH_TIMEOUT` | `remote_fetch_timeout_seconds` | Seconds |
| `SEXTANT_PEER_QUERY_TOKEN` | `peer_query_token` | Shared token presented to every configured peer |

```bash
export SEXTANT_PEERS="https://standalone.processstack.dev"
export SEXTANT_REMOTE_FETCH_TIMEOUT=10
# export SEXTANT_PEER_QUERY_TOKEN="<token>"   # only if the peer enforces a query token
```

### B3. Index locally and connect your agent

Do an initial local index, then wire up your tool's MCP config for the local stdio server:

```bash
sextant index src/App.sln          # full local index (one-time; the daemon keeps it fresh after)
sextant install copilot            # writes .vscode/mcp.json → { "command": "sextant", "args": ["serve", "--stdio"] }
```

`sextant install <tool>` accepts `claude-code`, `cursor`, `copilot`, `vscode`, `codex`, or `opencode`, and
also adds `.sextant/` to your `.gitignore`.

### B4. Keep the overlay fresh with the daemon (recommended)

The daemon watches for file changes and incrementally re-indexes, maintaining a working-tree **overlay**
layered over the committed base so queries stay in sync with your **uncommitted** edits:

```bash
sextant daemon           # or: sextant daemon start
sextant daemon status
sextant daemon stop
```

The MCP server auto-spawns a daemon when it starts (`sextant serve`); disable that with
`SEXTANT_AUTO_SPAWN_DAEMON=false` or `"auto_spawn_daemon": false`. The daemon also runs a periodic
authoritative git reconciliation pass so the overlay converges to git state even if a file-watch event is
missed; tune its interval with `SEXTANT_RECONCILE_INTERVAL` (seconds, default `30`; `0` disables the
periodic pass, startup reconciliation still runs). See [daemon.md](daemon.md) for the full model.

### B5. Verify the overlay (and, optionally, federation)

**Overlay:** run `get_index_status` — it reports the served generation and, when your uncommitted changes
are reflected, an `overlay` block (`is_overlay`, `has_working_tree_delta`, `base_snapshot_id`). Edit a
symbol locally and confirm the change shows up in `find_symbol` / `find_references`. If your local edits are
reflected in queries, the overlay is working.

**Federation (only if you configured `peers`):** call `get_base_snapshot_symbols` with the `identity_hash`
of a base snapshot published by a peer. On the result, `meta.snapshot.origin` reads `local` (served from
your local catalog) or `remote` (a peer served a base your local catalog lacked), and `base_identity_hash`
records which base the remote fetch resolved. Once a page has been fetched it keeps answering with
`origin=remote` even if the peer later goes offline. Note that this cross-repo tool needs an explicit
identity hash — ordinary queries such as `find_symbol` read your local index and do not federate.

---

## Troubleshooting

**Empty results in Mode A ⇒ deny-by-default.** The gateway serves a repository only to a principal that has
**watched** it (or a connection-enrolled repo). A brand-new key returns nothing until you watch a repo. Fix
it with the chat assistant: `watch owner/repo on <branch>`, then `what am I watching` to confirm. Also
double-check the key carries the `mcp:sextant:read` scope and is sent as the `X-Api-Key` header.

**`/mcp` read-timeout on the query path.** The gateway's `/mcp` query path can hit a read-timeout under
load (tracked as ProcessStack #3075). If a query stalls or returns a timeout, retry; prefer narrow queries
(`get_index_status`, an exact `find_symbol`) over broad ones while the timeout is being addressed. In Mode
B, `SEXTANT_REMOTE_FETCH_TIMEOUT` bounds each remote base-snapshot fetch independently of this.

**Offline or peer unreachable (Mode B).** Federation is **cache-first**: once a base-snapshot page has been
fetched, it keeps answering with `meta.snapshot.origin=remote` even when the peer later goes offline
(transparent offline fallback — there is no separate "offline" flag). A base that was never warmed and is
neither local nor served by any reachable peer yields an **empty result with an explanatory message**,
never a silent zero-symbol answer. With `peers` unset, no remote source is constructed and the query path
stays fully local.

**Local index looks stale (Mode B).** Confirm the daemon is running (`sextant daemon status`) and that your
`sextant.json` `solutions` list points at the right solution. If the database needs a rebuild or a Sextant
upgrade, `get_index_status` surfaces an actionable readiness message instead of results.

---

## Where to go next

- [configuration.md](configuration.md) — full `sextant.json` field reference, all environment variables,
  CLI reference, and remote-federation settings.
- [mcp-tools.md](mcp-tools.md) — every MCP tool, its parameters, and response/`meta` formats.
- [daemon.md](daemon.md) — file watching, incremental indexing, the working-tree overlay, and status
  endpoints.
- [service.md](service.md) — the standalone index service that the gateway is built on (control/query
  planes, tokens, read-authorization policy).
- [indexing.md](indexing.md) — the Roslyn extraction pipeline and project identity model.
