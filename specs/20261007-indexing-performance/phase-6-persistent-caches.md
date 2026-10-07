# Phase 6 — Keep package and git caches between jobs

## Goal

Each job starts with a cold NuGet package folder (the sandbox points `NUGET_PACKAGES` at per-job scratch) and, for a
new commit, a fresh `git init` + depth-1 fetch + recursive submodules. Keep both between jobs of the same repository
so restore and checkout of a new commit cost close to nothing.

## Scope

- **Per-repository NuGet cache.** A persistent `NUGET_PACKAGES` per repository identity on the artifact volume,
  instead of per-job scratch. Per repository (not global) because a repository's own `nuget.config` decides which
  feeds serve a package id: a shared global folder would let one repository's feed plant a package another repository
  then trusts by id and version. Bounded by size with LRU eviction; never written by anything but restore.
- **Incremental git fetch.** Keep a persistent bare object store per repository; provision a new commit with a
  shallow-aware incremental fetch and a worktree or `checkout --detach`, keeping today's credential hardening
  (env-scoped extraheader, no redirects, scrub-and-verify of git dirs) and the atomic swap.
- Restore union: record why ProcessStack fell back to per-solution restore (`no-macos.slnx` declares solution-specific
  configuration) and support that shape in the union traversal if it is common, since per-solution restore evaluates
  shared projects once per solution.

## Technical design / files

- `Sextant.Service/Sandbox/SandboxedEnvironmentScope.cs:41, 56` (`NUGET_PACKAGES` redirection),
  `Restore/PackageRestoreRunner.cs:129, 197-251, 707-728`.
- `Sextant.Service/CloningCheckoutProvider.cs:149, 157-159, 335-384, 429-430` and `.Submodules.cs`.
- `SnapshotService.cs:917` (scratch release).

## Acceptance criteria

1. Restore of a ProcessStack commit after a previous commit's job takes ≤ 15 s (from ~80 s) with an unchanged
   lock state.
2. Checkout of a new commit of an already-cloned repository transfers only new objects (measured bytes) and keeps every
   existing credential-safety test green.
3. A package served by repository A's feed is never visible to repository B's restore (test with two fixture feeds
   publishing the same id/version with different contents).
4. Cache size stays under its configured bound; eviction is tested.

## Notes / risks / dependencies

- Output-neutral; can ship independently after Phase 1.
- Security review required: cache poisoning across repositories and tenants is the risk this design isolates.
