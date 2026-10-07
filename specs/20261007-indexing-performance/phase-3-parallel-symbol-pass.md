# Phase 3 — Parallelize the symbol pass

## Goal

The symbol pass runs on one core for 7.5 minutes on ProcessStack. Give it the same bounded, deterministic
parallelism the occurrence pass already has, and stop doing work whose result is thrown away. Target: ≤ 2.5 minutes.

## Scope

- Run per-document symbol extraction through `ParallelExtractionPipeline` (or a sibling): workers extract each
  document's declared symbols in parallel; results merge by ascending document ordinal; one consumer inserts. Rows,
  row order and ids identical to a serial run.
- Only visit declaration-bearing nodes: walk with `DescendantNodes(descendIntoChildren: …)` that does not enter
  method/accessor/lambda bodies, since locals, lambdas and parameters inside bodies are mapped to null and dropped
  (`MapSymbolKind`). Decide explicitly about local functions' type parameters, which are emitted today; preserve
  them or document their removal.
- Per symbol: compute each rendering once, memoize `SemanticSymbolKeyFactory` keys per `ISymbol`, use compiled
  (generated) regexes in `GetDocComment`.
- Hash each document once per run and pass the hash to both `FileStore` and the source-text store.

## Technical design / files

- `Sextant.Indexer/IndexOrchestrator.cs:679-760` (symbol loop, document hashing at `:737-742`, inserts at
  `:744-749`), `SymbolExtractor.cs:55-112, 174-264` (compilation, node walk, renderings, doc comments),
  `SemanticSymbolKeyFactory.cs:22`, `ParallelExtractionPipeline.cs:56-157`, `ExtractionParallelismOptions.cs:20, 45-57`.
- `SeedFileIndexAsync` (`IndexOrchestrator.cs:1644`) calls `GetCompilationAsync` again: confirm it hits the cached
  compilation (Phase 1 timing), or pass the compilation in.
- The parallelism cap (`min(cores, 8)`) applies; consider raising the default cap for the service (14 cores here)
  once Phase 1 shows the consumer is not the bottleneck.

## Acceptance criteria

1. ProcessStack's symbol pass takes **≤ 2.5 minutes** on cloudserver1 and averages **≥ 4 cores**.
2. `ParallelDeterminismTests` (extended to the symbol pass) show byte-identical canonical output at parallelism
   1/2/4/8/auto.
3. Symbol counts per project are identical to the serial walk, or every difference is a documented consequence of the
   local-function decision.
4. Peak managed memory within +15% of today's at default parallelism (X2 baseline: 2.45 GB managed).

## Notes / risks / dependencies

- If the body-descent change alters output, it rides the same `AnalyzerVersion` bump as Phases 2 and 4.
- Inserting on the consumer only: today inserts happen on the walking thread; moving them changes no data but must
  keep the per-project commit boundary (`CommitBatch` at `IndexOrchestrator.cs:757`).
