-- Add access_kind column to references table
-- Values: 'read', 'write', 'readwrite', null (for non-field/property references)
ALTER TABLE "references" ADD COLUMN access_kind TEXT;

-- Index for filtering by access kind
CREATE INDEX IF NOT EXISTS ix_references_access_kind ON "references"(access_kind);
