-- Keep the submitted URL for display, transport and historical snapshot identity.
ALTER TABLE repositories ADD COLUMN repository_key TEXT NOT NULL DEFAULT '';
UPDATE repositories SET repository_key = sextant_repository_key(remote_url);

CREATE TEMP TABLE merged_repositories AS
SELECT id, MIN(id) OVER (PARTITION BY repository_key) AS keep_id FROM repositories;
CREATE UNIQUE INDEX temp.ix_merged_repository ON merged_repositories(id);

UPDATE repositories AS r SET is_provider = (
    SELECT MIN(other.is_provider) FROM repositories other WHERE other.repository_key = r.repository_key)
WHERE r.id IN (SELECT keep_id FROM merged_repositories WHERE id <> keep_id);

CREATE TEMP TABLE merged_commits AS
SELECT c.id, m.keep_id AS repository_id,
       MIN(c.id) OVER (PARTITION BY m.keep_id, c.commit_sha) AS keep_id
FROM commits c JOIN merged_repositories m ON m.id = c.repository_id
WHERE m.keep_id IN (SELECT keep_id FROM merged_repositories WHERE id <> keep_id);
CREATE UNIQUE INDEX temp.ix_merged_commit ON merged_commits(id);
UPDATE commits AS c SET tree_sha = (
    SELECT MAX(other.tree_sha) FROM merged_commits m JOIN commits other ON other.id = m.id WHERE m.keep_id = c.id)
WHERE c.tree_sha IS NULL AND c.id IN (SELECT keep_id FROM merged_commits WHERE keep_id <> id);
UPDATE snapshots SET commit_id = (SELECT keep_id FROM merged_commits WHERE id = commit_id)
WHERE commit_id IN (SELECT id FROM merged_commits WHERE id <> keep_id);
DELETE FROM commits WHERE id IN (SELECT id FROM merged_commits WHERE id <> keep_id);
UPDATE commits SET repository_id = (SELECT repository_id FROM merged_commits WHERE id = commits.id)
WHERE id IN (SELECT id FROM merged_commits WHERE id = keep_id);

CREATE TEMP TABLE merged_projects AS
SELECT p.id, m.keep_id AS repository_id,
       MIN(p.id) OVER (PARTITION BY m.keep_id, p.canonical_id) AS keep_id
FROM logical_projects p JOIN merged_repositories m ON m.id = p.repository_id
WHERE m.keep_id IN (SELECT keep_id FROM merged_repositories WHERE id <> keep_id);
CREATE UNIQUE INDEX temp.ix_merged_project ON merged_projects(id);
UPDATE projects SET logical_project_id = (SELECT keep_id FROM merged_projects WHERE id = logical_project_id)
WHERE logical_project_id IN (SELECT id FROM merged_projects WHERE id <> keep_id);
DELETE FROM logical_projects WHERE id IN (SELECT id FROM merged_projects WHERE id <> keep_id);
UPDATE logical_projects SET repository_id = (SELECT repository_id FROM merged_projects WHERE id = logical_projects.id)
WHERE id IN (SELECT id FROM merged_projects WHERE id = keep_id);

UPDATE snapshots SET repository_id = (SELECT keep_id FROM merged_repositories WHERE id = repository_id)
WHERE repository_id IN (SELECT id FROM merged_repositories WHERE id <> keep_id);
UPDATE snapshot_dependencies SET provider_repository_id =
    (SELECT keep_id FROM merged_repositories WHERE id = provider_repository_id)
WHERE provider_repository_id IN (SELECT id FROM merged_repositories WHERE id <> keep_id);

CREATE TEMP TABLE merged_branches AS
SELECT b.id, m.keep_id,
       ROW_NUMBER() OVER (PARTITION BY m.keep_id, b.name ORDER BY
           (s.status IS 'complete') DESC, b.head_sequence DESC, s.published_at DESC, b.updated_at DESC, b.id DESC) AS rank,
       MAX(b.is_default) OVER (PARTITION BY m.keep_id, b.name) AS was_default,
       MAX(b.head_sequence) OVER (PARTITION BY m.keep_id, b.name) AS head_sequence
