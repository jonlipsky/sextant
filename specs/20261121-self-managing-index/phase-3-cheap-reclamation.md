# Phase 3 — cheap reclamation

## Goal
Reclaiming a snapshot or project version is a unit drop that takes seconds, and freed space returns to the OS.

## Scope
- **Design gate** (decision 5): prototype and measure (a) one attached SQLite file per project version against (b) `WITHOUT ROWID` clustering on `(project_version_id, …)`. Measure drop time, query latency (MCP tool p95), write throughput and file count, on TextEditor-sized fixtures.
- Implement the winner for occurrences and symbols (about 85% of bytes), then relationships, API surfaces and FTS.
- Drop `ix_occ_project` (16 GB in production). Justify `ix_occ_file_version` (16.7 GB) or drop it.
- Return space: incremental vacuum after reclaim passes (already enabled), or delete the file under (a).
- Migration from the current layout. A fresh-catalog rebuild is acceptable, since Phase 0 proved recovery.

## Testing seams
- **Storage harness:** drop time and file size before and after.
- **MCP tool latency** regression check.
- **Differential equivalence:** query answers identical before and after the layout change.

## Acceptance criteria
1. Dropping a TextEditor-sized snapshot takes ≤ 10 s.
2. The file shrinks by the dropped bytes after the pass.
3. No MCP p95 regression greater than 10%.

## Demo
Index 3 commits of a large fixture, then drop the oldest; time it, and show the file size before and after.
