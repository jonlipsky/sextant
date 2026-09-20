-- Phase 10: git-aware local overlay indexing.
--
-- Phase 9 made committed generations immutable, published-and-selected snapshots. Phase 10 layers a
-- LOCAL OVERLAY on top of an exact committed base snapshot: only the working-tree DIFFERENCES are
-- re-extracted (the affected undirected project closure) into a fresh overlay generation that SHARES
-- the base snapshot's unchanged project-versions, published atomically with the same one-transaction
-- branch-pointer advance. The base snapshot's rows are never mutated (issue #44).
--
-- This migration is purely additive (four nullable/defaulted columns + one index on the existing
-- `snapshots` table) and forward-only. It does NOT clear the `index_runs` ledger, so it is not a
-- rebuild-required migration in the Phase-7 sense. But it DOES advance the schema version, which the
-- Phase-9 snapshot-identity/fingerprint gate folds into every snapshot identity: an existing schema-13
-- base is therefore treated as schema-incompatible and rebuilt into a schema-14 base on the first run
-- (via IndexDatabase.CheckReadiness + DaemonHost.ConfigurationChangedSinceLastRun), which is the safe,
-- expected upgrade path — the overlay path needs a schema-14 base to build against.
--
-- New columns on `snapshots`:
--   base_snapshot_id   — the committed base snapshot this overlay layers on (NULL for a base/full
--                        snapshot). No inline FK: SQLite ALTER ADD COLUMN with a REFERENCES clause is
--                        fragile (see migration 013), so referential integrity for this pointer is
--                        maintained in code (SnapshotStore) exactly as projects.snapshot_id is. A live
--                        overlay pins its base generation via the Phase-10 OverlayBaseProtection
--                        retention provider so the shared base rows are never GC'd out from under it.
--   is_overlay         — 1 for an overlay generation (base_snapshot_id set), 0 for a base/full one.
--   working_tree_delta — the digest of the dirty working-tree delta (changed/renamed/deleted/untracked
--                        paths + their content hashes) folded into the snapshot's identity_hash. NULL
--                        for a clean checkout. This is what keeps a DIRTY tree under an otherwise-clean
--                        HEAD from ever being mis-identified as the clean committed snapshot (issue
--                        #43): a dirty snapshot's identity is commit + delta, never the bare commit.
--   fallback_reason    — when no compatible base existed and Phase 10 fell back to a COMPLETE local
--                        index (acceptance criterion 5), the human-readable reason it fell back. NULL
--                        for an overlay or a clean base snapshot. Surfaced by get_index_status.
--
-- A dirty FULL-fallback snapshot (no compatible base) has working_tree_delta set, fallback_reason set,
-- base_snapshot_id NULL, is_overlay 0. An overlay has base_snapshot_id + is_overlay + working_tree_delta
-- set, fallback_reason NULL. A clean base snapshot has all four NULL/0, exactly as a Phase-9 snapshot.

ALTER TABLE snapshots ADD COLUMN base_snapshot_id INTEGER;
ALTER TABLE snapshots ADD COLUMN is_overlay INTEGER NOT NULL DEFAULT 0;
ALTER TABLE snapshots ADD COLUMN working_tree_delta TEXT;
ALTER TABLE snapshots ADD COLUMN fallback_reason TEXT;

CREATE INDEX ix_snapshots_base ON snapshots(base_snapshot_id);
