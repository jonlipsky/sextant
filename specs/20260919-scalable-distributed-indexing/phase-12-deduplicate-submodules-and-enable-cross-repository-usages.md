# Phase 12 — Deduplicate submodules and enable cross-repository usages

## Goal

Store a pinned shared project version once and discover its authorized consumers and symbol usages across repositories.

## Scope

- Resolve submodule repository identity and exact pinned commit.
- Link parent snapshot dependencies to provider project versions instead of copying provider semantic rows.
- Store consumer occurrences using stable provider symbol identities.
- Add reverse dependency and cross-repository usage query APIs.
- Define default-branch, branch, commit, tenant, and authorization scopes.
- Handle symbol lineage across producer versions conservatively.

## Technical design / files

- Snapshot dependency edges include consumer project version, provider repository/project/commit/version, and reference kind.
- The provider snapshot may be indexed on demand when a parent references an unseen pin.
- Reverse queries first use the dependency catalog to narrow authorized consumer snapshots, then search target symbol occurrences.
- Default scope includes current/default branch heads to avoid duplicate results from retained historical snapshots.
- Results include consumer repository, branch/commit, project, file, location, and pinned producer version.
- FQN-only cross-repository matches are prohibited when stable identity/assembly lineage cannot be established.

## Acceptance criteria

1. The same MixAndMatch commit pinned by multiple parents has one provider project version.
2. Each parent retains its own dependency edge and pin.
3. A producer-symbol usage query returns authorized consumers from configured default-branch heads.
4. Inaccessible repositories contribute neither results nor existence/count metadata.
5. Explicit historical scope returns versioned results without conflating incompatible symbol definitions.
6. Updating one parent's submodule pin does not mutate another parent's snapshot.

## Notes / risks / dependencies

- Package/assembly consumers without source project identity require a compatible assembly/documentation-ID mapping.
- Symbol renames across commits require explicit lineage support or separate identities; heuristic lineage is never silently authoritative.