FROM branches b JOIN merged_repositories m ON m.id = b.repository_id
LEFT JOIN snapshots s ON s.id = b.snapshot_id
WHERE m.keep_id IN (SELECT keep_id FROM merged_repositories WHERE id <> keep_id);
CREATE UNIQUE INDEX temp.ix_merged_branch ON merged_branches(id);
DELETE FROM branches WHERE id IN (SELECT id FROM merged_branches WHERE rank > 1);
UPDATE branches AS b SET (repository_id, is_default, head_sequence) =
    (SELECT keep_id, was_default, head_sequence FROM merged_branches WHERE id = b.id)
WHERE id IN (SELECT id FROM merged_branches WHERE rank = 1);
UPDATE branches SET is_default = 0 WHERE id IN (
    SELECT id FROM (
        SELECT id, ROW_NUMBER() OVER (PARTITION BY repository_id ORDER BY updated_at DESC, id DESC) AS rank
        FROM branches WHERE is_default = 1 AND id IN (SELECT id FROM merged_branches WHERE rank = 1)
    ) WHERE rank > 1
);

CREATE TEMP TABLE merged_prs AS
SELECT p.id, m.keep_id, ROW_NUMBER() OVER (
    PARTITION BY m.keep_id, p.pr_number ORDER BY p.updated_at DESC, p.id DESC) AS rank
FROM pull_request_snapshots p JOIN merged_repositories m ON m.id = p.repository_id
WHERE m.keep_id IN (SELECT keep_id FROM merged_repositories WHERE id <> keep_id);
CREATE UNIQUE INDEX temp.ix_merged_pr ON merged_prs(id);
DELETE FROM pull_request_snapshots WHERE id IN (SELECT id FROM merged_prs WHERE rank > 1);
UPDATE pull_request_snapshots AS p SET repository_id = (SELECT keep_id FROM merged_prs WHERE id = p.id)
WHERE id IN (SELECT id FROM merged_prs WHERE rank = 1);

CREATE TEMP TABLE merged_retired_heads AS
SELECT m.keep_id, h.branch_name, h.commit_sha, h.retired_at, h.cause, ROW_NUMBER() OVER (
    PARTITION BY m.keep_id, h.branch_name ORDER BY h.retired_at DESC, m.id DESC) AS rank
FROM retired_branch_heads h JOIN merged_repositories m ON m.id = h.repository_id
WHERE m.keep_id IN (SELECT keep_id FROM merged_repositories WHERE id <> keep_id);
DELETE FROM retired_branch_heads WHERE repository_id IN (SELECT keep_id FROM merged_retired_heads);
INSERT INTO retired_branch_heads(repository_id, branch_name, commit_sha, retired_at, cause)
SELECT h.keep_id, h.branch_name, h.commit_sha, h.retired_at, h.cause FROM merged_retired_heads h
WHERE h.rank = 1 AND NOT EXISTS (
    SELECT 1 FROM branches b WHERE b.repository_id = h.keep_id AND b.name = h.branch_name);

DELETE FROM repositories WHERE id IN (SELECT id FROM merged_repositories WHERE id <> keep_id);
CREATE UNIQUE INDEX ix_repositories_key ON repositories(repository_key) WHERE repository_key <> '';
CREATE INDEX ix_snapshot_jobs_snapshot_branch ON snapshot_jobs(snapshot_id, branch_name);

-- Grant and handover keys are already canonical in normal service operation, but older
-- scheme or port spellings must not survive as separate keys.
DELETE FROM repository_grants WHERE id IN (
    SELECT id FROM (
        SELECT id, ROW_NUMBER() OVER (
            PARTITION BY tenant_id, principal, sextant_repository_key(repository_key), branch ORDER BY id) AS rank
        FROM repository_grants
    ) WHERE rank > 1
);
UPDATE repository_grants SET repository_key = sextant_repository_key(repository_key)
WHERE repository_key <> sextant_repository_key(repository_key);
DELETE FROM branch_advance_handovers WHERE rowid IN (
    SELECT rid FROM (
        SELECT rowid AS rid, ROW_NUMBER() OVER (
            PARTITION BY sextant_repository_key(repository_key), branch_name, coalesced_commit
            ORDER BY recorded_at DESC, rowid DESC) AS rank
        FROM branch_advance_handovers
    ) WHERE rank > 1
);
UPDATE branch_advance_handovers SET repository_key = sextant_repository_key(repository_key)
WHERE repository_key <> sextant_repository_key(repository_key);

DROP TABLE merged_retired_heads;
DROP TABLE merged_prs;
DROP TABLE merged_branches;
DROP TABLE merged_projects;
DROP TABLE merged_commits;
DROP TABLE merged_repositories;
