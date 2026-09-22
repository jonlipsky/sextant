using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>The lifecycle state of a tracked pull-request snapshot root (migration 019).</summary>
public static class PullRequestSnapshotState
{
    /// <summary>The pull request is open; its snapshot is a retention root and must be protected.</summary>
    public const string Open = "open";

    /// <summary>The pull request is closed/merged; its snapshot is no longer protected by this root.</summary>
    public const string Closed = "closed";
}

/// <summary>One tracked pull-request snapshot root row.</summary>
public sealed record PullRequestSnapshotRow
{
    public required long Id { get; init; }
    public required long RepositoryId { get; init; }
    public required long PrNumber { get; init; }
    public long? SnapshotId { get; init; }
    public string? HeadCommitSha { get; init; }
    public required string State { get; init; }
    public long UpdatedAt { get; init; }
}

/// <summary>
/// The durable open-pull-request retention roots (migration 019). Criterion 4 requires retention to
/// never delete a snapshot an OPEN pull request resolves to, even after its generation falls out of the
/// keep window and its head commit is not a branch pointer. Registering a PR head pins the immutable
/// snapshot it resolves to; closing the PR releases that protection so the snapshot becomes eligible for
/// ordinary keep-window / quota GC. Idempotent per <c>(repository_id, pr_number)</c> so re-registering a
/// PR that advanced its head simply repoints the same root. Enrols in the caller's ambient write
/// transaction — the service owns the writer lease + transaction boundaries.
/// </summary>
public sealed class PullRequestSnapshotStore(SqliteConnection connection)
{
    /// <summary>
    /// Registers (or advances) the open snapshot root for a pull request. Upserts on
    /// <c>(repository_id, pr_number)</c>: a re-registration repoints the root at the newly-resolved
    /// snapshot/commit and re-opens it if it had been closed. Returns the row id.
    /// </summary>
    public long Register(long repositoryId, long prNumber, long? snapshotId, string? headCommitSha, long now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pull_request_snapshots
                (repository_id, pr_number, snapshot_id, head_commit_sha, state, updated_at)
            VALUES (@repo, @pr, @snap, @sha, @open, @now)
            ON CONFLICT(repository_id, pr_number) DO UPDATE SET
                snapshot_id = excluded.snapshot_id,
                head_commit_sha = excluded.head_commit_sha,
                state = excluded.state,
                updated_at = excluded.updated_at
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@pr", prNumber);
        cmd.Parameters.AddWithValue("@snap", (object?)snapshotId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sha", (object?)headCommitSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@open", PullRequestSnapshotState.Open);
        cmd.Parameters.AddWithValue("@now", now);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Marks a pull request closed, releasing its retention protection. No-op when the PR was never
    /// registered. Returns the number of rows updated.
    /// </summary>
    public int Close(long repositoryId, long prNumber, long now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE pull_request_snapshots SET state = @closed, updated_at = @now
             WHERE repository_id = @repo AND pr_number = @pr AND state = @open;
            """;
        cmd.Parameters.AddWithValue("@closed", PullRequestSnapshotState.Closed);
        cmd.Parameters.AddWithValue("@open", PullRequestSnapshotState.Open);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@pr", prNumber);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>The tracked PR root for a (repository, pr) pair, or null when absent.</summary>
    public PullRequestSnapshotRow? Get(long repositoryId, long prNumber)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Select + " WHERE repository_id = @repo AND pr_number = @pr;";
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@pr", prNumber);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Every currently-open PR snapshot root.</summary>
    public IReadOnlyList<PullRequestSnapshotRow> GetOpen()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Select + " WHERE state = @open ORDER BY id;";
        cmd.Parameters.AddWithValue("@open", PullRequestSnapshotState.Open);
        var rows = new List<PullRequestSnapshotRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    private const string Select = """
        SELECT id, repository_id, pr_number, snapshot_id, head_commit_sha, state, updated_at
        FROM pull_request_snapshots
        """;

    private static PullRequestSnapshotRow Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        RepositoryId = reader.GetInt64(1),
        PrNumber = reader.GetInt64(2),
        SnapshotId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
        HeadCommitSha = reader.IsDBNull(4) ? null : reader.GetString(4),
        State = reader.GetString(5),
        UpdatedAt = reader.GetInt64(6)
    };
}
