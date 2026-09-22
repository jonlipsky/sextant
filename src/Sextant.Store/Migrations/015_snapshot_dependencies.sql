-- Phase 12: deduplicate submodules & enable cross-repository usages.
--
-- Phase 9 made committed generations immutable snapshots keyed off a commit-invariant logical project
-- identity; Phase 10/11 layered local overlays and a federated read planner on top. Until now a git
-- SUBMODULE shared across repositories (e.g. a MixAndMatch library pinned by many apps) was re-indexed
-- once per consuming repo — its projects were registered under the PARENT repository and mapped into
-- the parent snapshot, so the same shared code was stored N times and there was no way to answer
-- "which repositories use this method" without opening every consumer.
--
-- This migration adds the storage Phase 12 needs to index a pinned submodule ONCE into its own
-- PROVIDER snapshot (keyed by submodule remote + pinned commit) and record, per parent, a dependency
-- EDGE into that provider instead of copying the provider's semantic rows:
--
--   repositories.is_provider  — 1 when the repository row exists only because it is a submodule
--                               PROVIDER (its remote was discovered as a submodule of some parent),
--                               0 for a primary/consumer repository indexed in its own right. The
--                               scope-less single-repo local default (SnapshotStore.GetSelectedSnapshotId
--                               / HasUnselectedSnapshotProjectRows) counts only NON-provider repos, so
--                               a single parent repo that pulls in N submodule providers still resolves
--                               its own default branch exactly as a submodule-free repo did.
--   snapshots.is_provider     — 1 for a provider snapshot (a deduplicated, shared submodule generation
--                               owned by no parent), 0 for a normal parent/base/overlay snapshot. A
--                               provider snapshot carries no branch pointer; it is discovered and reused
--                               by identity_hash (BeginPending) and kept alive by the retention
--                               protection over the dependency edges below.
--   snapshot_dependencies     — the reverse-dependency catalog: one immutable row per
--                               (consumer project version -> provider project version) edge, carrying
--                               the parent's exact submodule pin (provider_commit_sha), the provider
--                               repository/snapshot the pinned code lives in, the reference kind, and
--                               whether the parent's checked-out submodule working tree was DIRTY (its
--                               HEAD differed from the recorded pin — issue #48) so a dirty pin is never
--                               conflated with the clean pinned commit. Cross-repo usage queries narrow
--                               to AUTHORIZED consumer snapshots through this catalog first, then search
--                               the target provider symbol's occurrences.
--
-- WHY ADDITIVE / FORWARD-ONLY (NOT rebuild-required): only two defaulted columns are added to existing
-- tables and one new table + its indices are created; nothing is dropped and the index_runs ledger is
-- NOT cleared. As with migrations 013/014 this DOES advance the schema version, which the Phase-9
-- snapshot-identity gate folds into every snapshot's identity_hash, so an existing schema-14 base is
-- treated as schema-incompatible and rebuilt into a schema-15 base on the first run (via
-- IndexDatabase.CheckReadiness) — the safe, expected upgrade path. No data migration is required.

ALTER TABLE repositories ADD COLUMN is_provider INTEGER NOT NULL DEFAULT 0;
ALTER TABLE snapshots ADD COLUMN is_provider INTEGER NOT NULL DEFAULT 0;

CREATE TABLE snapshot_dependencies (
    id INTEGER PRIMARY KEY,
    consumer_snapshot_id INTEGER NOT NULL,
    consumer_project_id INTEGER NOT NULL,
    provider_snapshot_id INTEGER NOT NULL,
    provider_project_id INTEGER NOT NULL,
    provider_repository_id INTEGER NOT NULL,
    provider_commit_sha TEXT NOT NULL,
    reference_kind TEXT NOT NULL,
    submodule_dirty INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL,
    FOREIGN KEY (consumer_snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE,
    FOREIGN KEY (consumer_project_id) REFERENCES projects(id) ON DELETE CASCADE,
    FOREIGN KEY (provider_snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE,
    FOREIGN KEY (provider_project_id) REFERENCES projects(id) ON DELETE CASCADE,
    FOREIGN KEY (provider_repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
    -- One edge per (consumer project version -> provider project version): a parent that references the
    -- same provider project twice records it once, and a re-index of the same consumer generation
    -- re-inserts idempotently (ON CONFLICT ... DO UPDATE in SnapshotDependencyStore).
    UNIQUE (consumer_project_id, provider_project_id)
);

-- Reverse lookup (usage query): given a provider project/snapshot, find its authorized consumers.
CREATE INDEX ix_snapdep_provider_project ON snapshot_dependencies(provider_project_id);
CREATE INDEX ix_snapdep_provider_snapshot ON snapshot_dependencies(provider_snapshot_id);
CREATE INDEX ix_snapdep_provider_repo ON snapshot_dependencies(provider_repository_id);
-- Forward lookup (a consumer snapshot's dependency set) + retention (which providers are still pinned).
CREATE INDEX ix_snapdep_consumer_snapshot ON snapshot_dependencies(consumer_snapshot_id);
CREATE INDEX ix_snapdep_consumer_project ON snapshot_dependencies(consumer_project_id);
