using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>The lifecycle states of a durable ensure-snapshot job (migration 016 <c>snapshot_jobs.status</c>).</summary>
public static class SnapshotJobStatus
{
    /// <summary>Accepted, awaiting a worker.</summary>
    public const string Queued = "queued";

    /// <summary>A worker (identified by <c>owner_token</c>) is producing the snapshot.</summary>
    public const string Running = "running";

    /// <summary>Every requested project indexed and the snapshot published.</summary>
    public const string Complete = "complete";

    /// <summary>Published, but some projects were skipped/degraded (see diagnostics).</summary>
    public const string Partial = "partial";

    /// <summary>The run failed before publication (see diagnostics / <c>last_error</c>).</summary>
    public const string Failed = "failed";

    /// <summary>The request targets something this build of Sextant cannot index (capability gate).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Explicitly cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>A job in a terminal state is never re-run for the same identity.</summary>
    public static bool IsTerminal(string status) =>
        status is Complete or Partial or Failed or Unsupported or Cancelled;
}

/// <summary>Severity of a per-project job diagnostic (acceptance criterion 5).</summary>
public static class JobDiagnosticSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

/// <summary>One durable ensure-snapshot job row.</summary>
public sealed record SnapshotJobRow
{
    public required long Id { get; init; }
    public required string IdentityHash { get; init; }
    public required string RepositoryUrl { get; init; }
    public required string CommitSha { get; init; }
    public string? BranchName { get; init; }
    public required string Status { get; init; }
    public long? SnapshotId { get; init; }
    public string? OwnerToken { get; init; }
    public int Attempts { get; init; }
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
    public long? StartedAt { get; init; }
    public long? CompletedAt { get; init; }
    public string? LastError { get; init; }
}

/// <summary>One structured, per-project diagnostic attached to a job.</summary>
public sealed record SnapshotJobDiagnostic
{
    public required long JobId { get; init; }
    public string? ProjectCanonicalId { get; init; }
    public string? ProjectPath { get; init; }
    public required string Severity { get; init; }
    public string? Code { get; init; }
    public required string Message { get; init; }
    public long CreatedAt { get; init; }
}

