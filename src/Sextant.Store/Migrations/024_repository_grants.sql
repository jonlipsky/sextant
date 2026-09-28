-- SVC-4: repository grants — which repositories a verified caller may read through the index service.
--
-- A delegate-token request carries a verified caller assertion (SVC-3) naming a tenant (`tid`), an actor
-- (`act`) and, for a user, the full idp-namespaced subject (`sub`). A repository is VISIBLE to a user caller
-- iff a grant exists for (tid, sub, key) or (tid, '*', key); an application caller sees only the tenant-wide
-- '*' grants. Visibility is repository-level: any indexed branch of a visible repository is readable, and the
-- `branch` column only drives indexing and reconcile targets.
--
--   tenant_id      — the caller's verified `tid` (never taken from a request body).
--   principal      — the verified full `sub` of a user grant (`/control/grants/self`), or '*' for a tenant-wide
--                    enrollment (`/control/grants/tenant`, application callers only).
--   repository_key — the SVC-5 canonical repository URL (RepositoryUrlPolicy canonical form, folded by
--                    RemoteUrlIdentity.Normalize): the uniqueness and visibility match key.
--   remote_url     — the first-submitted (policy-validated) spelling, kept on re-PUT, so a reconcile ensure
--                    submits the same spelling and hits the same snapshot identity.
--   branch         — '' = the repository's default branch, else the branch name.
--   source         — 'self' | 'tenant' | 'import' (kept from the first write).
--   created_at / updated_at — unix ms.
--
-- Schema decisions: NO foreign key to `repositories` (watching usually precedes the first index, and creating
-- catalog rows at grant time would perturb the single-repository default selection); the '' / '*' sentinels
-- instead of NULL because SQLite UNIQUE treats NULLs as distinct.
--
-- WHY ADDITIVE / FORWARD-ONLY (NOT rebuild-required): one new table and one index; nothing dropped and
-- index_runs is NOT cleared. Like migrations 012-023 this advances the schema version (folded into the Phase-9
-- snapshot identity_hash), so a repository's next ensure/full index produces a schema-24 snapshot.
-- LatestSchemaVersion auto-derives from LoadMigrations().Max().

CREATE TABLE repository_grants (
    id             INTEGER PRIMARY KEY,
    tenant_id      TEXT NOT NULL,
    principal      TEXT NOT NULL,
    repository_key TEXT NOT NULL,
    remote_url     TEXT NOT NULL,
    branch         TEXT NOT NULL DEFAULT '',
    source         TEXT NOT NULL,
    created_at     INTEGER NOT NULL,
    updated_at     INTEGER NOT NULL,
    UNIQUE (tenant_id, principal, repository_key, branch)
);
CREATE INDEX ix_grants_tenant_repo ON repository_grants(tenant_id, repository_key);
