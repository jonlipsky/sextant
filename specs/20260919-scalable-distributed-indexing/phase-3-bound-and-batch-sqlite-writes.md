# Phase 3 — Bound and batch SQLite writes

## Goal

Remove per-row transaction and command overhead and keep WAL growth within predictable limits during successful, failed, and cancelled indexing.

## Scope

- Add an indexing unit-of-work API with explicit transactions.
- Reuse prepared commands and write definitions/occurrences/relationships in batches.
- Commit at configurable document/project batch boundaries.
- Add WAL autocheckpoint, journal-size limits, explicit final checkpoint/truncate, and startup recovery.
- Preserve atomic visibility: readers see the previous complete generation or the new complete generation, never a partially published replacement.
- Add rollback and retry behavior for busy, disk-full, cancellation, and malformed-row failures.

## Technical design / files

- Extend `IndexDatabase` with write-session and checkpoint APIs.
- Refactor store insert methods to support reusable prepared commands and a caller-owned transaction.
- Use one writer connection and retain concurrent read-only connections.
- Stage replacement data under an index-run/generation ID and atomically publish after validation.
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

- FTS triggers can still amplify writes; later schema work may rebuild FTS after bulk load or make it profile-specific.
- Very large transactions reduce overhead but increase recovery and temporary-space cost, so batching is measured rather than guessed.
