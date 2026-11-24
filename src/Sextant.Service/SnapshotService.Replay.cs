using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Service.Grants;
using Sextant.Store;

namespace Sextant.Service;

// Issue #273 (rule 6): restart replay of durable queued ensures. Every queued row was admitted by this schema (migration
// 041 clears rows queued before the upgrade), so a branch-moving row already has its branch intent and following
// refresh; on restart durable intents simply resume in FIFO order. Each row is resumed by one decide-and-commit step: in
// a single transaction it reloads the row and re-applies the drop rules (age: `expired`; an unusable request, the URL
// policy or a revoked user grant: `refused`), deleting a dropped row and settling its intent in that transaction, and
// otherwise starts the row's ensure, which waits for its branch head. A step that fails is retried on its own bounded
// due time (rule 7). One in-process guard per row keeps two callers (startup, a due-time wake) from starting it twice.
public sealed partial class SnapshotService
{
    private static EnsureSnapshotRequest? TryReadQueuedRequest(QueuedRow row)
    {
        try
        {
            return JsonSerializer.Deserialize<EnsureSnapshotRequest>(row.Json, ServiceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The one replay check, shared by startup, replay retries and recovery: null when the queued ensure may run, else
    // why it is dropped. It reads the grants on the given connection, so the drop transaction can re-decide atomically.
    private (string Reason, Func<Task>? Audit)? QueuedEnsureRejectionOn(
        SqliteConnection conn, QueuedRow row, EnsureSnapshotRequest? request)
    {
        if (DateTimeOffset.FromUnixTimeMilliseconds(row.AdmittedAt) < _timeProvider.GetUtcNow() - _options.QueuedEnsureMaxAge)
            return ($"older than {_options.QueuedEnsureMaxAge}", null);
        if (request is null || request.BranchGuardProblem() is not null)
            return ("its request is unusable", null);
        var actor = new AuditCaller(null, (row.Suffix ?? "") + ";replay", row.Actor);
        var decision = _options.RepositoryUrlPolicy.Evaluate(request.RepositoryRemoteUrl);
        if (!decision.Ok)
            return ($"the repository URL policy now refuses it ({decision.Reason})",
                () => RecordEnsureDeniedAsync($"replay_{decision.Reason}", actor, _lifetime.Token));
        if (row.CallerSubject is not null)
        {
            var key = RepositoryGrantKey.Of(request.RepositoryRemoteUrl);
            var visible = new RepositoryGrantStore(conn)
                .VisibleKeys(row.CallerTenant ?? "", row.CallerSubject, LeaseCutoff(), LeaseCeiling());
            if (!visible.Contains(key))
                return ("its user caller no longer has a grant on the repository",
                    () => RecordUserEnsureDeniedAsync(key, $"replay_{GrantReason.NotGranted}", actor, _lifetime.Token));
        }
        return null;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _rowOwners = new();

    /// <summary>Test hook run inside a row's decide-and-commit transaction; it may throw to fail that step.</summary>
    internal Action<long>? RowOwnerStepForTesting { get; set; }
    /// <summary>Test hook run after a row's admission committed, before it starts the operation; it may throw.</summary>
    internal Action<long>? RowOwnerStartForTesting { get; set; }
    internal bool HasRowOwnerForTesting(long seq) => _rowOwners.ContainsKey(seq);

    // Resumes one queued row unless another caller is resuming it now. A failed step is retried on its own due time.
    private void ResumeQueuedRow(long seq, Action? afterAcquired = null)
    {
        if (!_rowOwners.TryAdd(seq, 0))
            return;
        try
        {
            // A failure here belongs to the caller (the head scan records it as the intent's durable retry time).
            afterAcquired?.Invoke();
        }
        catch
        {
            _rowOwners.TryRemove(seq, out _);
            throw;
        }
        bool done;
        try
        {
            done = ResumeQueuedRowStep(seq);
        }
        catch (Exception ex) when (ex is not (ObjectDisposedException or OperationCanceledException))
        {
            Log($"queued_row_retry seq={seq} error={ex.GetType().Name}");
            done = false;
        }
        finally
        {
            _rowOwners.TryRemove(seq, out _);
        }
        if (done)
            _rowRetryDelays.TryRemove(seq, out _);
        else
            ScheduleQueuedRowRetry(seq);
    }

    // Rule 7: the row's next attempt runs on its own bounded backoff (1 s doubling to 60 s), not on a sweep.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TimeSpan> _rowRetryDelays = new();

    private void ScheduleQueuedRowRetry(long seq)
    {
        var delay = _rowRetryDelays.AddOrUpdate(seq, TimeSpan.FromSeconds(1),
            (_, previous) => TimeSpan.FromSeconds(Math.Min(previous.TotalSeconds * 2, 60)));
        Task retry;
        lock (_inFlightLock)
        {
            if (_disposed || _lifetime.IsCancellationRequested)
                return;
            var wait = Task.Delay(delay, _timeProvider, _lifetime.Token);
            retry = Task.Run(async () =>
            {
                await wait.ConfigureAwait(false);
                ResumeQueuedRow(seq);
            }, CancellationToken.None);
            _operations.Add(retry);
        }
        _ = retry.ContinueWith(t =>
        {
            lock (_inFlightLock)
                _operations.Remove(t);
            _ = t.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private enum RowDecision { Gone, Dropped, Admitted }

    // One decide-and-commit step. True when a durable state ends the row's need to be resumed.
    private bool ResumeQueuedRowStep(long seq)
    {
        if (_lifetime.IsCancellationRequested || _disposed)
            return true;
        string? reason = null;
        Func<Task>? audit = null;
        QueuedRow queued = default;
        EnsureSnapshotRequest? request = null;
        long? deferredUntil = null;
        var decision = GrantWrite(conn =>
        {
            RowOwnerStepForTesting?.Invoke(seq);
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (ReadQueuedRow(conn, seq) is not { } row)
            {
                // The durable request is gone (it settled): an intent left without it can never run.
                new BranchIntentStore(conn).SettleByQueuedEnsureId(seq, "refused", now);
                return RowDecision.Gone;
            }
            queued = row;
            request = TryReadQueuedRequest(row);
            if (QueuedEnsureRejectionOn(conn, row, request) is { } rejection)
            {
                (reason, audit) = rejection;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM queued_ensures WHERE seq = @seq;";
                cmd.Parameters.AddWithValue("@seq", seq);
                cmd.ExecuteNonQuery();
                // Rule 4: a dropped request ends with a stated outcome.
                new BranchIntentStore(conn).SettleByQueuedEnsureId(
                    seq, reason.StartsWith("older than", StringComparison.Ordinal) ? "expired" : "refused", now);
                _metrics.RecordQueuedEnsureFinished(seq);
                return RowDecision.Dropped;
            }
            request = request! with { RestrictsImplicitDefault = row.Restricts, LookupTenant = row.Tenant };
            if (NeedsBranchConvergence(request))
            {
                var intents = new BranchIntentStore(conn);
                var intent = intents.GetByQueuedEnsureId(seq);
                if (intent is null)
                {
                    // Admitted while convergence was off (the remote-head resolver was configured since): admit it now,
                    // at the end of its branch queue, with its following refresh, as a new admission would be.
                    var snapshots = new SnapshotStore(conn);
                    var repository = snapshots.GetRepositoryId(request.RepositoryRemoteUrl)
                        ?? snapshots.EnsureRepository(request.RepositoryRemoteUrl, now);
                    intent = intents.Admit(repository, request.BranchName ?? "main", "ensure", seq, null,
                        JsonSerializer.Serialize(request, ServiceJson.Options), now);
                    AdmitFollowingRefresh(conn, intent);
                }
                deferredUntil = intent.State == "pending" ? intent.NextAttemptAt : null;
            }
            return RowDecision.Admitted;
        }, synchronizeQueuedMetrics: true);

        switch (decision)
        {
            case RowDecision.Gone:
                WakeBranchIntentForQueuedEnsure(seq);
                return true;
            case RowDecision.Dropped:
                Log($"queued_ensure_dropped seq={seq}: {reason}");
                if (audit is not null)
                    Observe(audit());
                WakeBranchIntentForQueuedEnsure(seq);
                return true;
        }

        if (RetryPending(deferredUntil, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()))
        {
            // A durable intent waits for its persisted retry time; its wake starts it then.
            ScheduleIntentRetryAt(deferredUntil!.Value);
            return true;
        }
        lock (_inFlightLock)
        {
            if (_liveQueuedEnsures.Contains(seq))
                return true;
        }
        RowOwnerStartForTesting?.Invoke(seq);
        var (_, completion, _, _, _, _) = StartEnsureOperation(request!,
            new AuditCaller(null, queued.Suffix, queued.Actor, queued.CallerTenant, queued.CallerSubject),
            seq, queued.AdmittedAt);
        Observe(completion);
        return true;
    }
}
