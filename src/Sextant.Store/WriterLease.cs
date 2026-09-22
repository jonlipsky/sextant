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

    /// <summary>
    /// True once a <see cref="Renew"/> observed that this lease was STOLEN — it expired (a stalled
    /// heartbeat, a paused/slept host, thread-pool starvation) and another writer legitimately reacquired
    /// it. The holder has lost single-writer exclusivity and MUST stop writing; <see cref="SnapshotService"/>
    /// checks this before every catalog write so it fails closed instead of silently racing the new owner
    /// (issue #38). It never becomes true for a lease released on the normal shutdown path.
    /// </summary>
    public bool IsLost => _lost;

    private WriterLease(SqliteConnection connection, string ownerToken, string holder, long ttlMs)
    {
        _connection = connection;
        OwnerToken = ownerToken;
        Holder = holder;
        _ttlMs = ttlMs;
    }

    /// <summary>
    /// Attempts to acquire the writer lease for <paramref name="dbPath"/>. Returns a held lease, or
    /// <c>null</c> when another writer holds a live (non-expired) lease. Steals an expired lease. When
    /// <paramref name="autoHeartbeat"/> is true (default) a background timer renews the lease at roughly
    /// a third of the TTL so a long index/retention run never lets it expire under a live holder.
    /// </summary>
    public static WriterLease? TryAcquire(
        string dbPath, string holder, TimeSpan? ttl = null, bool autoHeartbeat = true)
    {
        var ttlMs = (long)(ttl ?? DefaultTtl).TotalMilliseconds;
        var token = Guid.NewGuid().ToString("N");
        var connection = OpenCoordinationConnection(dbPath);

        try
        {
            if (!TryAcquireRow(connection, token, holder, ttlMs))
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

        var lease = new WriterLease(connection, token, holder, ttlMs);
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
    public static WriterLease AcquireOrThrow(string dbPath, string holder, TimeSpan? ttl = null) =>
        TryAcquire(dbPath, holder, ttl)
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
                return cmd.ExecuteNonQuery() == 1;
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
                    return true;
                });
            }
            catch (SqliteException)
            {
                // Best-effort release; an expired lease will be reclaimable by the next writer anyway.
            }
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

    private static bool TryAcquireRow(SqliteConnection connection, string token, string holder, long ttlMs)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return WithImmediate(connection, () =>
        {
            var current = GetCurrent(connection);

            // A live lease held by someone else blocks acquisition (fail closed for the second writer).
            if (current != null && !current.IsExpiredAt(now))
                return false;

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
            return true;
        });
    }

    // Runs a unit of work inside an explicit BEGIN IMMEDIATE/COMMIT on the coordination connection so the
    // read-then-write acquire/renew is atomic across processes (the second process blocks on the write
    // lock until the first commits). Rolls back and rethrows on failure.
    private static bool WithImmediate(SqliteConnection connection, Func<bool> work)
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
