# Releasing Sextant to NuGet.org

The Sextant CLI (`src/Sextant.Cli`) is packaged as a [.NET global tool](https://learn.microsoft.com/dotnet/core/tools/global-tools) and published to NuGet.org as **`Sextant.Cli`** (command name `sextant`).

Publishing is automated by [`.github/workflows/release.yml`](../.github/workflows/release.yml), which runs on any pushed tag matching `v*`.

## What the release workflow does

On a `v*` tag it runs three jobs:

**1. `build-test-pack` (windows-latest)**

1. Checks out the repo and installs the .NET 10 SDK.
2. Derives the package version from the tag (`v1.2.3` → `1.2.3`) and **validates** it against `v<MAJOR>.<MINOR>.<PATCH>[-prerelease]`, rejecting malformed tags.
3. Restores, builds `Sextant.slnx`, and runs the **full test suite as a gate** (`dotnet test Sextant.slnx --no-build`, Debug — the CLI integration tests spawn the CLI via `dotnet run --no-build`, which uses the Debug output).
4. Packs `Sextant.Cli` in Release with `-p:Version=<derived>` (passed via an env var, never interpolated into the shell) and uploads the `.nupkg` as a build artifact.

**2. `verify` (windows-latest, ubuntu-latest, macos-latest)**

Downloads the packed `.nupkg`, installs it as a tool (`dotnet tool install --tool-path`), and smoke-tests the **real installed tool**: `sextant --help`, then scaffolds a tiny solution and runs `sextant index` + `sextant query` — proving MSBuildLocator resolves the SDK from the tool store on every platform.

**3. `publish` (ubuntu-latest, `nuget` environment)**

Downloads the exact verified `.nupkg` and `dotnet nuget push`es it to NuGet.org with `--skip-duplicate`, using the `NUGET_API_KEY` repository secret.

The static `<Version>` in `Sextant.Cli.csproj` is only used for local dev builds; the published version always comes from the tag. Publishing the same artifact that `verify` exercised guarantees the bytes on NuGet.org are the bytes that were tested.

## One-time setup (before the first release)

1. **Own the package id.** Confirm `Sextant.Cli` is available/owned on [nuget.org](https://www.nuget.org/packages/Sextant.Cli). The first `dotnet nuget push` from an account that owns the prefix reserves it; otherwise reserve the id first.
2. **Create a NuGet API key** scoped to push `Sextant.Cli` (Push, "Push new packages and package versions").
3. **Add the key as a repo secret** named `NUGET_API_KEY` under *Settings → Secrets and variables → Actions*. The workflow references `${{ secrets.NUGET_API_KEY }}` and never hardcodes it.
4. **(Recommended) Protect the `nuget` environment.** Under *Settings → Environments → nuget*, add required reviewers so a human approves each publish, and optionally restrict it to protected tags/branches. The `publish` job already targets this environment. Consider a tag ruleset restricting who can create `v*` tags, since any `v*` tag triggers a release.

## Cutting a release

```bash
# from an up-to-date main
git tag v0.1.0
git push origin v0.1.0
```

Watch the **Release** workflow in the Actions tab. On success, `Sextant.Cli 0.1.0` appears on NuGet.org within a few minutes and:

```bash
dotnet tool install -g Sextant.Cli
```

installs a working `sextant` command.

Pre-release versions work too — tag `v0.2.0-beta.1` and install with `dotnet tool install -g Sextant.Cli --prerelease`.

## Runtime prerequisite (MSBuild / Roslyn)

Sextant calls `MSBuildLocator.RegisterDefaults()` and loads Roslyn/MSBuild at runtime. As a **framework-dependent** global tool it does not carry its own SDK, so the target machine must have the **.NET 10 SDK** installed (not just the runtime) — MSBuildLocator discovers MSBuild from the installed SDK. This has been verified end-to-end from the global tool store: `dotnet tool install -g Sextant.Cli` → `sextant index <solution>` indexes successfully. Document this prerequisite anywhere install is described (see the README install section).
