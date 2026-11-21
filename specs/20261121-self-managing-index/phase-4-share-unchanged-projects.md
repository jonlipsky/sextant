# Phase 4 — storage proportional to change

## Goal
A commit stores only the project versions it changed. Unchanged project versions are shared with the parent snapshot. This is #274, phase 8 of the indexing-performance spec, carried here as the storage phase.

## Scope
- **Per-project input fingerprint:** file blob hashes, referenced projects' fingerprints, configuration and toolchain.
- When the fingerprint equals the parent's, `MapProject` reuses the parent's project version instead of re-extracting, using the closure rule the overlay path already applies.
- **Reference-counted project versions:** retention reclaims a project version only when no remaining snapshot maps it, as a unit drop after Phase 3.
- **Provenance:** a snapshot records which project versions it reused.

## Testing seams
- **Differential equivalence:** answers from a reused snapshot equal a full index of the same commit.
- **Storage harness:** bytes added by a one-file commit.

## Acceptance criteria
1. A one-file TextEditor commit adds ≤ 5% of a full snapshot's bytes.
2. Answers are equivalent to a full index.
3. A one-leaf-project change publishes in ≤ 25% of a full index's time, per #274.

## Demo
Index two TextEditor-sized commits that differ by one file; show the bytes added and the reuse provenance.
