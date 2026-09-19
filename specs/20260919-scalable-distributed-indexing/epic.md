# GitHub issue plan

The repository currently has only GitHub's default labels. Canonical labels are proposed below; filing falls back to the existing `enhancement` label unless the repository label taxonomy is bootstrapped first.

Use native GitHub sub-issue relationships when available and retain the body checklists as a readable fallback.

## Umbrella epic

### Title

`[Epic] Scalable and distributed semantic indexing`

### Proposed labels

`epic`, `type:feature`, `foundation`
Fallback: `enhancement`

### Body

Sextant's current semantic indexing pipeline does not complete reliably on large solutions and monorepos. It also stores mutable project state in a form that cannot safely represent committed branches, reuse pinned submodules, or answer cross-repository usages.

This initiative makes local indexing correct and scalable, introduces compact versioned snapshots and local working-tree overlays, and adds a standalone index service coordinated by ProcessStack across Linux, Windows, macOS, client, and CI execution environments.

Specification: [`specs/20260919-scalable-distributed-indexing/overview.md`](specs/20260919-scalable-distributed-indexing/overview.md)

Architecture:

- [`architecture.md`](specs/20260919-scalable-distributed-indexing/architecture.md)
- [`platform-indexing.md`](specs/20260919-scalable-distributed-indexing/platform-indexing.md)

#### Workstreams

- [ ] #WORKSTREAM_A — Correct and observable local indexing
- [ ] #WORKSTREAM_B — Scalable extraction and compact storage
- [ ] #WORKSTREAM_C — Versioned snapshots and federated overlays
- [ ] #WORKSTREAM_D — Distributed and platform-aware indexing

#### Initiative outcomes

- Initial indexing is at least 5× faster and peak DB+WAL usage is at least 70% lower on the agreed corpus, unless benchmark evidence approves revised thresholds.
- Definitions and occurrences use stable semantic identities without duplicate occurrence rows.
- Full and incremental indexing converge on equivalent correct semantic state.
- Committed snapshots are immutable, versioned, atomically published, and explicitly complete/partial/unsupported.
- Local overlays index only working-tree changes and federate with an exact committed base.
- Shared submodule versions are stored once and authorized cross-repository usages are queryable.
- ProcessStack coordinates durable, capability-aware work while Sextant owns persistent semantic data and queries.
- Platform-specific projects run on capable workers or use validated client/CI contributions without silently publishing partial data.

---

## Workstream epic A

### Title

`[Epic] Correct and observable local indexing`

### Proposed labels

`epic`, `type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #UMBRELLA.

Establish reliable measurements, correct symbol identity, bounded SQLite persistence, and equivalent full/incremental behavior before changing the extraction algorithm.

Specification: [`overview.md`](specs/20260919-scalable-distributed-indexing/overview.md)

- [ ] #PHASE_1 — Establish indexing benchmarks and diagnostics
- [ ] #PHASE_2 — Introduce stable semantic symbol identities
- [ ] #PHASE_3 — Bound and batch SQLite writes
- [ ] #PHASE_4 — Restore full and incremental index correctness

Complete when all four phase acceptance criteria pass and the phase-1 benchmark report records the resulting correctness and resource profile.

## Phase issue 1

### Title

`Establish indexing benchmarks and diagnostics`

### Proposed labels

`type:task`, `foundation`, `documentation`
Fallback: `enhancement`, `documentation`

### Body

Part of #WORKSTREAM_A and #UMBRELLA.

Create the repeatable benchmark, diagnostics, cancellation, and reporting foundation for the initiative.

Plan: [`phase-1-establish-indexing-benchmarks-and-diagnostics.md`](specs/20260919-scalable-distributed-indexing/phase-1-establish-indexing-benchmarks-and-diagnostics.md)

Acceptance is defined in the phase plan. This issue does not optimize the indexer beyond instrumentation needed for accurate measurement.

## Phase issue 2

### Title

`Introduce stable semantic symbol identities`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_A and #UMBRELLA.

Replace collision-prone member FQNs as primary identity with project/TFM-aware stable semantic keys, and update every semantic edge to use them.

Plan: [`phase-2-introduce-stable-semantic-symbol-identities.md`](specs/20260919-scalable-distributed-indexing/phase-2-introduce-stable-semantic-symbol-identities.md)

## Phase issue 3

### Title

`Bound and batch SQLite writes`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_A and #UMBRELLA.

Introduce bounded transactions, prepared batch persistence, WAL controls, rollback, and atomic generation publication.

Plan: [`phase-3-bound-and-batch-sqlite-writes.md`](specs/20260919-scalable-distributed-indexing/phase-3-bound-and-batch-sqlite-writes.md)

## Phase issue 4

### Title

`Restore full and incremental index correctness`

### Proposed labels

`type:bug`, `severity:high`, `foundation`
Fallback: `bug`

### Body

Part of #WORKSTREAM_A and #UMBRELLA.

Make full and incremental indexing use the same replaceable contribution model, seed file state during the initial run, and preserve every affected semantic artifact through changes and restarts.

Plan: [`phase-4-restore-full-and-incremental-index-correctness.md`](specs/20260919-scalable-distributed-indexing/phase-4-restore-full-and-incremental-index-correctness.md)

---

## Workstream epic B

### Title

`[Epic] Scalable extraction and compact storage`

### Proposed labels

`epic`, `type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #UMBRELLA.

