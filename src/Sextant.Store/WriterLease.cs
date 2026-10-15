using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Thrown when an in-flight index/retention write is aborted because this process LOST the single-writer
/// lease (issue #38): it expired and another writer legitimately stole it. A batched write session checks
/// the lease at every batch boundary (<see cref="IndexWriteSession.CommitBatch"/>/<c>Complete</c>) so a
/// writer that lost exclusivity stops BETWEEN batches — rolling back the open batch and never flipping a
/// generation/snapshot pointer — instead of racing the new owner and risking a corrupt publish (Phase 17
/// criterion 3). Already-committed staging batches are left for recovery to sweep; nothing is published.
/// </summary>
public sealed class WriterLeaseLostException(string message) : Exception(message);

/// <summary>A read-only view of the current writer-lease holder (for status/reporting).</summary>
public sealed record WriterLeaseInfo
{
    public required string OwnerToken { get; init; }
    public string? Holder { get; init; }
    public required long AcquiredAt { get; init; }
    public required long HeartbeatAt { get; init; }
    public required long ExpiresAt { get; init; }

    public bool IsExpiredAt(long now) => ExpiresAt < now;
}

/// <summary>What acquiring the lease learned about the previous holder (issue #320).</summary>
public sealed record WriterLeaseAcquisition
{
    /// <summary>The proof that a still-unexpired lease's holder was dead, or null when the lease was free or had expired.</summary>
    public string? TakenOverReason { get; init; }
    public string? TakenOverHolder { get; init; }
    public long TakenOverAgeMs { get; init; }

    /// <summary>True when the previous holder recorded in the catalog never released the lease (crash, kill, OOM, abandon).</summary>
    public bool PreviousRunUnclean { get; init; }
    public long? PreviousRunHeartbeatAt { get; init; }
    public string? PreviousRunHost { get; init; }

    /// <summary>Why the previous run ended when it was not clean: <c>unclean</c> | <c>abandoned</c> | <c>release_failed</c>.</summary>
    public string? PreviousRunEndReason { get; init; }

    /// <summary>The previous holder's kind (<c>service</c> | <c>daemon</c> | <c>cli</c>); null for a row written before kinds were recorded.</summary>
    public string? PreviousRunKind { get; init; }
}

/// <summary>Why a holder's run ended, as recorded in <c>writer_run_state.end_reason</c> (issue #320).</summary>
public static class WriterEndReason
{
    public const string Clean = "clean";
    public const string Abandoned = "abandoned";
    public const string ReleaseFailed = "release_failed";
    public const string Unclean = "unclean";
}

/// <summary>The kinds of writer holder recorded in <c>writer_run_state.holder_kind</c>.</summary>
public static class WriterKind
{
    public const string Service = "service";
    public const string Daemon = "daemon";
    public const string Cli = "cli";
}

/// <summary>The durable crash-visibility counters of <c>writer_run_state</c> (issue #320); they live in the catalog, so a restored backup restores them.</summary>
public sealed record WriterRunStateInfo
{
    public required bool PreviousRunUnclean { get; init; }
    public long? PreviousRunHeartbeatAt { get; init; }
    public string? PreviousRunEndReason { get; init; }
    public string? PreviousRunKind { get; init; }
    public required long UncleanShutdownsTotal { get; init; }
    public long AbandonedRunsTotal { get; init; }
    public long ReleaseFailuresTotal { get; init; }
    public required long StaleLeaseTakeoversTotal { get; init; }
    public required bool CleanShutdown { get; init; }
}

