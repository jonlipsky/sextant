# Phase 3 — Bound and batch SQLite writes

## Goal

Remove per-row transaction and command overhead and keep WAL growth within predictable limits during successful, failed, and cancelled indexing.

## Scope

- Add an indexing unit-of-work API with explicit transactions.
- Reuse prepared commands and write definitions/occurrences/relationships in batches.
- Commit at configurable document/project batch boundaries.
- Add WAL autocheckpoint, journal-size limits, explicit final checkpoint/truncate, and startup recovery.
- Preserve atomic visibility at the generation-pointer and per-batch level: each project/document replacement commits transactionally (readers never see a half-written project or file), the last-complete-run pointer flips atomically inside the final write transaction (readers resolve the previous complete generation until the new one is fully published), and startup recovery abandons stale staging generations. Phase-boundary clarification: whole-generation reader isolation — a concurrent reader never observing a partially rebuilt (hybrid) state across the *entire* index during a full rebuild — requires immutable-snapshot isolation and lands in Phase 9, building on this phase's `index_runs` ledger and version model. This is a deferral of the physical-isolation mechanism, not a dropped guarantee.
- Add rollback and retry behavior for busy, disk-full, cancellation, and malformed-row failures.

## Technical design / files

- Extend `IndexDatabase` with write-session and checkpoint APIs.
- Refactor store insert methods to support reusable prepared commands and a caller-owned transaction.
- Use one writer connection and retain concurrent read-only connections.
- Stage replacement data under an index-run/generation ID (the `index_runs` ledger) and publish it by flipping the last-complete-run pointer inside the final write transaction after validation. In this phase staging is logical (a ledger row + per-batch atomic replacement of the shared tables), not physical multi-version isolation; physical generation-scoped storage is Phase 9.
- Record peak main DB, WAL, SHM, and staged-artifact sizes.

Batch size is configuration with a conservative default. A transaction may not encompass an unbounded monorepo.

## Acceptance criteria

1. The benchmark performs no implicit transaction per semantic row.
2. Peak WAL size remains below the configured bound plus one active batch under normal operation.
3. The Sextant benchmark no longer produces a WAL tens of times larger than its final database.
4. A cancelled or failed batch rolls back without replacing the last complete index.
5. A process restart checkpoints or safely recovers a valid WAL and removes abandoned staging generations.
6. Existing read queries continue working while a new generation is built.

## Notes / risks / dependencies

- FTS triggers can still amplify writes; later schema work may rebuild FTS after bulk load or make it profile-specific. Measured here: the FTS5 per-row triggers are not the WAL driver once writes are batched, so a bulk defer/rebuild is left as a precise note for Phase 7.
- Very large transactions reduce overhead but increase recovery and temporary-space cost, so batching is measured rather than guessed.
- Atomic-visibility boundary (see Scope): criterion 4 holds at the last-complete-run pointer — a cancelled/failed run never advances it — but because staging is logical, earlier committed batches physically remain in the shared tables until overwritten by a later successful run or reconciled by recovery. Whole-generation reader isolation is delivered in Phase 9 via immutable snapshots.
