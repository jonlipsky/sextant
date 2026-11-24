using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service;

public sealed partial class SnapshotService
{
    private readonly string _refreshTurnId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Task<EnsureSnapshotResult>> _refreshTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<EnsureSnapshotResult>> _refreshCompletions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _refreshRetryWakeups = new(StringComparer.Ordinal);
    internal Action? BeforeRefreshClaim { get; set; }
    internal Action? BeforeRefreshRead { get; set; }
    internal Action? BeforeRefreshAdmissionWrite { get; set; }
    internal Action? AfterRefreshAdmissionCommit { get; set; }
    internal Action? ProgressForTesting { get; set; }
    internal Action? BeforeConvergenceAdmissionForTesting { get; set; }
    internal Action? BeforeRefreshRetryDelayForTesting { get; set; }
    internal Action? AfterProductionResultCommitForTesting { get; set; }

    internal void WakeRefreshRetriesForTesting()
    {
        lock (_inFlightLock)
            foreach (var wakeup in _refreshRetryWakeups.Values)
                wakeup.TrySetResult();
    }

    private bool NeedsBranchConvergence(EnsureSnapshotRequest request) =>
        !request.SuppressesBranchUpdate && _remoteDefaults is { HasBranchHeadResolver: true };
    internal int RefreshTurnCountForTesting
    {
        get { lock (_inFlightLock) return _refreshTurns.Count; }
    }

    private async Task<RefreshRequestDecision> ResolveRefreshRequestAsync(
        EnsureSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (!IsRefreshRequest(request))
            return new RefreshRequestDecision(false, false);
        if (string.IsNullOrWhiteSpace(request.BranchName)
            || request.SuppressesBranchUpdate || request.ConfigHash is not null || request.TreeSha is not null)
            return new RefreshRequestDecision(true, false);

        if (_remoteDefaults is not { HasBranchHeadResolver: true })
            return new RefreshRequestDecision(true, false);
        if (ReadCatalog(conn =>
            new SnapshotStore(conn).GetRepositoryId(request.RepositoryRemoteUrl) is long repository
            && new SnapshotStore(conn).GetBranch(repository, request.BranchName) is not null))
            return new RefreshRequestDecision(true, true);
        try
        {
            using var timeout = new CancellationTokenSource(RemoteHeadTimeout, _timeProvider);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            var head = await _remoteDefaults.ObserveBranchHeadForIntakeAsync(
                request.RepositoryRemoteUrl, request.BranchName, request.LookupTenant, wait.Token)
                .WaitAsync(wait.Token).ConfigureAwait(false);
            if (head is null)
                throw new RefreshLookupUnavailableException();
            return new RefreshRequestDecision(true, !string.IsNullOrWhiteSpace(head));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new RefreshLookupUnavailableException();
        }
        catch (RemoteLookupBusyException)
        {
            throw new RefreshLookupUnavailableException();
        }

    }

    // Refresh commit/CAS fields are deliberately not persisted. They are retained only in this admission audit row.
    private static bool IsRefreshRequest(EnsureSnapshotRequest request) =>
        request.Refresh == true;

    private static EnsureSnapshotResult RejectedRefreshResult() => new()
    {
        Status = "rejected",
        Reason = BranchGuardReason.RefreshDefaultUnconfirmed
    };

    private readonly record struct RefreshRequestDecision(bool IsRefresh, bool IsDefaultConfirmed);

