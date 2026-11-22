# Epic: Self-managing index — active branches stay current, everything else cleans itself up

Spec: `specs/20261121-self-managing-index/` · Filed: epic #418, phases #419–#424 and #426. Builds on #273 (coalescing) and #274 (cross-commit project reuse), and is part of the production-readiness epic #356.

## Tracking body
Sextant keeps an active set: the newest head plus K earlier snapshots per active branch, and open-PR heads. Everything else expires automatically, nothing is protected without a live owner, and reactivation rebuilds. Storage is proportional to change and cheap to reclaim. Recovery from an empty catalog is covered by an integration test.

- [ ] #419 Phase 0 — walking skeleton. Demo: 5 rapid pushes produce 1 index; `/control/status` shows queued depth and age; the recovery baseline is recorded.
- [ ] #420 Phase 1 — every protection has a live owner. Demo: kill a running index; within one tick its pending snapshot is failed and reclaimed.
- [ ] #421 Phase 2 — active set and per-branch retention. Demo: delete a branch, and its data is gone next pass; query an expired repository, and it rebuilds and answers.
- [ ] #422 Phase 3 — cheap reclamation. Demo: dropping a TextEditor-sized snapshot takes seconds, and the file shrinks.
- [ ] #423 Phase 4 — storage proportional to change. Demo: a one-file commit adds ≤ 5% of a full snapshot.
- [ ] #424 Phase 5 — storage alerts. Demo: replaying the November history makes each alert fire.
- [ ] #426 Phase 6 — compact storage. Demo: the storage harness shows bytes per symbol and per occurrence at ≤ 50% of the 2026-11-22 measurement with identical answers; `/control/status` reports bytes per storage class.

## Sub-issues
Each phase gets a sub-issue titled exactly as above, labelled `type:feature` (Phase 1: plus `severity:high`). Each links its phase file and carries its demo verbatim.

## Filing order
Epic first, then phases in order, then patch the #N placeholders. Link #273 from Phase 0 and #274 from Phase 4.
