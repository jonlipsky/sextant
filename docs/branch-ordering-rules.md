# Branch ordering: rules the implementation must hold

**Status:** Approved by the owner on 2026-11-24 as the contract for #273 (delivered); it remains the contract for remote branch ordering. Reviews judge the branch only
against it: a finding must name the rule it breaks. A case listed under "Not handled" becomes an issue, not a
review round.

Goal (from the [retired spec indexing-performance, phase 7](https://github.com/jonlipsky/sextant/blob/4883ace1c3e9d1e26d91226051f1fcf013054431/specs/20261007-indexing-performance/phase-7-coalesce-and-prioritize-queue.md)): stop indexing commits a newer push has already replaced, and
let default-branch and user requests go first.

## Rules

1. **Catch up.** After a push to a branch, its pointer ends at the newest head GitHub reports. No restart is needed,
   provided GitHub and a worker are reachable.
2. **Coalesce.** While the writer is busy, several pushes to one branch produce at most two indexes: the one running
   and the newest. A replaced request reports `coalesced` and the id of the request that replaced it. A running index
   is never cancelled.
3. **Priority.** Default-branch and user-initiated requests run before feature-branch push and reconcile requests.
   Within one branch, order is admission order, so a retire followed by a recreate stays in that order (#158).
4. **Conditional updates.** A request with an expected head never moves a pointer that isn't at that head. Every
   request ends with a stated outcome, never silently: `complete`, `coalesced`, `refused` with a reason, or `expired`.
5. **Retirements.** A retirement never removes a branch that has a push admitted after it.
6. **Durability.** A request answered with 202 survives a restart and finishes with one of the outcomes in rule 4.
7. **Retries.** A failing request retries with bounded backoff, on its own due time rather than a periodic sweep,
   and expires after the maximum age as `expired`, with no effect.

## Not handled

- **Requests queued before the upgrade:** cleared at upgrade. Pushes are redelivered and callers retry.
- **A snapshot for every commit:** not guaranteed, only for every branch head.
- **Settings outside their documented range:** an example is a queued-request age longer than the cleanup window.
  Startup refuses these with a clear error instead of the code handling them.
- **Tampering, database corruption, or a permanently failing database trigger:** the service fails loudly and safely.
- **Timing races that break none of rules 1–7.**
- **Out of scope for #273, moved to a follow-up issue:** handling a default-branch rename upstream, and deleting a
  branch after it is confirmed deleted upstream.
