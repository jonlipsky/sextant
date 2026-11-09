-- Issue #406: an ensure that was admitted but has not started must survive a service restart.
-- queued_ensures holds one row per admitted ensure request, written at admission and deleted when the ensure ends
-- (or left in place when shutdown, a lost lease or a transient requeue ended it, so the next instance re-admits it).
-- seq is the in-memory admission sequence, so a restart replays the requests in their original order; the lane
-- (expedited default-branch vs normal) is recomputed from the request on replay. The actor is already hashed
-- (AuditLogStore.HashActor), never a raw token. A user caller's row also keeps the tenant id and subject (the same
-- identifiers repository_grants already stores, never a token), so a replay can re-check the grant.
-- admitted_at (unix ms) bounds how long a row is replayed.
-- Service bookkeeping only: the indexer never reads or writes it and no snapshot contains it, so the migration is
-- identity-neutral (IndexDatabase.IdentityNeutralMigrations) and forces no re-index.
CREATE TABLE queued_ensures (
    seq INTEGER PRIMARY KEY,
    identity_hash TEXT NOT NULL,
    request_json TEXT NOT NULL,
    restricts_implicit_default INTEGER NOT NULL DEFAULT 0,
    lookup_tenant TEXT,
    hashed_actor TEXT,
    detail_suffix TEXT,
    caller_tenant TEXT,
    caller_subject TEXT,
    admitted_at INTEGER NOT NULL
);
