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
| `canonical_id` | `TEXT UNIQUE NOT NULL` | SHA256 hash of `(git_remote_url, repo_relative_path, target_framework)`, first 16 hex chars — each evaluated TFM is a distinct logical project |
| `git_remote_url` | `TEXT NOT NULL` | Normalized HTTPS remote URL |
| `repo_relative_path` | `TEXT NOT NULL` | Path from git root to `.csproj` |
| `disk_path` | `TEXT` | Absolute path on current machine |
| `assembly_name` | `TEXT` | |
| `target_framework` | `TEXT` | Evaluated per-instance TFM; part of project identity |
| `is_test_project` | `INTEGER NOT NULL DEFAULT 0` | Boolean flag |
| `last_indexed_at` | `INTEGER NOT NULL` | Unix epoch ms |
| `evaluation_fingerprint` | `TEXT` | Hash of evaluation inputs (csproj, `Directory.Build.*`, `global.json`, package assets, analyzer/editor config) captured at index time; nullable = unknown/changed (migration `010`) |

### `symbols`

Every named type and member extracted from the codebase.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | Auto-increment |
| `project_id` | `INTEGER NOT NULL` | FK to `projects.id` |
| `symbol_key` | `TEXT NOT NULL` | Stable semantic declaration key — a Roslyn documentation ID (`M:Ns.Type.M(System.Int32)`) when available, else a version-scoped `src:`/`meta:` fallback. This is the identity; the FQN is display/query data. |
| `fully_qualified_name` | `TEXT NOT NULL` | Roslyn `ISymbol.ToDisplayString(FullyQualifiedFormat)`. Display/query data — **not unique** (overloads share it). |
| `display_name` | `TEXT NOT NULL` | Short name for display and FTS |
| `kind` | `INTEGER NOT NULL` | Enum ordinal: `class`=0, `interface`=1, `struct`=2, `enum`=3, `delegate`=4, `record`=5, `method`=6, `constructor`=7, `property`=8, `field`=9, `event`=10, `indexer`=11, `type_parameter`=12. Stored compactly as an integer (Phase 7); the store maps it back to the enum for query/display. |
| `accessibility` | `INTEGER NOT NULL` | Enum ordinal: `public`=0, `internal`=1, `protected`=2, `private`=3, `protected_internal`=4, `private_protected`=5 (Phase 7). |
| `is_static` | `INTEGER NOT NULL DEFAULT 0` | |
| `is_abstract` | `INTEGER NOT NULL DEFAULT 0` | |
| `is_virtual` | `INTEGER NOT NULL DEFAULT 0` | |
| `is_override` | `INTEGER NOT NULL DEFAULT 0` | |
| `signature` | `TEXT` | Full signature string |
| `signature_hash` | `TEXT` | SHA256 of signature for change detection |
| `doc_comment` | `TEXT` | XML doc comment, tags stripped |
| `file_version_id` | `INTEGER` | FK to `file_versions.id`, `ON DELETE SET NULL` (Phase 7). Replaces the absolute `file_path`; the source path is stored once in `files` and reconstructed to an absolute path at query time from `projects.disk_path`. |
| `line_start` | `INTEGER NOT NULL` | |
| `line_end` | `INTEGER NOT NULL` | |
| `attributes` | `TEXT` | JSON array of attribute FQNs |
| `last_indexed_at` | `INTEGER NOT NULL` | Unix epoch ms |

### `files`

The repository-relative path of a source file, stored **once** per `(project, file)` (Phase 7). Replaces the absolute path that `symbols`, `references`, and `call_graph` previously repeated on every row. Never absolute for a real index — the indexer stores paths relative to the project's repo root and the reader reconstructs an absolute path from `projects.disk_path` on demand.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `project_id` | `INTEGER NOT NULL` | FK to `projects.id`, `ON DELETE CASCADE` |
| `repo_relative_path` | `TEXT NOT NULL` | Path from repo root to the source file |

Unique constraint: `(project_id, repo_relative_path)`.

### `file_versions`

