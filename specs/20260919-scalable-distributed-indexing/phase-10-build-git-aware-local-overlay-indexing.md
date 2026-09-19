# Phase 10 — Build Git-aware local overlay indexing

## Goal

Persist only working-tree differences locally when a compatible committed base snapshot is available.

## Scope

- Resolve an exact committed base snapshot for local `HEAD`.
- Discover modified, staged, unstaged, renamed, deleted, and untracked files at startup.
- Reconcile Git state before enabling the existing file watcher.
- Store changed contributions and tombstones in a local overlay database.
- Escalate project/import/SDK changes to project-level invalidation.
- Maintain a full-local fallback when no compatible base exists.

## Technical design / files

- Add a Git change provider using machine-readable, rename-aware Git output.
- Overlay metadata records base repository, commit, snapshot, schema/analyzer/profile versions, and touched files/projects.
- Changed files use the same contribution pipeline as full indexing.
- Deleted files and replaced base declarations produce tombstones.
- Watcher events are hints; periodic/startup Git reconciliation is authoritative.
- Overlay generations publish atomically so MCP never reads half an update.

## Acceptance criteria

1. A clean checkout with an exact base creates an empty overlay.
2. Modified, added, renamed, deleted, staged, and untracked files produce correct overlay state.
3. Restart reconstructs the same overlay from Git state without relying on missed watcher events.
4. Project/configuration changes invalidate every required project contribution.
5. A missing or incompatible base falls back to a complete local index with an explicit reason.
6. One-file edits meet the five-second p95 queryable-state objective on the agreed benchmark.

## Notes / risks / dependencies

- `HEAD` may not yet exist remotely; the service can index it on demand or local Sextant can build a temporary full base.
- Worktrees, submodules, sparse checkouts, and case sensitivity need dedicated fixtures.
