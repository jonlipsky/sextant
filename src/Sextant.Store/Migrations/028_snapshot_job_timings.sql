-- Issue #267: per-job phase and per-project timings (wall and CPU time per indexing phase, the slowest projects,
-- BuildHost process accounting). Job telemetry only: nothing a snapshot contains reads or depends on this table, so the
-- migration is identity-neutral (IndexDatabase.IdentityNeutralMigrations) and forces no re-index.
CREATE TABLE snapshot_job_timings (
    job_id       INTEGER PRIMARY KEY REFERENCES snapshot_jobs(id) ON DELETE CASCADE,
    recorded_at  INTEGER NOT NULL,
    timings_json TEXT NOT NULL
);
CREATE INDEX ix_snapshot_job_timings_recorded_at ON snapshot_job_timings(recorded_at);
