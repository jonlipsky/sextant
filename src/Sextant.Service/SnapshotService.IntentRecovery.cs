using System.Text.Json;
using Sextant.Store;

namespace Sextant.Service;

// Issue #273 (rules 6 and 7): one recovery rule for every branch intent kind. A turn that ends without settling its
// intent returns it to pending with a next attempt time (its due time) from its own finally path. When even that write
// fails, the turn's claim token is dropped from this process's live set and an in-memory backoff stands in, so the next
// claim treats the intent as owner-gone and reclaims it. Retries run on due times, never on a periodic sweep: a deferred
// intent arms one wake for the earliest due time (ScheduleIntentRetryAt), and startup arms it from the durable due times.
// Only a branch's head ever runs, so order is preserved.
public sealed partial class SnapshotService
{
    // Claim tokens of the refresh turns running now. A claimed intent whose token is absent is reclaimable.
    private readonly HashSet<string> _liveIntentTurns = new(StringComparer.Ordinal);
    // Retire/delete intents with a live executor (admitted, waiting for their head, or applying).
    private readonly HashSet<long> _liveIntentRuns = [];
    // Queued-ensure sequences with a live ensure operation.
    private readonly HashSet<long> _liveQueuedEnsures = [];
    // In-memory backoff for heads whose failure could not be recorded durably: "i:{intent id}" or "e:{queued ensure seq}".
    private readonly Dictionary<string, (int Attempts, long DueAt)> _intentBackoff = new(StringComparer.Ordinal);

    /// <summary>Test hook run inside branch-intent write transactions; it may throw to inject a write failure.</summary>
    internal Action<string>? BeforeIntentWriteForTesting { get; set; }
    internal Action? BeforeEnsureIntentRescheduleForTesting { get; set; }
    internal void RescanBranchIntentsForTesting() => ScheduleBranchIntentHeads();
    internal bool HasLiveQueuedEnsureForTesting(long seq)
    {
        lock (_inFlightLock)
            return _liveQueuedEnsures.Contains(seq);
    }

    private bool IsLiveIntentTurn(string turnId)
    {
        lock (_inFlightLock)
            return _liveIntentTurns.Contains(turnId);
    }

