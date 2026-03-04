-- Projects table
CREATE TABLE IF NOT EXISTS projects (
    id INTEGER PRIMARY KEY,
    canonical_id TEXT UNIQUE NOT NULL,
    git_remote_url TEXT NOT NULL,
    repo_relative_path TEXT NOT NULL,
    disk_path TEXT,
    assembly_name TEXT,
    target_framework TEXT,
    is_test_project INTEGER NOT NULL DEFAULT 0,
    last_indexed_at INTEGER NOT NULL
);

-- Symbols table
CREATE TABLE IF NOT EXISTS symbols (
    id INTEGER PRIMARY KEY,
    project_id INTEGER NOT NULL,
    fully_qualified_name TEXT NOT NULL,
    display_name TEXT NOT NULL,
    kind TEXT NOT NULL,
    accessibility TEXT NOT NULL,
    is_static INTEGER NOT NULL DEFAULT 0,
    is_abstract INTEGER NOT NULL DEFAULT 0,
    is_virtual INTEGER NOT NULL DEFAULT 0,
    is_override INTEGER NOT NULL DEFAULT 0,
    signature TEXT,
    signature_hash TEXT,
    doc_comment TEXT,
    file_path TEXT NOT NULL,
    line_start INTEGER NOT NULL,
    line_end INTEGER NOT NULL,
    attributes TEXT,
    last_indexed_at INTEGER NOT NULL,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
);

-- References table
CREATE TABLE IF NOT EXISTS "references" (
    id INTEGER PRIMARY KEY,
    symbol_id INTEGER NOT NULL,
    in_project_id INTEGER NOT NULL,
    file_path TEXT NOT NULL,
    line INTEGER NOT NULL,
    context_snippet TEXT,
    reference_kind TEXT NOT NULL,
    FOREIGN KEY (symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
    FOREIGN KEY (in_project_id) REFERENCES projects(id) ON DELETE CASCADE
);

-- Relationships table
CREATE TABLE IF NOT EXISTS relationships (
    id INTEGER PRIMARY KEY,
    from_symbol_id INTEGER NOT NULL,
    to_symbol_id INTEGER NOT NULL,
    kind TEXT NOT NULL,
    FOREIGN KEY (from_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
    FOREIGN KEY (to_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
);

-- Call graph table
CREATE TABLE IF NOT EXISTS call_graph (
    id INTEGER PRIMARY KEY,
    caller_symbol_id INTEGER NOT NULL,
    callee_symbol_id INTEGER NOT NULL,
    call_site_file TEXT NOT NULL,
    call_site_line INTEGER NOT NULL,
    FOREIGN KEY (caller_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
    FOREIGN KEY (callee_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
);

-- File index table (for incremental indexing)
CREATE TABLE IF NOT EXISTS file_index (
    id INTEGER PRIMARY KEY,
    project_id INTEGER NOT NULL,
    file_path TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    last_indexed_at INTEGER NOT NULL,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_file_index_lookup ON file_index(project_id, file_path);

-- FTS5 virtual table for symbol search
CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
    display_name, doc_comment,
    content='symbols', content_rowid='id'
);

-- FTS sync triggers
CREATE TRIGGER IF NOT EXISTS symbols_ai AFTER INSERT ON symbols BEGIN
    INSERT INTO symbols_fts(rowid, display_name, doc_comment)
    VALUES (new.id, new.display_name, new.doc_comment);
END;

CREATE TRIGGER IF NOT EXISTS symbols_ad AFTER DELETE ON symbols BEGIN
    INSERT INTO symbols_fts(symbols_fts, rowid, display_name, doc_comment)
    VALUES ('delete', old.id, old.display_name, old.doc_comment);
END;

CREATE TRIGGER IF NOT EXISTS symbols_au AFTER UPDATE ON symbols BEGIN
    INSERT INTO symbols_fts(symbols_fts, rowid, display_name, doc_comment)
    VALUES ('delete', old.id, old.display_name, old.doc_comment);
    INSERT INTO symbols_fts(rowid, display_name, doc_comment)
    VALUES (new.id, new.display_name, new.doc_comment);
END;

-- Indexes
CREATE UNIQUE INDEX IF NOT EXISTS ix_symbols_fqn ON symbols(fully_qualified_name, project_id);
CREATE INDEX IF NOT EXISTS ix_symbols_project_access ON symbols(project_id, accessibility);
CREATE INDEX IF NOT EXISTS ix_symbols_file ON symbols(file_path);
CREATE INDEX IF NOT EXISTS ix_symbols_kind ON symbols(kind);
CREATE INDEX IF NOT EXISTS ix_references_symbol ON "references"(symbol_id);
CREATE INDEX IF NOT EXISTS ix_references_project ON "references"(in_project_id);
CREATE INDEX IF NOT EXISTS ix_references_file ON "references"(file_path);
CREATE INDEX IF NOT EXISTS ix_callgraph_caller ON call_graph(caller_symbol_id);
CREATE INDEX IF NOT EXISTS ix_callgraph_callee ON call_graph(callee_symbol_id);
CREATE INDEX IF NOT EXISTS ix_callgraph_file ON call_graph(call_site_file);
CREATE INDEX IF NOT EXISTS ix_relationships_from ON relationships(from_symbol_id, kind);
CREATE INDEX IF NOT EXISTS ix_relationships_to ON relationships(to_symbol_id, kind);