/// <summary>
/// A single-node, cross-process SINGLE-WRITER lease over one index database (issue #38). Before this,
/// retention, a running daemon, and (now) the service could each open the database as a second writer,
/// and startup recovery could abandon a live writer's staging generation. This lease makes that
/// cooperative: a writer <see cref="TryAcquire"/>s the singleton <c>writer_lease</c> row, holds it
/// (auto-renewing via a background heartbeat) for the duration of its writes, and <see cref="Release"/>s
/// it on shutdown. A second writer that finds a LIVE (non-expired) lease held by someone else gets
/// <c>null</c> and must refuse; a STALE (expired) lease may be stolen so a crashed holder never wedges
/// the database forever.
///
/// It is a cooperative lease layered over the existing single-writer-connection + <c>BEGIN IMMEDIATE</c>
/// invariant, not a replacement for it. The lease uses its OWN dedicated connection (not the caller's
/// writer connection) so the background heartbeat never races an in-flight indexing write on the writer
/// connection; the heartbeat write is a tiny metadata <c>UPDATE</c> that serializes against the writer
/// through WAL + <c>busy_timeout</c>.
/// </summary>
public sealed class WriterLease : IDisposable
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private readonly SqliteConnection _connection;
    private readonly long _ttlMs;
    private readonly object _gate = new();
    private Timer? _heartbeat;
    private bool _released;
    private volatile bool _lost;

    public string OwnerToken { get; }
    public string Holder { get; }

    /// <summary>What acquisition learned about the previous holder: a stale-lease takeover and/or an unclean shutdown.</summary>
    public WriterLeaseAcquisition Acquisition { get; }

    /// <summary>
    /// True once a <see cref="Renew"/> observed that this lease was STOLEN — it expired (a stalled
    /// heartbeat, a paused/slept host, thread-pool starvation) and another writer legitimately reacquired
    /// it. The holder has lost single-writer exclusivity and MUST stop writing; <see cref="SnapshotService"/>
    /// checks this before every catalog write so it fails closed instead of silently racing the new owner
    /// (issue #38). It never becomes true for a lease released on the normal shutdown path; it does become
    /// true once the holder <see cref="Abandon"/>s the lease.
    /// </summary>
    public bool IsLost => _lost;

    private WriterLease(
        SqliteConnection connection, string ownerToken, string holder, long ttlMs, WriterLeaseAcquisition acquisition)
    {
        _connection = connection;
        OwnerToken = ownerToken;
        Holder = holder;
        _ttlMs = ttlMs;
        Acquisition = acquisition;
    }

    /// <summary>
    /// Attempts to acquire the writer lease for <paramref name="dbPath"/>. Returns a held lease, or
    /// <c>null</c> when another writer holds a live (non-expired) lease. Steals an expired lease. When
    /// <paramref name="autoHeartbeat"/> is true (default) a background timer renews the lease at roughly
    /// a third of the TTL so a long index/retention run never lets it expire under a live holder.
    /// A live lease whose holder is PROVABLY dead (same host, see <see cref="HolderLiveness"/>) is taken over
    /// without waiting for its TTL (issue #320); one that cannot be proven dead still blocks.
    /// </summary>
    public static WriterLease? TryAcquire(
        string dbPath, string holder, TimeSpan? ttl = null, bool autoHeartbeat = true,
        IProcessInspector? inspector = null, string kind = WriterKind.Cli)
    {
        var ttlMs = (long)(ttl ?? DefaultTtl).TotalMilliseconds;
        var token = Guid.NewGuid().ToString("N");
        var connection = OpenCoordinationConnection(dbPath);

        WriterLeaseAcquisition? acquisition;
        try
        {
            acquisition = TryAcquireRow(connection, token, holder, ttlMs, inspector ?? ProcessInspector.Instance, kind);
            if (acquisition is null)
            {
                connection.Dispose();
                return null;
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        var lease = new WriterLease(connection, token, holder, ttlMs, acquisition);
        if (autoHeartbeat)
        {
            var period = Math.Max(1000, ttlMs / 3);
            lease._heartbeat = new Timer(_ => lease.SafeRenew(), null, period, period);
        }
        return lease;
    }

    /// <summary>
    /// Acquires the single-writer lease for a write path, or throws a fail-closed
    /// <see cref="InvalidOperationException"/> when another LIVE writer already holds it (issue #38 / #59).
    /// This is the shared guard EVERY write path uses — the standalone service, the daemon, and the
    /// one-shot CLI index — so two writers can never race one database and corrupt a publish (criterion 3).
    /// Call it AFTER migrations (the <c>writer_lease</c> table must exist) and BEFORE recovery, so recovery
    /// never abandons a live writer's staging generation. A holder identifies the process in the lease row
    /// and logs.
    /// </summary>
    public static WriterLease AcquireOrThrow(string dbPath, string holder, TimeSpan? ttl = null, string kind = WriterKind.Cli) =>
        TryAcquire(dbPath, holder, ttl, kind: kind)
        ?? throw new InvalidOperationException(
            $"Another Sextant writer already holds the single-writer lease on '{dbPath}'. " +
            "A daemon, index service, or one-shot index is already writing this database; stop it or wait " +
            "before starting another writer (issue #38) — two concurrent writers could corrupt a publish.");

    /// <summary>Reads the current lease holder without acquiring anything (null when unheld).</summary>
    public static WriterLeaseInfo? GetCurrent(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT owner_token, holder, acquired_at, heartbeat_at, expires_at FROM writer_lease WHERE id = 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new WriterLeaseInfo
        {
            OwnerToken = reader.GetString(0),
            Holder = reader.IsDBNull(1) ? null : reader.GetString(1),
            AcquiredAt = reader.GetInt64(2),
            HeartbeatAt = reader.GetInt64(3),
            ExpiresAt = reader.GetInt64(4)
        };
    }

    /// <summary>Reads the crash-visibility counters (null before the first acquisition under migration 032).</summary>
    public static WriterRunStateInfo? GetRunState(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT previous_run_unclean, previous_run_heartbeat_at, unclean_shutdowns_total,
                   stale_lease_takeovers_total, clean_shutdown, previous_run_end_reason, previous_run_kind,
                   abandoned_runs_total, release_failures_total
              FROM writer_run_state WHERE id = 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new WriterRunStateInfo
        {
            PreviousRunUnclean = reader.GetInt64(0) != 0,
            PreviousRunHeartbeatAt = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            UncleanShutdownsTotal = reader.GetInt64(2),
            StaleLeaseTakeoversTotal = reader.GetInt64(3),
            CleanShutdown = reader.GetInt64(4) != 0,
            PreviousRunEndReason = reader.IsDBNull(5) ? null : reader.GetString(5),
            PreviousRunKind = reader.IsDBNull(6) ? null : reader.GetString(6),
            AbandonedRunsTotal = reader.GetInt64(7),
            ReleaseFailuresTotal = reader.GetInt64(8)
        };
    }

    /// <summary>
    /// Renews the lease (extends its expiry) while this process still owns it. Returns false if the lease
    /// was stolen (expired and taken by another writer) — the caller has lost single-writer exclusivity.
    /// </summary>
    public bool Renew()
    {
        lock (_gate)
        {
            if (_released) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var renewed = WithImmediate(_connection, () =>
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE writer_lease
                       SET heartbeat_at = @now, expires_at = MAX(expires_at, @expires)
                     WHERE id = 1 AND owner_token = @token;
                    """;
                cmd.Parameters.AddWithValue("@now", now);
                cmd.Parameters.AddWithValue("@expires", now + _ttlMs);
                cmd.Parameters.AddWithValue("@token", OwnerToken);
                var owned = cmd.ExecuteNonQuery() == 1;
                if (owned) TouchRunState(_connection, OwnerToken, now, null);
                return owned;
            });

            // A 0-row update means row 1 is no longer owned by our token — it expired and another writer
            // stole it (an unstolen-but-expired lease still matches owner_token and self-heals above). We
            // have lost single-writer exclusivity and must stop writing (issue #38).
            if (!renewed)
                _lost = true;
            return renewed;
        }
    }

    /// <summary>Releases the lease if still owned. Idempotent and safe to call from <see cref="Dispose"/>.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_released) return;
            _released = true;
            _heartbeat?.Dispose();
            _heartbeat = null;
            try
            {
                WithImmediate(_connection, () =>
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "DELETE FROM writer_lease WHERE id = 1 AND owner_token = @token;";
                    cmd.Parameters.AddWithValue("@token", OwnerToken);
                    cmd.ExecuteNonQuery();
                    // The clean-shutdown marker: only a holder that got here is not a crash (issue #320).
                    TouchRunState(_connection, OwnerToken, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), WriterEndReason.Clean);
                    return true;
                });
                EndReason = WriterEndReason.Clean;
            }
            catch (SqliteException)
            {
                // Best-effort release; an expired lease will be reclaimable by the next writer anyway. The row stays,
                // so say why (when even that cannot be written, the next holder reports it as unclean).
                EndReason = WriterEndReason.ReleaseFailed;
                TryMarkEnd(WriterEndReason.ReleaseFailed);
            }
        }
    }

    /// <summary>
    /// Gives the lease up WITHOUT releasing its row, for a holder that can no longer guarantee it has stopped
    /// writing (issue #148: a worker outlived the shutdown drain). <see cref="IsLost"/> becomes true, so every
    /// write probe fails closed and the straggler's write session aborts at its next batch boundary, and the
    /// heartbeat stops so the row EXPIRES by its TTL, exactly as for a crashed holder, instead of being
    /// immediately reacquirable by another writer while the straggler may still commit. The run is recorded as
    /// <see cref="WriterEndReason.Abandoned"/>, so the next holder does not report a crash. Idempotent;
    /// <see cref="Release"/>/<see cref="Dispose"/> afterwards never delete the row.
    /// </summary>
    public void Abandon()
    {
        lock (_gate)
        {
            var first = !_released;
            _lost = true;
            _released = true;
            _heartbeat?.Dispose();
            _heartbeat = null;
            if (first)
            {
                EndReason = WriterEndReason.Abandoned;
                TryMarkEnd(WriterEndReason.Abandoned);
            }
        }
    }

    /// <summary>How this holder's run ended (<see cref="WriterEndReason"/>), or null while it still holds the lease.</summary>
    public string? EndReason { get; private set; }

    private void TryMarkEnd(string reason)
    {
        try
        {
            WithImmediate(_connection, () =>
            {
                TouchRunState(_connection, OwnerToken, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reason);
                return true;
            });
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Release();
        _connection.Dispose();
    }

    private void SafeRenew()
    {
        try
        {
            // Renew() itself flags a stolen lease via IsLost; a false return here needs no extra handling.
            Renew();
        }
        catch (SqliteException)
        {
            // A transient busy/locked heartbeat is non-fatal; the next tick retries before expiry.
        }
    }

    private static WriterLeaseAcquisition? TryAcquireRow(
        SqliteConnection connection, string token, string holder, long ttlMs, IProcessInspector inspector, string kind)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return WithImmediate(connection, () =>
        {
            var current = GetCurrent(connection);
            var previous = ReadRunState(connection);
            string? takenOverReason = null;

            // A live lease held by someone else blocks acquisition (fail closed for the second writer), unless its
            // holder is provably dead (issue #320). The proof needs the identity recorded for THIS lease row: a row
            // written without one (an older binary) or with a different owner token proves nothing.
            if (current != null && !current.IsExpiredAt(now))
            {
                if (previous is null || previous.OwnerToken != current.OwnerToken
                    || HolderLiveness.ProveDead(previous.Identity, inspector) is not { } reason)
                    return null;
                takenOverReason = reason;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO writer_lease (id, owner_token, holder, acquired_at, heartbeat_at, expires_at)
                VALUES (1, @token, @holder, @now, @now, @expires)
                ON CONFLICT(id) DO UPDATE SET
                    owner_token = excluded.owner_token,
                    holder = excluded.holder,
                    acquired_at = excluded.acquired_at,
                    heartbeat_at = excluded.heartbeat_at,
                    expires_at = excluded.expires_at;
                """;
            cmd.Parameters.AddWithValue("@token", token);
            cmd.Parameters.AddWithValue("@holder", holder);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@expires", now + ttlMs);
            cmd.ExecuteNonQuery();

            // The previous run was unclean when its holder never recorded a clean release; the recorded reason says
            // whether that was a crash (none recorded) or a deliberate abandon / failed release.
            var unclean = previous is { Clean: false };
            var endReason = !unclean ? null : previous!.EndReason ?? WriterEndReason.Unclean;
            WriteRunState(connection, token, inspector.Current, kind, now, endReason, previous, takenOverReason is not null);
            return new WriterLeaseAcquisition
            {
                TakenOverReason = takenOverReason,
                TakenOverHolder = takenOverReason is null ? null : current?.Holder,
                TakenOverAgeMs = takenOverReason is null || current is null ? 0 : Math.Max(0, now - current.HeartbeatAt),
                PreviousRunUnclean = unclean,
                PreviousRunHeartbeatAt = unclean ? previous?.HeartbeatAt : null,
                PreviousRunHost = previous?.Identity.HostId,
                PreviousRunEndReason = endReason,
                PreviousRunKind = previous?.Kind
            };
        });
    }

    private sealed record RunStateRow(
        string OwnerToken, ProcessIdentity Identity, long HeartbeatAt, bool Clean, string? EndReason, string? Kind,
        long UncleanTotal, long AbandonedTotal, long ReleaseFailureTotal, long TakeoverTotal);

    private static RunStateRow? ReadRunState(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT owner_token, instance_id, host_id, container_id, boot_id, pid_namespace, pid,
                   process_start_ticks, heartbeat_at, clean_shutdown, unclean_shutdowns_total,
                   stale_lease_takeovers_total, machine_id, init_start_ticks, end_reason, holder_kind,
                   abandoned_runs_total, release_failures_total
              FROM writer_run_state WHERE id = 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new RunStateRow(
            reader.GetString(0),
            new ProcessIdentity
            {
                InstanceId = reader.GetString(1),
                HostId = Text(2),
                ContainerId = Text(3),
                BootId = Text(4),
                PidNamespace = Text(5),
                Pid = reader.IsDBNull(6) ? 0 : (int)reader.GetInt64(6),
                StartTicks = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                MachineId = Text(12),
                InitStartTicks = reader.IsDBNull(13) ? null : reader.GetInt64(13)
            },
            reader.GetInt64(8), reader.GetInt64(9) != 0, Text(14), Text(15),
            reader.GetInt64(10), reader.GetInt64(16), reader.GetInt64(17), reader.GetInt64(11));
    }

    private static void WriteRunState(
        SqliteConnection connection, string token, ProcessIdentity me, string kind, long now, string? previousEndReason,
        RunStateRow? previous, bool tookOver)
    {
        var previousUnclean = previousEndReason is not null;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO writer_run_state (id, owner_token, instance_id, host_id, machine_id, container_id,
                init_start_ticks, boot_id, pid_namespace, pid, process_start_ticks, holder_kind, heartbeat_at,
                clean_shutdown, end_reason, previous_run_unclean, previous_run_heartbeat_at, previous_run_end_reason,
                previous_run_kind, unclean_shutdowns_total, abandoned_runs_total, release_failures_total,
                stale_lease_takeovers_total)
            VALUES (1, @token, @instance, @host, @machine, @container, @initTicks, @boot, @pidns, @pid, @ticks, @kind,
                @now, 0, NULL, @unclean, @prevHeartbeat, @prevReason, @prevKind, @uncleanTotal, @abandonedTotal,
                @releaseFailedTotal, @takeoverTotal)
            ON CONFLICT(id) DO UPDATE SET
                owner_token = excluded.owner_token,
                instance_id = excluded.instance_id,
                host_id = excluded.host_id,
                machine_id = excluded.machine_id,
                container_id = excluded.container_id,
                init_start_ticks = excluded.init_start_ticks,
                boot_id = excluded.boot_id,
                pid_namespace = excluded.pid_namespace,
                pid = excluded.pid,
                process_start_ticks = excluded.process_start_ticks,
                holder_kind = excluded.holder_kind,
                heartbeat_at = excluded.heartbeat_at,
                clean_shutdown = 0,
                end_reason = NULL,
                previous_run_unclean = excluded.previous_run_unclean,
                previous_run_heartbeat_at = excluded.previous_run_heartbeat_at,
                previous_run_end_reason = excluded.previous_run_end_reason,
                previous_run_kind = excluded.previous_run_kind,
                unclean_shutdowns_total = excluded.unclean_shutdowns_total,
                abandoned_runs_total = excluded.abandoned_runs_total,
                release_failures_total = excluded.release_failures_total,
                stale_lease_takeovers_total = excluded.stale_lease_takeovers_total;
            """;
        cmd.Parameters.AddWithValue("@token", token);
        cmd.Parameters.AddWithValue("@instance", me.InstanceId);
        cmd.Parameters.AddWithValue("@host", (object?)me.HostId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@machine", (object?)me.MachineId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@container", (object?)me.ContainerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@initTicks", (object?)me.InitStartTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@boot", (object?)me.BootId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pidns", (object?)me.PidNamespace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pid", me.Pid);
        cmd.Parameters.AddWithValue("@ticks", (object?)me.StartTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@unclean", previousUnclean ? 1 : 0);
        cmd.Parameters.AddWithValue("@prevHeartbeat", (object?)(previousUnclean ? previous?.HeartbeatAt : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prevReason", (object?)previousEndReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prevKind", (object?)(previousUnclean ? previous?.Kind : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uncleanTotal",
            (previous?.UncleanTotal ?? 0) + (previousEndReason == WriterEndReason.Unclean ? 1 : 0));
        cmd.Parameters.AddWithValue("@abandonedTotal",
            (previous?.AbandonedTotal ?? 0) + (previousEndReason == WriterEndReason.Abandoned ? 1 : 0));
        cmd.Parameters.AddWithValue("@releaseFailedTotal",
            (previous?.ReleaseFailureTotal ?? 0) + (previousEndReason == WriterEndReason.ReleaseFailed ? 1 : 0));
        cmd.Parameters.AddWithValue("@takeoverTotal", (previous?.TakeoverTotal ?? 0) + (tookOver ? 1 : 0));
        cmd.ExecuteNonQuery();
    }

    private static void TouchRunState(SqliteConnection connection, string token, long now, string? endReason)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE writer_run_state
               SET heartbeat_at = @now, clean_shutdown = MAX(clean_shutdown, @clean),
                   end_reason = COALESCE(@reason, end_reason)
             WHERE id = 1 AND owner_token = @token;
            """;
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@clean", endReason == WriterEndReason.Clean ? 1 : 0);
        cmd.Parameters.AddWithValue("@reason", (object?)endReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@token", token);
        cmd.ExecuteNonQuery();
    }

    // Runs a unit of work inside an explicit BEGIN IMMEDIATE/COMMIT on the coordination connection so the
    // read-then-write acquire/renew is atomic across processes (the second process blocks on the write
    // lock until the first commits). Rolls back and rethrows on failure.
    private static T WithImmediate<T>(SqliteConnection connection, Func<T> work)
    {
        using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }
        try
        {
            var result = work();
            using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
            return result;
        }
        catch
        {
            try
            {
                using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Ignore rollback failure; the primary exception is what matters.
            }
            throw;
        }
    }

    private static SqliteConnection OpenCoordinationConnection(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // A private (non-shared) cache so the tiny heartbeat write serializes against the indexing
            // writer through WAL + busy_timeout (SQLITE_BUSY) rather than shared-cache table locks.
            Cache = SqliteCacheMode.Default
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
