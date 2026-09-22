-- Phase 2: stable semantic symbol identities.
--
-- Symbols were previously keyed by their display fully-qualified name under the unique index
-- (fully_qualified_name, project_id). That display string omits parameter lists, so overloads and
-- same-named members on different types collided and overwrote each other. This migration replaces
-- the identity with a stable semantic declaration key (symbols.symbol_key).
--
-- Project identity is also tightened: the project canonical id now folds in the evaluated target
-- framework, so each evaluated framework of a multi-targeted project is a distinct logical project
-- row (target_framework already exists on the projects table from migration 001; project_id
-- composition — the UNIQUE canonical_id — now reflects it). This prevents a #if-conditional member
-- under one framework from being deleted when its sibling framework is processed.
--
-- The old index cannot be reinterpreted as valid data: colliding symbol rows already lost
-- information, and project rows written before this change used a framework-less canonical id that
-- cannot be reconciled. This migration is therefore forward-only and rebuild-required. It clears
-- every derived semantic table, the file_index, and logical project identity (plus the solution and
-- dependency mappings keyed on the old project ids) so the next full index re-populates the schema
-- with correct keys. On a fresh database these clears are no-ops. Foreign keys are ON, but the
-- deletes are ordered explicitly rather than relying on cascade.
--
-- REBUILD REQUIRED — INCLUDING FOR PRE-AMENDMENT DATABASES ON THIS STACK. This migration was amended
-- in place (the count stayed at 007, no 008 was added) to fold the evaluated TFM into project
-- identity. Migrations only run when their version exceeds the recorded schema_version, so a database
-- that already ran an EARLIER revision of 007 (schema_version = 7) will NOT re-run this file and will
-- keep framework-less project ids and pre-amendment keys. There is no in-place upgrade: delete the
-- .sextant index database (any DB built from an earlier commit of this Phase-2 stack) and run a full
-- re-index. This is safe because the index is a derived artifact — nothing here is a source of truth.

DELETE FROM argument_flow;
DELETE FROM return_flow;
DELETE FROM call_graph;
DELETE FROM "references";
DELETE FROM relationships;
DELETE FROM api_surface_snapshots;
DELETE FROM comments;
DELETE FROM symbols;
DELETE FROM file_index;

-- Clear logical project identity: canonical ids are recomputed (now framework-scoped) on rebuild.
DELETE FROM project_dependencies;
DELETE FROM solution_projects;
DELETE FROM solutions;
DELETE FROM projects;

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
