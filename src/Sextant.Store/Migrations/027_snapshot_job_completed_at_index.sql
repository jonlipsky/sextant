-- Issue #256: recent-window job health is selected by completion time, not registration order.
-- This index bounds the rolling terminal-outcome query without changing snapshot data or identity.
CREATE INDEX ix_snapshot_jobs_completed_at ON snapshot_jobs(completed_at);
