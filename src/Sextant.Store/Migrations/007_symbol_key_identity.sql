-- Phase 2: stable semantic symbol identities.
--
-- Symbols were previously keyed by their display fully-qualified name under the unique index
-- (fully_qualified_name, project_id). That display string omits parameter lists, so overloads and
-- same-named members on different types collided and overwrote each other. This migration replaces
-- the identity with a stable semantic declaration key (symbols.symbol_key).
--
-- The old index cannot be reinterpreted as valid data: colliding rows already lost information.
-- This migration is therefore forward-only and rebuild-required. It clears every derived semantic
-- table (but preserves logical project identity rows) and clears file_index so the next full index
-- re-populates the schema with correct keys. On a fresh database these clears are no-ops.

DELETE FROM argument_flow;
DELETE FROM return_flow;
DELETE FROM call_graph;
DELETE FROM "references";
DELETE FROM relationships;
DELETE FROM api_surface_snapshots;
DELETE FROM comments;
DELETE FROM symbols;
DELETE FROM file_index;

-- Drop the display-FQN uniqueness; the FQN is now display/query data and may repeat (e.g. overloads).
DROP INDEX IF EXISTS ix_symbols_fqn;

-- Add the stable semantic declaration key. NOT NULL with an empty default keeps the ALTER valid;
-- the indexer always writes a real key, and the clears above leave no rows carrying the default.
ALTER TABLE symbols ADD COLUMN symbol_key TEXT NOT NULL DEFAULT '';

-- New identity: one row per (project, semantic declaration key).
CREATE UNIQUE INDEX IF NOT EXISTS ix_symbols_key ON symbols(project_id, symbol_key);

-- Keep fully-qualified-name lookups fast even though they are no longer unique. The FQN leads the
-- index because most resolver lookups arrive without a project_id (they resolve ambiguity across all
-- projects), so SQLite must be able to seek on fully_qualified_name alone; project_id follows as an
-- optional second seek column for the scoped callers.
CREATE INDEX IF NOT EXISTS ix_symbols_fqn_lookup ON symbols(fully_qualified_name, project_id);
