# Phase 5 — Tune the catalog writer

## Goal

Make the single writer fast on a multi-gigabyte catalog, driven by Phase 1's persist timings rather than guesses.
Today the catalog is ~35 GB with SQLite's default ~2 MB page cache, and the service ignores its own write settings.
The evidence so far says persistence is not the bottleneck (symbols and occurrences took the same time against the
35 GB catalog and a fresh DB), so this phase is sized by measurement and may shrink to the configuration fix.

## Scope

- The service constructs `IndexDatabase` with `IndexWriteOptions.Default` (`SnapshotService.cs:174`); honor
  `IndexWriteOptions.FromConfiguration` so `write_batch_size`, `wal_autocheckpoint_pages` and
  `journal_size_limit_bytes` apply.
- Set `cache_size` (e.g. 256–512 MB) and `mmap_size` on the writer connection, and `temp_store=MEMORY`, within the
  memory budget; raise `wal_autocheckpoint` to an explicit, documented WAL budget (Phase 3 of the original epic
  bounded peak WAL deliberately; keep a bound, just a larger one).
- Only if Phase 1 shows persist time ≥ 15% of extraction: defer FTS5 population to one `INSERT … SELECT` per
  project at the commit boundary, and evaluate dropping write-heavy indexes with no query that uses them
  (`ix_symbols_kind`, `ix_symbols_project_access`), using `EXPLAIN QUERY PLAN` tests as the index-consolidation
  work did.
- `SqlParam.Set` looks parameters up by name per row; cache parameter objects per prepared command.

## Technical design / files

- `Sextant.Store/IndexDatabase.cs:27, 154-166`, `IndexWriteOptions.cs:40-48`, `IndexWriteSession.cs:76-111`,
  `SqlParam.cs:16`, `SymbolStore.cs:51-73`, `ReferenceStore.cs:34-38`, `WriterLease.cs:106, 149-170`.
- Migrations only if an index is dropped (with `PerformanceTests` plan assertions updated).

## Acceptance criteria

1. The service applies configured write options (test: options from environment reach the writer connection).
2. Phase 1's persist time for ProcessStack drops by ≥ 30%, **or** the phase records that persist time is < 10% of
   extraction and stops after the configuration fix.
3. Peak WAL stays within the documented budget; recovery and lease behavior unchanged (existing tests).
4. Query plans for every MCP tool are unchanged (plan tests).

## Notes / risks / dependencies

- Output-neutral; can ship independently after Phase 1.
- `cache_size` counts against the container's memory; account for it in the sandbox budget.