    private async Task<(EnsureSnapshotResult Admission, Task<EnsureSnapshotResult> Completion)> AdmitRefreshAsync(
        EnsureSnapshotRequest request, AuditCaller principal, string admissionId, CancellationToken cancellationToken)
    {
        Task<(EnsureSnapshotResult Admission, Task<EnsureSnapshotResult> Completion)> operation;
        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfStopping();
            operation = Task.Run(() => AdmitRefreshCoreAsync(request, principal, admissionId), CancellationToken.None);
            _operations.Add(operation);
        }
        _ = operation.ContinueWith(t =>
        {
            lock (_inFlightLock)
                _operations.Remove(t);
            _ = t.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await WaitForCallerAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(EnsureSnapshotResult Admission, Task<EnsureSnapshotResult> Completion)> AdmitRefreshCoreAsync(
        EnsureSnapshotRequest request, AuditCaller principal, string candidateId)
    {
        var marker = await RunGrantWriteAsync(conn =>
        {
            BeforeRefreshAdmissionWrite?.Invoke();
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var snapshots = new SnapshotStore(conn);
            var repositoryId = snapshots.GetRepositoryId(request.RepositoryRemoteUrl)
                ?? snapshots.EnsureRepository(request.RepositoryRemoteUrl, now);

            var (admitted, created) = AdmitRefreshIntent(conn, repositoryId, request.BranchName!, candidateId, now);
            PruneSettledRefreshState(conn);
            var detail = principal.Detail(
                $"admission_id={admitted.AdmissionId};commit={request.CommitSha};" +
                $"expected_head_commit={request.ExpectedHeadCommit};forced={request.Forced}");
            new AuditLogStore(conn).Append(
                AuditAction.Ensure, created ? AuditOutcome.Accepted : AuditOutcome.Coalesced,
                actor: principal.Actor, repositoryScope: request.RepositoryRemoteUrl, detail: detail);
            return (admitted, created);
        }, CancellationToken.None).ConfigureAwait(false);

        if (ReadCatalog(conn => new BranchIntentStore(conn).GetByAdmissionId(marker.admitted.AdmissionId)) is { } admittedIntent)
            WakeBranchIntent(admittedIntent.RepositoryId, admittedIntent.Branch);
        AfterRefreshAdmissionCommit?.Invoke();
        TaskCompletionSource<EnsureSnapshotResult> completion;
        lock (_inFlightLock)
        {
            if (!_refreshCompletions.TryGetValue(marker.admitted.AdmissionId, out completion!))
            {
                completion = new TaskCompletionSource<EnsureSnapshotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _refreshCompletions.Add(marker.admitted.AdmissionId, completion);
            }
        }
        var (completed, coverage) = ReadCatalog(conn =>
        {
            var finished = new PendingRefreshStore(conn).GetCompleted(marker.admitted.AdmissionId);
            return (finished, CoverageOn(conn, finished?.SnapshotId));
        });
        if (completed is not null)
        {
            completion.TrySetResult(CompletedRefreshResult(completed, coverage));
            lock (_inFlightLock)
                _refreshCompletions.Remove(completed.AdmissionId);
        }
        SchedulePendingRefreshes();
        return (new EnsureSnapshotResult
        {
            Status = marker.created ? SnapshotJobStatus.Queued : SnapshotJobStatus.Coalesced,
            AdmissionId = marker.admitted.AdmissionId,
            Attached = !marker.created
        }, completion.Task);
    }

    // Issue #273 (rule 7): there is no periodic sweep. At startup every durable intent is scheduled once: a runnable head
    // starts now, and a deferred one arms the wake for the next durable due time (ScheduleIntentRetryAt). From then on an
    // intent that defers arms its own wake. Settled intents and outcomes past the seven-day status window are pruned
    // here and on every refresh admission.
    private void StartIntentScheduling()
    {
        try
        {
            GrantWrite(conn => PruneSettledRefreshState(conn));
        }
        catch (Exception ex) when (ex is not (ObjectDisposedException or OperationCanceledException))
        {
            Log($"branch_intent_prune_failed error={ex.GetType().Name}");
        }
        ScheduleBranchIntentHeads();
    }

    private int PruneSettledRefreshState(SqliteConnection conn) =>
        new PendingRefreshStore(conn).PruneCompleted(_timeProvider.GetUtcNow().AddDays(-7).ToUnixTimeMilliseconds());

    private void SchedulePendingRefreshes()
    {
        if (_lifetime.IsCancellationRequested || _disposed)
            return;

        var pending = ReadCatalog(conn => new PendingRefreshStore(conn).ListPending());
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var marker in pending)
        {
            var head = ReadCatalog(conn => new BranchIntentStore(conn).Head(marker.RepositoryId, marker.Branch));
            if (head is not { Kind: "refresh", AdmissionId: var headAdmission }
                || !string.Equals(headAdmission, marker.AdmissionId, StringComparison.Ordinal))
                continue;

            lock (_inFlightLock)
            {
                if (_refreshTurns.ContainsKey(marker.AdmissionId))
                {
                    if (!RetryPending(marker.NextAttemptAt, now) && _refreshRetryWakeups.TryGetValue(marker.AdmissionId, out var wakeup))
                        wakeup.TrySetResult();
                    continue;
                }
                if (_disposed)
                    continue;
                // A turn whose claim could not be returned waits out its bounded backoff before the reclaim.
                if (IntentBackoffPending(IntentKey(head.Id), now))
                    continue;

                if (!_refreshCompletions.TryGetValue(marker.AdmissionId, out var completion))
                {
                    completion = new TaskCompletionSource<EnsureSnapshotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _refreshCompletions.Add(marker.AdmissionId, completion);
                }

                Task? turn = !RetryPending(marker.NextAttemptAt, now)
                    ? TakeWriteTurnLocked(out _, expedited: true, ensureAdmission: true)
                    : null;
                var operation = Task.Run(
                    () => RunPendingRefreshAsync(marker.AdmissionId, completion, turn), CancellationToken.None);
                _refreshTurns.Add(marker.AdmissionId, operation);
                _operations.Add(operation);
                _ = operation.ContinueWith(
                    t =>
                    {
                        lock (_inFlightLock)
                        {
                            _refreshTurns.Remove(marker.AdmissionId);
                            _operations.Remove(t);
                        }
                        ProgressForTesting?.Invoke();
                        if (t.IsFaulted)
                        {
                            Log($"pending_refresh_stopped admission_id={marker.AdmissionId} error={t.Exception?.GetBaseException().GetType().Name}");
                            // Rule 7: a turn that stopped on a fault retries on its own bounded due time.
                            RecordIntentBackoff(IntentKey(head.Id));
                        }
                        _ = t.Exception;
                    },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task<EnsureSnapshotResult> RunPendingRefreshAsync(
        string admissionId, TaskCompletionSource<EnsureSnapshotResult> completion, Task? firstTurn)
    {
        var lifetime = _lifetime.Token;
        var turn = firstTurn;
        try
        {
            while (true)
            {
                BeforeRefreshRead?.Invoke();
                lifetime.ThrowIfCancellationRequested();
                TaskCompletionSource wakeup;
                lock (_inFlightLock)
                    _refreshRetryWakeups[admissionId] = wakeup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var marker = ReadCatalog(conn => new PendingRefreshStore(conn).GetByAdmissionId(admissionId));
                if (marker is null)
                {
                    var finished = ReadCatalog(conn => new PendingRefreshStore(conn).GetCompleted(admissionId))
                        ?? throw new InvalidOperationException($"Pending refresh marker {admissionId} disappeared before completion.");
                    var settled = CompletedRefreshResult(finished);
                    completion.TrySetResult(settled);
                    lock (_inFlightLock)
                        _refreshCompletions.Remove(admissionId);
                    return settled;
                }

                var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                if (RetryPending(marker.NextAttemptAt, now))
                {
                    using var retryWait = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    BeforeRefreshRetryDelayForTesting?.Invoke();
                    var delay = Task.Delay(TimeSpan.FromMilliseconds(marker.NextAttemptAt - now), _timeProvider, retryWait.Token);
                    if (RetryPending(marker.NextAttemptAt, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()))
                        await Task.WhenAny(delay, wakeup.Task).ConfigureAwait(false);
                    await retryWait.CancelAsync().ConfigureAwait(false);
                    lifetime.ThrowIfCancellationRequested();
                    lock (_inFlightLock)
                        _refreshRetryWakeups.Remove(admissionId);
                    continue;
                }
                lock (_inFlightLock)
                    _refreshRetryWakeups.Remove(admissionId);

                turn ??= TakeRefreshTurn();
                var granted = false;
                var claimed = false;
                // Issue #273 (B1): this turn's claim token. It is live only while the turn runs, so a claim it leaves
                // behind (when even returning the claim fails) is reclaimable by a later turn of this process.
                var claimTurn = Guid.NewGuid().ToString("N");
                long? claimedIntentId = null;
                var stopping = false;
                Exception? failure = null;
                var failedWhileClaimed = false;
                var returnFailed = false;
                lock (_inFlightLock)
                    _liveIntentTurns.Add(claimTurn);
                try
                {
                    await turn.ConfigureAwait(false);
                    granted = true;
                    using var owner = EnterWriterTurn(turn);
                    EnsureLeaseHeld();
                    BeforeRefreshClaim?.Invoke();
                    var claimTime = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                    BeginRaw("claim-refresh");
                    try
                    {
                        var intent = new BranchIntentStore(_conn).GetByAdmissionId(admissionId)
                            ?? throw new InvalidOperationException($"Pending refresh {admissionId} has no branch intent.");
                        var intents = new BranchIntentStore(_conn);
                        var intentClaimed = intents.Claim(intent.Id, claimTurn, IsLiveIntentTurn);
                        var markerClaimed = intentClaimed
                            && new PendingRefreshStore(_conn).Claim(admissionId, _refreshTurnId, claimTime);
                        if (intentClaimed && !markerClaimed)
                            intents.Unclaim(intent.Id);
                        claimed = intentClaimed && markerClaimed;
                        if (claimed)
                            claimedIntentId = intent.Id;
                        ExecRaw("COMMIT;");
                        ProgressForTesting?.Invoke();
                    }
                    catch
                    {
                        ExecRaw("ROLLBACK;");
                        throw;
                    }
                    if (!claimed)
                    {
                        var current = ReadCatalog(conn => new PendingRefreshStore(conn).GetByAdmissionId(admissionId));
                        if (current is null)
                        {
                            var completed = ReadCatalog(conn => new PendingRefreshStore(conn).GetCompleted(admissionId));
                            var missingResult = completed is { } finished
                                ? CompletedRefreshResult(finished)
                                : new EnsureSnapshotResult { Status = "unknown", AdmissionId = admissionId };
                            completion.TrySetResult(missingResult);
                            return missingResult;
                        }
                        Log($"pending_refresh_claim_failed admission_id={admissionId}");
                        ArmRefreshRetry(admissionId);
                        return new EnsureSnapshotResult { Status = SnapshotJobStatus.Queued, AdmissionId = admissionId };
                    }

                    marker = ReadCatalog(conn => new PendingRefreshStore(conn).GetByAdmissionId(admissionId));
                    if (marker is null)
                        throw new InvalidOperationException($"Pending refresh marker {admissionId} disappeared after claim.");
                    // Rule 7: a refresh whose latest admission (an absorbed push included) is older than the maximum age
                    // expires without effect.
                    var latestAdmission = ReadCatalog(conn => new BranchIntentStore(conn).GetByAdmissionId(admissionId))
                        ?.AdmittedAt ?? marker.AdmittedAt;
                    if (latestAdmission < (_timeProvider.GetUtcNow() - _options.QueuedEnsureMaxAge).ToUnixTimeMilliseconds())
                    {
                        var expired = ExpireRefresh(marker, claimTurn);
                        claimed = false;
                        completion.TrySetResult(expired);
                        lock (_inFlightLock)
                            _refreshCompletions.Remove(admissionId);
                        return expired;
                    }
                    var head = await ReadFreshRepairHeadAsync(marker.RepositoryUrl, marker.Branch).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(head))
                    {
                        // Rule 1 indexes only a head GitHub reports. An unknown head (unreachable remote, or a branch
                        // absent upstream; deleting it is out of scope) retries on its own due time (rule 7).
                        RetryRefresh(marker, claimTurn, "remote_head_unknown");
                        claimed = false;
                        continue;
                    }
                    var request = new EnsureSnapshotRequest
                    {
                        RepositoryRemoteUrl = marker.RepositoryUrl,
                        CommitSha = head,
                        BranchName = marker.Branch,
                        BranchUpdate = BranchUpdateMode.None
                    };
                    request = PinEnsureRepositorySpelling(request).Request;
                    var result = await ProduceOrAttachRefreshAsync(request, firstAttempt: marker.Attempts == 0).ConfigureAwait(false);
                    if (result.Status is SnapshotJobStatus.Complete or SnapshotJobStatus.Partial)
                    {
                        RemoteBranchStateObservation? observation;
                        try
                        {
                            observation = await _remoteDefaults!.ObserveBranchStateAsync(
                                marker.RepositoryUrl, marker.Branch, lifetime).ConfigureAwait(false);
                        }
                        catch (RemoteLookupBusyException)
                        {
                            // Saturated lookup capacity is the same unobservable final state: hand off below.
                            observation = null;
                        }
                        if (observation is null)
                        {
                            // Its head is built; only the final head observation is missing: retry on its due time.
                            RetryRefresh(marker, claimTurn, "remote_state_unknown");
                            claimed = false;
                            continue;
                        }
                        var ownsDefault = string.Equals(observation.DefaultBranch, marker.Branch, StringComparison.Ordinal);
                        if (!string.Equals(observation.Head, head, StringComparison.OrdinalIgnoreCase))
                        {
                            RetryRefresh(marker, claimTurn, "remote_head_changed", immediate: true);
                            claimed = false;
                            continue;
                        }
                        if (result.SnapshotId is long target
                            && RepairRefreshPointer(marker, head, target, ownsDefault))
                            result = result with { BranchAdvanced = true };
                        var settledDuringRepair = ReadCatalog(conn =>
                            new PendingRefreshStore(conn).GetCompleted(admissionId));
                        if (settledDuringRepair is not null)
                        {
                            claimed = false;
                            var settled = CompletedRefreshResult(settledDuringRepair);
                            completion.TrySetResult(settled);
                            lock (_inFlightLock)
                                _refreshCompletions.Remove(admissionId);
                            return settled;
                        }
                        var snapshots = new SnapshotStore(_conn);
                        var repositoryId = snapshots.GetRepositoryId(marker.RepositoryUrl);
                        var branchId = repositoryId is long id ? snapshots.GetBranchId(id, marker.Branch) : null;
                        if (result.SnapshotId is not long snapshotId || branchId is not long branch
                            || snapshots.GetBranchSnapshotId(branch) != snapshotId)
                        {
                            RetryRefresh(marker, claimTurn, "pointer_not_advanced");
                            claimed = false;
                            continue;
                        }

                        BeginRaw("complete-refresh");
                        try
                        {
                            if (!new PendingRefreshStore(_conn).CompleteClaimed(
                                    admissionId, _refreshTurnId, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                                    snapshotId, head))
                                throw new InvalidOperationException($"Pending refresh marker {admissionId} lost its claim.");
                            new BranchIntentStore(_conn).SettleByAdmissionId(
                                admissionId, "completed", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                            ExecRaw("COMMIT;");
                            claimed = false;
                            ProgressForTesting?.Invoke();
                        }
                        catch
                        {
                            ExecRaw("ROLLBACK;");
                            throw;
                        }
                        var completed = result with { JobId = null, IdentityHash = null, AdmissionId = admissionId };
                        completion.TrySetResult(completed);
                        lock (_inFlightLock)
                            _refreshCompletions.Remove(admissionId);
                        WakeBranchIntent(marker.RepositoryId, marker.Branch);
                        SchedulePendingRefreshes();
                        return completed;
                    }

                    RetryRefresh(marker, claimTurn, $"index_{result.Status}");
                    claimed = false;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    stopping = true;
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    failedWhileClaimed = claimed;
                    Log($"pending_refresh_failed admission_id={admissionId} error={ex.GetType().Name}");
                }
                finally
                {
                    // Issue #273 (B1): a turn that ends still holding its claim (exception, transient write failure,
                    // cancellation, shutdown) returns it to pending with a next attempt time, on this same turn.
                    if (claimed && granted)
                        returnFailed = !ReturnRefreshClaim(
                            admissionId, claimTurn, claimedIntentId,
                            stopping ? "shutdown" : $"turn_{failure?.GetType().Name ?? "unsettled"}", stopping, turn!);
                    claimed = false;
                    lock (_inFlightLock)
                        _liveIntentTurns.Remove(claimTurn);
                    if (turn is not null)
                        ReleaseTurn(turn, granted);
                    turn = null;
                    ProgressForTesting?.Invoke();
                }
                // Rule 7: a failure before the claim, or a claim that could not be returned, retries on its own bounded
                // due time (an in-memory backoff with an armed wake; a claim left behind is reclaimed as owner-gone).
                if (failure is not null && (!failedWhileClaimed || returnFailed))
                {
                    if (!returnFailed)
                        ArmRefreshRetry(admissionId);
                    return new EnsureSnapshotResult { Status = SnapshotJobStatus.Queued, AdmissionId = admissionId };
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            completion.TrySetCanceled(lifetime);
            throw;
        }
        finally
        {
            lock (_inFlightLock)
                _refreshRetryWakeups.Remove(admissionId);
            if (turn is not null)
                ReleaseTurn(turn, granted: false);
        }
    }

    // Rule 7: a refresh turn that ended without a durable retry time retries after a bounded in-memory backoff.
    private void ArmRefreshRetry(string admissionId)
    {
        try
        {
            if (ReadCatalog(conn => new BranchIntentStore(conn).GetByAdmissionId(admissionId)) is { } intent)
                RecordIntentBackoff(IntentKey(intent.Id));
        }
        catch (Exception ex) when (ex is not (ObjectDisposedException or OperationCanceledException))
        {
            Log($"pending_refresh_retry_arm_failed admission_id={admissionId} error={ex.GetType().Name}");
        }
    }

    // Returns a claim the turn could not settle to pending, on the still-granted turn. False when that write failed:
    // the claim token then dies with the turn (owner gone) and an in-memory backoff schedules the reclaim.
    private bool ReturnRefreshClaim(
        string admissionId, string claimTurn, long? intentId, string error, bool stopping, Task turn)
    {
        try
        {
            using var owner = EnterWriterTurn(turn);
            var marker = ReadCatalog(conn => new PendingRefreshStore(conn).GetByAdmissionId(admissionId));
            if (marker is null || marker.ClaimedByTurn != _refreshTurnId)
                return true;
            RetryRefresh(marker, claimTurn, error, immediate: stopping, countAttempt: !stopping);
            return true;
        }
        catch (Exception ex)
        {
            Log($"pending_refresh_retry_failed admission_id={admissionId} error={ex.GetType().Name}");
            if (intentId is long id)
                RecordIntentBackoff(IntentKey(id));
            return false;
        }
    }

    private Task TakeRefreshTurn()
    {
        lock (_inFlightLock)
        {
            ThrowIfStopping();
            return TakeWriteTurnLocked(out _, expedited: true, ensureAdmission: true);
        }
    }

    // Issue #273: admits a refresh request on a branch. Directly behind a pending, unclaimed refresh it is absorbed into
    // that one (no new intent or marker; the caller gets that refresh's admission). Otherwise it joins the end of the
    // branch queue with its own marker. Caller owns the transaction.
    private (PendingRefresh Marker, bool Created) AdmitRefreshIntent(
        SqliteConnection conn, long repositoryId, string branch, string candidateId, long now)
    {
        var intent = new BranchIntentStore(conn).Admit(repositoryId, branch, "refresh", null, candidateId, null, now);
        var refreshes = new PendingRefreshStore(conn);
        if (!string.Equals(intent.AdmissionId, candidateId, StringComparison.Ordinal))
            return (refreshes.GetByAdmissionId(intent.AdmissionId!)
                ?? throw new InvalidOperationException($"Refresh {intent.AdmissionId} has no marker."), false);
        return refreshes.Admit(repositoryId, branch, candidateId, now);
    }

    // Rule 7: settles a claimed refresh past the maximum age as expired, without effect, and removes its marker.
    private EnsureSnapshotResult ExpireRefresh(PendingRefresh marker, string claimTurn)
    {
        BeginRaw("expire-refresh");
        try
        {
            if (!new PendingRefreshStore(_conn).DeleteClaimed(marker.AdmissionId, _refreshTurnId))
                throw new InvalidOperationException($"Pending refresh marker {marker.AdmissionId} lost its claim.");
            new BranchIntentStore(_conn).SettleByAdmissionId(
                marker.AdmissionId, "expired", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            ExecRaw("COMMIT;");
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
        Log($"refresh_expired admission_id={marker.AdmissionId} attempts={marker.Attempts} last_error={marker.LastError ?? "none"}");
        WakeBranchIntent(marker.RepositoryId, marker.Branch);
        SchedulePendingRefreshes();
        return new EnsureSnapshotResult { Status = "expired", AdmissionId = marker.AdmissionId, Reason = "refresh_expired" };
    }

    private bool RepairRefreshPointer(PendingRefresh marker, string verifiedHead, long target, bool ownsDefault)
    {
        EnsureLeaseHeld();
        var snapshots = new SnapshotStore(_conn);
        BeginRaw("repair-refresh-pointer");
        try
        {
            if (new PendingRefreshStore(_conn).GetByAdmissionId(marker.AdmissionId)?.ClaimedByTurn != _refreshTurnId)
            {
                ExecRaw("ROLLBACK;");
                return false;
            }
            var branch = snapshots.GetBranch(marker.RepositoryId, marker.Branch);
            var published = snapshots.GetById(target);
            var current = branch?.SnapshotId is long id ? snapshots.GetById(id) : null;
            var commitless = current is { Status: SnapshotStatus.Complete }
                && snapshots.GetCommitSha(current.CommitId) is null;
            var repair = branch?.SnapshotId != target
                && published is { Status: SnapshotStatus.Complete, PublishedAt: not null,
                    IsOverlay: false, IsProvider: false, WorkingTreeDelta: null }
                && published.RepositoryId == marker.RepositoryId
                && string.Equals(snapshots.GetCommitSha(published.CommitId), verifiedHead, StringComparison.OrdinalIgnoreCase);
            if (repair)
            {
                var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var branchId = branch?.Id ?? snapshots.EnsureBranch(marker.RepositoryId, marker.Branch, isDefault: false, now);
                if (commitless)
                {
                    snapshots.SetBranchPointer(branchId, target, now);
                    if (current is not null && !snapshots.IsPointedByAnotherBranch(current.Id, branchId))
                        snapshots.MarkStatus(current.Id, SnapshotStatus.Superseded);
                }
                else
                    snapshots.AdvanceBranchPointerIfHeadMatches(branchId, target,
                        snapshots.GetBranchHeadCommit(branchId) ?? "", now);
                new AuditLogStore(_conn).Append(AuditAction.Reconcile, AuditOutcome.Complete,
                    actor: RefreshPolicyCaller.Actor, repositoryScope: marker.RepositoryUrl,
                    detail: $"refresh_pointer_repaired;snapshot={target}");
            }
            if (ownsDefault && snapshots.GetBranchId(marker.RepositoryId, marker.Branch) is long selectedBranch
                && snapshots.GetBranchSnapshotId(selectedBranch) == target)
                snapshots.SetDefaultBranch(marker.RepositoryId, marker.Branch,
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            ExecRaw("COMMIT;");
            return repair;
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
    }

    private void RetryRefresh(
        PendingRefresh marker, string claimTurn, string error, bool immediate = false, bool countAttempt = true)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var next = immediate ? now : now + IntentRetryDelayMilliseconds(marker.Attempts);
        BeginRaw("retry-refresh");
        try
        {
            BeforeIntentWriteForTesting?.Invoke("retry-refresh");
            if (!new PendingRefreshStore(_conn).Retry(marker.AdmissionId, _refreshTurnId, next, error, countAttempt))
                throw new InvalidOperationException($"Pending refresh marker {marker.AdmissionId} lost its claim.");
            var intents = new BranchIntentStore(_conn);
            var intent = intents.GetByAdmissionId(marker.AdmissionId);
            if (intent is not null)
                intents.Release(intent.Id, claimTurn, next, error, countAttempt);
            ExecRaw("COMMIT;");
            ProgressForTesting?.Invoke();
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
    }

    private async Task<EnsureSnapshotResult> ProduceOrAttachRefreshAsync(EnsureSnapshotRequest request, bool firstAttempt)
    {
        var hash = IdentityHashFor(request);
        var snapshots = new SnapshotStore(_conn);
        var repository = snapshots.GetRepositoryId(request.RepositoryRemoteUrl);
        var branch = repository is long repo ? snapshots.GetBranchId(repo, request.BranchName!) : null;
        if (branch is long selectedBranch && snapshots.GetBranchSnapshotId(selectedBranch) is long selected
            && snapshots.GetById(selected) is
                { Status: SnapshotStatus.Complete, PublishedAt: not null, IsOverlay: false,
                    IsProvider: false, WorkingTreeDelta: null } current
            && current.RepositoryId == repository
            && string.Equals(snapshots.GetCommitSha(current.CommitId), request.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            var coverage = CoverageOn(_conn, selected);
            var selectedJob = new SnapshotJobStore(_conn).GetJobByIdentity(current.IdentityHash);
            var compatibleHash = hash;
            if (selectedJob is not null
                && RemoteUrlIdentity.Normalize(selectedJob.RepositoryUrl) == RemoteUrlIdentity.Normalize(request.RepositoryRemoteUrl)
                && string.Equals(selectedJob.CommitSha, request.CommitSha, StringComparison.OrdinalIgnoreCase))
                compatibleHash = IdentityHashFor(request with { RepositoryRemoteUrl = selectedJob.RepositoryUrl, CommitSha = selectedJob.CommitSha });
            if (string.Equals(coverage?.Rebuild?.OriginalIdentityHash, compatibleHash, StringComparison.Ordinal)
                || (selectedJob is not null && selectedJob.RepositoryUrl != request.RepositoryRemoteUrl
                    && string.Equals(current.IdentityHash, compatibleHash, StringComparison.Ordinal)))
            {
                return new EnsureSnapshotResult
                {
                    Status = coverage is { IsPartial: true } ? SnapshotJobStatus.Partial : SnapshotJobStatus.Complete,
                    SnapshotId = selected,
                    Coverage = coverage,
                    Reason = coverage is { IsPartial: true } ? PartialCoverageReason(coverage) : null,
                    BranchAdvanced = false
                };
            }
        }
        var jobs = new SnapshotJobStore(_conn);
        // Rule 7: on its first attempt a refresh does not produce again an identity that failed within the last retry
        // interval (the ensure it follows just ran it); it attaches that result and retries on its own backoff.
        if (jobs.GetJobByIdentity(hash) is { Status: SnapshotJobStatus.Failed or SnapshotJobStatus.Unsupported or SnapshotJobStatus.Cancelled } failed)
        {
            if (firstAttempt
                && failed.UpdatedAt > _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - IntentRetryDelayMilliseconds(0))
                return new EnsureSnapshotResult
                {
                    JobId = failed.Id, IdentityHash = hash, Status = failed.Status, Attached = true
                };
            jobs.Requeue(failed.Id);
        }

        var (job, existed, attached) = TryAttachTerminal(request, hash);
        if (attached is not null)
        {
            RecordEnsureAuditLocked(request, attached, RefreshPolicyCaller, null);
            return attached;
        }

        return await StartProduction(request, hash, job.Id, existed, RefreshPolicyCaller, seq: null, isRefresh: true)
            .ConfigureAwait(false);
    }

    private static readonly AuditCaller RefreshPolicyCaller = new("refresh", ";source=refresh_marker");

    private static EnsureSnapshotResult CompletedRefreshResult(
        CompletedRefreshAdmission completed, SnapshotCoverage? coverage = null) =>
        new()
        {
            Status = completed.Status == "expired"
                ? "expired"
                : coverage is { IsPartial: true } ? SnapshotJobStatus.Partial : SnapshotJobStatus.Complete,
            SnapshotId = completed.SnapshotId,
            AdmissionId = completed.AdmissionId,
            Reason = completed.Status == "expired"
                ? "refresh_expired"
                : coverage is { IsPartial: true } ? PartialCoverageReason(coverage) : null,
            Coverage = coverage
        };
}

public sealed class RefreshLookupUnavailableException()
    : Exception("The remote branch confirmation is unavailable; retry the refresh.");
