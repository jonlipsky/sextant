-- Issue #160: find_cross_repository_usages took ~410 s per call on the prod service catalog.
--
-- Pure references are occurrences with source_symbol_id IS NULL and make up most of the table (~1.5 M of
-- 2.36 M rows in prod). The old full index ix_occ_source(source_symbol_id) let the heuristic planner (no
-- sqlite_stat1) treat `o.source_symbol_id IS NULL` like a selective equality. With an authorization
-- filter it led every monorepo logical project into `ix_occ_source (source_symbol_id=?)`. For each of
-- them it walked EVERY pure reference: a nested loop of (logical projects x all pure references).
--
-- The index is rebuilt as PARTIAL over call edges only (source_symbol_id IS NOT NULL):
--   * `source_symbol_id IS NULL` predicates can no longer select it. Those queries fall back to their
--     real driver (target_symbol_id / in_project_id / file_version_id) or a sequential scan.
--   * `source_symbol_id = ?` still uses it: CallGraphStore.GetByCaller, and the ON DELETE CASCADE /
--     FK child scans run when a caller symbol is deleted. SQLite proves `x = ?` implies `x IS NOT NULL`.
--   * It stops indexing NULL keys, so it is smaller and pure-reference inserts no longer maintain it.
-- SnapshotDependencyStore additionally forces the cross-repo usage query's join order (CROSS JOIN), so
-- the authorization filter can never lead that plan again, whatever the index set or statistics.
--
-- WHY ADDITIVE / FORWARD-ONLY (NOT rebuild-required): one index is dropped and recreated over the same
-- column. No table or row changes, and index_runs is NOT cleared. Existing data stays servable as-is.
-- Like migrations 012-022 this advances the schema version (folded into the Phase-9 snapshot
-- identity_hash), so a repository's next ensure/full index produces a schema-23 snapshot.
-- LatestSchemaVersion auto-derives from LoadMigrations().Max().

DROP INDEX IF EXISTS ix_occ_source;
CREATE INDEX ix_occ_source ON occurrences(source_symbol_id) WHERE source_symbol_id IS NOT NULL;
