-- Issue #119: durable, machine-readable snapshot COVERAGE.
--
-- A snapshot row's status = 'complete' only means "published and servable" — it is the only status a
-- branch pointer can reference, so a service job whose selection skipped solutions/projects/submodules
-- (a PARTIAL job) still publishes a 'complete' snapshot row. Before this table nothing durable recorded
-- HOW MUCH of the checkout that published data covers, so the snapshot page, /control/resolve and MCP
-- meta all reported a 5-of-415-project monorepo snapshot as complete.
--
--   snapshot_coverage.snapshot_id  — the published snapshot this coverage describes (1:1).
--   snapshot_coverage.verdict      — 'complete' (the whole checkout was indexed) or 'partial' (see reasons).
--   snapshot_coverage.summary_json — the full SnapshotCoverage record (snake_case JSON): solution/project/
--                                    submodule counts and one reason per coverage gap.
--   snapshot_coverage.recorded_at  — unix ms.
--
-- The row is written by the orchestrator INSIDE the transaction that publishes the snapshot, so a reader
-- never observes a published service snapshot without its coverage (no crash window). It is immutable
-- once written (INSERT OR IGNORE), like the snapshot it describes. Snapshots produced by paths that do
-- not compute coverage (local CLI/daemon, overlays, provider snapshots, contributions, pre-022 rows) have
-- NO row: "coverage not recorded", which readers treat as not-known-partial (back-compatible).
-- ON DELETE CASCADE: retention's snapshot GC (DELETE FROM snapshots, foreign_keys = ON) reclaims it.
--
-- WHY ADDITIVE / FORWARD-ONLY (NOT rebuild-required): one new table; nothing dropped and index_runs is
-- NOT cleared. Like migrations 012-021 this advances the schema version (folded into the Phase-9
-- snapshot identity_hash), so every repository re-indexes into a schema-22 snapshot on its next ensure —
-- which is what records coverage for it. LatestSchemaVersion auto-derives from LoadMigrations().Max().

CREATE TABLE snapshot_coverage (
    snapshot_id  INTEGER PRIMARY KEY REFERENCES snapshots(id) ON DELETE CASCADE,
    verdict      TEXT NOT NULL CHECK (verdict IN ('complete', 'partial')),
    summary_json TEXT NOT NULL,
    recorded_at  INTEGER NOT NULL
);