Replace whole-solution search per symbol with bounded document-oriented semantic extraction and compact normalized persistence.

- [ ] #PHASE_5 — Build the document-oriented semantic extractor
- [ ] #PHASE_6 — Add bounded parallel extraction and persistence
- [ ] #PHASE_7 — Normalize files and compact occurrence storage
- [ ] #PHASE_8 — Add indexing profiles and retention policies

Architecture: [`architecture.md`](specs/20260919-scalable-distributed-indexing/architecture.md)

## Phase issue 5

### Title

`Build the document-oriented semantic extractor`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_B and #UMBRELLA.

Catalog declarations, then emit references, calls, relationships, access kinds, and optional evidence from one semantic/operation walk per document.

Plan: [`phase-5-build-the-document-oriented-semantic-extractor.md`](specs/20260919-scalable-distributed-indexing/phase-5-build-the-document-oriented-semantic-extractor.md)

## Phase issue 6

### Title

`Add bounded parallel extraction and persistence`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_B and #UMBRELLA.

Parallelize Roslyn analysis under explicit resource bounds and feed deterministic contribution batches to one backpressured SQLite writer.

Plan: [`phase-6-add-bounded-parallel-extraction-and-persistence.md`](specs/20260919-scalable-distributed-indexing/phase-6-add-bounded-parallel-extraction-and-persistence.md)

## Phase issue 7

### Title

`Normalize files and compact occurrence storage`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_B and #UMBRELLA.

Normalize logical files/file versions, store source spans instead of repeated snippets and absolute paths, compact kinds/hashes, and consolidate occurrence storage.

Plan: [`phase-7-normalize-files-and-compact-occurrence-storage.md`](specs/20260919-scalable-distributed-indexing/phase-7-normalize-files-and-compact-occurrence-storage.md)

## Phase issue 8

### Title

`Add indexing profiles and retention policies`

### Proposed labels

`type:feature`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_B and #UMBRELLA.

Define core/standard/deep feature profiles and bounded retention for optional semantic evidence, generations, snapshots, and source blobs.

Plan: [`phase-8-add-indexing-profiles-and-retention-policies.md`](specs/20260919-scalable-distributed-indexing/phase-8-add-indexing-profiles-and-retention-policies.md)

---

## Workstream epic C

### Title

`[Epic] Versioned snapshots and federated overlays`

### Proposed labels

`epic`, `type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #UMBRELLA.

Separate logical identities from immutable committed versions, maintain a Git-aware local overlay, federate queries, and reuse shared project versions across repositories.

- [ ] #PHASE_9 — Introduce immutable repository snapshots
- [ ] #PHASE_10 — Build Git-aware local overlay indexing
- [ ] #PHASE_11 — Federate local and committed-snapshot queries
- [ ] #PHASE_12 — Deduplicate submodules and enable cross-repository usages

Architecture: [`architecture.md`](specs/20260919-scalable-distributed-indexing/architecture.md)

## Phase issue 9

### Title

`Introduce immutable repository snapshots`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_C and #UMBRELLA.

Add repository, commit, branch pointer, logical project, project version, and atomic snapshot concepts with explicit compatibility and completeness.

Plan: [`phase-9-introduce-immutable-repository-snapshots.md`](specs/20260919-scalable-distributed-indexing/phase-9-introduce-immutable-repository-snapshots.md)

## Phase issue 10

### Title

`Build Git-aware local overlay indexing`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_C and #UMBRELLA.

Build and maintain a small local overlay from authoritative Git diff state plus file watching, with tombstones and project-level invalidation.

Plan: [`phase-10-build-git-aware-local-overlay-indexing.md`](specs/20260919-scalable-distributed-indexing/phase-10-build-git-aware-local-overlay-indexing.md)

## Phase issue 11

### Title

`Federate local and committed-snapshot queries`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_C and #UMBRELLA.

Merge overlay and committed base results with deterministic shadowing, paging, caching, authorization, and freshness/completeness metadata.

Plan: [`phase-11-federate-local-and-committed-snapshot-queries.md`](specs/20260919-scalable-distributed-indexing/phase-11-federate-local-and-committed-snapshot-queries.md)

## Phase issue 12

### Title

`Deduplicate submodules and enable cross-repository usages`

### Proposed labels

`type:feature`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_C and #UMBRELLA.

Reference exact shared project versions from parent snapshots and add authorized reverse dependency and symbol-usage queries across current repository heads.

Plan: [`phase-12-deduplicate-submodules-and-enable-cross-repository-usages.md`](specs/20260919-scalable-distributed-indexing/phase-12-deduplicate-submodules-and-enable-cross-repository-usages.md)

---

## Workstream epic D

### Title

`[Epic] Distributed and platform-aware indexing`

### Proposed labels

`epic`, `type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #UMBRELLA.