The versioned content identity of a `files` row (Phase 7). The content hash is a **raw 32-byte SHA-256 BLOB** (not a 64-char hex string) and gates query-time snippet reproduction: the local file is read for context only when its hash matches. Seeds the incremental fingerprint that `file_index` used to hold.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `file_id` | `INTEGER NOT NULL` | FK to `files.id`, `ON DELETE CASCADE` |
| `content_hash` | `BLOB NOT NULL` | Raw SHA-256 of file contents (binary) |
| `git_blob_hash` | `BLOB` | Optional Git blob hash (binary) |
| `source_ref` | `TEXT` | Optional reference to a committed/compressed source blob |
| `last_indexed_at` | `INTEGER NOT NULL` | Unix epoch ms |

Unique constraint: `(file_id, content_hash)`.

### `occurrences`

Every usage of a symbol across the codebase — the unified table that **replaces both `references` and `call_graph`** (Phase 7). A row locates a usage by `(file_version_id, line, col)`, names its `target_symbol_id` (the referenced/called declaration, in its declaring project) and, for a call, its enclosing `source_symbol_id` (the caller). `in_project_id` is the **using** project.

- A **pure reference** has `source_symbol_id IS NULL`. This projection is byte-for-byte the pre-Phase-7 `references` table (the extractor emits one source-NULL row per usage, including at call sites).
- A **call edge** additionally has `source_symbol_id` set to the enclosing caller — the pre-Phase-7 `call_graph` row.

Cross-project usages have `target_symbol_id` in the dependency project and `in_project_id` in the consumer — the connectivity the incremental closure relies on (`ReferenceStore.GetCrossProjectPairs` selects source-NULL rows whose `in_project_id != symbols.project_id`).

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `in_project_id` | `INTEGER NOT NULL` | FK to `projects.id` `ON DELETE CASCADE` (the using project) |
| `target_symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` `ON DELETE CASCADE` (referenced/called declaration) |
| `source_symbol_id` | `INTEGER` | FK to `symbols.id` `ON DELETE CASCADE`; the enclosing caller for a call edge, `NULL` for a pure reference |
| `file_version_id` | `INTEGER NOT NULL` | FK to `file_versions.id` `ON DELETE CASCADE` (the usage location) |
| `line` | `INTEGER NOT NULL` | 1-based line |
| `col` | `INTEGER NOT NULL DEFAULT 0` | Disambiguates two usages on one line |
| `kind` | `INTEGER NOT NULL` | `ReferenceKind` ordinal: `invocation`=0, `type_ref`=1, `attribute`=2, `inheritance`=3, `override`=4, `object_creation`=5. The document extractor folds `override` into the real occurrence kind (so it does not emit `override`); object-creation/attribute occurrences target the constructed type. |
| `flags` | `INTEGER NOT NULL DEFAULT 0` | Bit-packed. Bits 0–1 = access kind: 0 none, 1 read, 2 write, 3 read/write. |
| `last_indexed_at` | `INTEGER NOT NULL DEFAULT 0` | Unix epoch ms |

Context snippets are **no longer stored**: they are reproduced at query time from the exact matching source version (read the local file only when its content hash matches `file_versions.content_hash`; otherwise return the location without a snippet).

### `relationships`

Semantic relationships between symbols (inheritance, implementation, overrides).

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `from_symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` |
| `to_symbol_id` | `INTEGER NOT NULL` | FK to `symbols.id` |
| `kind` | `INTEGER NOT NULL` | Enum ordinal (Phase 7): `implements`=0, `inherits`=1, `overrides`=2, `instantiates`=3, `returns`=4, `parameter_of`=5. |

### call graph (folded into `occurrences`)

Phase 7 removes the standalone `call_graph` table. A direct method invocation is an `occurrences` row whose `source_symbol_id` (caller) **and** `target_symbol_id` (callee) are both set, located by `(file_version_id, line, col)`. `CallGraphStore` reads/writes those rows (`source_symbol_id IS NOT NULL`).

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

