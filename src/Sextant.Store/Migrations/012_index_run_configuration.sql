-- Phase 8: record the resolved indexing profile / feature set / configuration hash on every run.
--
-- A run's semantic output now depends on the selected indexing profile (core/standard/deep): a
-- lower profile deliberately omits optional tables (comments, documentation text, argument/return
-- dataflow) and gates the matching query capabilities. To make that reproducible and safe we record,
-- on each index_runs row, the resolved profile name, the feature bit set that was actually built, and
-- a stable configuration hash (see Sextant.Core.IndexConfigurationHash).
--
--   * config_hash      — stable hash over profile + feature bits + generated-source policy +
--                        analyzer version. The daemon compares the current configuration's hash to
--                        the last-complete run's hash; a difference (e.g. core→standard, which never
--                        built the optional tables) forces a full rebuild instead of an incremental
--                        catch-up. Distinct from projects.evaluation_fingerprint (migration 010),
--                        which hashes per-project MSBuild inputs — the two compose.
--   * indexing_profile — the canonical profile name for status/reporting.
--   * features          — the IndexFeature bit set (integer) capability-aware query tools consult to
--                        decide whether requested data was indexed.
--
-- Additive and forward-only: existing complete generations keep serving. Their new columns are NULL,
-- which the daemon treats as "unknown configuration" (forces one rebuild, mirroring the null
-- evaluation-fingerprint semantics) and capability checks treat as "all features available" so a
-- pre-Phase-8 index is never mistaken for a feature-reduced one. No table is dropped and the
-- index_runs ledger is NOT cleared, so this migration does NOT require a full rebuild.

ALTER TABLE index_runs ADD COLUMN config_hash TEXT;
ALTER TABLE index_runs ADD COLUMN indexing_profile TEXT;
ALTER TABLE index_runs ADD COLUMN features INTEGER;
