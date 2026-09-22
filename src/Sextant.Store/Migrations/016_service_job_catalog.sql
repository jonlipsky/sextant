-- Phase 13: standalone Sextant index service — durable job catalog + single-writer lease.
--
-- Phase 9 made committed generations immutable, atomically-published snapshots keyed by a stable
-- identity_hash (repository + commit + tree + schema + analyzer + config + toolchain); Phase 10/11/12
-- layered overlays, a federated read planner, and submodule dedup on top. Until now there was no
-- persistent DATA PLANE that hosts those snapshots: a client re-ran the whole local indexer. Phase 13
-- adds a standalone service that accepts snapshot requests, ingests worker output, caches, reports
-- status, and answers low-latency semantic queries — independently of ProcessStack.
--
-- This migration adds the bookkeeping that service needs. It EXTENDS the Phase-9 snapshot catalog
-- rather than duplicating it: the durable snapshot rows, branch pointers, and dependency edges stay
-- exactly as Phase 9/10/12 left them; we add a JOB ledger over them plus a cross-process writer lease.
--
--   snapshot_jobs            — the durable ensure-snapshot request ledger. Keyed by identity_hash
--                              (UNIQUE), which is the SAME Phase-9 SnapshotIdentity.Hash idempotency
--                              key, so a repeated ensure request for the same snapshot attaches to the
--                              ONE existing job/result instead of creating a second (acceptance
--                              criterion 1). status is one of queued | running | complete | partial |
--                              failed | unsupported | cancelled. snapshot_id points at the published
--                              Phase-9 snapshot once the job completes (ON DELETE SET NULL so retention
--                              GC of an old snapshot never deletes the job-history row). owner_token is
--                              the writer-lease token that owns an in-progress job, so a service
--                              restart can reconcile jobs left 'running' by a dead worker (criterion 2)
--                              back to 'queued' without touching a job a live writer still owns.
--
--   snapshot_job_diagnostics — structured, per-project diagnostics for a job (acceptance criterion 5).
--                              A partial/failed/unsupported job records one row per affected project
--                              with a machine-parseable code + severity + message, so the status API
--                              can explain WHICH projects failed and WHY (extends the Phase-9
--                              completeness gate + Phase-8 capability meta) instead of a single opaque
--                              failure. ON DELETE CASCADE with its job.
--
--   writer_lease             — a single-node, cross-process SINGLE-WRITER lease (issue #38). Phase 8
--                              added retention but with NO lease: retention, a running daemon, and the
--                              service could each open the DB as a second writer, and recovery could
--                              abandon a live writer's staging generation. The service is the natural
--                              lease owner. It is a SINGLETON row (CHECK (id = 1)); a writer acquires it
--                              by an atomic conditional upsert under BEGIN IMMEDIATE, renews it via
--                              heartbeat, and releases it on shutdown. A stale (expired) lease may be
--                              stolen so a crashed holder never wedges the DB forever. This is a
--                              cooperative lease over the existing single-writer-connection +
--                              BEGIN IMMEDIATE invariant, not a replacement for it (initial deployment
--                              may be single-writer even when multiple analysis workers run).
--
-- WHY ADDITIVE / FORWARD-ONLY (NOT rebuild-required): only new tables + their indices are created;
-- nothing is dropped and the index_runs ledger is NOT cleared. As with migrations 013/014/015 this DOES
-- advance the schema version, which the Phase-9 snapshot-identity gate folds into every snapshot's
-- identity_hash, so an existing schema-15 base is treated as schema-incompatible and rebuilt into a
-- schema-16 base on the first run (via IndexDatabase.CheckReadiness) — the safe, expected upgrade path.
-- No data migration is required.

CREATE TABLE snapshot_jobs (
    id INTEGER PRIMARY KEY,
    identity_hash TEXT NOT NULL UNIQUE,
    repository_url TEXT NOT NULL,
    commit_sha TEXT NOT NULL,
    branch_name TEXT,
    status TEXT NOT NULL,
    snapshot_id INTEGER,
    owner_token TEXT,
    attempts INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    started_at INTEGER,
    completed_at INTEGER,
    last_error TEXT,
    FOREIGN KEY (snapshot_id) REFERENCES snapshots(id) ON DELETE SET NULL
);

CREATE INDEX ix_snapshot_jobs_status ON snapshot_jobs(status);
CREATE INDEX ix_snapshot_jobs_owner ON snapshot_jobs(owner_token);

CREATE TABLE snapshot_job_diagnostics (
    id INTEGER PRIMARY KEY,
    job_id INTEGER NOT NULL,
    project_canonical_id TEXT,
    project_path TEXT,
    severity TEXT NOT NULL,
    code TEXT,
    message TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    FOREIGN KEY (job_id) REFERENCES snapshot_jobs(id) ON DELETE CASCADE
);

CREATE INDEX ix_job_diag_job ON snapshot_job_diagnostics(job_id);

CREATE TABLE writer_lease (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    owner_token TEXT NOT NULL,
    holder TEXT,
    acquired_at INTEGER NOT NULL,
    heartbeat_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL
);
