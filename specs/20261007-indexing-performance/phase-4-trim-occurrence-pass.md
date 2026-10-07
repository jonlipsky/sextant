# Phase 4 — Trim occurrence-pass work and overlap compilations

## Goal

The occurrence pass reaches ~5 cores but still waits on one serial compilation per project and does per-occurrence
work it discards. Remove the waste and overlap the next project's compilation with the current project's analysis.
Target: ProcessStack occurrences ≤ 1.5 minutes (from 2.9).

## Scope

- **Drop discarded work:** `Snippet()` (`GetSubText().ToString()` + regex + substring) is computed for every
  reference and never stored (the insert has no snippet column; snippets are rebuilt at query time).
- **Memoize** `DeclarationKey` per symbol and `EnclosingMemberKey` per member node (per-document dictionaries, so
  no cross-thread contention); collapse the paired `GetOperation`/`GetSymbolInfo` calls on constructor and
  `CreatedType` paths; avoid the second `GetOperation` in `ClassifyAccess` where the first result suffices.
- **Overlap compilations:** let the producer start `GetCompilationAsync` for the next K projects (default K = 1,
  bounded by the memory budget) while the current project's documents are analyzed. Persistence order and the
  per-project commit boundary stay unchanged.
- **Source text:** `SourceTextStore.Put` must not decompress and re-hash an existing blob on every commit; check
  existence by content address (and verify lazily or on a sampling basis).

## Technical design / files

- `Sextant.Indexer/DocumentSemanticExtractor.cs:293, 348, 368, 388, 413-415, 445, 500-503, 524-525, 544, 579,
  617-636, 669, 704-715, 740-747`.
- `Sextant.Indexer/ParallelExtractionPipeline.cs:97-157` (producer awaits each project's compilation),
  `IndexOrchestrator.cs:799-829, 852` (the "one compilation live at a time" bound; Phase 1 confirms whether
  `Solution` caches compilations, which would already break that bound).
- `Sextant.Store/SourceTextStore.cs:63-64, 93-117`, `FileStore.cs:108-118`.

## Acceptance criteria

1. ProcessStack's occurrence pass takes **≤ 1.5 minutes** on cloudserver1.
2. Reference/occurrence rows byte-identical to before (canonical comparison in the determinism tests); snippet output
   at query time unchanged.
3. With K = 1, peak managed memory ≤ +25% of the pre-phase baseline; K is configurable and bounded by the sandbox
   memory budget.
4. Re-indexing a commit whose files are all already in the source-text store performs no decompression in `Put`
   (counter in Phase 1's metrics).

## Notes / risks / dependencies

- Output-neutral by construction (no bump of its own); ships with Phases 2–3.
- Compilation overlap trades memory for time; on the 7 GiB container, measure with BuildHost memory included.
