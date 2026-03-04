using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class FileIndexStore(SqliteConnection connection)
{
    public long Upsert(FileIndexEntry entry)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_index (project_id, file_path, content_hash, last_indexed_at)
            VALUES (@project_id, @file_path, @content_hash, @last_indexed_at)
            ON CONFLICT(project_id, file_path) DO UPDATE SET
                content_hash = @content_hash,
                last_indexed_at = @last_indexed_at
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@project_id", entry.ProjectId);
        cmd.Parameters.AddWithValue("@file_path", entry.FilePath);
        cmd.Parameters.AddWithValue("@content_hash", entry.ContentHash);
        cmd.Parameters.AddWithValue("@last_indexed_at", entry.LastIndexedAt);

        return (long)cmd.ExecuteScalar()!;
    }

    public FileIndexEntry? GetByProjectAndFile(long projectId, string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_index WHERE project_id = @project_id AND file_path = @file_path;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@file_path", filePath);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public List<FileIndexEntry> GetByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_index WHERE project_id = @project_id;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        return ReadAll(cmd);
    }

    public void DeleteByProjectAndFile(long projectId, string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM file_index WHERE project_id = @project_id AND file_path = @file_path;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.ExecuteNonQuery();
    }

    public void DeleteByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM file_index WHERE project_id = @project_id;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.ExecuteNonQuery();
    }

    private static List<FileIndexEntry> ReadAll(SqliteCommand cmd)
    {
        var results = new List<FileIndexEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadEntry(reader));
        return results;
    }

    private static FileIndexEntry ReadEntry(SqliteDataReader reader)
    {
        return new FileIndexEntry
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ProjectId = reader.GetInt64(reader.GetOrdinal("project_id")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
            LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
        };
    }
}
