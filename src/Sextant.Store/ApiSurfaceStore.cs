using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ApiSurfaceStore(SqliteConnection connection)
{
    // Phase 9: each immutable snapshot of a project gets its OWN physical project row (a new project_id),
    // but every version of one logical project shares a logical_project_id. Historical api-surface rows
    // for an older commit therefore live under a DIFFERENT physical project_id than the currently selected
    // snapshot's row. To keep cross-commit API comparisons working across reindexing (criterion 5), every
    // api-history lookup resolves a physical project_id to its whole logical-project group. The trailing
    // UNION is the legacy fallback: a pre-Phase-9 row has logical_project_id IS NULL, so the group is just
    // the row itself — byte-for-byte the original single-project behavior.
    private const string LogicalProjectGroup = """
        (SELECT p2.id FROM projects p1
              JOIN projects p2 ON p2.logical_project_id = p1.logical_project_id
              WHERE p1.id = @project_id AND p1.logical_project_id IS NOT NULL
         UNION SELECT @project_id)
        """;

    public long Insert(ApiSurfaceSnapshot snapshot)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO api_surface_snapshots
                (project_id, symbol_id, symbol_key, fully_qualified_name, accessibility, signature_hash, captured_at, git_commit)
            VALUES (@project_id, @symbol_id, @symbol_key, @fully_qualified_name, @accessibility, @signature_hash, @captured_at, @git_commit)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@project_id", snapshot.ProjectId);
        cmd.Parameters.AddWithValue("@symbol_id", (object?)snapshot.SymbolId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@symbol_key", snapshot.SymbolKey);
        cmd.Parameters.AddWithValue("@fully_qualified_name", snapshot.FullyQualifiedName);
        cmd.Parameters.AddWithValue("@accessibility", snapshot.Accessibility);
        cmd.Parameters.AddWithValue("@signature_hash", snapshot.SignatureHash);
        cmd.Parameters.AddWithValue("@captured_at", snapshot.CapturedAt);
        cmd.Parameters.AddWithValue("@git_commit", snapshot.GitCommit);

        return (long)cmd.ExecuteScalar()!;
    }

    public List<ApiSurfaceSnapshot> GetByProjectAndCommit(long projectId, string gitCommit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.id, a.project_id, a.symbol_id, a.symbol_key, a.fully_qualified_name, a.accessibility,
                   a.signature_hash, a.captured_at, a.git_commit
            FROM api_surface_snapshots a
            WHERE a.project_id IN {LogicalProjectGroup} AND a.git_commit = @git_commit;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@git_commit", gitCommit);

        return ReadSnapshots(cmd);
    }

    public List<ApiSurfaceSnapshot> GetLatestByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.id, a.project_id, a.symbol_id, a.symbol_key, a.fully_qualified_name, a.accessibility,
                   a.signature_hash, a.captured_at, a.git_commit
            FROM api_surface_snapshots a
            WHERE a.project_id IN {LogicalProjectGroup}
              AND a.captured_at = (
                  SELECT MAX(captured_at) FROM api_surface_snapshots WHERE project_id IN {LogicalProjectGroup}
              );
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);

        return ReadSnapshots(cmd);
    }

    public string? GetPreviousCommit(long projectId, string currentCommit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT git_commit FROM api_surface_snapshots
            WHERE project_id IN {LogicalProjectGroup} AND git_commit != @current_commit
            ORDER BY captured_at DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@current_commit", currentCommit);

        return cmd.ExecuteScalar() as string;
    }

    public void DeleteByProjectAndCommit(long projectId, string gitCommit)
    {
        // Deletion stays precise to the physical project row: a (commit, logical project) has its api rows
        // under exactly one physical project version, so re-capture replaces only that snapshot's rows and
        // never a sibling snapshot's committed history (criterion 5 — historical snapshots survive rebuilds).
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM api_surface_snapshots WHERE project_id = @project_id AND git_commit = @git_commit;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@git_commit", gitCommit);
        cmd.ExecuteNonQuery();
    }

    private static List<ApiSurfaceSnapshot> ReadSnapshots(SqliteCommand cmd)
    {
        var results = new List<ApiSurfaceSnapshot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ApiSurfaceSnapshot
            {
                Id = reader.GetInt64(0),
                ProjectId = reader.GetInt64(1),
                SymbolId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                SymbolKey = reader.GetString(3),
                FullyQualifiedName = reader.GetString(4),
                Accessibility = reader.GetString(5),
                SignatureHash = reader.GetString(6),
                CapturedAt = reader.GetInt64(7),
                GitCommit = reader.GetString(8)
            });
        }
        return results;
    }
}
