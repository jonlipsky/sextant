# Scalable and Distributed Semantic Indexing

**Status:** Draft · **Date:** 2026-09-19 · **Owner:** Jon Lipsky

## Why this exists

Sextant is useful on repositories small enough for its initial semantic index to complete, but its current indexing pipeline does not scale to large solutions and monorepos. A representative failure mode is an index that runs indefinitely and grows beyond 4 GB before it is terminated. That prevents Sextant from becoming a standard tool across all projects.

A benchmark of the current Sextant solution exposed structural causes rather than a fundamental SQLite limit:

| Measurement | Current result |
|---|---:|
| Projects | 13 |
| Total runtime | 33.1 seconds |
| Reference extraction | 16.5 seconds |
| Attempted symbol writes | 1,955 |
| Final symbol rows | 1,337 |
| Reference rows | 14,375 |
| Distinct reference locations | 3,503 |
| Duplicate reference rows | 10,872 (75.6%) |
| Final main database | 8.8 MB |
| WAL before checkpoint | 293.6 MB |

The current implementation also cannot safely represent multiple branches or commits of the same project. A project is logically identified by repository and project path, while its mutable symbol rows are overwritten during reindexing. That model cannot deduplicate a shared submodule at a pinned commit, retain branch snapshots, or answer cross-repository usage queries reliably.

The initiative therefore has two connected goals:

1. Make local initial and incremental indexing correct, bounded, observable, and fast enough for large codebases.
2. Introduce immutable committed snapshots, local working-tree overlays, and a distributed indexing service that can reuse project versions across repositories and answer authorized cross-repository queries.

## The model / approach

The design separates the system into four layers:

1. **Semantic extraction** performs one bounded, document-oriented Roslyn analysis pass and emits stable definitions and deduplicated occurrences.
2. **Versioned storage** separates logical repository/project/symbol identity from immutable commit-specific definitions and source occurrences.
3. **Federated querying** combines an immutable committed base snapshot with a small local overlay containing only working-tree changes and tombstones.
4. **Distributed coordination** uses ProcessStack for GitHub event ingestion, durable orchestration, capability-aware worker dispatch, progress, and audit state while a standalone Sextant service owns index data and low-latency queries.

Linux workers handle ordinary SDK-style projects. Index jobs declare required operating system, SDK feature band, workloads, targeting packs, SDK resolvers, and external toolchains. Projects that cannot be faithfully evaluated on Linux are routed to Windows or macOS workers. Authenticated developer machines and CI jobs may also publish versioned per-project index contributions when they have already built the exact commit and target framework.

Client-assisted indexing is an optimization and compatibility path, not the source of truth for uncommitted code and not a mandatory compiler analyzer. Uncommitted overlays remain local by default.

Detailed design:

- [Architecture](architecture.md)
- [Platform-aware and client-assisted indexing](platform-indexing.md)

## What already exists (build against, not greenfield)

- `Sextant.Indexer` already loads complete Roslyn solutions, extracts semantic symbols, references, relationships, calls, comments, and dataflow, and supports a daemon-driven incremental path.
- `Sextant.Store` already provides raw parameterized SQLite access, schema migrations, WAL mode, FTS5, project identity, API snapshots, and read-optimized query stores.
- `Sextant.Mcp` already provides the agent-facing query contract over stdio and HTTP MCP.
- `Sextant.Daemon` already watches local files and queues immediate and background indexing work.
- Project identity already uses normalized Git remote plus repository-relative project path, which remains the logical-project identity foundation.
- Graphify C# demonstrates a useful extraction pattern: catalog declarations, walk documents and `IOperation` trees once, bound parallelism, and deduplicate semantic edges.
- ProcessStack already provides tenant-aware GitHub webhook validation, durable Orleans orchestration, JetStream delivery, worker capability matching, isolated execution, artifact storage, and authenticated MCP exposure.

This initiative evolves those components. It does not replace Roslyn, SQLite, MCP, or ProcessStack.

## Non-goals

- Replacing Roslyn with syntax-only or heuristic indexing.
- Replacing SQLite for Sextant's local index and overlay.
- Storing uncommitted source code on the server by default.
- Building or packaging platform applications as part of indexing.
- Making ProcessStack grain state or temporary worker directories the semantic database.
- Indexing every historical commit or every branch indefinitely.
- Providing complete runtime reachability for reflection, dynamic dispatch, dependency injection, or generated runtime code.
- Supporting non-.NET languages in this initiative.
- Requiring developers to install a compiler analyzer or block normal builds on remote index upload.

## Phases

### Workstream A — Correct and observable local indexing

| Phase | Title | Outcome |
|---:|---|---|
| 1 | Establish indexing benchmarks and diagnostics | Reproducible performance baselines and per-phase resource telemetry protect subsequent work. [Plan](phase-1-establish-indexing-benchmarks-and-diagnostics.md) |
| 2 | Introduce stable semantic symbol identities | Definitions, overloads, projects, TFMs, and occurrences use collision-resistant semantic identities. [Plan](phase-2-introduce-stable-semantic-symbol-identities.md) |
| 3 | Bound and batch SQLite writes | Transactions, prepared batches, and WAL controls eliminate per-row write amplification. [Plan](phase-3-bound-and-batch-sqlite-writes.md) |
| 4 | Restore full and incremental index correctness | Full indexing seeds incremental state and file changes replace every affected semantic artifact correctly. [Plan](phase-4-restore-full-and-incremental-index-correctness.md) |

