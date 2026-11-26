# Phase 3 — cheap reclamation

> **Proposal.** This describes a change, not current behaviour.

## Goal
Reclaiming a snapshot or project version is a unit drop that takes seconds, and freed space returns to the OS.

## Scope
- **Layout (decision 5):** one SQLite file per project version, holding its symbols, occurrences, relationships, API surfaces and FTS rows. The catalog keeps the snapshot → project-version mapping, and queries attach the files they need.
- Removing a project version deletes its file. Measure query latency (MCP tool p95) and attach overhead on TextEditor-sized fixtures, to confirm no regression.
- Drop `ix_occ_project` (16 GB in production). Justify `ix_occ_file_version` (16.7 GB) or drop it.
- Space returns to the OS as soon as the file is deleted.
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