/// <summary>
/// The durable ensure-snapshot job ledger (migration 016), owned by the standalone index service. A job
/// is keyed by the SAME Phase-9 <c>identity_hash</c> that keys immutable snapshots, so a repeated ensure
/// request for one snapshot attaches to the ONE existing job/result instead of forking a second run
/// (acceptance criterion 1). Structured per-project diagnostics explain a partial/failed/unsupported
/// outcome (criterion 5). On restart the service reconciles jobs a dead worker left <c>running</c>
/// (criterion 2). It is a thin SQL store over the catalog tables (same shape as <see cref="SnapshotStore"/>)
/// and enrols in the caller's ambient write transaction — the service, not this store, owns the writer
/// lease and transaction boundaries.
/// </summary>
public sealed class SnapshotJobStore(SqliteConnection connection)
{
    /// <summary>
    /// Idempotently attaches to (or creates) the job for <paramref name="identityHash"/>. A repeated call
    /// with the same identity returns the SAME job row (criterion 1) without disturbing its status or
    /// resetting an in-flight/terminal run — only <c>updated_at</c> is bumped so "last requested" is
    /// observable. Returns the current row and whether it already existed.
    /// </summary>
    public (SnapshotJobRow job, bool existed) EnsureJob(
        string identityHash, string repositoryUrl, string commitSha, string? branchName)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long existingId;
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT id FROM snapshot_jobs WHERE identity_hash = @h;";
            probe.Parameters.AddWithValue("@h", identityHash);
            var found = probe.ExecuteScalar();
            existingId = found is null or DBNull ? 0 : Convert.ToInt64(found);
        }

        if (existingId != 0)
        {
            using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE snapshot_jobs SET updated_at = @now WHERE id = @id;";
            touch.Parameters.AddWithValue("@now", now);
            touch.Parameters.AddWithValue("@id", existingId);
            touch.ExecuteNonQuery();
            return (GetJob(existingId)!, true);
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO snapshot_jobs
                (identity_hash, repository_url, commit_sha, branch_name, status, attempts, created_at, updated_at)
            VALUES (@h, @repo, @commit, @branch, @status, 0, @now, @now)
            RETURNING id;
            """;
        insert.Parameters.AddWithValue("@h", identityHash);
        insert.Parameters.AddWithValue("@repo", repositoryUrl);
        insert.Parameters.AddWithValue("@commit", commitSha);
        insert.Parameters.AddWithValue("@branch", (object?)branchName ?? DBNull.Value);
        insert.Parameters.AddWithValue("@status", SnapshotJobStatus.Queued);
        insert.Parameters.AddWithValue("@now", now);
        var id = Convert.ToInt64(insert.ExecuteScalar()!);
        return (GetJob(id)!, false);
    }

    public SnapshotJobRow? GetJob(long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectJob + " WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    public SnapshotJobRow? GetJobByIdentity(string identityHash)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectJob + " WHERE identity_hash = @h;";
        cmd.Parameters.AddWithValue("@h", identityHash);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    /// <summary>
    /// Transitions a queued job to <c>running</c> under <paramref name="ownerToken"/> (the writer-lease
    /// token), stamping <c>started_at</c> and incrementing <c>attempts</c>. Guarded on the job not already
    /// being terminal; returns false if it lost the race or is already done.
    /// </summary>
    public bool MarkRunning(long jobId, string ownerToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE snapshot_jobs
               SET status = @running, owner_token = @owner, started_at = @now, updated_at = @now,
                   attempts = attempts + 1
             WHERE id = @id AND status IN (@queued, @running);
            """;
        cmd.Parameters.AddWithValue("@running", SnapshotJobStatus.Running);
        cmd.Parameters.AddWithValue("@queued", SnapshotJobStatus.Queued);
        cmd.Parameters.AddWithValue("@owner", ownerToken);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@id", jobId);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Records a terminal result for a job: its final status (<c>complete</c>/<c>partial</c>/<c>failed</c>/
    /// <c>unsupported</c>/<c>cancelled</c>), the published snapshot id (when any), a <c>completed_at</c>
    /// stamp, and an optional last-error string. Clears <c>owner_token</c> so it is no longer owned.
    /// </summary>
    public void MarkResult(long jobId, string status, long? snapshotId, string? lastError = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE snapshot_jobs
               SET status = @status, snapshot_id = @snap, owner_token = NULL,
                   completed_at = @now, updated_at = @now, last_error = @err
             WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@snap", (object?)snapshotId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@err", (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", jobId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Resets a job to <c>queued</c> so a subsequent ensure re-attempts it, clearing the stale
    /// snapshot pointer / owner / error / timing. Used when a previously TERMINAL result is no longer
    /// usable — a <c>complete</c>/<c>partial</c> job whose published snapshot was reclaimed by retention
    /// (its <c>snapshot_id</c> NULLed via <c>ON DELETE SET NULL</c>), or a run cancelled mid-flight — so
    /// the service regenerates it instead of reporting a phantom-complete (or a permanent failure) forever.
    /// </summary>
    public void Requeue(long jobId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE snapshot_jobs
               SET status = @queued, snapshot_id = NULL, owner_token = NULL, last_error = NULL,
                   started_at = NULL, completed_at = NULL, updated_at = @now
             WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@queued", SnapshotJobStatus.Queued);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@id", jobId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reconciles jobs left <c>running</c> by a worker that is no longer alive (criterion 2). On service
    /// restart the new writer holds a fresh lease token, so every <c>running</c> job whose
    /// <c>owner_token</c> is not <paramref name="liveOwnerToken"/> is reset to <c>queued</c> (owner
    /// cleared) to be re-attempted. Pass null to reconcile ALL running jobs (nothing is live yet).
    /// Returns the number of jobs reconciled.
    /// </summary>
    public int ReconcileOrphanedJobs(string? liveOwnerToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = liveOwnerToken is null
            ? """
              UPDATE snapshot_jobs SET status = @queued, owner_token = NULL, updated_at = @now
               WHERE status = @running;
              """
            : """
              UPDATE snapshot_jobs SET status = @queued, owner_token = NULL, updated_at = @now
               WHERE status = @running AND (owner_token IS NULL OR owner_token <> @owner);
              """;
        cmd.Parameters.AddWithValue("@queued", SnapshotJobStatus.Queued);
        cmd.Parameters.AddWithValue("@running", SnapshotJobStatus.Running);
        cmd.Parameters.AddWithValue("@now", now);
        if (liveOwnerToken is not null)
            cmd.Parameters.AddWithValue("@owner", liveOwnerToken);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reconciles jobs a previous worker left in a PHANTOM terminal state (Phase 17 criterion 3): a job
    /// recorded <c>complete</c>/<c>partial</c> whose <c>snapshot_id</c> is now NULL (the published snapshot
    /// was reclaimed by retention via <c>ON DELETE SET NULL</c>, or a crash recorded the terminal status
    /// before the publish transaction committed and the pending snapshot was later abandoned) is reset to
    /// <c>queued</c> so a later ensure regenerates it, instead of reporting a phantom-complete forever. A
    /// terminal job that still points at a live snapshot is untouched (its usability is verified at
    /// ensure time). Returns the number of jobs reconciled.
    /// </summary>
    public int ReconcilePhantomTerminalJobs()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE snapshot_jobs
               SET status = @queued, owner_token = NULL, last_error = NULL,
                   started_at = NULL, completed_at = NULL, updated_at = @now
             WHERE status IN (@complete, @partial) AND snapshot_id IS NULL;
            """;
        cmd.Parameters.AddWithValue("@queued", SnapshotJobStatus.Queued);
        cmd.Parameters.AddWithValue("@complete", SnapshotJobStatus.Complete);
        cmd.Parameters.AddWithValue("@partial", SnapshotJobStatus.Partial);
        cmd.Parameters.AddWithValue("@now", now);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Clears any prior diagnostics for a job, then records the supplied set (idempotent per attempt).</summary>
    public void ReplaceDiagnostics(long jobId, IEnumerable<SnapshotJobDiagnostic> diagnostics)
    {
        using (var del = connection.CreateCommand())
        {
            del.CommandText = "DELETE FROM snapshot_job_diagnostics WHERE job_id = @id;";
            del.Parameters.AddWithValue("@id", jobId);
            del.ExecuteNonQuery();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var d in diagnostics)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO snapshot_job_diagnostics
                    (job_id, project_canonical_id, project_path, severity, code, message, created_at)
                VALUES (@job, @canon, @path, @sev, @code, @msg, @now);
                """;
            cmd.Parameters.AddWithValue("@job", jobId);
            cmd.Parameters.AddWithValue("@canon", (object?)d.ProjectCanonicalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@path", (object?)d.ProjectPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sev", d.Severity);
            cmd.Parameters.AddWithValue("@code", (object?)d.Code ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@msg", d.Message);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SnapshotJobDiagnostic> GetDiagnostics(long jobId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT job_id, project_canonical_id, project_path, severity, code, message, created_at
            FROM snapshot_job_diagnostics
            WHERE job_id = @id
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("@id", jobId);
        var rows = new List<SnapshotJobDiagnostic>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new SnapshotJobDiagnostic
            {
                JobId = reader.GetInt64(0),
                ProjectCanonicalId = reader.IsDBNull(1) ? null : reader.GetString(1),
                ProjectPath = reader.IsDBNull(2) ? null : reader.GetString(2),
                Severity = reader.GetString(3),
                Code = reader.IsDBNull(4) ? null : reader.GetString(4),
                Message = reader.GetString(5),
                CreatedAt = reader.GetInt64(6)
            });
        return rows;
    }

    private const string SelectJob = """
        SELECT id, identity_hash, repository_url, commit_sha, branch_name, status, snapshot_id,
               owner_token, attempts, created_at, updated_at, started_at, completed_at, last_error
        FROM snapshot_jobs
        """;

    private static SnapshotJobRow ReadJob(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        IdentityHash = reader.GetString(1),
        RepositoryUrl = reader.GetString(2),
        CommitSha = reader.GetString(3),
        BranchName = reader.IsDBNull(4) ? null : reader.GetString(4),
        Status = reader.GetString(5),
        SnapshotId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
        OwnerToken = reader.IsDBNull(7) ? null : reader.GetString(7),
        Attempts = reader.GetInt32(8),
        CreatedAt = reader.GetInt64(9),
        UpdatedAt = reader.GetInt64(10),
        StartedAt = reader.IsDBNull(11) ? null : reader.GetInt64(11),
        CompletedAt = reader.IsDBNull(12) ? null : reader.GetInt64(12),
        LastError = reader.IsDBNull(13) ? null : reader.GetString(13)
    };
}
