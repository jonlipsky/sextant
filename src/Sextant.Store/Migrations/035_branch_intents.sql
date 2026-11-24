-- Issue #273: durable per-branch request ordering (service tables only; identity-neutral: no snapshot reads them).
--
-- Requests queued before this upgrade are cleared, not converted: pushes are redelivered and callers retry. Every
-- queued_ensures row from now on is admitted together with its branch intent.
DELETE FROM queued_ensures;

-- One branch's queue: every remote branch-moving ensure, refresh, retirement and upstream deletion, in admission order.
-- The AUTOINCREMENT id is the admission order (SQLite serializes inserts through its single writer); only a branch's
-- lowest unsettled intent runs.
CREATE TABLE branch_intents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    repository_id INTEGER NOT NULL,
    branch TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('ensure', 'refresh', 'retire', 'delete')),
    queued_ensure_id INTEGER,
    -- The public id a caller polls (refresh-status); a refresh absorbed into an earlier one answers with its id.
    admission_id TEXT,
    payload_json TEXT,
    state TEXT NOT NULL CHECK (state IN ('pending', 'claimed', 'settled')),
    outcome TEXT,
    admitted_at INTEGER NOT NULL,
    settled_at INTEGER,
    -- Recovery: the in-process turn that holds a claim, and the retry schedule (due time) of an intent whose turn
    -- ended without settling it. A claim owned by a turn that is no longer live is reclaimable.
    claimed_by_turn TEXT,
    attempts INTEGER NOT NULL DEFAULT 0,
    next_attempt_at INTEGER,
    last_error TEXT,
    -- An ensure's own following refresh, admitted in the ensure's transaction directly behind it. An identical ensure
    -- may share this ensure only while every intent admitted after it is that refresh, identified by id.
    following_refresh_id INTEGER,
    FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE
);
-- Each branch's unsettled intents in queue order: head, tail and head discovery read it without sorting.
CREATE INDEX ix_branch_intents_queue
    ON branch_intents(repository_id, branch, id) WHERE state <> 'settled';
-- Retention's bounded oldest-first scan of settled intents.
CREATE INDEX ix_branch_intents_settled_at
    ON branch_intents(settled_at, id) WHERE state = 'settled';
CREATE UNIQUE INDEX ux_branch_intents_admission_id
    ON branch_intents(admission_id) WHERE admission_id IS NOT NULL;
CREATE UNIQUE INDEX ux_branch_intents_queued_ensure_id
    ON branch_intents(queued_ensure_id) WHERE queued_ensure_id IS NOT NULL;

-- A refresh's marker: its retry state, without a commit hint (a refresh indexes the head GitHub reports).
CREATE TABLE pending_refreshes (
    repository_id INTEGER NOT NULL,
    branch TEXT NOT NULL,
    admission_id TEXT NOT NULL UNIQUE,
    admitted_at INTEGER NOT NULL,
    claimed_by_turn TEXT,
    claimed_at INTEGER,
    attempts INTEGER NOT NULL DEFAULT 0,
    next_attempt_at INTEGER NOT NULL,
    last_error TEXT,
    FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE
);
CREATE INDEX ix_pending_refreshes_branch
    ON pending_refreshes(repository_id, branch);
CREATE INDEX ix_pending_refreshes_order
    ON pending_refreshes(next_attempt_at, admitted_at, admission_id);

-- Completed refresh outcomes, kept for the seven-day status window.
CREATE TABLE completed_refresh_admissions (
    admission_id TEXT PRIMARY KEY,
    repository_id INTEGER NOT NULL,
    branch TEXT NOT NULL,
    completed_at INTEGER NOT NULL,
    snapshot_id INTEGER NOT NULL,
    commit_sha TEXT NOT NULL,
    FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE
);
CREATE INDEX ix_completed_refresh_admissions_retention
    ON completed_refresh_admissions(completed_at);
