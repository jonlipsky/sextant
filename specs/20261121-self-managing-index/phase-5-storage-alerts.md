# Phase 5 — storage alerts

## Goal
Growth and cleanup problems are caught by alerts, not by a full disk.

The owner dropped a scheduled recovery drill on 2026-11-21. Recovery from an empty catalog is covered by Phase 0's integration test instead, which runs on every change and puts no load on the server.

## Scope
Alerts as monitor-app issues, computed from `index_runs.final_db_bytes`, retention audit rows, queue metrics and host disk:
- catalog bytes per day over budget;
- retention rows per day below the expected rate while a backlog exists;
- queued-ensure age over 1 hour;
- free disk under 20%;
- storage budget enforcement (decision 6) active for more than 24 hours.

## Testing seams
- **Alert rules:** unit tests over recorded metric series, using the Nov 8–21 history as a fixture.
- Deliberately not tested: delivery to the monitor app, which is covered by its existing tests.

## Acceptance criteria
1. Each alert fires on the Nov 19–21 history fixture.
2. No alert fires on a steady-state fixture.

## Demo
Replay the November history into the alert evaluator, and each alert fires.
