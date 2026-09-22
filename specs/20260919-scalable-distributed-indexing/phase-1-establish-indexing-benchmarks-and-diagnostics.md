# Phase 1 — Establish indexing benchmarks and diagnostics

## Goal

Create a repeatable performance and correctness baseline that makes indexing regressions visible before the extraction and schema changes begin.

## Scope

- Add benchmark corpora covering Sextant itself, a generated large solution, duplicate member names, overloads, partial types, project references, and multi-target projects.
- Provide an opt-in harness for recording a representative private monorepo without committing its source or identifying paths.
- Record end-to-end and per-phase duration, row counts, distinct occurrence counts, duplicate counts, database/WAL size, peak memory, workspace diagnostics, and cancellation outcome.
- Establish baseline and target reports in CI artifacts.
- Add structured progress and cancellation tokens to solution loading and each indexing phase.

## Technical design / files

- Add an indexing metrics model in `Sextant.Core`.
- Instrument `SolutionLoader`, `IndexOrchestrator`, `IncrementalIndexer`, and `IndexDatabase`.
- Add a benchmark runner under `tests/` or `scripts/` that creates isolated databases outside the source tree.
- Add deterministic code-generation helpers for large solution fixtures.
- Document the benchmark command and environment fields needed for comparable results.

The benchmark report must distinguish final database bytes from peak database-plus-WAL bytes. It must preserve phase results when indexing fails or is cancelled.

## Acceptance criteria

1. One command produces machine-readable JSON and human-readable summaries for full and incremental indexing.
2. The report contains all required phase, row, duplicate, storage, memory, and diagnostic metrics.
3. The existing Sextant baseline reproduces the current order of magnitude: 33-second total run, 14,375 references, and large transient WAL, allowing documented environment variance.
4. Cancellation stops Roslyn work and database writes without publishing a successful index.
5. CI runs correctness-scale fixtures and uploads performance output without enforcing unstable wall-clock thresholds initially.
6. The agreed 5× runtime and 70% peak-disk reduction targets are recorded or explicitly revised with rationale.

## Notes / risks / dependencies

- This phase is intentionally first so every later phase can show measured impact.
- Private monorepo results must redact absolute paths, repository names, symbol names, and source snippets.
- Performance CI should use relative comparisons or dedicated runners before becoming a required check.
