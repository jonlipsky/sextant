using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store;

/// <summary>
/// Durable per-snapshot coverage (migration 022, issue #119). One immutable row per published snapshot,
/// written inside the publish transaction by the orchestrator when the producing worker computed coverage.
/// A snapshot with no row has "coverage not recorded" (a local/overlay/provider/contribution/pre-022
/// snapshot) — distinct from a recorded complete verdict. Enrols in the caller's ambient transaction.
/// </summary>
public sealed class SnapshotCoverageStore(SqliteConnection connection)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Records <paramref name="coverage"/> for <paramref name="snapshotId"/> unless a row already exists
    /// (immutable, like the snapshot). Returns true when this call wrote the row.
    /// </summary>
    public bool Record(long snapshotId, SnapshotCoverage coverage, long recordedAt)
    {
        if (coverage.Verdict is not (SnapshotCoverageVerdict.Complete or SnapshotCoverageVerdict.Partial))
            throw new ArgumentException($"Unknown coverage verdict '{coverage.Verdict}'.", nameof(coverage));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshot_coverage (snapshot_id, verdict, summary_json, recorded_at)
            VALUES (@id, @verdict, @json, @at)
            ON CONFLICT(snapshot_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        cmd.Parameters.AddWithValue("@verdict", coverage.Verdict);
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(coverage, JsonOptions));
        cmd.Parameters.AddWithValue("@at", recordedAt);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Drops the coverage recorded for <paramref name="snapshotId"/> — only for a snapshot identity that is
    /// about to be REBUILT from scratch (a prior non-complete generation), whose old coverage is stale.
    /// </summary>
    public void Delete(long snapshotId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM snapshot_coverage WHERE snapshot_id = @id;";
        cmd.Parameters.AddWithValue("@id", snapshotId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The recorded coverage for <paramref name="snapshotId"/>, or null when none was recorded — including
    /// when a read-only reader opened a catalog that predates migration 022 (no table ⇒ nothing recorded).
    /// </summary>
    public SnapshotCoverage? Get(long snapshotId)
    {
        if (!TableExists())
            return null;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT verdict, summary_json FROM snapshot_coverage WHERE snapshot_id = @id;";
        cmd.Parameters.AddWithValue("@id", snapshotId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var verdict = reader.GetString(0);
        SnapshotCoverage? parsed = null;
        try
        {
            parsed = JsonSerializer.Deserialize<SnapshotCoverage>(reader.GetString(1), JsonOptions);
        }
        catch (JsonException)
        {
            // An unreadable summary still has an authoritative verdict column; never let a corrupt JSON
            // blob turn a recorded partial into "not recorded".
        }

        return parsed is null
            ? new SnapshotCoverage { Verdict = verdict, Reasons = ["coverage summary could not be read"] }
            : parsed with { Verdict = verdict };
    }

    // Schema probe, not exception-text matching: a catalog opened before migration 022 has no table.
    private bool TableExists()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'snapshot_coverage' LIMIT 1;";
        return cmd.ExecuteScalar() is not null;
    }
}
