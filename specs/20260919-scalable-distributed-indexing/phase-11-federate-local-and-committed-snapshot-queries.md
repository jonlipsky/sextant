# Phase 11 — Federate local and committed-snapshot queries

## Goal

Make existing MCP tools transparently query the local overlay and committed base without returning shadowed or stale rows.

## Scope

- Add a query planner over local overlay and remote/local cached committed snapshot.
- Define shadowing for files, definitions, occurrences, calls, relationships, and deletes.
- Add remote result paging, caching, timeout, and offline behavior.
- Include snapshot/overlay freshness, completeness, scope, and provenance in MCP metadata.
- Preserve existing tool contracts where results remain unambiguous.

## Technical design / files

- Resolve definitions from overlay first, then base excluding touched/deleted files.
- Union overlay occurrences with unaffected base occurrences.
- Mark queries that may depend on invalidated but not yet re-evaluated reverse dependencies.
- Cache immutable remote query pages/content by snapshot ID.
- Allow explicit local-only, base-only, and federated diagnostic modes.
- Fail closed for authorization and fail transparently to a compatible cached base or full-local index for availability.

## Acceptance criteria

1. No base occurrence from a touched or deleted file appears in a federated result.
2. Changed definitions shadow base definitions deterministically.
3. Offline queries use only compatible cached data and identify its freshness.
4. Existing MCP tool response `meta` includes base snapshot, overlay generation, completeness, and result count.
5. Paging and grouping return the same logical result independent of local/remote partition.
6. Authorization errors are not presented as empty successful results.

## Notes / risks / dependencies

- Renames are represented as delete-plus-add for shadowing even when Git identifies a rename.
- Query behavior during an actively rebuilding overlay must use the last published generation.
