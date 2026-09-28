# ProcessStack apps

This directory holds the Sextant **ProcessStack application**: the app that drives the standalone Sextant
index service from ProcessStack. The index service itself stays ProcessStack-agnostic. Only
`apps/processstack/**` references ProcessStack packages, and nothing outside this directory references
the projects here.

```
apps/processstack/
  nuget.config                                   package sources for everything under this directory
  sextant/
    psapp.yaml                                   the app manifest (name sextant, v2.0.0)
    orchestrations/                              the chat entry point and the per-repository watch/unwatch
    processes/                                   ensure, the MCP processes, the v1 memory dual-write
    tests/*.scenario.yaml                        `processstack app test` scenarios
    Sextant.ProcessStack.slnx                    the app's own solution (NOT part of Sextant.slnx)
    Directory.Build.props                        moves bin/ and obj/ out to apps/processstack/artifacts/
    build.sh / build.ps1                         publish the activities bundle to activities/sextant/
    src/Sextant.ProcessStack.Activities/         the app's bundled activities (pure computation)
    tests/Sextant.ProcessStack.Activities.Tests/ MSTest tests for them
```

The design lives in `specs/20260927-processstack-app-extraction/` (`app.md` and `app-activities.md`).

## Why a separate solution

The activities compile against `ProcessStack.Abstractions`, which is published only to a private GitHub
Packages feed (`https://nuget.pkg.github.com/elevenworks/index.json`). `Sextant.slnx` is the public build
and the required **Build & Test** gate, so it must restore from nuget.org alone. The app therefore has its
own `Sextant.ProcessStack.slnx`, its own `nuget.config` and its own workflow
(`.github/workflows/processstack-app.yml`). `ArchitectureBoundaryTests` fails the public build if anything
outside this directory references the SDK or these projects, or if `Sextant.slnx` includes them.

## Building and testing locally

You need the .NET 10 SDK and read access to the private feed.

1. Create a GitHub token with the `read:packages` scope (a classic personal access token; GitHub Packages
   NuGet feeds do not accept fine-grained tokens).
2. Store it once in your **user-level** NuGet config, under the same source key that
   `apps/processstack/nuget.config` uses, so NuGet merges it with the committed source. Never put the
   token in a file inside the repository.

   ```bash
   dotnet nuget add source https://nuget.pkg.github.com/elevenworks/index.json \
     --name elevenworks-github --username <your-github-user> --password <token>
   ```

   On Windows the password is stored encrypted. On Linux and macOS add `--store-password-in-clear-text`,
   which writes it to your user-level config (`~/.nuget/NuGet/NuGet.Config`) only.
3. From the repository root:

   ```bash
   dotnet test apps/processstack/sextant/Sextant.ProcessStack.slnx
   bash apps/processstack/sextant/build.sh     # or: pwsh apps/processstack/sextant/build.ps1
   ```

Extra arguments are passed to `dotnet publish`. From PowerShell, quote MSBuild switches that contain a
colon (for example `./build.ps1 '-m:4'`), because PowerShell splits an unquoted `-m:4`.

## The activities bundle

`build.sh` / `build.ps1` run `dotnet publish src/Sextant.ProcessStack.Activities -c Release -o
activities/sextant/`, relative to `apps/processstack/sextant/`. The output is git-ignored and contains only
the app's own assemblies:

```
Sextant.Core.dll
Sextant.Core.pdb
Sextant.ProcessStack.Activities.deps.json
Sextant.ProcessStack.Activities.dll
Sextant.ProcessStack.Activities.pdb
```

The host supplies the ProcessStack SDK at run time, so the activities project references the package with
`ExcludeAssets="runtime" PrivateAssets="all"`. The scripts fail if the bundle contains any
`ProcessStack.*.dll`, or lacks `Sextant.Core.dll` or `Sextant.ProcessStack.Activities.dll`. The test
project references the package without `ExcludeAssets`, because the tests run the SDK's base class.

`sextant/Directory.Build.props` sends every build's `bin/` and `obj/` to `apps/processstack/artifacts/`
(git-ignored), outside the app directory. `processstack app test` reads every `.yaml`/`.yml`/`.json` file
under `tests/` as a scenario, so the MSTest project's build output must not land there.

## CI

`.github/workflows/processstack-app.yml` runs on pull requests and pushes that touch this directory,
`src/Sextant.Core/`, `src/Sextant.Service/` or `src/Sextant.Store/`. The last two are included because
the tests check parity with the service's URL policy, wire contract and job statuses. The workflow reads
the feed credential from the `PROCESSSTACK_PACKAGES_TOKEN` repository secret, a token with
`read:packages`. It writes the credential into the job's working copy of `nuget.config` only, and restores
the file when the job ends.

When that secret is unavailable, the job emits a `::notice::` and succeeds without restoring, testing,
publishing, validating or running scenarios. That covers a secret that is not configured yet and a pull
request from a fork.

After publishing the bundle, the job installs the `processstack` CLI (`ProcessStack.Cli`, pinned to
1.1.1) from the same feed and runs `processstack app validate` and `processstack app test` on
`apps/processstack/sextant`.

## Validating and testing the app

With the feed configured as above, install the pinned CLI from `apps/processstack/`, so NuGet reads this
directory's `nuget.config`. It maps `ProcessStack.Cli` to the private feed. Then publish the bundle
(`app validate` and `app test` load it from `activities/sextant/`) and run:

```bash
cd apps/processstack && dotnet tool install -g ProcessStack.Cli --version 1.1.1 && cd ../..
bash apps/processstack/sextant/build.sh
processstack app validate -p apps/processstack/sextant
processstack app test -p apps/processstack/sextant --all      # or -s <scenario name>
```

The scenarios run offline. They stub the Sextant control plane with `http:` stubs on connection
`sextant-control`, seed user memory, and assert the requests the flows send, including the
`act=user` caller claims. The GitHub activities (`GitHubGetRepository`, `GitHubListBranches`) run for
real against `http:` stubs on connection `github`, with GitHub REST paths (`/repos/{owner}/{repo}`,
`/repos/{owner}/{repo}/branches`) and response shapes. An error status raises Octokit's own exception
(404 `NotFoundException`, 403 `ForbiddenException`, 5xx `ApiException`), which the flows route on.
A bare `connection: github` criterion (one with no `path`) in `expected.httpCalls` also matches the
`sextant-control` requests, because only GitHub requests carry a bound connection id and the GitHub
connection has no base URL. Scope a GitHub assertion by path (`/repos/**`).
