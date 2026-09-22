-- Phase 7: normalize files and compact occurrence storage.
--
-- Source identity is stored ONCE and semantic evidence COMPACTLY:
--   * files            — one repository-relative path per (project, file), never an absolute path.
--   * file_versions    — a file's content identity (SHA-256 as raw BLOB, optional git blob hash).
--   * occurrences      — a single unified usage table that REPLACES both `references` and
--                        `call_graph`. Each row locates a usage by (file_version, line, col),
--                        names its target declaration and (for a call) its enclosing source symbol,
--                        and encodes kind/access as compact integers. No absolute path, no snippet.
--
-- The high-cardinality tables previously repeated an absolute path and a ~120-char context snippet
-- on every row and stored kinds/flags/hashes as text. On a monorepo this bloats the database to
-- multiple gigabytes. This migration removes those repetitions: paths are stored once in `files`,
-- context snippets are reproduced at query time from the exact matching source version, kinds/flags
-- are integers, and hashes are binary.
--
-- REBUILD REQUIRED. The old rows cannot be reinterpreted as valid data for the new schema: there are
-- no file-version identities to attach existing occurrences to, and the source snippets were the only
-- record of reference context (now derived at query time). Like migration 007 this migration is
-- forward-only and clears every derived semantic table so the next FULL index re-populates the compact
-- schema. It also deletes the index_runs ledger so no pre-migration generation is left marked
-- 'complete': after the upgrade `IndexRunStore.GetLastCompleteRun` returns null, so an old index is
-- never mistaken for a complete new-generation index and the reader/writer surface an actionable
-- "schema upgraded — full re-index required" message instead of serving a partially-rebuilt hybrid.
-- On a fresh database every DROP/DELETE below is a no-op. Re-run a full index after upgrading.

-- FTS is external-content over `symbols`; drop it and its sync triggers before symbols is recreated.
DROP TRIGGER IF EXISTS symbols_ai;
DROP TRIGGER IF EXISTS symbols_ad;
DROP TRIGGER IF EXISTS symbols_au;
DROP TABLE IF EXISTS symbols_fts;

-- Drop derived tables in dependency order (children before parents). argument_flow/return_flow
-- referenced call_graph; references/call_graph/relationships/comments referenced symbols. Dropping a
-- table with foreign_keys ON performs an implicit DELETE of its rows, which fires ON DELETE actions on
-- dependents — so api_surface_snapshots is intentionally NOT dropped: dropping `symbols` nulls its
-- soft symbol_id back-pointers (ON DELETE SET NULL) while its self-contained rows (symbol_key/fqn/
-- accessibility captured inline by migration 009) survive this schema change (acceptance criterion 7).
DROP TABLE IF EXISTS argument_flow;
DROP TABLE IF EXISTS return_flow;
DROP TABLE IF EXISTS call_graph;
DROP TABLE IF EXISTS "references";
DROP TABLE IF EXISTS relationships;
DROP TABLE IF EXISTS comments;
DROP TABLE IF EXISTS file_index;
DROP TABLE IF EXISTS symbols;

-- Rebuild gate: no complete generation survives the compact-schema change.
DELETE FROM index_runs;

-- Logical file: the repository-relative path stored ONCE per (project, file). Never absolute for a
-- real index — the indexer stores paths relative to the project's repo root, and the reader
-- reconstructs an absolute path from projects.disk_path at query time.
CREATE TABLE files (
    id                 INTEGER PRIMARY KEY,
    project_id         INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    repo_relative_path TEXT NOT NULL
);

CREATE UNIQUE INDEX ix_files_path ON files(project_id, repo_relative_path);

-- Versioned content of a file. Within one working generation a file has one current version; the
-- content hash is a raw 32-byte SHA-256 (binary, not a 64-char hex string) and gates query-time
-- snippet reproduction (the local file is read only when its hash matches).
CREATE TABLE file_versions (
    id              INTEGER PRIMARY KEY,
    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    content_hash    BLOB NOT NULL,
    git_blob_hash   BLOB,
    source_ref      TEXT,
    last_indexed_at INTEGER NOT NULL
);

CREATE UNIQUE INDEX ix_file_versions_file ON file_versions(file_id, content_hash);