### Workstream B — Scalable extraction and compact storage

| Phase | Title | Outcome |
|---:|---|---|
| 5 | Build the document-oriented semantic extractor | References and calls are emitted from one semantic/operation walk per document instead of whole-solution searches per symbol. [Plan](phase-5-build-the-document-oriented-semantic-extractor.md) |
| 6 | Add bounded parallel extraction and persistence | Analysis uses capability-aware bounded concurrency while one batched writer preserves SQLite safety. [Plan](phase-6-add-bounded-parallel-extraction-and-persistence.md) |
| 7 | Normalize files and compact occurrence storage | Repeated paths, snippets, strings, and duplicate reference/call rows are replaced by normalized compact records. [Plan](phase-7-normalize-files-and-compact-occurrence-storage.md) |
| 8 | Add indexing profiles and retention policies | Repositories can choose core or deep semantic coverage and bounded historical retention. [Plan](phase-8-add-indexing-profiles-and-retention-policies.md) |

### Workstream C — Versioned snapshots and federated overlays

| Phase | Title | Outcome |
|---:|---|---|
| 9 | Introduce immutable repository snapshots | Logical identities are separated from immutable commit-, project-, and analyzer-version-specific content. [Plan](phase-9-introduce-immutable-repository-snapshots.md) |
| 10 | Build Git-aware local overlay indexing | Startup diffs and file watching maintain a small local overlay over an exact committed base. [Plan](phase-10-build-git-aware-local-overlay-indexing.md) |
| 11 | Federate local and committed-snapshot queries | MCP queries merge overlay and base results with deterministic shadowing and freshness metadata. [Plan](phase-11-federate-local-and-committed-snapshot-queries.md) |
| 12 | Deduplicate submodules and enable cross-repository usages | Pinned shared project versions are stored once and authorized consumers become discoverable across repositories. [Plan](phase-12-deduplicate-submodules-and-enable-cross-repository-usages.md) |

### Workstream D — Distributed and platform-aware indexing

| Phase | Title | Outcome |
|---:|---|---|
| 13 | Build the standalone Sextant index service | Persistent snapshot ingestion, query, cache, and worker APIs run independently of ProcessStack. [Plan](phase-13-build-the-standalone-sextant-index-service.md) |
| 14 | Integrate ProcessStack repository-event orchestration | GitHub push and pull-request events idempotently request and monitor committed snapshots. [Plan](phase-14-integrate-processstack-repository-event-orchestration.md) |
| 15 | Route platform-specific indexing by worker capability | Index jobs select Linux, Windows, or macOS workers using explicit SDK/workload/toolchain requirements. [Plan](phase-15-route-platform-specific-indexing-by-worker-capability.md) |
| 16 | Add client- and CI-assisted index contributions | Trusted clients and CI publish validated per-project contributions for exact commits and target frameworks. [Plan](phase-16-add-client-and-ci-assisted-index-contributions.md) |
| 17 | Secure and operationalize distributed indexing | Authorization, isolation, retention, recovery, rollout, and service-level objectives make the service production-ready. [Plan](phase-17-secure-and-operationalize-distributed-indexing.md) |

## Acceptance criteria

1. Symbol identities distinguish same-named members, overloads, projects, assemblies, target frameworks, and immutable project versions without relying on mutable database row IDs.
2. The reference table contains no duplicate semantic occurrence keys, and correctness tests cover anonymous types, partial types, overloads, explicit interface implementations, and duplicate FQNs across projects.
3. On the agreed benchmark corpus, initial indexing is at least 5× faster and peak main-database-plus-WAL disk usage is at least 70% lower than the recorded baseline, or the benchmark report documents and approves a revised threshold before rollout.
4. A one-file implementation-only edit reaches a queryable local state within five seconds at p95 on the agreed developer-machine benchmark.
5. Full indexing, daemon restart catch-up, changed/deleted/renamed files, project configuration changes, and signature changes preserve correct references, calls, relationships, comments, dataflow, and file hashes.
6. Every committed snapshot is immutable, atomically published, associated with a repository commit, analyzer/schema/configuration versions, and reports complete, partial, failed, or unsupported status explicitly.
7. A local overlay based on an exact commit shadows changed base files, tombstones deleted files, and never returns stale base occurrences from touched files.
8. A project version pinned as a submodule in multiple repositories is stored once, while each authorized consumer dependency remains independently queryable.
9. Cross-repository usage queries default to authorized default-branch heads and can optionally target explicit branches or commits without leaking inaccessible repository information.
10. ProcessStack handles duplicate and out-of-order repository events idempotently and never stores the semantic index in Orleans grain state or ephemeral worker directories.
11. Platform-sensitive projects either run on a worker satisfying their declared capability fingerprint, accept a validated client/CI contribution, or report an actionable unsupported reason; they never silently publish an incomplete index as complete.
12. Linux, Windows, macOS, ordinary SDK, Windows-targeted, Android, and Apple-workload fixtures exercise capability discovery and routing in automated or environment-gated tests.
13. Uncommitted source remains local unless a user explicitly opts into upload, and all remote query results are tenant- and repository-authorized.
14. Existing Sextant MCP tools remain backward compatible or provide a documented versioned migration path with index freshness and completeness metadata.
