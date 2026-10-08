-- Issue #273: a branch-advance ensure coalesced while queued (a newer push of its branch superseded it) never moves the
-- branch head, so whatever expected its commit as the head must expect what it expected instead. One row per coalesced
-- (repository, branch, commit) records that handover durably, so a retried or re-submitted successor still resolves it.
-- It applies only while that commit's job (coalesced_identity) is still coalesced: once built, it may have moved the head.
-- Service bookkeeping only: the indexer never reads or writes it, so the migration is identity-neutral
-- (IndexDatabase.IdentityNeutralMigrations) and forces no re-index.
CREATE TABLE branch_advance_handovers (
    repository_key       TEXT NOT NULL,
    branch_name          TEXT NOT NULL,
    coalesced_commit     TEXT NOT NULL,
    coalesced_identity   TEXT NOT NULL,
    expected_head_commit TEXT NOT NULL,
    successor_commit     TEXT NOT NULL,
    recorded_at          INTEGER NOT NULL,
    PRIMARY KEY (repository_key, branch_name, coalesced_commit)
);
CREATE INDEX ix_branch_advance_handovers_recorded_at ON branch_advance_handovers(recorded_at);
