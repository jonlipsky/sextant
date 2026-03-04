CREATE TABLE IF NOT EXISTS comments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id      INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    file_path       TEXT NOT NULL,
    line            INTEGER NOT NULL,
    tag             TEXT NOT NULL,           -- 'TODO', 'HACK', 'FIXME', 'BUG', 'NOTE'
    text            TEXT NOT NULL,           -- The comment text after the tag
    enclosing_symbol_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL,
    last_indexed_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_comments_project ON comments(project_id);
CREATE INDEX IF NOT EXISTS ix_comments_tag ON comments(tag);
CREATE INDEX IF NOT EXISTS ix_comments_file ON comments(file_path);
CREATE INDEX IF NOT EXISTS ix_comments_symbol ON comments(enclosing_symbol_id);