Build the standalone Sextant data plane, integrate ProcessStack orchestration, route indexing to capable operating systems/workloads, accept validated client/CI contributions, and productionize the service.

- [ ] #PHASE_13 — Build the standalone Sextant index service
- [ ] #PHASE_14 — Integrate ProcessStack repository-event orchestration
- [ ] #PHASE_15 — Route platform-specific indexing by worker capability
- [ ] #PHASE_16 — Add client- and CI-assisted index contributions
- [ ] #PHASE_17 — Secure and operationalize distributed indexing

Platform design: [`platform-indexing.md`](specs/20260919-scalable-distributed-indexing/platform-indexing.md)

## Phase issue 13

### Title

`Build the standalone Sextant index service`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_D and #UMBRELLA.

Add persistent, independently deployable snapshot ingestion, catalog, status, retention, and low-latency query APIs without requiring ProcessStack.

Plan: [`phase-13-build-the-standalone-sextant-index-service.md`](specs/20260919-scalable-distributed-indexing/phase-13-build-the-standalone-sextant-index-service.md)

## Phase issue 14

### Title

`Integrate ProcessStack repository-event orchestration`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_D and #UMBRELLA.

Normalize repository events and add durable, idempotent ProcessStack orchestration for Sextant snapshots, worker dispatch, progress, and control-plane MCP.

Plan: [`phase-14-integrate-processstack-repository-event-orchestration.md`](specs/20260919-scalable-distributed-indexing/phase-14-integrate-processstack-repository-event-orchestration.md)

## Phase issue 15

### Title

`Route platform-specific indexing by worker capability`

### Proposed labels

`type:feature`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_D and #UMBRELLA.

Discover and match SDK, workload, OS, targeting-pack, Xcode, Android, and custom toolchain requirements to Linux, Windows, or macOS workers.

Plan: [`phase-15-route-platform-specific-indexing-by-worker-capability.md`](specs/20260919-scalable-distributed-indexing/phase-15-route-platform-specific-indexing-by-worker-capability.md)

## Phase issue 16

### Title

`Add client- and CI-assisted index contributions`

### Proposed labels

`type:feature`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_D and #UMBRELLA.

Allow authenticated clients and CI environments to upload deterministic, verified project-version contributions for exact clean commits without making normal builds depend on the service.

Plan: [`phase-16-add-client-and-ci-assisted-index-contributions.md`](specs/20260919-scalable-distributed-indexing/phase-16-add-client-and-ci-assisted-index-contributions.md)

## Phase issue 17

### Title

`Secure and operationalize distributed indexing`

### Proposed labels

`type:task`, `foundation`
Fallback: `enhancement`

### Body

Part of #WORKSTREAM_D and #UMBRELLA.

Add authorization, worker isolation, quotas, retention, recovery, observability, load/failure testing, runbooks, and staged production rollout.

Plan: [`phase-17-secure-and-operationalize-distributed-indexing.md`](specs/20260919-scalable-distributed-indexing/phase-17-secure-and-operationalize-distributed-indexing.md)

---

## Filing order

1. File the umbrella epic and record `#UMBRELLA`.
2. File workstream epics A-D with `Part of #UMBRELLA`.
3. Add each workstream epic as a native sub-issue of the umbrella when supported.
4. File phase issues 1-17 in phase order with their workstream and umbrella references.
5. Add each phase issue as a native sub-issue of its workstream epic when supported.
6. Patch the umbrella and workstream checklists with real issue numbers.
7. Replace every placeholder in this file with the filed number and commit the result.
