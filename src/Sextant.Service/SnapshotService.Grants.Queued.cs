using System.Text.Json;
using Sextant.Service.Grants;
using Sextant.Store;

namespace Sextant.Service;

// Issue #406: durable admission. An ensure admitted (it holds a writer turn) but not yet started is recorded in the
// catalog's queued_ensures table, so a restart re-admits it instead of leaving it to the upstream heal and the app's
// reconcile. The row is written on a grant-style side connection (never under the write gate, so admission never
// waits behind a production) and removed when the ensure ends. It is KEPT when shutdown, a lost lease or a transient
// requeue ended the ensure, which is exactly the case a restart must resume. At startup the rows are replayed through
// StartEnsureOperation in admission order, so branch refresh deduplication and
// the idempotent attach of an already-built identity all apply exactly
// as for a live admission. Branch-moving remote ensures require durable convergence admission before running.
public sealed partial class SnapshotService
{
    private long _ensureSeq;
    // Per branch, the latest live ensure admission: an identical request may share it. A branch-moving remote ensure
    // shares only when its admission transaction finds that ensure's intent still the branch's newest (issue #273, D2).
    // An ensure without an intent (locate mode, branch_update: none, a terminal failure attach) keeps the in-process
    // barrier instead: a different request on this branch or any non-ensure control write admitted in between.
    private readonly Dictionary<string, PendingEnsureAdmission> _ensureAdmissions = new(StringComparer.Ordinal);
    private long _ensureAdmissionBarrier;
    private long _duplicateEnsureAdmissions;
    internal Action? BeforeQueuedEnsureWriteForTesting { get; set; }

    private static string EnsureAdmissionKey(EnsureSnapshotRequest request) =>
        Sextant.Core.RemoteUrlIdentity.Normalize(request.RepositoryRemoteUrl) + "\n" + (request.BranchName ?? "main");

    private sealed record PendingEnsureAdmission(
        EnsureSnapshotRequest Request, AuditCaller Principal, long Seq, long WriteAdmission,
        string? AdmissionId, Task<EnsureSnapshotResult> Accepted, Task<EnsureSnapshotResult> Completion,
        Task<EnsureAdmissionOutcome> Admission);

