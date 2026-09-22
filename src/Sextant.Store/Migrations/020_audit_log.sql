-- Phase 17 (slice 3): durable operational + security audit log.
--
-- Slices 1 and 2 made the standalone index service authorized (fail-closed read policy, per-repository
-- scope), isolated (sandboxed MSBuild evaluation), and recoverable (reconciliation + retention under the
-- single-writer lease). This migration adds the last operational-safety piece the service needs to be
-- pilot-ready: a DURABLE audit log that records who did what to which repository scope, with what
-- outcome, and at what indexing/storage cost. It backs three criterion-5 surfaces at once:
--
--   * a security/operational trail — every ACCEPTED operator control-plane action (ensure, contribute,
--                               retention, backup) and its terminal outcome is durably recorded with a
--                               hashed actor, so operator activity and authorization CHANGES are
--                               reconstructable after the fact;
--   * cost attribution        — index seconds + bytes are attributed to a repository scope, so an
--                               operator can see which repositories drive worker/storage cost;
--   * completeness/health     — terminal ensure outcomes (complete/partial/failed/unsupported) are
--                               recorded alongside the live in-process metrics so a dashboard has a
--                               durable success/completeness history that survives a restart.
--
-- SCOPE OF WHAT IS RECORDED: rows are written on the SERVICE writer path (under the single-writer lease),
-- so the audit log deliberately records accepted operator actions and their outcomes. Per-request query-
-- plane auditing and durable `denied`-decision rows are intentionally NOT emitted from the auth middleware
-- hot path: a write per unauthenticated request would drive the single writer into contention (a DoS
-- amplifier), and the uniform-not-found denial (slice 1) already prevents an unauthorized caller from
-- learning anything. The `query`/`denied` action/outcome vocabulary below is RESERVED for a future
-- out-of-band (non-writer-path) audit sink; today no code writes those rows. See docs/runbooks.md.
--
-- CRITERION-1 LEAKAGE CONTRACT (enforced in code, documented here): audit rows are keyed by a
-- repository scope and therefore reveal repository/snapshot existence and cross-tenant counts. They are
-- OPERATOR-ONLY: every read path is gated behind the CONTROL token (never the query token), so a
-- query-plane tenant can never read another tenant's audit rows. The `actor` column stores a NON-
-- REVERSIBLE hash of the presented token/principal, never the raw secret, so the audit log itself is
-- not a credential store. No source text, symbol content, or raw token is ever written here.
--
-- WHY ADDITIVE / FORWARD-ONLY (NOT rebuild-required): only one new table + its indices are created;
-- nothing is dropped and the index_runs ledger is NOT cleared. As with migrations 012–019 this advances
-- the schema version (folded into every snapshot's identity_hash), so an existing older base is treated
-- as schema-incompatible and rebuilt on first run via IndexDatabase.CheckReadiness — the expected
-- upgrade path. No data migration is required. LatestSchemaVersion auto-derives to 20.

CREATE TABLE audit_log (
    id INTEGER PRIMARY KEY,
    -- Unix epoch milliseconds the event was recorded.
    ts INTEGER NOT NULL,
    -- The operation: ensure | contribute | retention | backup | reconcile are EMITTED today; resolve |
    -- query are RESERVED vocabulary (see the header note — not written from the writer hot path).
    action TEXT NOT NULL,
    -- A NON-REVERSIBLE hash of the presented token/principal (never the raw secret). NULL for the
    -- zero-policy local/dev path where no principal is presented.
    actor TEXT,
    -- The repository scope the action targeted (remote URL). OPERATOR-ONLY: reads are control-token
    -- gated so this is never exposed to a query-plane tenant. NULL for service-wide actions
    -- (backup/restore/retention) that are not scoped to one repository.
    repository_scope TEXT,
    -- accepted | complete | partial | failed | unsupported | error are EMITTED today. `denied` is
    -- RESERVED vocabulary for a future out-of-band audit sink (an authorization refusal would be recorded
    -- WITHOUT confirming the target exists); it is not written from the writer hot path today.
    outcome TEXT NOT NULL,
    -- A short machine-parseable code/message with NO sensitive payload (e.g. 'unauthorized_repository',
    -- 'job_5_complete'). Never source text or a raw token.
    detail TEXT,
    -- Cost attribution: worker index milliseconds and bytes attributed to this event's repository scope.
    -- NULL when the action carries no measurable cost.
    cost_index_ms INTEGER,
    cost_bytes INTEGER
);

CREATE INDEX ix_audit_log_ts ON audit_log(ts);
CREATE INDEX ix_audit_log_action ON audit_log(action);
CREATE INDEX ix_audit_log_scope ON audit_log(repository_scope);