Point-in-time snapshots of public/protected API signatures for breaking change detection. Each snapshot is **self-contained** (migration `009`): it stores the stable `symbol_key`, display FQN, and accessibility inline so a diff never needs to resolve a live symbol row, and historical commit snapshots survive a working-index rebuild (acceptance criterion 7). `symbol_id` is a nullable soft back-pointer to the current generation's row (`ON DELETE SET NULL`) — rebuilding symbols nulls it instead of cascade-deleting the snapshot.

| Column | Type | Description |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY` | |
| `project_id` | `INTEGER NOT NULL` | FK to `projects.id`, `ON DELETE CASCADE` |
| `symbol_id` | `INTEGER` | Nullable FK to `symbols.id`, `ON DELETE SET NULL` |
| `symbol_key` | `TEXT NOT NULL` | Stable semantic key, captured inline |
| `fully_qualified_name` | `TEXT NOT NULL` | Display FQN, captured inline |
| `accessibility` | `TEXT NOT NULL` | Captured inline |
| `signature_hash` | `TEXT NOT NULL` | |
| `captured_at` | `INTEGER NOT NULL` | Unix epoch ms |
| `git_commit` | `TEXT NOT NULL` | HEAD SHA at capture time |

### `file_index` (removed in Phase 7)

Phase 7 removes the standalone `file_index` table. Its role — one content hash per indexed document, seeded during full indexing so a daemon restart can short-circuit unchanged files — moves to `file_versions` (`content_hash`, now a raw SHA-256 BLOB) joined to `files` (`repo_relative_path`). The symbol phase still seeds one `files`/`file_versions` pair per indexed document (acceptance criteria 1–2).

File-content hashes alone do not capture configuration changes (an edited `Directory.Build.props`, `global.json`, restored package assets, or analyzer/editor config leaves every `.cs` byte-identical). The per-project `projects.evaluation_fingerprint` column (migration `010`) hashes those evaluation inputs at index time; startup catch-up recomputes it from disk and escalates a project whose fingerprint changed to a full project rebuild (acceptance criterion 6). A null fingerprint means "unknown" and is treated as changed.

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
CREATE UNIQUE INDEX ix_symbols_key ON symbols(project_id, symbol_key);
CREATE INDEX ix_symbols_fqn_lookup ON symbols(fully_qualified_name, project_id);
CREATE INDEX ix_symbols_project_access ON symbols(project_id, accessibility);
CREATE INDEX ix_symbols_file_version ON symbols(file_version_id);
CREATE INDEX ix_symbols_kind ON symbols(kind);
CREATE UNIQUE INDEX ix_files_path ON files(project_id, repo_relative_path);
CREATE UNIQUE INDEX ix_file_versions_file ON file_versions(file_id, content_hash);
CREATE INDEX ix_occ_target ON occurrences(target_symbol_id);
CREATE INDEX ix_occ_source ON occurrences(source_symbol_id);
CREATE INDEX ix_occ_project ON occurrences(in_project_id);
CREATE INDEX ix_occ_file_version ON occurrences(file_version_id);
CREATE INDEX ix_relationships_from ON relationships(from_symbol_id, kind);
CREATE INDEX ix_relationships_to ON relationships(to_symbol_id, kind);
CREATE INDEX ix_comments_project ON comments(project_id);
CREATE INDEX ix_comments_tag ON comments(tag);
CREATE INDEX ix_comments_file_version ON comments(file_version_id);
CREATE INDEX ix_comments_symbol ON comments(enclosing_symbol_id);
```

**`occurrences` index rationale (Phase 7, `EXPLAIN QUERY PLAN`-verified).** Each of the four `ix_occ_*` indexes backs a foreign-key column so the `ON DELETE CASCADE` from a replaced project/symbol/file-version is index-driven (never a full scan), and each also serves a hot read:

- `ix_occ_target` — `GetBySymbolId` (references to a declaration) + the target cascade.
- `ix_occ_source` — the cross-project-pair closure query (`source_symbol_id IS NULL`) and `GetByCaller` (call edges); backs the source cascade.
- `ix_occ_project` — project scoping + `DeleteByProject`.
- `ix_occ_file_version` — `DeleteByFile` / per-file replacement.

