# Phase 7 — Normalize files and compact occurrence storage

## Goal

Reduce database size and index maintenance by storing source identity once and representing semantic evidence compactly.

## Scope

- Introduce logical files and versioned file-content records.
- Replace repeated absolute paths with integer file-version IDs and repository-relative paths.
- Replace per-reference snippets with source spans and query-time context retrieval.
- Unify reference and call-site location storage where query semantics permit.
- Encode kinds and flags compactly.
- Store hashes as binary values.
- Review and consolidate secondary indexes using measured query plans.
- Provide a forward migration/new-generation path and compatibility views or adapters.

## Technical design / files

- Add a numbered migration for the compact schema or a versioned database generation.
- `files` stores logical repository-relative path; `file_versions` stores content/Git blob hash and optional compressed source reference.
- `occurrences` stores source/enclosing symbol, target symbol key, file version, span, line cache, kind, and flags.
- Call/dataflow details reference an occurrence instead of repeating call-site paths.
- Query-time source context reads the local file only when its content hash matches; otherwise it uses a matching committed source blob or returns location without snippet.
- Use `EXPLAIN QUERY PLAN` and benchmark queries before removing an index.

## Acceptance criteria

1. No semantic table stores an absolute source path.
2. One logical file path is stored once per relevant scope, not once per occurrence.
3. Reference context is reproduced from an exact matching source version.
4. The compact database passes all existing MCP query tests.
5. Final database size and write volume are reported against the phase-1 baseline.
6. Opening an old index provides an actionable rebuild/migration message and cannot be mistaken for a complete new-generation index.

## Notes / risks / dependencies

- Source snippets are best-effort when source content is unavailable; location and completeness metadata remain valid.
- Correct schema normalization may increase join count, so covering indexes are driven by MCP query traces.
