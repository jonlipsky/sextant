using System.Text.Json;
using Sextant.Service.Grants;
using Sextant.Store;

namespace Sextant.Service;

// Issue #406: durable admission. An ensure admitted (it holds a writer turn) but not yet started is recorded in the
// catalog's queued_ensures table, so a restart re-admits it instead of leaving it to the upstream heal and the app's
// reconcile. The row is written on a grant-style side connection (never under the write gate, so admission never
// waits behind a production) and removed when the ensure ends. It is KEPT when shutdown, a lost lease or a transient
// requeue ended the ensure, which is exactly the case a restart must resume. At startup the rows are replayed through
// StartEnsureOperation in admission order, so the lane (expedited default-branch vs normal), the #273/#405 coalescing
// (AdmitAdvanceLocked / WillBuildBranchHead) and the idempotent attach of an already-built identity all apply exactly
// as for a live admission. Persistence is best effort: a failed write is logged and the ensure still runs.
public sealed partial class SnapshotService
{
    private long _ensureSeq;

    private void PersistQueuedEnsure(long seq, EnsureSnapshotRequest request, string hash, AuditCaller principal)
    {
        try
        {
            GrantWrite(conn =>
            {
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
                cmd.Parameters.AddWithValue("@at", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
                return 0;
            });
        }
        catch (Exception ex)
        {
            Log($"Could not persist the queued ensure {hash} (it still runs, but a restart would not resume it): {ex.Message}");
        }
    }

    // An ensure whose identity already has a complete/partial job just attaches it (and moves the branch pointer, in
    // milliseconds): a restart gains nothing from replaying it, so it costs no catalog write.
    private bool IsNoOpAttach(EnsureSnapshotRequest request, string hash)
    {
        if (request.RebuildGeneration is not null)
            return false;
        try
        {
            var job = ReadCatalog(conn => new SnapshotJobStore(conn).GetJobByIdentity(hash));
            return job?.Status is SnapshotJobStatus.Complete or SnapshotJobStatus.Partial;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Removes the row of an ensure that ended for good. Shutdown, a lost lease and a requeued (still queued) result
    // keep it for the next instance.
    private void ForgetQueuedEnsure(long seq, Task<EnsureSnapshotResult> outcome)
    {
        if (_lifetime.IsCancellationRequested || LeaseLost || outcome.IsCanceled
            || (outcome.IsCompletedSuccessfully && outcome.Result.Status == SnapshotJobStatus.Queued))
            return;
        _ = DeleteQueuedEnsureAsync(seq);
    }

    private async Task DeleteQueuedEnsureAsync(long seq)
    {
        try
        {
            await RunGrantWriteAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM queued_ensures WHERE seq = @seq;";
                cmd.Parameters.AddWithValue("@seq", seq);
                return cmd.ExecuteNonQuery();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Left behind, the row only makes the next start re-admit an ensure that already settled, which attaches
            // its terminal job at no cost (or drops it once it is older than QueuedEnsureMaxAge).
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private readonly record struct QueuedRow(
        long Seq, string Json, bool Restricts, string? Tenant, string? Actor, string? Suffix,
        string? CallerTenant, string? CallerSubject, long AdmittedAt);

    /// <summary>
    /// Startup only, after <see cref="ReconcileOnStartup"/> has settled the orphaned queued jobs: re-admits every
    /// persisted ensure in its original admission order. Each row is evaluated on its own: one older than
    /// <see cref="ServiceOptions.QueuedEnsureMaxAge"/>, malformed, refused by the CURRENT <see cref="RepositoryUrlPolicy"/>
    /// or no longer granted to its user caller is dropped (logged; a policy or grant refusal is audited), and a row
    /// that throws never stops the others or the startup. Returns how many were re-admitted.
    /// </summary>
    internal int ReplayQueuedEnsures()
    {
        var (rows, maxSeq) = ReadCatalog(conn =>
        {
            var list = new List<QueuedRow>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT seq, request_json, restricts_implicit_default, lookup_tenant, hashed_actor, detail_suffix,
                       caller_tenant, caller_subject, admitted_at
                FROM queued_ensures ORDER BY seq;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new QueuedRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8)));
            return (list, list.Count == 0 ? 0L : list.Max(r => r.Seq));
        });

        // Seeded from EVERY row, usable or not, before any admission: a live ensure must never take the seq of a row
        // that is still to be deleted (INSERT OR REPLACE would let that delete remove the live ensure's row).
        lock (_inFlightLock)
            _ensureSeq = Math.Max(_ensureSeq, maxSeq);

        var oldest = _timeProvider.GetUtcNow() - _options.QueuedEnsureMaxAge;
        var replayed = 0;
        var dropped = 0;
        foreach (var row in rows)
        {
            try
            {
                if (DateTimeOffset.FromUnixTimeMilliseconds(row.AdmittedAt) < oldest)
                {
                    Log($"Dropped queued ensure #{row.Seq}: older than {_options.QueuedEnsureMaxAge}.");
                    Drop(row);
                    continue;
                }

                var request = JsonSerializer.Deserialize<EnsureSnapshotRequest>(row.Json, ServiceJson.Options);
                if (request is null || request.BranchGuardProblem() is not null)
                {
                    Log($"Dropped queued ensure #{row.Seq}: its request is unusable.");
                    Drop(row);
                    continue;
                }
                request = request with { RestrictsImplicitDefault = row.Restricts, LookupTenant = row.Tenant };
                var actor = new AuditCaller(null, (row.Suffix ?? "") + ";replay", row.Actor);

                var decision = _options.RepositoryUrlPolicy.Evaluate(request.RepositoryRemoteUrl);
                if (!decision.Ok)
                {
                    Log($"Dropped queued ensure #{row.Seq}: the repository URL policy now refuses it ({decision.Reason}).");
                    Observe(RecordEnsureDeniedAsync($"replay_{decision.Reason}", actor, _lifetime.Token));
                    Drop(row);
                    continue;
                }

                if (row.CallerSubject is not null)
                {
                    var key = RepositoryGrantKey.Of(request.RepositoryRemoteUrl);
                    var visible = ReadCatalog(conn => new RepositoryGrantStore(conn)
                        .VisibleKeys(row.CallerTenant ?? "", row.CallerSubject, LeaseCutoff(), LeaseCeiling()));
                    if (!visible.Contains(key))
                    {
                        Log($"Dropped queued ensure #{row.Seq}: its user caller no longer has a grant on the repository.");
                        Observe(RecordUserEnsureDeniedAsync(key, $"replay_{GrantReason.NotGranted}", actor, _lifetime.Token));
                        Drop(row);
                        continue;
                    }
                }

                var (_, completion, _, _) = StartEnsureOperation(request, new AuditCaller(null, row.Suffix, row.Actor), row.Seq);
                Observe(completion);
                replayed++;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                Log($"Stopped replaying queued ensures at #{row.Seq} (shutting down); the rest stay for the next start.");
                break;
            }
            catch (Exception ex)
            {
                Log($"Dropped queued ensure #{row.Seq}: it could not be re-admitted ({ex.GetType().Name}: {ex.Message}).");
                Drop(row);
            }
        }

        if (rows.Count > 0)
            Log($"Re-admitted {replayed} queued ensure(s) persisted by the previous run ({dropped} dropped).");
        return replayed;

        void Drop(QueuedRow row)
        {
            dropped++;
            Observe(DeleteQueuedEnsureAsync(row.Seq));
        }
    }
}
