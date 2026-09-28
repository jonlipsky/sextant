# Scenarios waiting for ProcessStack CLI 1.1.1

These scenarios exercise the flows that call the workspace's GitHub connection (`GitHubGetRepository`,
`GitHubListBranches`): `grant-watch` (watch) and `start-indexing` with a branch.

`processstack app test` 1.1.0 cannot run them. The GitHub activities build their Octokit client from the
connection registry, which `app test` does not configure, so they fail with
`CredentialConnectionProvider is not configured` before any `http:` stub is consulted
(ProcessStack PS-19). CLI 1.1.1 lets an `http:` stub on connection `github` answer those
requests. The stubs here are written in that form, with GitHub REST response shapes.

Until then, this directory is outside `testDirectory`, so `app test` and CI skip it. Under 1.1.0 every
scenario here fails on that error only, except `watch-inaccessible`. That one passes, but only because
the connection error takes the same refusal path as the 404 it stubs.

When 1.1.1 is released (tracked in jonlipsky/sextant#201):
1. Re-pin the CLI in `.github/workflows/processstack-app.yml`.
2. Move these files into `../tests/`.
3. Delete this directory.