    private void DeleteSettledQueuedEnsureLocked(long seq, EnsureSnapshotResult result)
    {
        if (_lifetime.IsCancellationRequested || LeaseLost || !SnapshotJobStatus.IsSettled(result.Status))
            return;
        BeginRaw("settle-queued-ensure");
        int removed;
        try
        {
            removed = _metrics.WithQueuedEnsureLock(() =>
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM queued_ensures WHERE seq = @seq;";
                cmd.Parameters.AddWithValue("@seq", seq);
                var count = cmd.ExecuteNonQuery();
                new BranchIntentStore(_conn).SettleByQueuedEnsureId(
                    seq, result.Status, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                ExecRaw("COMMIT;");
                return count;
            });
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
        if (removed > 0)
        {
            WakeBranchIntentForQueuedEnsure(seq);
            Log($"queued_ensure_settled seq={seq} status={result.Status} removed={removed}");
            SchedulePendingRefreshes();
        }
    }

    // The branch intent an ensure admission resolved to (issue #273, D2): its own queued sequence, or the sequence of the
    // identical earlier ensure it shares, with that intent's public admission id.
    private readonly record struct EnsureAdmissionOutcome(long Seq, string? AdmissionId)
    {
        public bool SharedWith(long seq) => Seq != seq;
    }

    // An identical earlier ensure this admission may share: its resolved admission and whether its operation is live.
    private sealed record EnsureShareCandidate(Task<EnsureAdmissionOutcome> Admission, Func<bool> Live);

    // Records the queued ensure and, for a branch-moving remote ensure, its intent and following refresh, in one catalog
    // transaction. Issue #273 (D2): when an identical earlier ensure is still live, the same transaction first decides
    // whether a LIVE request shares it: only while that ensure's intent is the branch's newest (only its own following
    // refresh may follow it). Otherwise this ensure is admitted on its own and runs after every intervening intent.
    // The decision is stored by its effect: a share admits no intent, and an admitted intent always takes its own turn.
    private EnsureAdmissionOutcome PersistQueuedEnsure(
        long seq, EnsureSnapshotRequest request, string hash, AuditCaller principal, long admittedAt,
        bool bestEffort = false, string? admissionId = null,
        long? shareWithSeq = null, Func<bool>? shareLive = null, Action? onAdmitted = null)
    {
        try
        {
            var outcome = GrantWrite(conn =>
            {
                var convergence = NeedsBranchConvergence(request);
                // The share decision is made here, once, for a live request: a share admits nothing, so a replayed
                // queued ensure (durable, so never a share) always takes its own turn and nothing re-derives it.
                if (convergence && shareWithSeq is long target && target != seq && (shareLive?.Invoke() ?? true))
                {
                    var shareIntents = new BranchIntentStore(conn);
                    if (shareIntents.GetByQueuedEnsureId(target) is { } shared
                        && shareIntents.IsNewestEnsureBefore(shared.Id))
                        return new EnsureAdmissionOutcome(target, shared.AdmissionId);
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO queued_ensures
                        (seq, identity_hash, request_json, restricts_implicit_default, lookup_tenant, hashed_actor, detail_suffix,
                         caller_tenant, caller_subject, admitted_at)
                    VALUES (@seq, @hash, @json, @restricts, @tenant, @actor, @suffix, @ctenant, @csubject, @at);
                    """;
                cmd.Parameters.AddWithValue("@seq", seq);
                cmd.Parameters.AddWithValue("@hash", hash);
                cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(request, ServiceJson.Options));
                cmd.Parameters.AddWithValue("@restricts", request.RestrictsImplicitDefault ? 1 : 0);
                cmd.Parameters.AddWithValue("@tenant", (object?)request.LookupTenant ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actor", (object?)principal.Actor ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@suffix", (object?)principal.DetailSuffix ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ctenant", (object?)principal.UserTenant ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@csubject", (object?)principal.UserSubject ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@at", admittedAt);
                cmd.ExecuteNonQuery();
                BeforeQueuedEnsureWriteForTesting?.Invoke();
                string? admitted = admissionId;
                if (convergence)
                {
                    BeforeConvergenceAdmissionForTesting?.Invoke();
                    var snapshots = new SnapshotStore(conn);
                    var repository = snapshots.GetRepositoryId(request.RepositoryRemoteUrl)
                        ?? snapshots.EnsureRepository(request.RepositoryRemoteUrl, admittedAt);
                    var intents = new BranchIntentStore(conn);
                    var ensureIntent = intents.GetByQueuedEnsureId(seq);
                    ensureIntent ??= intents.Admit(repository, request.BranchName ?? "main", "ensure", seq, admissionId,
                        JsonSerializer.Serialize(request, ServiceJson.Options), admittedAt);
                    admitted = ensureIntent.AdmissionId;
                    AdmitFollowingRefresh(conn, ensureIntent);
                }
                onAdmitted?.Invoke();
                return new EnsureAdmissionOutcome(seq, admitted);
            }, synchronizeQueuedMetrics: true);
            if (outcome.SharedWith(seq))
                return outcome;
            if (NeedsBranchConvergence(request))
                WakeBranchIntentForQueuedEnsure(seq);
            ProgressForTesting?.Invoke();
            return outcome;
        }
        catch (Exception ex)
        {
            Log($"queued_ensure_admission_failed seq={seq} error={ex.GetType().Name}");
            if (!bestEffort || LeaseLost || _lifetime.IsCancellationRequested)
                throw;
            onAdmitted?.Invoke();
            return new EnsureAdmissionOutcome(seq, admissionId);
        }
    }

    // Admits an ensure intent's own following refresh (and its marker) directly behind the ensure, in the transaction that
    // admits the ensure (rule 1: the branch then converges to the newest head after the ensure's own commit). Its id is
    // recorded on the ensure (following_refresh_id) for the identical-ensure share check. An ensure that already has one,
    // or has settled, gets none.
    private static void AdmitFollowingRefresh(Microsoft.Data.Sqlite.SqliteConnection conn, BranchIntent ensureIntent)
    {
        if (ensureIntent.FollowingRefreshId is not null || ensureIntent.State == "settled")
            return;
        var refresh = new BranchIntentStore(conn).AdmitFollowingRefresh(ensureIntent, Guid.NewGuid().ToString("N"));
        new PendingRefreshStore(conn).Admit(refresh.RepositoryId, refresh.Branch, refresh.AdmissionId!, refresh.AdmittedAt);
    }

    private bool CanAttachWithoutAdmission(EnsureSnapshotRequest request, string hash)
    {
        if (request.RebuildGeneration is not null)
            return false;
        try
        {
            return ReadCatalog(conn =>
            {
                var jobs = new SnapshotJobStore(conn);
                var job = jobs.GetJobByIdentity(hash);
                return job is not null && SnapshotJobStatus.IsTerminal(job.Status)
                    && TerminalResultUsable(job, hash, new SnapshotStore(conn), jobs)
                    && (!NeedsBranchConvergence(request) || job.SnapshotId is null);
            });
        }
        catch (Exception ex)
        {
            Log($"queued_ensure_attach_lookup_failed error={ex.GetType().Name}");
            return false;
        }
    }

    private static QueuedRow? ReadQueuedRow(Microsoft.Data.Sqlite.SqliteConnection conn, long seq)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT seq, request_json, restricts_implicit_default, lookup_tenant, hashed_actor, detail_suffix,
                   caller_tenant, caller_subject, admitted_at
            FROM queued_ensures WHERE seq = @seq;
            """;
        cmd.Parameters.AddWithValue("@seq", seq);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new QueuedRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8))
            : null;
    }

    // The sequence an identical earlier ensure resolved to, once its own admission transaction has finished (so this
    // admission's transaction can see its intent). A candidate whose admission failed has no intent to share.
    private static async Task<long?> ShareTargetAsync(EnsureShareCandidate? share)
    {
        if (share is null)
            return null;
        await ((Task)share.Admission).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return share.Admission.IsCompletedSuccessfully ? share.Admission.Result.Seq : null;
    }

    // Duplicate replay rows are cleaned up only after their shared operation settles. Faults retain durable work.
    private void ForgetQueuedEnsure(long seq, Task<EnsureSnapshotResult> outcome, bool finishMetricAfterCleanup = false)
    {
        if (_lifetime.IsCancellationRequested || LeaseLost || !outcome.IsCompletedSuccessfully
            || !SnapshotJobStatus.IsSettled(outcome.Result.Status))
        {
            if (finishMetricAfterCleanup)
                _metrics.RecordQueuedEnsureFinished(seq);
            return;
        }
        _ = DeleteQueuedEnsureAsync(seq, outcome.Result.Status, finishMetricAfterCleanup);
    }

    private async Task DeleteQueuedEnsureAsync(long seq, string status, bool finishMetricAfterCleanup = false)
    {
        try
        {
            var removed = await RunGrantWriteAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM queued_ensures WHERE seq = @seq;";
                cmd.Parameters.AddWithValue("@seq", seq);
                var removed = cmd.ExecuteNonQuery();
                new BranchIntentStore(conn).SettleByQueuedEnsureId(
                    seq, status, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                if (finishMetricAfterCleanup)
                    _metrics.RecordQueuedEnsureFinished(seq);
                return removed;
            }, CancellationToken.None, synchronizeQueuedMetrics: true).ConfigureAwait(false);
            if (removed > 0)
            {
                WakeBranchIntentForQueuedEnsure(seq);
                SchedulePendingRefreshes();
            }
        }

        catch (Exception ex)
        {
            Log($"queued_ensure_cleanup_failed seq={seq} error={ex.GetType().Name}; retained for replay");
            if (finishMetricAfterCleanup)
                _metrics.RecordQueuedEnsureFinished(seq);
        }
    }

    private void WakeBranchIntentForQueuedEnsure(long seq)
    {
        var intent = ReadCatalog(conn => new BranchIntentStore(conn).GetByQueuedEnsureId(seq));
        if (intent is not null)
            WakeBranchIntent(intent.RepositoryId, intent.Branch);
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private readonly record struct QueuedRow(
        long Seq, string Json, bool Restricts, string? Tenant, string? Actor, string? Suffix,
        string? CallerTenant, string? CallerSubject, long AdmittedAt);

    /// <summary>
    /// Startup only, after <see cref="ReconcileOnStartup"/> has settled the orphaned queued jobs (rule 6): resumes every
    /// persisted ensure in sequence order and every unsettled retire/delete intent. Their order on a branch is the branch
    /// intent queue's (each waits for its branch head), so startup order decides nothing. A replayed ensure never shares
    /// (only a live request can): it takes its own turn at its place. Each row is decided on its own (see
    /// <see cref="ResumeQueuedRow"/>), and a row that fails never stops the others or the startup. Returns how many
    /// queued rows were handed to replay.
    /// </summary>
    internal int ReplayDurableAdmissions()
    {
        var (rows, maxSeq, retirements) = ReadCatalog(conn =>
        {
            var seqs = new List<long>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT seq FROM queued_ensures ORDER BY seq;";
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    seqs.Add(reader.GetInt64(0));
            var retires = new BranchIntentStore(conn).ListUnsettled().Where(i => i.Kind is "retire" or "delete").ToList();
            return (seqs, seqs.Count == 0 ? 0L : seqs.Max(), retires);
        });

        // Seeded from every row before any admission: a live ensure must never take the seq of a row still to be deleted.
        lock (_inFlightLock)
            _ensureSeq = Math.Max(_ensureSeq, maxSeq);

        var replayed = 0;
        var duplicatesBefore = _duplicateEnsureAdmissions;
        foreach (var seq in rows)
        {
            if (_lifetime.IsCancellationRequested || LeaseLost)
            {
                Log("Stopped replaying queued ensures (shutting down or writer unavailable); the rest stay for the next start.");
                break;
            }
            ResumeQueuedRow(seq);
            replayed++;
        }
        foreach (var retirement in retirements)
        {
            if (_lifetime.IsCancellationRequested || LeaseLost)
                break;
            ReplayRetirement(retirement);
        }

        if (rows.Count > 0)
            Log($"Resumed {replayed} queued ensure(s) persisted by the previous run " +
                $"({_duplicateEnsureAdmissions - duplicatesBefore} duplicate admission(s) shared).");
        return replayed;
    }
}
