using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ReferenceStore(SqliteConnection connection)
{
    private const string InsertSql = """
        INSERT INTO "references" (symbol_id, in_project_id, file_path, line, context_snippet, reference_kind, access_kind, last_indexed_at)
        VALUES (@symbol_id, @in_project_id, @file_path, @line, @context_snippet, @reference_kind, @access_kind, @last_indexed_at)
        RETURNING id;
        """;

    public SqliteCommand CreateInsertCommand()
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = InsertSql;
        return cmd;
    }

    public long Insert(ReferenceInfo reference)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, reference);
    }

    public long Insert(SqliteCommand cmd, ReferenceInfo reference)
    {
        SqlParam.Set(cmd, "@symbol_id", reference.SymbolId);
        SqlParam.Set(cmd, "@in_project_id", reference.InProjectId);
        SqlParam.Set(cmd, "@file_path", reference.FilePath);
        SqlParam.Set(cmd, "@line", reference.Line);
        SqlParam.Set(cmd, "@context_snippet", reference.ContextSnippet);
        SqlParam.Set(cmd, "@reference_kind", reference.ReferenceKind.ToString().ToLowerInvariant());
        SqlParam.Set(cmd, "@access_kind", reference.AccessKind.HasValue
            ? reference.AccessKind.Value.ToString().ToLowerInvariant()
            : null);
        SqlParam.Set(cmd, "@last_indexed_at", reference.LastIndexedAt);
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

    /// <summary>
    /// Returns the distinct (consumerProjectId, dependencyProjectId) pairs implied by the persisted
    /// cross-project references: a reference row whose usage site (in_project_id) differs from the
    /// project that owns the referenced symbol (symbols.project_id). Incremental reindexing unions
    /// these OLD edges into the invalidation closure so that after a removed project reference (or a
    /// deleted cross-project usage) the previously-referenced project is reprocessed too, letting its
    /// reference extraction rebuild away the now-stale inbound rows its symbols still own.
    /// </summary>
    public List<(long ConsumerProjectId, long DependencyProjectId)> GetCrossProjectPairs()
    {
        var pairs = new List<(long, long)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT r.in_project_id AS consumer, s.project_id AS dependency
            FROM "references" r
            JOIN symbols s ON s.id = r.symbol_id
            WHERE r.in_project_id != s.project_id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            pairs.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return pairs;
    }

    public void DeleteByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """DELETE FROM "references" WHERE file_path = @file_path;""";
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: references carry the project they occur in (in_project_id), so clearing
    // one logical (per-TFM) project's references for a shared source file leaves the sibling
    // framework's references for that same file intact.
    public void DeleteByFile(string filePath, long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """DELETE FROM "references" WHERE file_path = @file_path AND in_project_id = @project_id;""";
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.Parameters.AddWithValue("@project_id", projectId);
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
