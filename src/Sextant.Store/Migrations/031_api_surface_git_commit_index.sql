-- Issue #303: retention discovers API-history commits with GROUP BY git_commit, MAX(captured_at) and deletes a
-- commit's rows by git_commit. The only index leading with git_commit was (project_id, git_commit), so both scanned
-- the whole table. (git_commit, captured_at) serves the grouping as a covering index scan and the per-commit delete
-- as a seek. Index-only: identity-neutral (IndexDatabase.IdentityNeutralMigrations), no re-index.
CREATE INDEX IF NOT EXISTS ix_api_surface_git_commit ON api_surface_snapshots(git_commit, captured_at);
