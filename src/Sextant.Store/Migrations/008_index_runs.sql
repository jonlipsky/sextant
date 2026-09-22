-- Phase 3: bounded, batched writes with an atomic generation boundary.
--
-- index_runs records each indexing run as a generation with an explicit lifecycle so a failed or
-- cancelled run never leaves the last complete index ambiguous, and so a process restart can find
-- and abandon staging generations left by a dead process.
--
--   status: 'staging'   — a run is in progress (writes may be partially committed in batches).
--           'complete'  — the run finished and validated; the newest complete run is the current generation.
--           'abandoned' — the run was cancelled, failed, or was left staging by a dead process.
--
-- The newest row with status='complete' is the "last complete index" pointer
-- (MAX(id) WHERE status='complete'). Later phases build immutable snapshot selection on this anchor.

CREATE TABLE IF NOT EXISTS index_runs (
    id INTEGER PRIMARY KEY,
    mode TEXT NOT NULL,                 -- 'full' | 'incremental'
    status TEXT NOT NULL,               -- 'staging' | 'complete' | 'abandoned'
    started_at INTEGER NOT NULL,
    completed_at INTEGER,
    projects INTEGER NOT NULL DEFAULT 0,
    final_db_bytes INTEGER,
    final_wal_bytes INTEGER,
    final_shm_bytes INTEGER,
    peak_staged_bytes INTEGER
);

CREATE INDEX IF NOT EXISTS ix_index_runs_status ON index_runs(status);
