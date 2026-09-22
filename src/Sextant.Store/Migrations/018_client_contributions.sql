-- Phase 16: client- and CI-assisted index contributions — durable contribution provenance.
--
-- Phase 13 added a standalone service that ensures/ingests/publishes immutable Phase-9 snapshots via a
-- durable job catalog (snapshot_jobs). Phase 15 recorded the producing worker's capability fingerprint on
-- each snapshot. Phase 16 lets an environment that already has the required SDKs/workloads (a dev machine
-- or CI after a successful restore/build) PRODUCE a validated semantic contribution for committed source
-- and upload it; the service authenticates, authorizes, hash-verifies against provider Git content, and
-- publishes it. Multiple capability-specific contributions (Windows + macOS) ASSEMBLE into ONE repository
-- snapshot (acceptance criterion 4), so capability is recorded PER contributed project version, not just
-- once per snapshot.
--
-- This migration adds the durable provenance a contribution needs, EXTENDING (never duplicating) the
-- Phase-9/13/15 catalog:
--
--   snapshot_contributions  — one row per accepted contribution artifact. content_hash is the artifact's
--                             content address and is UNIQUE, so re-uploading the SAME artifact is a no-op
--                             (idempotent, content-addressed — acceptance criterion 2); the service skips
--                             re-import and attaches to the existing snapshot. It records the producing
--                             capability fingerprint, producer identity, toolchain, tenant, and the
--                             assembled snapshot it fed, so an assembled snapshot's provenance names every
--                             environment that contributed to it. ON DELETE CASCADE with its snapshot so
--                             retention GC of a reclaimed snapshot removes its contribution provenance too.
--
--   projects.capability_fingerprint — the capability that produced each contributed project VERSION. An
--                             assembled snapshot spans project versions built by different capabilities
--                             (Windows + macOS), so per-snapshot capability_fingerprint (migration 017) is
--                             not enough to record which capability produced a given project version. This
--                             nullable column is stamped only for contributed project versions; an ordinary
--                             locally-indexed project leaves it NULL (unknown), keeping its rows unchanged.
--
-- WHY ADDITIVE / FORWARD-ONLY (NOT rebuild-required): only one new table + one nullable column are added;
-- nothing is dropped and the index_runs ledger is NOT cleared. As with migrations 012-017 this DOES advance
-- the schema version, which the Phase-9 snapshot-identity gate folds into every snapshot's identity_hash,
-- so an existing schema-17 base is treated as schema-incompatible and rebuilt into a schema-18 base on the
-- first run (via IndexDatabase.CheckReadiness) — the safe, expected upgrade path. No data migration is
-- required.

CREATE TABLE snapshot_contributions (
    id INTEGER PRIMARY KEY,
    snapshot_id INTEGER NOT NULL,
    content_hash TEXT NOT NULL UNIQUE,
    tenant TEXT,
    repository_url TEXT NOT NULL,
    commit_sha TEXT NOT NULL,
    capability_fingerprint TEXT,
    producer TEXT,
    toolchain_fingerprint TEXT,
    manifest_hash TEXT,
    created_at INTEGER NOT NULL,
    FOREIGN KEY (snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE
);

CREATE INDEX ix_snapshot_contributions_snapshot ON snapshot_contributions(snapshot_id);

-- Per-project-version capability (an assembled snapshot spans capabilities). Nullable; NULL for ordinary
-- locally-indexed project versions, stamped only for contributed ones.
ALTER TABLE projects ADD COLUMN capability_fingerprint TEXT;
