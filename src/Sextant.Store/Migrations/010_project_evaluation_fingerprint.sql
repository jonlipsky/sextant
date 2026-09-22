-- Phase 4: per-project evaluation fingerprint for configuration-change invalidation.
--
-- A project's semantic output depends on more than its .cs file contents: the evaluated csproj, the
-- Directory.Build.props/targets chain, global.json, the analyzer/editor config chain, and the restored
-- package assets all shape the compilation. Startup catch-up compares only per-file content hashes, so
-- a change to one of these evaluation inputs (which leaves every .cs byte-identical) currently
-- schedules no reindex even though symbols/references may now differ.
--
-- This column stores a hash of those evaluation inputs captured at index time. Catch-up recomputes the
-- fingerprint from disk and escalates a project whose fingerprint changed to a full project rebuild
-- (acceptance criterion 6). It is nullable: a null recorded fingerprint means "unknown", which is
-- treated as changed so a project indexed before this column existed is re-evaluated once.

ALTER TABLE projects ADD COLUMN evaluation_fingerprint TEXT;
