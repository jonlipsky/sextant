# Onboarding: Get Sextant Running on Your Machine

This guide takes a developer from zero to their first successful Sextant query. Sextant is a
Roslyn-based semantic index for .NET (solution → SQLite → MCP) that your AI coding agent queries for
symbols, references, call graphs, type hierarchies, and impact analysis instead of grepping and reading
files.

There are **two supported modes**. Pick one:

| | Mode A — Thin / server-backed | Mode B — Hybrid / local overlay |
|---|---|---|
| **You index locally?** | No | Only your **uncommitted** changes |
| **Where the index lives** | A shared Sextant index service, reached through the `sextant` ProcessStack app | Local SQLite (committed base + a working-tree overlay); optional peer federation for cross-repo base snapshots |
| **Best for** | Large repos, the monorepo, "just query it" | Working on a branch with dirty changes you want reflected in queries |
| **What you install** | An MCP endpoint config + a ProcessStack API key | The `sextant` CLI + `sextant.json` (+ optional peer config) |
| **Network dependency** | Always (every query goes through ProcessStack to the service) | Local queries need no network; the `get_base_snapshot_symbols` federation tool fetches remote base snapshots and **falls back to a cached base when offline** |

**Which should I use?**

- **Start with Mode A.** It is the recommended default for large repositories and the monorepo — there is
  no local indexing, so there is nothing to build or keep fresh. Your agent talks to the shared index,
  and sees only the repositories you watch.
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
- For Mode A: access to a ProcessStack tenant where the `sextant` app is deployed, and an API key for it
  (see below). Setting the app up is an operator task; the app and its operator docs have moved to a
  separate private repository.

---

## Mode A — Thin / server-backed (recommended)

In this mode your agent speaks MCP to the **`sextant` ProcessStack app**. The app forwards each query to
the Sextant index service with your signed identity, and the service answers only for the repositories you
watch. No local indexing, no `sextant.json`, no daemon — just an MCP endpoint and a key.

### A1. Get an API key for the app

Mint a key that can reach only the app's MCP surface:

```bash
processstack api-key create --name <key-name> --app sextant
```

`--app sextant` grants `mcp:run:sextant` only, and the key is **live-bounded**: it never has more access
than you do right now, so it stops working when you lose access. Send it in the **`X-API-Key`** header on
every request. Keep it out of source control — reference it through an input prompt or environment
variable in your MCP config (examples below), never a literal.

### A2. Configure the MCP endpoint

The app's MCP surface is:

```
POST <processstack-api>/v1/<tenant>/mcp/sextant
```

`<processstack-api>` is your ProcessStack API's base URL and `<tenant>` your tenant's slug; ask your
ProcessStack operator for both. If the tenant runs more than one deployment of the app, add
`?deployment=<name>` to pick one.

