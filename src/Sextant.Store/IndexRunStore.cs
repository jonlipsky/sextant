using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>Lifecycle status of an <see cref="IndexRun"/> generation.</summary>
public static class IndexRunState
{
    public const string Staging = "staging";
    public const string Complete = "complete";
    public const string Abandoned = "abandoned";
}

/// <summary>A recorded indexing run (generation) and its final resource footprint.</summary>
public sealed record IndexRun
{
    public required long Id { get; init; }
    public required string Mode { get; init; }
    public required string Status { get; init; }
    public required long StartedAt { get; init; }
    public long? CompletedAt { get; init; }
    public long Projects { get; init; }
    public long? FinalDbBytes { get; init; }
    public long? FinalWalBytes { get; init; }
    public long? FinalShmBytes { get; init; }
    public long? PeakStagedBytes { get; init; }
}

/// <summary>
/// Reads and writes the <c>index_runs</c> generation ledger. A run opens as
/// <see cref="IndexRunState.Staging"/>, is marked <see cref="IndexRunState.Complete"/> only after
/// all batches commit and the run validates, and is marked <see cref="IndexRunState.Abandoned"/> on
/// cancellation, failure, or startup recovery.
/// </summary>
public sealed class IndexRunStore(SqliteConnection connection)
{
    public long BeginRun(string mode, long startedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO index_runs (mode, status, started_at)
            VALUES (@mode, @status, @started_at)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@mode", mode);
        cmd.Parameters.AddWithValue("@status", IndexRunState.Staging);
        cmd.Parameters.AddWithValue("@started_at", startedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Atomically marks a staging run complete. Guarded on <c>status = staging</c> so a run abandoned
    /// by recovery is never resurrected, and so the publish is a no-op if the generation is not the
    /// one this caller staged. Runs on the caller's connection: invoke it inside the final write
    /// transaction to publish the generation pointer atomically with the last batch of data. Returns
    /// the number of rows updated (1 on success, 0 if the run was not in staging) so callers can refuse
    /// to treat a no-op publish as a successful completion.
    /// </summary>
    public int MarkComplete(long id, long completedAt, long projects)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE index_runs
               SET status = @status, completed_at = @completed_at, projects = @projects
             WHERE id = @id AND status = @staging;
            """;
        cmd.Parameters.AddWithValue("@status", IndexRunState.Complete);
        cmd.Parameters.AddWithValue("@completed_at", completedAt);
        cmd.Parameters.AddWithValue("@projects", projects);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@staging", IndexRunState.Staging);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records a completed run's final resource footprint. Provenance only — separate from
    /// <see cref="MarkComplete"/> so a failure to sample sizes (after the checkpoint) can never undo
    /// or block the already-published generation.
    /// </summary>
    public void RecordFootprint(long id, long? finalDbBytes, long? finalWalBytes, long? finalShmBytes, long? peakStagedBytes = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE index_runs
               SET final_db_bytes = @db, final_wal_bytes = @wal, final_shm_bytes = @shm,
                   peak_staged_bytes = @staged
             WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@db", (object?)finalDbBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@wal", (object?)finalWalBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@shm", (object?)finalShmBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@staged", (object?)peakStagedBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Marks a specific staging run abandoned (no-op if it already reached a terminal state).</summary>
    public void AbandonRun(long id, long at)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE index_runs
               SET status = @abandoned, completed_at = @at
             WHERE id = @id AND status = @staging;
            """;
        cmd.Parameters.AddWithValue("@abandoned", IndexRunState.Abandoned);
        cmd.Parameters.AddWithValue("@at", at);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@staging", IndexRunState.Staging);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks every lingering staging run abandoned. Used by startup recovery to clean up generations
    /// left behind by a process that died mid-index. Returns the number of runs abandoned.
    /// </summary>
    public int AbandonStaleRuns(long at)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE index_runs
               SET status = @abandoned, completed_at = @at
             WHERE status = @staging;
            """;
        cmd.Parameters.AddWithValue("@abandoned", IndexRunState.Abandoned);
        cmd.Parameters.AddWithValue("@at", at);
        cmd.Parameters.AddWithValue("@staging", IndexRunState.Staging);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>The newest complete run — the current generation / last-complete-index pointer.</summary>
    public IndexRun? GetLastCompleteRun()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM index_runs WHERE status = @complete ORDER BY id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@complete", IndexRunState.Complete);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IndexRun? GetById(long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM index_runs WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// Opens a run and returns a scope that marks it complete when the caller succeeds and abandons it
    /// on any failure/cancellation. Combined with a <c>using</c> write session (which rolls the open
    /// batch back), this gives the atomic generation boundary: readers keep seeing the previous
    /// complete run until the caller calls <see cref="IndexRunScope.Complete"/>.
    /// </summary>
    public IndexRunScope BeginScope(string mode, long startedAt)
        => new(this, BeginRun(mode, startedAt));

    private static IndexRun Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        Mode = reader.GetString(reader.GetOrdinal("mode")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        StartedAt = reader.GetInt64(reader.GetOrdinal("started_at")),
        CompletedAt = NullableLong(reader, "completed_at"),
        Projects = reader.GetInt64(reader.GetOrdinal("projects")),
        FinalDbBytes = NullableLong(reader, "final_db_bytes"),
        FinalWalBytes = NullableLong(reader, "final_wal_bytes"),
        FinalShmBytes = NullableLong(reader, "final_shm_bytes"),
        PeakStagedBytes = NullableLong(reader, "peak_staged_bytes")
    };

    private static long? NullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
}

/// <summary>
/// Lifetime of one staging generation. Disposing before <see cref="Complete"/> abandons the run so a
/// cancelled or failed index never leaves a staging generation behind (startup recovery also sweeps
/// any it misses). Disposal is idempotent and never throws, so it is safe to use with <c>using</c>.
/// </summary>
public sealed class IndexRunScope : IDisposable
{
    private readonly IndexRunStore _store;
    private bool _finished;

    internal IndexRunScope(IndexRunStore store, long runId)
    {
        _store = store;
        RunId = runId;
    }

    /// <summary>The staging run's id.</summary>
    public long RunId { get; }

    /// <summary>
    /// Publishes the run as complete in its own statement and suppresses abandonment on dispose. Use
    /// this for the simple, standalone case. Indexers instead call
    /// <see cref="IndexRunStore.MarkComplete"/> inside their final write transaction (so the pointer
    /// flips atomically with the data) and then call <see cref="Detach"/>. Throws if the run was not in
    /// staging (e.g. already abandoned by recovery) so a no-op update is never mistaken for a publish.
    /// </summary>
    public void Complete(long completedAt, long projects)
    {
        if (_store.MarkComplete(RunId, completedAt, projects) != 1)
            throw new InvalidOperationException(
                $"Index run {RunId} was not in staging state and could not be published as complete.");
        _finished = true;
    }

    /// <summary>
    /// Suppresses dispose-time abandonment after the caller has already published the run (e.g. via an
    /// in-transaction <see cref="IndexRunStore.MarkComplete"/> that has since committed). Call it only
    /// once the publishing commit has succeeded.
    /// </summary>
    public void Detach() => _finished = true;

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        try
        {
            _store.AbandonRun(RunId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (SqliteException)
        {
            // Best-effort abandonment; startup recovery will abandon a run this could not.
        }
    }
}

