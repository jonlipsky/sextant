# Phase 8 — Reuse unchanged projects across commits

## Goal

Every service snapshot today re-extracts every project, even when a push changed one file. Reuse the previous
snapshot's project versions for projects whose inputs did not change, so a typical push re-extracts only the changed
projects and their dependents. Target: a one-leaf-project change publishes in ≤ 25% of a full index.

This phase starts with a **design gate**: a short design doc, reviewed before implementation, because it touches the
immutability and identity model.

## Scope

- **Project-version fingerprint** (Merkle-style): hashes of the project's source documents, its
  `EvaluationFingerprint` (project file, imports, `global.json`, assets file), resolved package versions, the snapshot
  identity components (analyzer, config, toolchain, capability, SDK-pin, restore policy), and the fingerprints of every
  referenced project. A change in a dependency invalidates its consumers.
- **Base selection:** the newest complete snapshot of the same repository and branch (or the default branch) whose
  identity components match.
- **Reuse mechanics:** map an unchanged project-version row into the new snapshot, as overlays already do
  (`MapProject`, `IndexOrchestrator.cs:589-599`), instead of re-extracting it. Still load and compile everything the
  changed projects need (loading stays full; extraction becomes partial).
- **Occurrence targets:** today occurrences reference symbol rows of a specific project version
  (`occurrences.target_symbol_id`). With directed invalidation (consumers of a changed project re-extracted, the
  changed project's dependents re-pointed) every reused consumer's targets must still resolve: decide between (a) the
  undirected closure the daemon uses (simple, but in ProcessStack likely most of the graph), (b) re-extracting all
  consumers of a changed project (directed closure), or (c) resolving cross-project targets by stable `symbol_key` at
  query time. The design gate picks one with measurements on ProcessStack's real project graph.
- **Retention:** shared project-version rows need reference counting across snapshots (the same concern overlays and
  provider snapshots already raise, #47/#53).
- **Coverage provenance:** a reused project's coverage and binding health come from its base; the published coverage
  says which projects were reused and from which snapshot.

## Technical design / files

- `Sextant.Indexer/IndexOrchestrator.cs:218-339, 440-536, 589-608` (identity, providers, overlay mapping, project
  upsert), `IncrementalIndexer.cs:166-194`, `ProjectClosure.cs`, `EvaluationFingerprint.cs:28-86`,
  `ProjectStore.cs:58-88`, `docs/schema.md:90-103`, `docs/architecture.md` (local overlay and query merge), #44.

## Acceptance criteria

1. Design doc approved (closure model, retention, provenance, identity effect).
2. For a commit that changes one leaf project of ProcessStack, extraction touches only that project and its required
   dependents, and the job publishes in ≤ 25% of a full index (threshold confirmed or revised at the gate).
3. **Equivalence:** for a sample of commits, every MCP tool returns identical results on the reused snapshot and on a
   from-scratch full index of the same commit (an automated differential test).
4. Retention never deletes a project version a live snapshot references.

## Notes / risks / dependencies

- Largest and riskiest phase; benefits multiply with Phase 7 (fewer jobs) and Phase 6 (cheap checkout/restore).
- Depends on Phases 1–4 (measurement, and a fast full index as the fallback and the equivalence oracle).
