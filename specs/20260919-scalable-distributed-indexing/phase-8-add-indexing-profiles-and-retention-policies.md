# Phase 8 — Add indexing profiles and retention policies

## Goal

Let repositories choose the semantic depth they need while preventing optional data and historical snapshots from growing without bounds.

## Scope

- Define `core`, `standard`, and `deep` indexing profiles.
- Make comments, documentation FTS, argument/return flow, test indexing, generated-source policy, and source-blob retention explicit.
- Record the profile/configuration hash in every index run and snapshot.
- Add retention for superseded local generations, branch snapshots, pull-request snapshots, API history, and source blobs.
- Add CLI/config/service status for enabled features and retained storage.

## Technical design / files

- Extend `SextantConfiguration` with a versioned profile contract.
- `core`: definitions, occurrences, calls, type relationships, project dependencies.
- `standard`: core plus documentation search, comments, tests, and normal agent features.
- `deep`: standard plus detailed dataflow and extended evidence.
- Query tools declare required feature capabilities and return explicit unavailable metadata when omitted.
- Retention never deletes snapshots referenced by configured protected/default branches, open pull requests, submodule pins, or active overlays.

## Acceptance criteria

1. The same input/profile produces a stable configuration hash.
2. Core indexing omits optional tables/work without breaking supported core queries.
3. A query requiring absent data returns a structured feature-unavailable response.
4. Retention dry-run and execution report protected, retained, and deleted generations with reclaimed bytes.
5. Active overlays and dependency pins prevent required snapshot deletion.
6. Profile-specific performance and size results are included in benchmark output.

## Notes / risks / dependencies

- Profiles affect snapshot compatibility and cache reuse.
- Default remains `standard` unless migration data shows a safer compatibility choice.
