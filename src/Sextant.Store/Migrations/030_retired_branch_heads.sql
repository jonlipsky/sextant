-- Issue #299: the service's idle-branch rule deletes a non-default branch pointer that has not moved for days, while the
-- branch still exists in git. The next push of that branch sends expected_head_commit = its `before`, the retired head,
-- which a missing branch would never match. One row per retired (repository, branch) records that head, so an advance
-- expecting exactly it re-creates the branch (SnapshotStore.BranchHeadMatches). Creating the branch consumes the row,
-- a branch-delete retire of the missing branch consumes it too, and the maintenance loop prunes rows past their window.
-- Service bookkeeping only: the indexer never reads or writes it, so the migration is identity-neutral
-- (IndexDatabase.IdentityNeutralMigrations) and forces no re-index.
CREATE TABLE retired_branch_heads (
    repository_id INTEGER NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    branch_name   TEXT NOT NULL,
    commit_sha    TEXT NOT NULL,
    retired_at    INTEGER NOT NULL,
    cause         TEXT NOT NULL,
    PRIMARY KEY (repository_id, branch_name)
);
CREATE INDEX ix_retired_branch_heads_retired_at ON retired_branch_heads(retired_at);
