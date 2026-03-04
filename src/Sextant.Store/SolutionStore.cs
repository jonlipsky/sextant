using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class SolutionStore(SqliteConnection connection)
{
    public long Upsert(string filePath, string name, long lastIndexedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO solutions (file_path, name, last_indexed_at)
            VALUES (@file_path, @name, @last_indexed_at)
            ON CONFLICT(file_path) DO UPDATE SET
                name = excluded.name,
                last_indexed_at = excluded.last_indexed_at
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@last_indexed_at", lastIndexedAt);

        return (long)cmd.ExecuteScalar()!;
    }

    public void AddProjectMapping(long solutionId, long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO solution_projects (solution_id, project_id)
            VALUES (@solution_id, @project_id);
            """;
        cmd.Parameters.AddWithValue("@solution_id", solutionId);
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.ExecuteNonQuery();
    }

    public List<(long id, string filePath, string name, long lastIndexedAt)> GetAll()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, file_path, name, last_indexed_at FROM solutions;";

        var results = new List<(long, string, string, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)
            ));
        }
        return results;
    }

    public List<long> GetProjectIds(long solutionId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM solution_projects WHERE solution_id = @solution_id;";
        cmd.Parameters.AddWithValue("@solution_id", solutionId);

        var results = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetInt64(0));
        return results;
    }

    public HashSet<long> GetProjectIdsForSolution(string solutionPath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT sp.project_id FROM solution_projects sp
            JOIN solutions s ON s.id = sp.solution_id
            WHERE s.file_path = @path;
            """;
        cmd.Parameters.AddWithValue("@path", solutionPath);

        var results = new HashSet<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetInt64(0));
        return results;
    }
}
