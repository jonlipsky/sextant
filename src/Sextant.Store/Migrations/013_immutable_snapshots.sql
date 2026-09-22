-- Phase 9: immutable, versioned repository snapshots.
--
-- Before this migration a project was a single mutable row: a full re-index overwrote it in place, so
-- two commits of the same project could never coexist and a reader could observe a half-rebuilt
-- generation (the whole-generation reader isolation deferred from Phase 3). This migration introduces
-- an immutable snapshot model layered on the existing compact rows:
--
--   repositories       — one row per normalized git remote (or local:// identity).
--   commits            — a (repository, commit_sha, tree_sha) the snapshot was taken at.
--   logical_projects   — the COMMIT-INVARIANT project identity: repository + logical canonical_id
--                        (the existing hash of git-remote|repo-relative-path|tfm) + tfm. This is the
--                        identity Phase 11/12 correlate across snapshots/repos, and the identity the
--                        MCP/query layer surfaces to clients — never the snapshot-scoped value below.
--   snapshots          — an immutable generation for one commit, identified by identity_hash (the
--                        idempotency key = repository + commit_sha + tree_sha + schema_version +
--                        analyzer_version + config_hash + toolchain_fingerprint). status is one of
--                        pending | complete | partial | failed | unsupported | superseded. A snapshot
--                        is published (pending->complete) and selected atomically; a failed/partial
--                        snapshot stays diagnosable but is never pointed at by a branch.
--   branches           — MUTABLE name->snapshot pointers. A branch pointer advances or rolls back
--                        WITHOUT mutating any snapshot; branch names never appear in a semantic-row
--                        primary key. is_default marks the pointer a scope-less local query resolves.
--   snapshot_projects  — the snapshot -> project-version mapping (the read-scope source).
--
-- WHY THIS IS PURELY ADDITIVE / ADOPT-IN-PLACE (NOT rebuild-required):
--
-- The `projects` table doubles as the "project version": the immutable per-snapshot project row. To
-- version it we only ADD nullable columns (logical_project_id, snapshot_id) and CREATE the new tables
-- above. We deliberately do NOT rebuild `projects`. Its identity uniqueness today is an INLINE column
-- constraint (`canonical_id TEXT UNIQUE NOT NULL`, migration 001), which SQLite cannot relax without a
-- create/copy/drop/rename rebuild — and rebuilding `projects` would ripple through every child FK
-- (symbols, files, comments, api_surface_snapshots) and the Phase-4/7 DeleteByProject dual cascade +
-- GetCrossProjectPairs closure. Instead the inline UNIQUE is kept verbatim: a snapshot-tagged project
-- row stores a per-snapshot-unique canonical_id ("{logicalHash}:{snapshotId}") so the global UNIQUE
-- still holds, while its commit-invariant identity lives in logical_project_id -> logical_projects.
-- Legacy rows (snapshot_id IS NULL) keep canonical_id = the logical hash and behave exactly as before.
--
-- Because no existing table is dropped and the index_runs ledger is NOT cleared, an already-complete
-- pre-Phase-9 generation keeps serving unchanged after this migration: its project rows have
-- snapshot_id IS NULL, so a scope-less query applies no snapshot filter and sees them exactly as
-- today. The first post-migration FULL index publishes the first real snapshot, advances the default
-- branch pointer to it, and sweeps the now-superseded legacy (snapshot_id IS NULL) rows in the same
-- publish transaction so the database returns to ~1x size. This migration therefore does NOT require a
-- full rebuild and does NOT clear index_runs. Forward-only.

CREATE TABLE repositories (
    id INTEGER PRIMARY KEY,
    remote_url TEXT NOT NULL UNIQUE,
    created_at INTEGER NOT NULL
);

CREATE TABLE commits (
    id INTEGER PRIMARY KEY,
    repository_id INTEGER NOT NULL,
    commit_sha TEXT NOT NULL,
    tree_sha TEXT,
    created_at INTEGER NOT NULL,
    FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
    UNIQUE (repository_id, commit_sha)
);

CREATE TABLE logical_projects (
    id INTEGER PRIMARY KEY,
    repository_id INTEGER NOT NULL,
    canonical_id TEXT NOT NULL,
    repo_relative_path TEXT NOT NULL,
    target_framework TEXT,
    created_at INTEGER NOT NULL,
    FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
    UNIQUE (repository_id, canonical_id)
);

CREATE TABLE snapshots (
    id INTEGER PRIMARY KEY,
    repository_id INTEGER NOT NULL,
    commit_id INTEGER,
    run_id INTEGER,
    identity_hash TEXT NOT NULL UNIQUE,
    tree_sha TEXT,
    schema_version INTEGER NOT NULL,
    analyzer_version TEXT NOT NULL,
    config_hash TEXT,
    toolchain_fingerprint TEXT,
    status TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    published_at INTEGER,
    FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
    FOREIGN KEY (commit_id) REFERENCES commits(id) ON DELETE CASCADE,
    FOREIGN KEY (run_id) REFERENCES index_runs(id) ON DELETE SET NULL
);

CREATE TABLE branches (
    id INTEGER PRIMARY KEY,
    repository_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    snapshot_id INTEGER,
    is_default INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL,
    FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
    FOREIGN KEY (snapshot_id) REFERENCES snapshots(id) ON DELETE SET NULL,
    UNIQUE (repository_id, name)
);

CREATE TABLE snapshot_projects (
    snapshot_id INTEGER NOT NULL,
    project_id INTEGER NOT NULL,
    PRIMARY KEY (snapshot_id, project_id),
    FOREIGN KEY (snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
);

-- project-version versioning columns on the existing (kept) projects table. Nullable, no inline FK:
-- SQLite ALTER ADD COLUMN with a REFERENCES clause is fragile, so referential integrity for these two
-- columns is maintained in code (SnapshotStore + orchestrator) and by snapshot_projects' FKs.
ALTER TABLE projects ADD COLUMN logical_project_id INTEGER;
ALTER TABLE projects ADD COLUMN snapshot_id INTEGER;

-- One project-version per (snapshot, logical project). Partial so legacy (snapshot_id IS NULL) rows are
-- untouched and keep their inline canonical_id UNIQUE. Snapshot-tagged rows are unique per snapshot.
CREATE UNIQUE INDEX ux_projects_working ON projects(snapshot_id, logical_project_id) WHERE snapshot_id IS NOT NULL;
CREATE INDEX ix_projects_snapshot ON projects(snapshot_id);

CREATE INDEX ix_snapshot_projects_project ON snapshot_projects(project_id);
CREATE INDEX ix_snapshots_status ON snapshots(status);
CREATE INDEX ix_snapshots_repo_commit ON snapshots(repository_id, commit_id);
CREATE INDEX ix_snapshots_run ON snapshots(run_id);
CREATE INDEX ix_branches_default ON branches(repository_id, is_default);
