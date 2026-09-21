-- Phase 17 (slice 3): durable operational + security audit log.
--
-- Slices 1 and 2 made the standalone index service authorized (fail-closed read policy, per-repository
-- scope), isolated (sandboxed MSBuild evaluation), and recoverable (reconciliation + retention under the
-- single-writer lease). This migration adds the last operational-safety piece the service needs to be
-- pilot-ready: a DURABLE audit log that records who did what to which repository scope, with what
-- outcome, and at what indexing/storage cost. It backs three criterion-5 surfaces at once:
--
--   * a security audit trail  — every control/query decision (accepted vs denied) is durably recorded,
--                               so a cross-tenant probe or an authorization change is reconstructable
--                               after the fact;
--   * cost attribution        — index seconds + bytes are attributed to a repository scope, so an
--                               operator can see which repositories drive worker/storage cost;
--   * completeness/health     — terminal ensure outcomes (complete/partial/failed/unsupported) are
--                               recorded alongside the live in-process metrics so a dashboard has a
--                               durable success/completeness history that survives a restart.
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
    -- The operation: ensure | contribute | retention | resolve | query | backup | restore | reconcile.
    action TEXT NOT NULL,
    -- A NON-REVERSIBLE hash of the presented token/principal (never the raw secret). NULL for the
    -- zero-policy local/dev path where no principal is presented.
    actor TEXT,
    -- The repository scope the action targeted (remote URL). OPERATOR-ONLY: reads are control-token
    -- gated so this is never exposed to a query-plane tenant. NULL for service-wide actions
    -- (backup/restore/retention) that are not scoped to one repository.
    repository_scope TEXT,
    -- accepted | denied | complete | partial | failed | unsupported | error. `denied` is an
    -- authorization refusal (recorded WITHOUT confirming the target exists — see the code path).
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
