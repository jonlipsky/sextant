-- Issue #196 (SX-7b): bound the per-call cost of the service's federated `search_symbols`.
--
-- search_symbols pages each snapshot with a case-insensitive name-prefix match. Before this migration the page
-- query walked the snapshot's symbols in id order and tested `display_name LIKE 'prefix%'` on every row, so a
-- prefix that matched nothing read every symbol of every snapshot a page visited.
--
--   * ix_symbols_project_name_nocase orders each project version's symbols by display_name under NOCASE (the
--     same ASCII-only case folding as SQLite's LIKE), then by rowid (symbols.id, implicit in every index). The
--     page query turns the prefix into a range on that key (`display_name COLLATE NOCASE >= @lo AND < @hi`) and
--     walks the index in (project, name, id) order, so a page costs a seek plus the rows it examines.
--   * ix_repositories_remote_url_nocase lets the search resolve the caller's visible repositories with one
--     batched range lookup over their grant keys instead of loading every repository of the catalog.
--
-- WHY IDENTITY-NEUTRAL (NOT rebuild-required, NO re-index): indexes only. No table or row changes, and
-- index_runs is NOT cleared. Unlike 012-024, this migration is listed in IndexDatabase.IdentityNeutralMigrations:
-- it advances the DATABASE schema (LatestSchemaVersion = 25, what CheckReadiness and backups compare) but not
-- IndexDatabase.SnapshotSchemaVersion (still 24), the value folded into the Phase-9 snapshot identity_hash and
-- compared by reuse/compatibility checks. Every published snapshot keeps its identity and is reused as-is.

CREATE INDEX ix_symbols_project_name_nocase ON symbols(project_id, display_name COLLATE NOCASE);
CREATE INDEX ix_repositories_remote_url_nocase ON repositories(remote_url COLLATE NOCASE);