No wall-clock index assertions remain; `PerformanceTests` asserts the chosen plan via `EXPLAIN QUERY PLAN` (deterministic).

## Migrations

Schema changes are managed through hand-written SQL migration scripts in `src/Sextant.Store/Migrations/`, numbered sequentially (`001_initial_schema.sql`, `002_add_file_index.sql`, etc.) and embedded as assembly resources.

A `schema_version` table tracks the current version. On startup, the store applies all unapplied migrations in order. Migrations are forward-only — no rollback support.

### Symbol identity (migration `007`)

Symbols were originally keyed by their display FQN under a unique index on `(fully_qualified_name, project_id)`. Because the display FQN omits parameter lists, overloads, same-named members on different types, and anonymous-object property names (`type`, `description`) collided and overwrote each other. Migration `007_symbol_key_identity.sql` replaces that identity with the stable `symbol_key` column and a unique index on `(project_id, symbol_key)`; the FQN keeps a non-unique lookup index.

This migration is **rebuild-required**: colliding rows in an existing index already lost information and cannot be reinterpreted as valid. The migration clears every derived semantic table (symbols, references, relationships, call graph, dataflow, API snapshots, comments) and `file_index` — preserving only logical `projects` rows — so the next index run re-populates correct keys. On a fresh database the clears are no-ops. Re-run a full index after upgrading.

### Normalize files and compact occurrences (migration `011`)

Migration `011_normalize_files_and_occurrences.sql` is the Phase-7 compaction. It was the schema head through Phase 7 and is **rebuild-required**. It:

- Introduces `files` (repo-relative path stored once) + `file_versions` (raw SHA-256 `content_hash` BLOB, optional `git_blob_hash`/`source_ref`).
- Rebuilds `symbols` to reference `file_version_id` instead of an absolute `file_path`, and stores `kind`/`accessibility` as compact **integer** ordinals instead of text.
- Replaces `references` **and** `call_graph` with one `occurrences` table (source/target symbol, `file_version_id`, `line`/`col`, integer `kind`, bit-packed `flags`); a pure reference is `source_symbol_id IS NULL`, a call edge additionally sets the caller. Context snippets are no longer stored — they are reproduced at query time from the exact matching source version.
- Moves `argument_flow`/`return_flow` onto `occurrence_id`, and rebuilds `relationships`/`comments` with integer `kind` and `file_version_id`.
- Keeps `api_surface_snapshots` intact (self-contained since migration `009`).

**Rebuild gate.** Because the old row shapes cannot be reinterpreted, the migration runs `DELETE FROM index_runs`, dropping the last-complete-generation pointer so an upgraded-but-not-reindexed database is **not** mistaken for a complete index. `IndexDatabase.CheckReadiness()` reports an actionable message in three cases: an older schema ("built by an older Sextant schema … rebuild"), a newer schema ("newer schema … upgrade Sextant"), and a current-schema database with no complete generation and no symbols ("no complete index generation … run a full index"). The CLI query handler, `serve`, and the `get_index_status` MCP tool surface this instead of failing on a missing table. Re-run a full index after upgrading.

### Later additive migrations (`012`–`021`)

Migrations `012` through `021` are all **additive / forward-only** (new tables, indices, or columns only; nothing is dropped and `index_runs` is never cleared), so they are *not* rebuild-required in the destructive sense of `007`/`011`. Each still advances `schema_version`, and because the Phase-9 snapshot-identity hash folds the schema version in, an existing lower-schema base is treated as schema-incompatible and rebuilt into the current schema on the next full run (the safe, expected upgrade path via `IndexDatabase.CheckReadiness`). `LatestSchemaVersion` auto-derives from `LoadMigrations().Max()` and is currently **21**.

