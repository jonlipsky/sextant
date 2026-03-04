using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ReferenceStore(SqliteConnection connection)
{
    public long Insert(ReferenceInfo reference)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "references" (symbol_id, in_project_id, file_path, line, context_snippet, reference_kind, access_kind, last_indexed_at)
            VALUES (@symbol_id, @in_project_id, @file_path, @line, @context_snippet, @reference_kind, @access_kind, @last_indexed_at)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@symbol_id", reference.SymbolId);
        cmd.Parameters.AddWithValue("@in_project_id", reference.InProjectId);
        cmd.Parameters.AddWithValue("@file_path", reference.FilePath);
        cmd.Parameters.AddWithValue("@line", reference.Line);
        cmd.Parameters.AddWithValue("@context_snippet", (object?)reference.ContextSnippet ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reference_kind", reference.ReferenceKind.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@access_kind", reference.AccessKind.HasValue
            ? reference.AccessKind.Value.ToString().ToLowerInvariant()
            : DBNull.Value);
        cmd.Parameters.AddWithValue("@last_indexed_at", reference.LastIndexedAt);

        return (long)cmd.ExecuteScalar()!;
    }

    public List<ReferenceInfo> GetBySymbolId(long symbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """SELECT * FROM "references" WHERE symbol_id = @symbol_id;""";
        cmd.Parameters.AddWithValue("@symbol_id", symbolId);
        return ReadAll(cmd);
    }

    public List<ReferenceInfo> GetByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """SELECT * FROM "references" WHERE in_project_id = @project_id;""";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """DELETE FROM "references" WHERE file_path = @file_path;""";
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.ExecuteNonQuery();
    }

    private static List<ReferenceInfo> ReadAll(SqliteCommand cmd)
    {
        var results = new List<ReferenceInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var accessKindOrdinal = reader.GetOrdinal("access_kind");
            results.Add(new ReferenceInfo
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SymbolId = reader.GetInt64(reader.GetOrdinal("symbol_id")),
                InProjectId = reader.GetInt64(reader.GetOrdinal("in_project_id")),
                FilePath = reader.GetString(reader.GetOrdinal("file_path")),
                Line = reader.GetInt32(reader.GetOrdinal("line")),
                ContextSnippet = reader.IsDBNull(reader.GetOrdinal("context_snippet")) ? null : reader.GetString(reader.GetOrdinal("context_snippet")),
                ReferenceKind = Enum.Parse<ReferenceKind>(reader.GetString(reader.GetOrdinal("reference_kind")), ignoreCase: true),
                AccessKind = reader.IsDBNull(accessKindOrdinal) ? null : Enum.Parse<AccessKind>(reader.GetString(accessKindOrdinal), ignoreCase: true),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
