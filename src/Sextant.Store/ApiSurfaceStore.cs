using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ApiSurfaceStore(SqliteConnection connection)
{
    public long Insert(ApiSurfaceSnapshot snapshot)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO api_surface_snapshots (project_id, symbol_id, signature_hash, captured_at, git_commit)
            VALUES (@project_id, @symbol_id, @signature_hash, @captured_at, @git_commit)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@project_id", snapshot.ProjectId);
        cmd.Parameters.AddWithValue("@symbol_id", snapshot.SymbolId);
        cmd.Parameters.AddWithValue("@signature_hash", snapshot.SignatureHash);
        cmd.Parameters.AddWithValue("@captured_at", snapshot.CapturedAt);
        cmd.Parameters.AddWithValue("@git_commit", snapshot.GitCommit);

        return (long)cmd.ExecuteScalar()!;
    }

    public List<ApiSurfaceSnapshot> GetByProjectAndCommit(long projectId, string gitCommit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.project_id, a.symbol_id, a.signature_hash, a.captured_at, a.git_commit
            FROM api_surface_snapshots a
            WHERE a.project_id = @project_id AND a.git_commit = @git_commit;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@git_commit", gitCommit);

        return ReadSnapshots(cmd);
    }

    public List<ApiSurfaceSnapshot> GetLatestByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.project_id, a.symbol_id, a.signature_hash, a.captured_at, a.git_commit
            FROM api_surface_snapshots a
            WHERE a.project_id = @project_id
              AND a.captured_at = (
                  SELECT MAX(captured_at) FROM api_surface_snapshots WHERE project_id = @project_id
              );
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);

        return ReadSnapshots(cmd);
    }

    public string? GetPreviousCommit(long projectId, string currentCommit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT git_commit FROM api_surface_snapshots
            WHERE project_id = @project_id AND git_commit != @current_commit
            ORDER BY captured_at DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@current_commit", currentCommit);

        return cmd.ExecuteScalar() as string;
    }

    public void DeleteByProjectAndCommit(long projectId, string gitCommit)
    {
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
                SymbolId = reader.GetInt64(2),
                SignatureHash = reader.GetString(3),
                CapturedAt = reader.GetInt64(4),
                GitCommit = reader.GetString(5)
            });
        }
        return results;
    }
}
