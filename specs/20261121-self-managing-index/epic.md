# Epic: Self-managing index — active branches stay current, everything else cleans itself up

Spec: `specs/20261121-self-managing-index/`. Builds on #273 (coalescing) and #274 (cross-commit project reuse), and is part of the production-readiness epic #356.

## Tracking body
Sextant keeps an active set: the newest head plus K earlier snapshots per active branch, and open-PR heads. Everything else expires automatically, nothing is protected without a live owner, and reactivation rebuilds. Storage is proportional to change and cheap to reclaim. Recovery from an empty catalog is proven on a schedule.

- [ ] #N0 Phase 0 — walking skeleton. Demo: 5 rapid pushes produce 1 index; `/control/status` shows queued depth and age; the recovery baseline is recorded.
- [ ] #N1 Phase 1 — every protection has a live owner. Demo: kill a running index; within one tick its pending snapshot is failed and reclaimed.
- [ ] #N2 Phase 2 — active set and per-branch retention. Demo: delete a branch, and its data is gone next pass; query an expired repository, and it rebuilds and answers.
- [ ] #N3 Phase 3 — cheap reclamation. Demo: dropping a TextEditor-sized snapshot takes seconds, and the file shrinks.
- [ ] #N4 Phase 4 — storage proportional to change. Demo: a one-file commit adds ≤ 5% of a full snapshot.
- [ ] #N5 Phase 5 — storage SLOs and the recovery drill. Demo: alerts fire on the November history; the drill meets the SLO.

## Sub-issues
Each phase gets a sub-issue titled exactly as above, labelled `type:feature` (Phase 1: plus `severity:high`). Each links its phase file and carries its demo verbatim.

## Filing order
Epic first, then phases in order, then patch the #N placeholders. Link #273 from Phase 0 and #274 from Phase 4.
