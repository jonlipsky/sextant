-- Phase 4: decouple API-surface snapshots from mutable symbol rows.
--
-- api_surface_snapshots previously carried only (project_id, symbol_id, signature_hash) and declared
-- symbol_id as REFERENCES symbols(id) ON DELETE CASCADE. Every full re-index deletes and re-inserts
-- every symbol row (new row ids), which cascade-deleted ALL historical snapshots — for every commit,
-- not just the working one — and left the diff tool recovering a snapshot's fqn/accessibility from a
-- symbol id that no longer existed. Historical snapshots must survive a working-index rebuild
-- (acceptance criterion 7), so this migration makes each snapshot self-contained.
--
-- The snapshot now stores the stable semantic key, display fqn, and accessibility inline, so a diff
-- never needs to resolve the live symbol row. symbol_id is kept only as a soft, nullable back-pointer
-- (ON DELETE SET NULL) to the current generation's row: rebuilding symbols nulls it instead of
-- deleting the snapshot. SQLite cannot drop a foreign key in place, so the table is recreated and its
-- rows copied (pulling the stable columns from symbols via a left join). On this stack migration 007
-- already cleared the table, so the copy is a no-op there; the join keeps the migration correct for
-- any database that carried snapshot rows.

CREATE TABLE api_surface_snapshots_new (
    id INTEGER PRIMARY KEY,
    project_id INTEGER NOT NULL,
    symbol_id INTEGER,
    symbol_key TEXT NOT NULL DEFAULT '',
    fully_qualified_name TEXT NOT NULL DEFAULT '',
    accessibility TEXT NOT NULL DEFAULT '',
    signature_hash TEXT NOT NULL,
    captured_at INTEGER NOT NULL,
    git_commit TEXT NOT NULL,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
    FOREIGN KEY (symbol_id) REFERENCES symbols(id) ON DELETE SET NULL
);

INSERT INTO api_surface_snapshots_new
    (id, project_id, symbol_id, symbol_key, fully_qualified_name, accessibility, signature_hash, captured_at, git_commit)
SELECT a.id, a.project_id, a.symbol_id,
       COALESCE(s.symbol_key, ''),
       COALESCE(s.fully_qualified_name, ''),
       COALESCE(s.accessibility, ''),
       a.signature_hash, a.captured_at, a.git_commit
FROM api_surface_snapshots a
LEFT JOIN symbols s ON s.id = a.symbol_id;

DROP TABLE api_surface_snapshots;
ALTER TABLE api_surface_snapshots_new RENAME TO api_surface_snapshots;

CREATE INDEX IF NOT EXISTS ix_api_surface_project ON api_surface_snapshots(project_id, git_commit);
CREATE INDEX IF NOT EXISTS ix_api_surface_symbol ON api_surface_snapshots(symbol_id);
CREATE INDEX IF NOT EXISTS ix_api_surface_key ON api_surface_snapshots(project_id, symbol_key);