Point your agent at it as an HTTP MCP server. For **Copilot / VS Code**, add to `.vscode/mcp.json`
(the `servers` key is what Sextant's own installer uses for these tools):

```jsonc
{
  "inputs": [
    { "id": "sextant_api_key", "type": "promptString", "description": "ProcessStack API key for the sextant app", "password": true }
  ],
  "servers": {
    "sextant": {
      "type": "http",
      "url": "<processstack-api>/v1/<tenant>/mcp/sextant",
      "headers": {
        "X-API-Key": "${input:sextant_api_key}"
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

`tools/list` shows:

- the service's query tools under their own names: `list_repositories`, `search_symbols`, `find_symbol`,
  `find_references`, `get_type_hierarchy`, `get_call_hierarchy`, `get_impact`, `get_index_status`, and the
  rest of the remote query set (`research_codebase` and the local-only tools are not offered);
- the app's own tools: `start-indexing` (index an exact commit of a repository you watch),
  `get-indexing-status` (poll the job it returns) and `import-legacy-watches` (below).

### A3. Watch the repositories you need

Reads are **deny-by-default per caller**: a repository returns data **only if** you hold a grant for it (a
watch), or your tenant holds a tenant-wide grant (a repository the tenant's GitHub App installation sends
events for). A freshly issued key therefore returns nothing until you watch something — it is a safety
property, not a bug.

Watch a repository through the **`sextant` chat** in ProcessStack's web chat, API or CLI (watch commands
are refused on Slack):

- `watch owner/repo on <branch>` (or just `watch owner/repo` for its default branch) — start serving that
  repository to you, and start indexing it if it has no snapshot yet.
- `what am I watching` — list your watches and whether each one is indexed yet.
- `stop watching owner/repo` — remove a watch (every branch), or `stop watching owner/repo on <branch>`.

Only github.com repositories that the workspace's GitHub connection can see can be watched.

**Coming from the older `_sextant` gateway?** Your v1 watches are imported the first time you send the chat
a message, and the reply starts with a note on what was imported. You can also run the
`import-legacy-watches` tool at any time; a watch already imported counts as imported. The old
`mcp:sextant:read` key and the `/mcp/_sextant` endpoint are retired at the cutover; mint a new key as in A1.

### A4. Run your first query

Good first calls:

- `list_repositories` — every repository you can see, with its branches and whether each is indexed.
  **Call this first.**
- `find_symbol` — look up any symbol by name (exact or fuzzy).
- `find_references` — every usage of a symbol.
- `search_symbols` — a name-prefix search across **every** repository you can see, paged with a cursor.

Name the repository on each call with the optional `repository` argument (`owner/repo` or its full URL)
and, if you need a branch other than the default, `branch`. When you can see exactly one repository with an
indexed default branch (your watches plus the tenant's repositories, as `list_repositories` shows them), a
call that names neither reads that one.

Every response carries a `meta` object (`queried_at`, `index_freshness`, `result_count`) so you can see
how fresh the served data is. If `list_repositories` lists your watch and `find_symbol` returns rows for
it, Mode A is working.

---

## Mode B — Hybrid / local overlay (for uncommitted work)

Use Mode B when you want queries to reflect **local, uncommitted** changes. You index the committed state
of your repo locally **once**; from then on the daemon re-indexes only the files you change and maintains a
small **working-tree overlay** layered over that committed base, so queries stay in sync with your
uncommitted edits without re-indexing the whole repo on every save.

The overlay always layers over a **local** committed base snapshot (produced by your initial local index).
Separately, configuring `peers` / `SEXTANT_PEERS` wires **remote base-snapshot federation** for the
`get_base_snapshot_symbols` MCP tool: when a base snapshot addressed by its identity hash is not in your
local catalog, Sextant transparently fetches it from a configured peer (for example a shared Sextant index
service), so you can page symbols from a base that lives in **another repository's** service without
cloning it. Ordinary queries (`find_symbol`, `find_references`, …) read your local index; federation is
specifically that cross-repo base-snapshot path.

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
  "peers": ["https://<sextant-service-host>"],
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

If the peer's query plane requires a token, keep it in an owner-only file and supply it through
`SEXTANT_PEER_QUERY_TOKEN` (below) rather than as `"peer_query_token"` in `sextant.json`, which is usually
committed. A remote fetch never widens local authorization — the peer authorizes that token against its
**own** read policy, so you read only what the peer already grants. The shared service behind Mode A does
not accept ProcessStack API keys on its query plane, so federating to it needs a query token that its
operator issues to a trusted peer.

Environment variables override `sextant.json` and are handy for keeping secrets and endpoints out of a
committed file:

| Variable | Maps to | Notes |
|---|---|---|
| `SEXTANT_PEERS` | `peers` | **Comma-separated** list of peer base URLs |
| `SEXTANT_REMOTE_FETCH_TIMEOUT` | `remote_fetch_timeout_seconds` | Seconds |
| `SEXTANT_PEER_QUERY_TOKEN` | `peer_query_token` | Shared token presented to every configured peer |

```bash
export SEXTANT_PEERS="https://<sextant-service-host>"
export SEXTANT_REMOTE_FETCH_TIMEOUT=10
# export SEXTANT_PEER_QUERY_TOKEN="$(cat <secrets-dir>/peer-query-token)"   # only if the peer enforces a query token
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

**Empty results in Mode A ⇒ you hold no grant.** The service answers only for the repositories you watch
(or that your tenant's GitHub App installation sends events for), so a new key sees nothing until you watch
one: `list_repositories` comes back empty, `search_symbols` answers `no_visible_repositories`, and a query
that names a repository you cannot see gets the same not-found answer as one that does not exist. Fix it
with the `sextant` chat: `watch owner/repo on <branch>`, then `what am I watching` to confirm. A new watch
returns data once its first snapshot is published; `list_repositories` shows whether each branch is
indexed yet.

**`repository_required` in Mode A.** A query that names neither a repository nor a branch reads the one
repository you can see with an indexed default branch. When there are several (or none), it answers
`repository_required`: name one with the `repository` argument (`owner/repo`). A call that names a `branch`
must name its `repository` too.

**401 or "upstream unavailable" in Mode A.** Send the key in the **`X-API-Key`** header; a ProcessStack API
key sent as `Authorization: Bearer` is rejected with 401. A key minted with `--app sextant` reaches only
`/v1/<tenant>/mcp/sextant`. If the query tools answer "upstream unavailable", the tenant runs more than one
deployment of the app: add `?deployment=<name>` to the URL (ask your operator which one).

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
- [service.md](service.md) — the standalone index service behind Mode A (control/query planes, tokens,
  caller assertions, grants and the read-authorization policy).
- [indexing.md](indexing.md) — the Roslyn extraction pipeline and project identity model.
