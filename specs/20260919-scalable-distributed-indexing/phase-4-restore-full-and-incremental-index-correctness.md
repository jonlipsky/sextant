# Phase 4 — Restore full and incremental index correctness

## Goal

Make full indexing and every supported file/project change converge on the same correct semantic state.

## Scope

- Populate `file_index` during initial indexing.
- Replace data by project/file generation rather than deleting cross-project occurrences in phase order.
- Recreate definitions, references, calls, relationships, comments, and enabled dataflow for changed files.
- Handle file creation, deletion, rename, linked files, generated-file transitions, and project removal.
- Detect project/import/global configuration changes and escalate invalidation.
- Re-resolve reverse dependents when a public or binding-relevant declaration changes.
- Preserve historical snapshots independently of mutable current definitions.
- Add full-versus-incremental differential tests.

## Technical design / files

- Define one contribution model used by both full and incremental paths.
- `IndexOrchestrator` generates contributions for all inputs; `IncrementalIndexer` generates the same contribution shape for an invalidated subset.
- Replacement deletes prior contributions by owner file/project-version ID, then inserts the new batch atomically.
- Startup catch-up compares content/evaluation fingerprints and deleted-path sets.
- Dependent invalidation uses old and new semantic keys/signatures captured before replacement.

## Acceptance criteria

1. Initial indexing records every indexed file fingerprint.
2. Restarting an unchanged daemon schedules no semantic reindex work.
3. Full indexing and a sequence of incremental edits produce equivalent canonical database output.
4. Cross-project references survive project processing order and file replacement.
5. Changed or deleted declarations remove stale inbound/outbound edges and rebuild required dependents.
6. Renames, linked files, project removal, `Directory.Build.*`, `global.json`, package assets, and analyzer-config changes have tested invalidation behavior.
7. Historical commit snapshots do not cascade-delete when the working index is rebuilt.

## Notes / risks / dependencies

- Conservative project-level invalidation is acceptable before fine-grained dependency invalidation is proven.
- Correctness takes precedence over minimal incremental work.
- This phase should land before the extraction rewrite so old and new extractors share a trusted replacement model.
