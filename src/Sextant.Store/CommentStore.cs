using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class CommentStore(SqliteConnection connection)
{
    public long Insert(long projectId, string filePath, int line, string tag,
                       string text, long? enclosingSymbolId, long lastIndexedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO comments (project_id, file_path, line, tag, text, enclosing_symbol_id, last_indexed_at)
            VALUES (@project_id, @file_path, @line, @tag, @text, @enclosing_symbol_id, @last_indexed_at)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@tag", tag);
        cmd.Parameters.AddWithValue("@text", text);
        cmd.Parameters.AddWithValue("@enclosing_symbol_id", enclosingSymbolId.HasValue ? enclosingSymbolId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@last_indexed_at", lastIndexedAt);

        return (long)cmd.ExecuteScalar()!;
    }

    public List<CommentInfo> GetByTag(string tag, long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        var projectClause = projectId.HasValue ? " AND project_id = @projectId" : "";
        cmd.CommandText = $"SELECT * FROM comments WHERE tag = @tag{projectClause} ORDER BY file_path, line;";
        cmd.Parameters.AddWithValue("@tag", tag);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        return ReadAll(cmd);
    }

    public List<CommentInfo> GetAll(long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        if (projectId.HasValue)
        {
            cmd.CommandText = "SELECT * FROM comments WHERE project_id = @projectId ORDER BY file_path, line;";
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM comments ORDER BY file_path, line;";
        }
        return ReadAll(cmd);
    }

    public List<CommentInfo> GetByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM comments WHERE file_path = @filePath ORDER BY line;";
        cmd.Parameters.AddWithValue("@filePath", filePath);
        return ReadAll(cmd);
    }

    public List<CommentInfo> GetBySymbol(long symbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM comments WHERE enclosing_symbol_id = @symbolId ORDER BY line;";
        cmd.Parameters.AddWithValue("@symbolId", symbolId);
        return ReadAll(cmd);
    }

    public List<CommentInfo> Search(string query, long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        var projectClause = projectId.HasValue ? " AND project_id = @projectId" : "";
        cmd.CommandText = $"SELECT * FROM comments WHERE text LIKE '%' || @query || '%'{projectClause} ORDER BY file_path, line;";
        cmd.Parameters.AddWithValue("@query", query);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM comments WHERE file_path = @filePath;";
        cmd.Parameters.AddWithValue("@filePath", filePath);
        cmd.ExecuteNonQuery();
    }

    private static List<CommentInfo> ReadAll(SqliteCommand cmd)
    {
        var results = new List<CommentInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var enclosingOrdinal = reader.GetOrdinal("enclosing_symbol_id");
            results.Add(new CommentInfo
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ProjectId = reader.GetInt64(reader.GetOrdinal("project_id")),
                FilePath = reader.GetString(reader.GetOrdinal("file_path")),
                Line = reader.GetInt32(reader.GetOrdinal("line")),
                Tag = reader.GetString(reader.GetOrdinal("tag")),
                Text = reader.GetString(reader.GetOrdinal("text")),
                EnclosingSymbolId = reader.IsDBNull(enclosingOrdinal) ? null : reader.GetInt64(enclosingOrdinal),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