- `012_index_run_configuration.sql` — per-run indexing profile + configuration hash (Phase 8).
- `013_immutable_snapshots.sql` — the Phase-9 immutable snapshot catalog: `snapshots` (identity-hashed generations), `branches`, `commits`, branch/commit pointers.
- `014_local_overlay.sql` — Phase-10 git-aware local overlay bookkeeping.
- `015_snapshot_dependencies.sql` — Phase-12 cross-repository `snapshot_dependencies` (consumer→provider) edges + `snapshot_projects` mapping.
- `016_service_job_catalog.sql` — Phase-13 standalone-service job ledger + writer lease (see below).
- `017_snapshot_capability_fingerprint.sql` — Phase-15 worker-capability fingerprint: adds the nullable `snapshots.capability_fingerprint` column (the producing worker's `WorkerCapability.Fingerprint`), recorded in provenance and folded into the snapshot identity only when non-null so a snapshot built under one capability set is never silently reused under an incompatible one (a null value keeps a local/single-node snapshot byte-identical to pre-Phase-15).
- `018_client_contributions.sql` — Phase-16 client/CI contribution ingest: the `snapshot_contributions` assembly ledger and supporting tables for deterministic, multi-environment contribution upload/finalize.
- `019_pull_request_retention_roots.sql` — Phase-17 (slice 2) `pull_request_snapshots` — one `(repository_id, pr_number)` row (UNIQUE) pinning the immutable snapshot an open PR head resolves to, so retention/quota GC never reclaims an open-PR snapshot even when its generation has fallen out of the keep window; only `state = 'open'` rows contribute to the protected set (closing a PR releases protection). The same migration adds `snapshot_contributions.completeness` so a multi-call assembly missing/partial contributions publishes `partial`, never silently `complete` (issue #70).
- `020_audit_log.sql` — Phase-17 (slice 3) durable operational + security `audit_log`: who did what to which repository scope, with what outcome and worker cost. The actor is stored as a **non-reversible hash**, never the raw token.
- `021_branch_head_sequence.sql` — Phase-14 (issue #84) forward-only branch-head advance on the service `/control/ensure` path: adds the nullable `branches.head_sequence` (the highest control-plane head sequence the branch pointer has advanced to). The service advances the pointer + stores the sequence only when the supplied `branch_head_sequence` is strictly greater, so a late/older out-of-order ensure attaches to the immutable snapshot without regressing the branch pointer; a NULL supplied sequence (the local CLI/daemon path) advances unconditionally and never writes the column.

### Standalone index service catalog (migration `016`)

Migration `016_service_job_catalog.sql` adds the durable bookkeeping the Phase-13 standalone index service needs. It **extends** the Phase-9 snapshot catalog rather than duplicating it — the snapshot rows, branch/commit pointers, and dependency edges stay exactly as Phase 9/10/12 left them — by adding a JOB ledger over them plus a cross-process writer lease:

- `snapshot_jobs` — the durable ensure-snapshot request ledger, keyed by `identity_hash` (**UNIQUE**, the same Phase-9 `SnapshotIdentity.Hash` idempotency key), so a repeated ensure for the same snapshot attaches to the ONE existing job/result instead of creating a second (**criterion 1**). `status` is `queued | running | complete | partial | failed | unsupported | cancelled`. `snapshot_id` → `snapshots(id)` `ON DELETE SET NULL` so retention GC of an old snapshot never deletes the job-history row. `owner_token` is the writer-lease token that owns an in-progress job, so a restart can reconcile jobs a dead worker left `running` back to `queued` without touching a job a live writer still owns (**criterion 2**).
- `snapshot_job_diagnostics` — structured, per-project diagnostics for a job (**criterion 5**): one row per affected project with a machine-parseable `code` + `severity` + `message`, so the status API can explain WHICH projects failed and WHY (extends the Phase-9 completeness gate + Phase-8 capability meta). `ON DELETE CASCADE` with its job.
- `writer_lease` — a single-node, cross-process **single-writer** lease (issue #38): a singleton row (`CHECK (id = 1)`) a writer acquires by an atomic conditional upsert under `BEGIN IMMEDIATE`, renews via heartbeat, and releases on shutdown; a stale (expired) lease may be stolen so a crashed holder never wedges the DB. It is a cooperative lease over the existing single-writer-connection + `BEGIN IMMEDIATE` invariant so retention/publish/GC cannot race a live daemon/service, not a replacement for it.

Migration `016` is **additive / forward-only** (see above): only new tables + their indices are created, so it is not rebuild-required.
