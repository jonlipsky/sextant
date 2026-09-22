# Phase 9 — Introduce immutable repository snapshots

## Goal

Represent committed semantic state as immutable, versioned snapshots instead of overwriting one mutable project row.

## Scope

- Add repositories, commits, branch pointers, logical projects, project versions, snapshots, and snapshot-project mappings.
- Include schema, analyzer, profile/configuration, and toolchain fingerprints.
- Publish snapshots atomically with explicit completeness status.
- Provide whole-generation reader isolation: a reader never observes a hybrid/partially-published state across the index during a full rebuild (the guarantee deferred from Phase 3), delivered via immutable-snapshot isolation rather than in-place table replacement.
- Preserve API and semantic history independently of current mutable rows.
- Add compatibility and rebuild behavior for local single-repository operation.

## Technical design / files

- Keep logical project identity based on repository and repository-relative project path, adding TFM.
- Key the first implementation's project version conservatively by commit and evaluation fingerprint.
- Branch tables point to snapshots; semantic rows do not use branch names as identity.
- Build snapshot rows in a pending generation, validate counts/references/completeness, then publish in one transaction.
- A failed/partial snapshot remains diagnosable but is not selected as a complete branch head.
- Query APIs accept optional repository/commit/branch/snapshot scope.

## Acceptance criteria

1. Two commits of the same project can coexist without overwriting definitions or occurrences.
2. A branch pointer can advance and roll back without mutating an existing snapshot.
3. Snapshot publication is atomic and idempotent.
4. Schema/analyzer/profile/toolchain incompatibility prevents unsafe reuse.
5. Historical API comparisons survive local or remote reindexing.
6. Existing local users can query the selected current snapshot without specifying scope explicitly.
7. A reader never observes a hybrid or partially-published state during a full rebuild (whole-generation reader isolation, deferred from Phase 3), enforced by snapshot pinning / generation-scoped reads over Phase 3's `index_runs` ledger and version model.

## Notes / risks / dependencies

- Content-addressed project reuse across separate commits may begin conservatively and improve later.
- Immutable data increases retention requirements, addressed by phase 8 policies.
- Builds directly on Phase 3's `index_runs` generation ledger and version model: Phase 3 establishes the atomic last-complete-run pointer, per-batch atomic replacement, and staging/recovery lifecycle; Phase 9 replaces in-place table replacement with immutable snapshots to add the whole-generation reader isolation deferred from Phase 3.
