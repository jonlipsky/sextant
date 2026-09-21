-- Phase 17 (slice 2): open-pull-request snapshot retention roots.
--
-- Criterion 4 requires retention to never delete "protected branch heads, open-PR snapshots,
-- submodule pins, or active-overlay bases". Slice 1 + Phases 9/10/12 already protect branch heads
-- (BranchPointerProtection), active-overlay bases (OverlayBaseProtection), and submodule provider
-- pins (SubmoduleProviderProtection). The one accumulated protected-set member with no durable
-- backing was the OPEN PULL REQUEST: a PR head resolves to an immutable snapshot that must survive a
-- retention/quota GC for as long as the PR is open, even when that snapshot's generation has fallen
-- out of the keep window and its head commit is not a branch pointer.
--
--   pull_request_snapshots — one row per (repository, pr_number) the service has been asked to keep a
--                            snapshot alive for. snapshot_id points at the immutable Phase-9 snapshot
--                            the PR head currently resolves to (ON DELETE SET NULL so a genuinely
--                            reclaimed snapshot never dangles the row); head_commit_sha records the
--                            resolved commit so PruneOrphanFileVersions/API-history trimming can spare
--                            it by commit too; state is 'open' | 'closed'. Only 'open' rows contribute
--                            to the protected set — closing a PR (ClosePullRequestSnapshot) releases
--                            its protection so the snapshot becomes eligible for the ordinary
--                            keep-window / quota GC. UNIQUE (repository_id, pr_number) makes register
--                            idempotent (a re-open/advance upserts the same row).
--
-- WHY ADDITIVE / FORWARD-ONLY (NOT rebuild-required): one new table + its index; nothing dropped and
-- index_runs is NOT cleared. Like migrations 012–018 this advances the schema version (folded into the
-- Phase-9 snapshot identity_hash), so an existing schema-18 base is treated as schema-incompatible and
-- rebuilt into a schema-19 base on first run via IndexDatabase.CheckReadiness — the safe upgrade path.

CREATE TABLE pull_request_snapshots (
    id INTEGER PRIMARY KEY,
    repository_id INTEGER NOT NULL,
    pr_number INTEGER NOT NULL,
    snapshot_id INTEGER,
    head_commit_sha TEXT,
    state TEXT NOT NULL DEFAULT 'open',
    updated_at INTEGER NOT NULL,
    FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
    FOREIGN KEY (snapshot_id) REFERENCES snapshots(id) ON DELETE SET NULL,
    UNIQUE (repository_id, pr_number)
);

CREATE INDEX ix_pull_request_snapshots_state ON pull_request_snapshots(state);
CREATE INDEX ix_pull_request_snapshots_snapshot ON pull_request_snapshots(snapshot_id);

-- Phase 17 (slice 2, issue #70): per-contribution assembly completeness.
--
-- Phase 16 assembles a repository snapshot from multiple client/CI contributions across several ingest
-- calls, then FINALIZE publishes it. Before slice 2, finalize published Complete without a
-- topological-completeness gate — so an assembly missing project versions, or one where a contributor
-- declared its own project versions partial/unsupported (ContributionManifest project Completeness),
-- was silently published Complete. Recording each accepted contribution's declared completeness makes
-- the finalize gate durable across the multi-call assembly: if ANY contribution that fed the snapshot
-- was non-complete, the assembled snapshot is published Partial (never silently Complete). Default
-- 'complete' preserves the historical behaviour for every existing row.
ALTER TABLE snapshot_contributions ADD COLUMN completeness TEXT NOT NULL DEFAULT 'complete';