-- Symbols: file identity moves to file_version_id (no absolute file_path); kind and accessibility are
-- compact integer enum ordinals (the store maps them back to the enum for query/display).
CREATE TABLE symbols (
    id INTEGER PRIMARY KEY,
    project_id INTEGER NOT NULL,
    symbol_key TEXT NOT NULL,
    fully_qualified_name TEXT NOT NULL,
    display_name TEXT NOT NULL,
    kind INTEGER NOT NULL,
    accessibility INTEGER NOT NULL,
    is_static INTEGER NOT NULL DEFAULT 0,
    is_abstract INTEGER NOT NULL DEFAULT 0,
    is_virtual INTEGER NOT NULL DEFAULT 0,
    is_override INTEGER NOT NULL DEFAULT 0,
    signature TEXT,
    signature_hash TEXT,
    doc_comment TEXT,
    file_version_id INTEGER REFERENCES file_versions(id) ON DELETE SET NULL,
    line_start INTEGER NOT NULL,
    line_end INTEGER NOT NULL,
    attributes TEXT,
    last_indexed_at INTEGER NOT NULL,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX ix_symbols_key ON symbols(project_id, symbol_key);
CREATE INDEX ix_symbols_fqn_lookup ON symbols(fully_qualified_name, project_id);
CREATE INDEX ix_symbols_project_access ON symbols(project_id, accessibility);
CREATE INDEX ix_symbols_file_version ON symbols(file_version_id);
CREATE INDEX ix_symbols_kind ON symbols(kind);

-- FTS5 over symbol display names + doc comments, kept in sync by triggers (unchanged from 001).
CREATE VIRTUAL TABLE symbols_fts USING fts5(
    display_name, doc_comment,
    content='symbols', content_rowid='id'
);

CREATE TRIGGER symbols_ai AFTER INSERT ON symbols BEGIN
    INSERT INTO symbols_fts(rowid, display_name, doc_comment)
    VALUES (new.id, new.display_name, new.doc_comment);
END;

CREATE TRIGGER symbols_ad AFTER DELETE ON symbols BEGIN
    INSERT INTO symbols_fts(symbols_fts, rowid, display_name, doc_comment)
    VALUES ('delete', old.id, old.display_name, old.doc_comment);
END;

CREATE TRIGGER symbols_au AFTER UPDATE ON symbols BEGIN
    INSERT INTO symbols_fts(symbols_fts, rowid, display_name, doc_comment)
    VALUES ('delete', old.id, old.display_name, old.doc_comment);
    INSERT INTO symbols_fts(rowid, display_name, doc_comment)
    VALUES (new.id, new.display_name, new.doc_comment);
END;

-- Unified occurrence table (replaces references + call_graph).
--   in_project_id    — the USING project (drives scope and the Phase-4 cross-project closure).
--   target_symbol_id — the referenced declaration / callee (inbound-usage cascade anchor).
--   source_symbol_id — the enclosing caller for a call edge; NULL for a pure reference.
--   file_version_id  — the usage location's file version (outbound-usage cascade anchor).
--   line/col         — 1-based line; col disambiguates two calls on one line.
--   kind             — ReferenceKind ordinal (invocation/type_ref/attribute/inheritance/object_creation).
--   flags            — bits 0-1 access kind (0 none, 1 read, 2 write, 3 read/write).
CREATE TABLE occurrences (
    id INTEGER PRIMARY KEY,
    in_project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    target_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    source_symbol_id INTEGER REFERENCES symbols(id) ON DELETE CASCADE,
    file_version_id INTEGER NOT NULL REFERENCES file_versions(id) ON DELETE CASCADE,
    line INTEGER NOT NULL,
    col INTEGER NOT NULL DEFAULT 0,
    kind INTEGER NOT NULL,
    flags INTEGER NOT NULL DEFAULT 0,
    last_indexed_at INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_occ_target ON occurrences(target_symbol_id);
CREATE INDEX ix_occ_source ON occurrences(source_symbol_id);
CREATE INDEX ix_occ_project ON occurrences(in_project_id);
CREATE INDEX ix_occ_file_version ON occurrences(file_version_id);

-- Relationships: kind is now a compact integer enum ordinal.
CREATE TABLE relationships (
    id INTEGER PRIMARY KEY,
    from_symbol_id INTEGER NOT NULL,
    to_symbol_id INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    last_indexed_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (from_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
    FOREIGN KEY (to_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
);

CREATE INDEX ix_relationships_from ON relationships(from_symbol_id, kind);
CREATE INDEX ix_relationships_to ON relationships(to_symbol_id, kind);

-- Comments: file identity moves to file_version_id (no absolute file_path).
CREATE TABLE comments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    file_version_id INTEGER REFERENCES file_versions(id) ON DELETE CASCADE,
    line INTEGER NOT NULL,
    tag TEXT NOT NULL,
    text TEXT NOT NULL,
    enclosing_symbol_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL,
    last_indexed_at INTEGER NOT NULL
);

CREATE INDEX ix_comments_project ON comments(project_id);
CREATE INDEX ix_comments_tag ON comments(tag);
CREATE INDEX ix_comments_file_version ON comments(file_version_id);
CREATE INDEX ix_comments_symbol ON comments(enclosing_symbol_id);

-- Argument/return dataflow now references an occurrence (the call edge) instead of a call_graph row.
CREATE TABLE argument_flow (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    occurrence_id       INTEGER NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE,
    parameter_ordinal   INTEGER NOT NULL,
    parameter_name      TEXT NOT NULL,
    argument_expression TEXT NOT NULL,
    argument_kind       TEXT NOT NULL,
    source_symbol_fqn   TEXT,
    last_indexed_at     INTEGER NOT NULL
);

CREATE INDEX ix_argflow_occurrence ON argument_flow(occurrence_id);
CREATE INDEX ix_argflow_source ON argument_flow(source_symbol_fqn);

CREATE TABLE return_flow (
    id                     INTEGER PRIMARY KEY AUTOINCREMENT,
    occurrence_id          INTEGER NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE,
    destination_kind       TEXT NOT NULL,
    destination_variable   TEXT,
    destination_symbol_fqn TEXT,
    last_indexed_at        INTEGER NOT NULL
);

CREATE INDEX ix_retflow_occurrence ON return_flow(occurrence_id);
CREATE INDEX ix_retflow_destination ON return_flow(destination_symbol_fqn);
