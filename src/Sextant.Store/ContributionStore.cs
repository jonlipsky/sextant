using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>One durable record of an accepted contribution artifact (migration 018 <c>snapshot_contributions</c>).</summary>
public sealed record SnapshotContributionRow
{
    public required long Id { get; init; }
    public required long SnapshotId { get; init; }
    public required string ContentHash { get; init; }
    public string? Tenant { get; init; }
    public required string RepositoryUrl { get; init; }
    public required string CommitSha { get; init; }
    public string? CapabilityFingerprint { get; init; }
    public string? Producer { get; init; }
    public string? ToolchainFingerprint { get; init; }
    public string? ManifestHash { get; init; }
    public long CreatedAt { get; init; }
}

/// <summary>
/// The durable contribution-provenance ledger (Phase 16, migration 018). A row is written for every
/// ACCEPTED contribution artifact, keyed by the artifact's content address (<c>content_hash</c> UNIQUE) so
/// re-uploading the same artifact is a content-addressed no-op (acceptance criterion 2) — the service looks
/// up the content hash before importing and, on a hit, attaches to the existing snapshot instead of
/// re-importing. Each row names the assembled snapshot it fed, the capability that produced it, and its
/// producer/toolchain/tenant, so an assembled snapshot's provenance names every environment that
/// contributed to it. It is a thin SQL adapter that enrols in the caller's ambient write transaction (the
/// service owns the writer lease + transaction boundaries).
/// </summary>
public sealed class ContributionStore(SqliteConnection connection)
{
    /// <summary>The contribution recorded for a content address, or null when none exists yet.</summary>
    public SnapshotContributionRow? GetByContentHash(string contentHash)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Select + " WHERE content_hash = @h;";
        cmd.Parameters.AddWithValue("@h", contentHash);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>All contributions that fed a snapshot (provenance for an assembled snapshot).</summary>
    public IReadOnlyList<SnapshotContributionRow> GetBySnapshot(long snapshotId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Select + " WHERE snapshot_id = @s ORDER BY id;";
        cmd.Parameters.AddWithValue("@s", snapshotId);
        var rows = new List<SnapshotContributionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>
    /// Records an accepted contribution against the assembled snapshot it fed. <paramref name="contentHash"/>
    /// is UNIQUE, so a duplicate insert is ignored (the idempotency guard is the earlier
    /// <see cref="GetByContentHash"/> lookup). <paramref name="completeness"/> is the contribution's declared
    /// assembly completeness ('complete'/'partial'/'unsupported', issue #70) — the finalize gate publishes the
    /// assembled snapshot Partial when ANY contribution that fed it was non-complete. Returns the row id
    /// (existing on conflict).
    /// </summary>
    public long Record(
        long snapshotId, string contentHash, string? tenant, string repositoryUrl, string commitSha,
        string? capabilityFingerprint, string? producer, string? toolchainFingerprint, string? manifestHash,
        string completeness = "complete")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshot_contributions
                (snapshot_id, content_hash, tenant, repository_url, commit_sha, capability_fingerprint,
                 producer, toolchain_fingerprint, manifest_hash, created_at, completeness)
            VALUES (@snap, @hash, @tenant, @repo, @commit, @cap, @producer, @tool, @manifest, @now, @complete)
            ON CONFLICT(content_hash) DO UPDATE SET content_hash = excluded.content_hash
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@tenant", (object?)tenant ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@repo", repositoryUrl);
        cmd.Parameters.AddWithValue("@commit", commitSha);
        cmd.Parameters.AddWithValue("@cap", (object?)capabilityFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@producer", (object?)producer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tool", (object?)toolchainFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@manifest", (object?)manifestHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@complete", completeness);
        return Convert.ToInt64(cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// True when ANY contribution that fed <paramref name="snapshotId"/> declared itself non-complete
    /// (issue #70). The finalize completeness gate uses this to publish the assembled snapshot Partial
    /// rather than silently Complete.
    /// </summary>
    public bool HasIncompleteContribution(long snapshotId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM snapshot_contributions WHERE snapshot_id = @s AND completeness <> 'complete' LIMIT 1;";
        cmd.Parameters.AddWithValue("@s", snapshotId);
        return cmd.ExecuteScalar() is not null;
    }

    private const string Select = """
        SELECT id, snapshot_id, content_hash, tenant, repository_url, commit_sha, capability_fingerprint,
               producer, toolchain_fingerprint, manifest_hash, created_at
        FROM snapshot_contributions
        """;

    private static SnapshotContributionRow Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SnapshotId = reader.GetInt64(1),
        ContentHash = reader.GetString(2),
        Tenant = reader.IsDBNull(3) ? null : reader.GetString(3),
        RepositoryUrl = reader.GetString(4),
        CommitSha = reader.GetString(5),
        CapabilityFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
        Producer = reader.IsDBNull(7) ? null : reader.GetString(7),
        ToolchainFingerprint = reader.IsDBNull(8) ? null : reader.GetString(8),
        ManifestHash = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAt = reader.GetInt64(10)
    };
}
