-- Issue #320: a crashed writer must not block its successor, and a crash must be visible.
-- writer_run_state is a SINGLETON companion of writer_lease that records WHO holds (or last held) the lease:
-- host, container, boot, pid namespace, pid, process start and a per-process instance id, so a starting process can
-- prove that a same-host holder is dead and take its lease over instead of waiting out the TTL. It also records
-- whether the last holder shut down cleanly (clean_shutdown, set by WriterLease.Release; cleared on acquire) and
-- why it ended otherwise (end_reason: clean | abandoned | release_failed; NULL = unclean, the process never got to
-- say), which kind of holder it was (holder_kind: service | daemon | cli), and keeps durable counters so an unclean
-- shutdown stays alertable across restarts. The counters live in the catalog, so a restored backup restores them.
-- Service bookkeeping only: the indexer never reads or writes it and no snapshot contains it, so the migration is
-- identity-neutral (IndexDatabase.IdentityNeutralMigrations) and forces no re-index.
CREATE TABLE writer_run_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    owner_token TEXT NOT NULL,
    instance_id TEXT NOT NULL,
    host_id TEXT,
    machine_id TEXT,
    container_id TEXT,
    init_start_ticks INTEGER,
    boot_id TEXT,
    pid_namespace TEXT,
    pid INTEGER,
    process_start_ticks INTEGER,
    holder_kind TEXT,
    heartbeat_at INTEGER NOT NULL,
    clean_shutdown INTEGER NOT NULL DEFAULT 0,
    end_reason TEXT,
    previous_run_unclean INTEGER NOT NULL DEFAULT 0,
    previous_run_heartbeat_at INTEGER,
    previous_run_end_reason TEXT,
    previous_run_kind TEXT,
    unclean_shutdowns_total INTEGER NOT NULL DEFAULT 0,
    abandoned_runs_total INTEGER NOT NULL DEFAULT 0,
    release_failures_total INTEGER NOT NULL DEFAULT 0,
    stale_lease_takeovers_total INTEGER NOT NULL DEFAULT 0
);
