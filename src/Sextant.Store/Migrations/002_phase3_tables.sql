-- Project dependencies table
CREATE TABLE IF NOT EXISTS project_dependencies (
    id INTEGER PRIMARY KEY,
    consumer_project_id INTEGER NOT NULL,
    dependency_project_id INTEGER NOT NULL,
    reference_kind TEXT NOT NULL,
    submodule_pinned_commit TEXT,
    FOREIGN KEY (consumer_project_id) REFERENCES projects(id) ON DELETE CASCADE,
    FOREIGN KEY (dependency_project_id) REFERENCES projects(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_project_deps_consumer ON project_dependencies(consumer_project_id);
CREATE INDEX IF NOT EXISTS ix_project_deps_dependency ON project_dependencies(dependency_project_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_project_deps_unique ON project_dependencies(consumer_project_id, dependency_project_id);

-- API surface snapshots table
CREATE TABLE IF NOT EXISTS api_surface_snapshots (
    id INTEGER PRIMARY KEY,
    project_id INTEGER NOT NULL,
    symbol_id INTEGER NOT NULL,
    signature_hash TEXT NOT NULL,
    captured_at INTEGER NOT NULL,
    git_commit TEXT NOT NULL,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
    FOREIGN KEY (symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_api_surface_project ON api_surface_snapshots(project_id, git_commit);
CREATE INDEX IF NOT EXISTS ix_api_surface_symbol ON api_surface_snapshots(symbol_id);
