# Database Schema

Sextant stores all indexed data in a SQLite database (`sextant.db`). Both the index builder (writer) and MCP server (reader) interact with it directly. WAL mode ensures concurrent access works correctly.

## Database Configuration

Applied on every connection:

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
```

## Tables

### `projects`

Represents a .NET project (`.csproj`) in the index.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | Auto-increment |
| `canonical_id` | `TEXT UNIQUE NOT NULL` | SHA256 hash of `(git_remote_url, repo_relative_path)`, first 16 hex chars |
| `git_remote_url` | `TEXT NOT NULL` | Normalized HTTPS remote URL |
| `repo_relative_path` | `TEXT NOT NULL` | Path from git root to `.csproj` |
| `disk_path` | `TEXT` | Absolute path on current machine |
| `assembly_name` | `TEXT` | |
| `target_framework` | `TEXT` | |
| `is_test_project` | `INTEGER NOT NULL DEFAULT 0` | Boolean flag |
| `last_indexed_at` | `INTEGER NOT NULL` | Unix epoch ms |

### `symbols`

Every named type and member extracted from the codebase.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | Auto-increment |
| `project_id` | `INTEGER NOT NULL` | FK to `projects.id` |
| `fully_qualified_name` | `TEXT NOT NULL` | Roslyn `ISymbol.ToDisplayString(FullyQualifiedFormat)` |
| `display_name` | `TEXT NOT NULL` | Short name for display and FTS |
| `kind` | `TEXT NOT NULL` | One of: `class`, `interface`, `struct`, `enum`, `delegate`, `record`, `method`, `constructor`, `property`, `field`, `event`, `indexer`, `type_parameter` |
| `accessibility` | `TEXT NOT NULL` | `public`, `internal`, `protected`, `private`, `protected_internal`, `private_protected` |
| `is_static` | `INTEGER NOT NULL DEFAULT 0` | |
| `is_abstract` | `INTEGER NOT NULL DEFAULT 0` | |
| `is_virtual` | `INTEGER NOT NULL DEFAULT 0` | |
| `is_override` | `INTEGER NOT NULL DEFAULT 0` | |
| `signature` | `TEXT` | Full signature string |
| `signature_hash` | `TEXT` | SHA256 of signature for change detection |
| `doc_comment` | `TEXT` | XML doc comment, tags stripped |
| `file_path` | `TEXT NOT NULL` | Source file path |
| `line_start` | `INTEGER NOT NULL` | |
| `line_end` | `INTEGER NOT NULL` | |
| `attributes` | `TEXT` | JSON array of attribute FQNs |
| `last_indexed_at` | `INTEGER NOT NULL` | Unix epoch ms |

### `references`

Every usage of a symbol across the codebase.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` |
| `in_project_id` | `INTEGER NOT NULL` | FK to `projects.id` |
| `file_path` | `TEXT NOT NULL` | |
| `line` | `INTEGER NOT NULL` | |
| `context_snippet` | `TEXT` | ~120 chars of surrounding context |
| `reference_kind` | `TEXT NOT NULL` | `invocation`, `type_ref`, `attribute`, `inheritance`, `override`, `object_creation` |

### `relationships`

Semantic relationships between symbols (inheritance, implementation, overrides).

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `from_symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` |
| `to_symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` |
| `kind` | `TEXT NOT NULL` | `implements`, `inherits`, `overrides`, `instantiates`, `returns`, `parameter_of` |

### `call_graph`

Direct method invocation edges.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `caller_symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` |
| `callee_symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` |
| `call_site_file` | `TEXT NOT NULL` | |
| `call_site_line` | `INTEGER NOT NULL` | |

### `project_dependencies`

Inter-project dependency edges.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `consumer_project_id` | `INTEGER NOT NULL` | FK to `projects.id` |
| `dependency_project_id` | `INTEGER NOT NULL` | FK to `projects.id` |
| `reference_kind` | `TEXT NOT NULL` | `project_ref`, `submodule_ref`, `nuget_ref` |
| `submodule_pinned_commit` | `TEXT` | For submodule references |

### `api_surface_snapshots`

Point-in-time snapshots of public/protected API signatures for breaking change detection.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `project_id` | `INTEGER NOT NULL` | FK to `projects.id` |
| `symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` |
| `signature_hash` | `TEXT NOT NULL` | |
| `captured_at` | `INTEGER NOT NULL` | Unix epoch ms |
| `git_commit` | `TEXT NOT NULL` | HEAD SHA at capture time |

### `file_index`

Tracks file content hashes for incremental indexing.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `project_id` | `INTEGER NOT NULL` | FK to `projects.id` |
| `file_path` | `TEXT NOT NULL` | |
| `content_hash` | `TEXT NOT NULL` | SHA256 of file contents |
| `last_indexed_at` | `INTEGER NOT NULL` | Unix epoch ms |

Unique constraint: `(project_id, file_path)`.

## Full-Text Search

A FTS5 virtual table provides full-text search over symbol names and documentation:

```sql
CREATE VIRTUAL TABLE symbols_fts USING fts5(
  display_name, doc_comment,
  content='symbols', content_rowid='id'
);
```

SQL triggers keep the FTS table in sync with `symbols` on insert, update, and delete.

## Indexes

```sql
CREATE UNIQUE INDEX ix_symbols_fqn ON symbols(fully_qualified_name, project_id);
CREATE INDEX ix_symbols_project_access ON symbols(project_id, accessibility);
CREATE INDEX ix_symbols_file ON symbols(file_path);
CREATE INDEX ix_symbols_kind ON symbols(kind);
CREATE INDEX ix_references_symbol ON references(symbol_id);
CREATE INDEX ix_references_project ON references(in_project_id);
CREATE INDEX ix_references_file ON references(file_path);
CREATE INDEX ix_callgraph_caller ON call_graph(caller_symbol_id);
CREATE INDEX ix_callgraph_callee ON call_graph(callee_symbol_id);
CREATE INDEX ix_callgraph_file ON call_graph(call_site_file);
CREATE INDEX ix_relationships_from ON relationships(from_symbol_id, kind);
CREATE INDEX ix_relationships_to ON relationships(to_symbol_id, kind);
CREATE INDEX ix_file_index_lookup ON file_index(project_id, file_path);
```

## Migrations

Schema changes are managed through hand-written SQL migration scripts in `src/Sextant.Store/Migrations/`, numbered sequentially (`001_initial_schema.sql`, `002_add_file_index.sql`, etc.) and embedded as assembly resources.

A `schema_version` table tracks the current version. On startup, the store applies all unapplied migrations in order. Migrations are forward-only — no rollback support.
