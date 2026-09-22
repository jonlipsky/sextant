using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Stores structural relationships between symbols (implements/inherits/overrides/instantiates/…).
/// Phase 7: <c>kind</c> is a compact integer enum ordinal, and file-scoped deletes resolve a source
/// path to the endpoint symbols through <c>file_versions</c> (symbols no longer carry an absolute path).
/// </summary>
public sealed class RelationshipStore(SqliteConnection connection)
{
    public FileStore? Files { get; set; }

    private FileStore FilesOrDefault => Files ??= new FileStore(connection);

    private const string InsertSql = """
        INSERT INTO relationships (from_symbol_id, to_symbol_id, kind, last_indexed_at)
        VALUES (@from, @to, @kind, @last_indexed_at)
        RETURNING id;
        """;

    public SqliteCommand CreateInsertCommand()
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = InsertSql;
        return cmd;
    }

    public long Insert(RelationshipInfo relationship)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, relationship);
    }

    public long Insert(SqliteCommand cmd, RelationshipInfo relationship)
    {
        SqlParam.Set(cmd, "@from", relationship.FromSymbolId);
        SqlParam.Set(cmd, "@to", relationship.ToSymbolId);
        SqlParam.Set(cmd, "@kind", (int)relationship.Kind);
        SqlParam.Set(cmd, "@last_indexed_at", relationship.LastIndexedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<RelationshipInfo> GetByFromSymbol(long fromSymbolId, RelationshipKind? kind = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = kind.HasValue
            ? "SELECT * FROM relationships WHERE from_symbol_id = @from AND kind = @kind;"
            : "SELECT * FROM relationships WHERE from_symbol_id = @from;";
        cmd.Parameters.AddWithValue("@from", fromSymbolId);
        if (kind.HasValue)
            cmd.Parameters.AddWithValue("@kind", (int)kind.Value);
        return ReadAll(cmd);
    }

    public List<RelationshipInfo> GetByToSymbol(long toSymbolId, RelationshipKind? kind = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = kind.HasValue
            ? "SELECT * FROM relationships WHERE to_symbol_id = @to AND kind = @kind;"
            : "SELECT * FROM relationships WHERE to_symbol_id = @to;";
        cmd.Parameters.AddWithValue("@to", toSymbolId);
        if (kind.HasValue)
            cmd.Parameters.AddWithValue("@kind", (int)kind.Value);
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        var candidates = CandidateRelatives(filePath);
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", candidates.Select((_, i) => $"@rel{i}"));
        var symbolsInFile = $"""
            SELECT s.id FROM symbols s
            JOIN file_versions fv ON fv.id = s.file_version_id
            JOIN files f ON f.id = fv.file_id
            WHERE f.repo_relative_path IN ({placeholders})
            """;
        cmd.CommandText = $"""
            DELETE FROM relationships WHERE
                from_symbol_id IN ({symbolsInFile})
                OR to_symbol_id IN ({symbolsInFile});
            """;
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue($"@rel{i}", candidates[i]);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: relationships have no project column, so scope through the endpoint
    // symbols' project. This clears only relationships anchored on one logical (per-TFM) project's
    // symbols in the shared source file, leaving the sibling framework's relationships intact.
    public void DeleteByFile(string filePath, long projectId)
    {
        var rel = SourcePaths.ToRepoRelative(FilesOrDefault.GetRepoRoot(projectId), filePath);
        using var cmd = connection.CreateCommand();
        var symbolsInFile = """
            SELECT s.id FROM symbols s
            JOIN file_versions fv ON fv.id = s.file_version_id
            JOIN files f ON f.id = fv.file_id
            WHERE s.project_id = @project_id AND f.project_id = @project_id AND f.repo_relative_path = @rel
            """;
        cmd.CommandText = $"""
            DELETE FROM relationships WHERE
                from_symbol_id IN ({symbolsInFile})
                OR to_symbol_id IN ({symbolsInFile});
            """;
        cmd.Parameters.AddWithValue("@rel", rel);
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.ExecuteNonQuery();
    }

    private List<string> CandidateRelatives(string path)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal) { path };
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT disk_path, repo_relative_path FROM projects;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var disk = reader.IsDBNull(0) ? null : reader.GetString(0);
            var projRel = reader.IsDBNull(1) ? null : reader.GetString(1);
            candidates.Add(SourcePaths.ToRepoRelative(SourcePaths.DeriveRepoRoot(disk, projRel), path));
        }
        return candidates.ToList();
    }

    private static List<RelationshipInfo> ReadAll(SqliteCommand cmd)
    {
        var results = new List<RelationshipInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RelationshipInfo
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                FromSymbolId = reader.GetInt64(reader.GetOrdinal("from_symbol_id")),
                ToSymbolId = reader.GetInt64(reader.GetOrdinal("to_symbol_id")),
                Kind = (RelationshipKind)reader.GetInt64(reader.GetOrdinal("kind")),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
