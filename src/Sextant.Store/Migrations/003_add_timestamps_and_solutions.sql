-- Add last_indexed_at to references, relationships, and call_graph tables
ALTER TABLE "references" ADD COLUMN last_indexed_at INTEGER NOT NULL DEFAULT 0;
ALTER TABLE relationships ADD COLUMN last_indexed_at INTEGER NOT NULL DEFAULT 0;
ALTER TABLE call_graph ADD COLUMN last_indexed_at INTEGER NOT NULL DEFAULT 0;

-- Solutions tracking
CREATE TABLE IF NOT EXISTS solutions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    last_indexed_at INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS solution_projects (
    solution_id INTEGER NOT NULL REFERENCES solutions(id) ON DELETE CASCADE,
    project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    PRIMARY KEY (solution_id, project_id)
);

CREATE INDEX IF NOT EXISTS ix_solution_projects_project ON solution_projects(project_id);
