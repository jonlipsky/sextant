# Sextant ProcessStack app

`psapp.yaml` here is the **sextant** ProcessStack application, v2.0.0. It is the next version of the v1
app (1.0.1) and replaces it when activated. It gives ProcessStack three things:

- **Chat** (`configure-watched-repos`): "watch owner/repo on main", "stop watching owner/repo", "what am I
  watching". Each watch is a repository grant the Sextant service holds for the caller
  (`PUT /control/grants/self`), so the service decides what each caller can read.
- **MCP tools** on `/v1/<tenant>/mcp/sextant`:
  - the app's own processes `start-indexing`, `get-indexing-status` and `import-legacy-watches`;
  - the service's query tools (`find_symbol`, `find_references`, `search_symbols`, `list_repositories`, …),
    re-exposed from the `sextant-query` connection under their own names by `mcp.connectionTools`. No
    process runs for them: each call is forwarded to the service's `/mcp` with the caller's signed identity,
    and the service answers only from that identity and its grants. `research_codebase` is left out (it
    calls an LLM on the service's side), and so are the service's local-only tools.
    `ProcessStackAppManifestTests` (in `tests/Sextant.Service.Tests`) fails when the list drifts from the
    service's remote tools.
- **The v1 watch import** (`import-legacy-watches`), described below.

Building, the private package feed, the activities bundle and CI are covered in
[`../README.md`](../README.md). The design is in `specs/20260927-processstack-app-extraction/` (`app.md`),
and the production rollout in its `cutover-runbook.md`.

Every value in angle brackets below is a placeholder. Secrets (tokens and signing keys) are entered by a
person, in the ProcessStack UI or the service host's environment. Never put one in this repository, a
manifest or a command line that is logged.

## 1. Service settings

The Sextant service must accept ProcessStack's signed callers before the app can reach it
(`docs/service.md`, "Caller assertions (SVC-3)"):

- `SEXTANT_SERVICE_CALLER_KEYS=<kid>=<signing-key>@<tenant-id>`: the key the connections below sign with;
- `SEXTANT_SERVICE_CALLER_AUDIENCE=sextant`;
- `SEXTANT_SERVICE_CALLER_APPS=sextant`;
- `SEXTANT_SERVICE_CALLER_IDPS=processstack` (the default; set it explicitly);
- `SEXTANT_SERVICE_DELEGATE_TOKENS=<delegate-token>`: the bearer the `sextant-query` connection sends;
- `SEXTANT_SERVICE_REPOSITORY_HOSTS=github.com` (the default).

## 2. Connections

The manifest declares three connections. Each is a dependency with no `config:`, so the deployment binds it
to a connection registered in the tenant:

| Manifest id | Register as | Credential |
|---|---|---|
| `sextant-query` | `type: mcp`, `transport: http`, URL `<sextant-service-url>/mcp` | `<delegate-token>` as the bearer, plus `callerIdentity` |
| `sextant-control` | `type: http-api`, base URL `<sextant-service-url>` | `<control-token>` (the service's control token) as the bearer, plus `callerIdentity` |
| `github` | the workspace's GitHub App connection | its webhook must deliver `push`, `delete` and `pull_request` |

Use an `https://` service URL unless the service is reachable only on a private network: both Sextant
connections carry a bearer token and a signed caller assertion on every request.

Both Sextant connections carry the same `callerIdentity` block:
`{mode: signed-header, audience: sextant, keyId: <kid>, signingKey: <signing-key>}`. Set `keyId`
explicitly on both. It defaults to the connection's own id, which would give the two connections different
key ids and one of them a key the service does not know.

## 3. Validate, test and publish

From the repository root, after building the activities bundle (`../README.md`):

```bash
processstack app validate -p apps/processstack/sextant
processstack app test -p apps/processstack/sextant --all
processstack app publish apps/processstack/sextant
```

The scenarios under `tests/` run offline against stubs. The connection tools cannot be exercised there:
they are pinned by the manifest test above and by the service's `McpClientCompatibilityTests`, which
checks that one pooled MCP client is authorized request by request, by each request's own caller.

## 4. Deploy and activate

1. In the WebClient's deploy dialog, create the deployment and bind `github`, `sextant-query` and
   `sextant-control` to the connections from step 2. The CLI does not set bindings.
2. Activate the published version:

   ```bash
   processstack app activate sextant
   ```

The connection tools are served through the deployment's bindings. With more than one usable deployment,
a client names one with `?deployment=<name>`; otherwise the query tools answer "upstream unavailable".

## 5. Import the v1 watches

The v1 app kept each user's watches in their own user memory (`sextant.watched-repos`). v2 imports them
into grants:

- **Lazily:** a user's first message in an internal channel (web, API or CLI; not Slack) imports their
  watches before it answers, and the reply starts with a note on what was imported. Later messages try
  again what could not be finished, until nothing is left.
- **On demand:** the user runs the `import-legacy-watches` MCP tool. It can be run again at any time:
  a watch already held counts as imported.

Each entry passes the same checks as a chat watch: it must be on github.com and visible to the
workspace's GitHub connection, and the URL sent to the service is GitHub's own spelling of the repository,
never the text stored in memory. The tool returns counts only (`imported`, `newlyImported`, `skipped`,
`failed`, `remaining`, `complete`, `outcome`, `message`). An entry the service refuses (or one over the
watch limit), or one GitHub did not show in two runs, is not checked again unless the tool is run with
`retryUnresolved: true`. At most 200 entries are checked per run, and `remaining` counts the rest. Once
nothing is left to retry, the flag `sextant.app:legacy-import-v1` is set and the chat stops importing.

## 6. API keys for agents

Mint a live-bounded key for the app:

```bash
processstack api-key create --name <key-name> --app sextant
```

`--app sextant` is shorthand for `--permissions mcp:run:sextant --permission-mode inherit`. The key can
reach only this app's MCP surface, and its effective permissions are always that ceiling intersected with
its owner's live permissions, so it loses access as soon as its owner does. Point MCP clients at
`POST /v1/<tenant>/mcp/sextant`.

## 7. Rollback to v1.0.1

```bash
processstack app rollback sextant 1.0.1
processstack app activate sextant
```

`app rollback` publishes 1.0.1's content as a new version, numbered up from the active one, and that
version becomes the app. v2.0.x keeps writing the stores v1 reads (`app.md`, "Legacy dual-write"), so
v1 resumes with current data. The `sextant-query` and `sextant-control`
connections can stay registered; v1 does not use them.

The import flag stays set through a rollback. To pick up watches added under v1 after it, run
`import-legacy-watches` once v2 is active again.
