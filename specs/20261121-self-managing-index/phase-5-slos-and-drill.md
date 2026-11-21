# Phase 5 — storage SLOs and the recovery drill

## Goal
Growth problems are caught by alerts, not by a full disk, and recovery from an empty catalog is proven on a schedule.

## Scope
- **Alerts** (the monitor app's issues), from `index_runs.final_db_bytes`, retention audit rows and queue metrics:
  - catalog bytes per day over budget;
  - retention rows per day below the expected rate while a backlog exists;
  - queued-ensure age over 1 hour;
  - disk free under 20%.
- **Recovery drill:**
  - a scheduled job (weekly) starts a second service instance on a scratch catalog with read-only credentials and no publishing;
  - it lets that instance rebuild the active set's default branches through normal triggers, and measures time to current against the SLO;
  - it reports and tears down;
  - it never touches the production catalog.
- **Runbook:** catalog reset procedure (stop, delete, start, watch), with today's procedure as the reference.

## Testing seams
- **Alert rules:** unit tests over recorded metric series (the Nov 8–21 history as a fixture).
- **The drill run itself** is the live test.

## Acceptance criteria
1. Each alert fires on the Nov 19–21 history fixture.
2. The drill meets the SLO, or opens an issue.

## Demo
Replay the November history into the alert evaluator, and each alert fires. Run the drill once and attach its report.
