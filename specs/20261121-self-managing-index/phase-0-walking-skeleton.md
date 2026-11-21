# Phase 0 — walking skeleton

## Goal
Bursts of default-branch pushes collapse to the newest head, the backlog is visible, and the service's recovery from an empty catalog is measured and recorded as a baseline.

## Scope
- **Coalescing** (finishes #273 for default branches): when a default-branch request reaches its turn and a newer request for the same repository and branch is queued or waiting (memory or `queued_ensures`), the older one terminates as `coalesced` and its CAS is carried forward. No exact chain is required. The #405 safety rules hold.
- **Metrics:** queued-ensure depth and oldest age (total and per lane, memory plus `queued_ensures`) in `/control/status`, daily-health and metrics.
- **The watched-repository list survives catalog loss** (decision 7):
  - repositories Sextant registers itself (source `self`) are persisted outside the catalog, or re-derived from durable configuration at startup;
  - after an empty-catalog start, every watched repository's default branch is re-queued with no operator action.
- **Resource-gated recovery:** the rebuild of an empty catalog proceeds one repository at a time, admitted only while host CPU, memory and disk are under configured thresholds (decision 7). There is no deadline.
- **Record the 2026-11-21 reset** as the baseline: which targets came back on their own, and which didn't.

## Testing seams
- **Service tests:** 5 rapid pushes with broken chains (PR base and healer ensures interleaved) produce 1 index; the CAS is carried forward; feature branches are unaffected; the metrics count waiting requests across a restart replay.
- **Production read-only check:** the coalesced counter rises with pushes, and the queue age stays under 1 hour.

## Acceptance criteria
1. N rapid default-branch pushes produce 1 index of the newest head.
2. Queue depth and age are visible and correct after a restart.
3. After deleting the catalog on a scratch instance, every watched repository (including `self` ones) is re-queued and rebuilt in order, without help.
4. Recovery admission backs off while host resources are over threshold.

## Demo
Push 5 commits in quick succession to a watched test repository's default branch. One index runs. `/control/status` shows the waiting depth during the burst.

## Notes / risks / dependencies
- In flight 2026-11-21: branch `fix/273-default-branch-newest-head` (Copilot).
- **Recovery baseline (partial).** Reset at 2026-11-21 19:08 UTC (catalog deleted, `catalog.db.job-id-floor` kept, default queue age). Within 1 minute the app re-sent its tenant grant (200) and an ensure (202). After about 30 minutes only the three grant-listed repositories (ProcessStack, factorystack, processstack-sonarscanner) were known, with ProcessStack indexing. The `self` repositories (TextEditor, MixAndMatch, Confluo, Crdt, monorepo, RecordStack and others) had not returned.