    // The same bounded schedule as refresh retries: 30 s doubling to at most one hour.
    internal static long IntentRetryDelayMilliseconds(int attempts) =>
        (long)TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(attempts, 7)))).TotalMilliseconds;

    private static bool RetryPending(long? nextAttemptAt, long now) => nextAttemptAt is long due && due > now;

    // Records an in-memory backoff (a failure that could not be written durably) and arms the wake for its due time.
    private void RecordIntentBackoff(string key)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long due;
        lock (_inFlightLock)
        {
            var attempts = _intentBackoff.TryGetValue(key, out var previous) ? previous.Attempts : 0;
            due = now + IntentRetryDelayMilliseconds(attempts);
            _intentBackoff[key] = (attempts + 1, due);
        }
        ScheduleIntentRetryAt(due);
    }

    private bool IntentBackoffPending(string key, long now)
    {
        lock (_inFlightLock)
            return _intentBackoff.TryGetValue(key, out var backoff) && backoff.DueAt > now;
    }

    private static string IntentKey(long intentId) => "i:" + intentId;
    private static string QueuedEnsureKey(long seq) => "e:" + seq;

    private void DeferQueuedEnsure(long seq, string error)
    {
        try
        {
            var due = GrantWrite(conn =>
            {
                var intents = new BranchIntentStore(conn);
                if (intents.GetByQueuedEnsureId(seq) is { State: "pending" } intent)
                {
                    var next = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                        + IntentRetryDelayMilliseconds(intent.Attempts);
                    intents.Defer(intent.Id, next, error);
                    return (long?)next;
                }
                return null;
            });
            if (due is long next)
                ScheduleIntentRetryAt(next);
        }
        catch (Exception ex)
        {
            Log($"queued_ensure_defer_failed seq={seq} error={ex.GetType().Name}");
            RecordIntentBackoff(QueuedEnsureKey(seq));
        }
    }

    // Rule 7: one armed wake for the earliest due time. A request for an earlier time replaces it (its delay is cancelled);
    // a later one is covered, since the scan the wake runs arms the next due time. No periodic sweep exists.
    private (long DueAt, CancellationTokenSource Cancel)? _armedIntentWake;
    private int _intentWakeFaults;

    /// <summary>Test hook run by an armed wake before its scan; it may throw to fault that scan.</summary>
    internal Action? BeforeIntentWakeScanForTesting { get; set; }
    internal void ScheduleIntentRetryAtForTesting(long due) => ScheduleIntentRetryAt(due);
    internal long? ArmedIntentWakeForTesting
    {
        get { lock (_inFlightLock) return _armedIntentWake?.DueAt; }
    }

    private void ScheduleIntentRetryAt(long due)
    {
        lock (_inFlightLock)
        {
            if (_disposed || _lifetime.IsCancellationRequested)
                return;
            if (_armedIntentWake is { } armed)
            {
                if (armed.DueAt <= due)
                    return;
                armed.Cancel.Cancel();
                armed.Cancel.Dispose();
            }
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _armedIntentWake = (due, cancel);
            var delay = Math.Max(0, due - _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var wait = Task.Delay(TimeSpan.FromMilliseconds(delay), _timeProvider, cancel.Token);
            var operation = Task.Run(async () =>
            {
                try
                {
                    await wait.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    return; // replaced by an earlier wake
                }
                lock (_inFlightLock)
                {
                    if (_armedIntentWake is { } current && ReferenceEquals(current.Cancel, cancel))
                    {
                        _armedIntentWake = null;
                        cancel.Dispose();
                    }
                }
                await _replayAdmissions.Task.ConfigureAwait(false);
                RunIntentWakeScan();
            }, CancellationToken.None);
            _operations.Add(operation);
            _ = operation.ContinueWith(t =>
            {
                lock (_inFlightLock)
                    _operations.Remove(t);
                if (t.IsFaulted)
                    Log($"branch_intent_retry_wakeup_failed error={t.Exception!.GetBaseException().GetType().Name}");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // A wake's scan that faults re-arms itself on a bounded backoff (it never relies on maintenance to retry).
    private void RunIntentWakeScan()
    {
        try
        {
            BeforeIntentWakeScanForTesting?.Invoke();
            ScheduleBranchIntentHeads();
            Interlocked.Exchange(ref _intentWakeFaults, 0);
        }
        catch (Exception ex) when (ex is not (ObjectDisposedException or OperationCanceledException))
        {
            var faults = Interlocked.Increment(ref _intentWakeFaults);
            var due = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + IntentRetryDelayMilliseconds(faults - 1);
            Log($"branch_intent_wake_failed error={ex.GetType().Name}; re-armed for {due}");
            ScheduleIntentRetryAt(due);
        }
    }

    private void ForgetLiveIntentRun(long? intentId)
    {
        if (intentId is not > 0)
            return;
        lock (_inFlightLock)
            _liveIntentRuns.Remove(intentId.Value);
    }

    /// <summary>
    /// Records the retry schedule of an unclaimed (retire/delete) intent whose turn failed before settling it. Runs on
    /// the failed turn. When the write fails too, the in-memory backoff stands in for it.
    /// </summary>
    private void DeferIntentAfterFailedTurn(long intentId, string error)
    {
        try
        {
            BeginRaw("defer-branch-intent");
            long? retryAt = null;
            try
            {
                BeforeIntentWriteForTesting?.Invoke("defer-intent");
                var intents = new BranchIntentStore(_conn);
                if (intents.Get(intentId) is { State: "pending" } intent)
                {
                    retryAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                        + IntentRetryDelayMilliseconds(intent.Attempts);
                    intents.Defer(intentId, retryAt.Value, error);
                }
                ExecRaw("COMMIT;");
            }
            catch
            {
                ExecRaw("ROLLBACK;");
                throw;
            }
            if (retryAt is long due)
                ScheduleIntentRetryAt(due);
            Log($"branch_intent_deferred intent_id={intentId} error={error}");
        }
        catch (Exception ex)
        {
            Log($"branch_intent_defer_failed intent_id={intentId} error={ex.GetType().Name}");
            RecordIntentBackoff(IntentKey(intentId));
        }
    }

    /// <summary>
    /// Starts every runnable branch head that has no live executor in this process, and arms one wake for the earliest
    /// future due time of the others. A head is runnable once its durable next attempt time and its in-memory backoff
    /// have both passed. Runs at startup, on each armed wake and when an intent settles or defers; never on a timer.
    /// </summary>
    private void ScheduleBranchIntentHeads()
    {
        if (_lifetime.IsCancellationRequested || _disposed)
            return;
        var heads = ReadCatalog(conn => new BranchIntentStore(conn).ListHeads().ToList());
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_inFlightLock)
        {
            var keep = heads.Select(h => IntentKey(h.Id))
                .Concat(heads.Where(h => h.QueuedEnsureId is not null).Select(h => QueuedEnsureKey(h.QueuedEnsureId!.Value)))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _intentBackoff.Keys.Where(k => !keep.Contains(k)).ToList())
                _intentBackoff.Remove(stale);
        }

        long? nextDue = null;
        foreach (var head in heads)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            if (RetryPending(head.NextAttemptAt, now))
            {
                nextDue = Math.Min(nextDue ?? long.MaxValue, head.NextAttemptAt!.Value);
                continue;
            }
            if (IntentBackoffPending(IntentKey(head.Id), now))
            {
                lock (_inFlightLock)
                    nextDue = Math.Min(nextDue ?? long.MaxValue, _intentBackoff[IntentKey(head.Id)].DueAt);
                continue;
            }
            try
            {
                switch (head.Kind)
                {
                    case "retire" or "delete":
                        RescheduleRetireIntent(head);
                        break;
                    case "ensure" when head.QueuedEnsureId is long seq:
                        RescheduleEnsureIntent(head, seq, now);
                        break;
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"branch_intent_reschedule_failed intent_id={head.Id} kind={head.Kind} error={ex.GetType().Name}");
                try
                {
                    var retryAt = GrantWrite(conn =>
                    {
                        var intents = new BranchIntentStore(conn);
                        if (intents.Get(head.Id) is { State: "pending" } current)
                        {
                            var next = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                                + IntentRetryDelayMilliseconds(current.Attempts);
                            intents.Defer(current.Id, next, $"reschedule_{ex.GetType().Name}");
                            return (long?)next;
                        }
                        return null;
                    });
                    if (retryAt is long retry)
                        ScheduleIntentRetryAt(retry);
                }
                catch (Exception writeError)
                {
                    Log($"branch_intent_defer_failed intent_id={head.Id} error={writeError.GetType().Name}");
                    RecordIntentBackoff(IntentKey(head.Id));
                }
            }
        }
        if (nextDue is long wake)
            ScheduleIntentRetryAt(wake);
        // Refresh heads (including a claim whose owner turn is gone) are started by the refresh scheduler; a deferred
        // refresh's own turn waits for its due time.
        SchedulePendingRefreshes();
    }

    private void RescheduleRetireIntent(BranchIntent head)
    {
        if (head.State != "pending")
            return;
        if (TryDropStaleRetireIntent(head))
            return;
        lock (_inFlightLock)
        {
            if (!_liveIntentRuns.Add(head.Id))
                return;
        }
        var payload = ReadRetirePayload(head);
        if (payload is null)
        {
            ForgetLiveIntentRun(head.Id);
            throw new InvalidOperationException($"Branch intent {head.Id} has no usable retirement payload.");
        }
        Log($"branch_intent_rescheduled intent_id={head.Id} kind={head.Kind} attempts={head.Attempts}");
        try
        {
            _ = StartRetireEntry(payload.Request, payload.Principal, payload.Idle, head.Id);
        }
        catch
        {
            ForgetLiveIntentRun(head.Id);
            throw;
        }
    }

    private static RetireIntentPayload? ReadRetirePayload(BranchIntent intent)
    {
        try
        {
            var payload = intent.PayloadJson is null
                ? null
                : JsonSerializer.Deserialize<RetireIntentPayload>(intent.PayloadJson, ServiceJson.Options);
            return payload is { Request.Repository: not null, Request.Branch: not null } ? payload : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Issue #273: a retire/delete head is dropped like a queued ensure, so a permanently failing one can never hold its
    /// branch: once older than <see cref="ServiceOptions.QueuedEnsureMaxAge"/> it is settled <c>expired</c>, and one whose
    /// persisted payload is unusable is settled <c>dropped</c>, both without effect (nothing is deleted, so dropping a
    /// destructive request is safe). One decide-and-commit transaction re-reads the intent, so a retirement that applied
    /// meanwhile is never overwritten. Returns whether it was dropped; later work on the branch then runs.
    /// </summary>
    private bool TryDropStaleRetireIntent(BranchIntent head)
    {
        lock (_inFlightLock)
        {
            if (_liveIntentRuns.Contains(head.Id))
                return false;
        }
        var now = _timeProvider.GetUtcNow();
        var cutoff = (now - _options.QueuedEnsureMaxAge).ToUnixTimeMilliseconds();
        var outcome = GrantWrite(conn =>
        {
            var intents = new BranchIntentStore(conn);
            if (intents.Get(head.Id) is not { State: "pending", Kind: "retire" or "delete" } current)
                return null;
            // Rule 4/7: an expired request is `expired`; one whose payload can no longer be read is `refused`.
            var drop = ReadRetirePayload(current) is null ? "refused" : current.AdmittedAt < cutoff ? "expired" : null;
            if (drop is null || !intents.Settle(current.Id, drop, now.ToUnixTimeMilliseconds()))
                return null;
            return drop;
        });
        if (outcome is null)
            return false;
        Log($"branch_intent_{outcome} intent_id={head.Id} kind={head.Kind} attempts={head.Attempts} "
            + $"last_error={head.LastError ?? "none"}");
        lock (_inFlightLock)
            _intentBackoff.Remove(IntentKey(head.Id));
        WakeBranchIntent(head.RepositoryId, head.Branch);
        ScheduleIntentRetryAt(now.ToUnixTimeMilliseconds());
        return true;
    }

    // A runnable ensure head with no live operation resumes its queued row, which re-applies the replay rules in its
    // decide-and-commit step and then starts it (or drops it, or settles an intent whose row is gone).
    private void RescheduleEnsureIntent(BranchIntent head, long seq, long now)
    {
        lock (_inFlightLock)
        {
            if (_liveQueuedEnsures.Contains(seq))
                return;
        }
        if (IntentBackoffPending(QueuedEnsureKey(seq), now))
            return;
        var active = ReadCatalog(conn =>
        {
            var intents = new BranchIntentStore(conn);
            return intents.Get(head.Id) is { State: "pending" } current
                && intents.Head(head.RepositoryId, head.Branch)?.Id == head.Id
                && !RetryPending(current.NextAttemptAt, now);
        });
        if (!active)
            return;
        Log($"branch_intent_rescheduled intent_id={head.Id} kind=ensure queued_ensure={seq}");
        ResumeQueuedRow(seq, BeforeEnsureIntentRescheduleForTesting);
    }

}
