using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class RelationshipStore(SqliteConnection connection)
{
    public long Insert(RelationshipInfo relationship)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO relationships (from_symbol_id, to_symbol_id, kind, last_indexed_at)
            VALUES (@from, @to, @kind, @last_indexed_at)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@from", relationship.FromSymbolId);
        cmd.Parameters.AddWithValue("@to", relationship.ToSymbolId);
        cmd.Parameters.AddWithValue("@kind", relationship.Kind.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@last_indexed_at", relationship.LastIndexedAt);

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
            cmd.Parameters.AddWithValue("@kind", kind.Value.ToString().ToLowerInvariant());
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
            cmd.Parameters.AddWithValue("@kind", kind.Value.ToString().ToLowerInvariant());
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM relationships WHERE
                from_symbol_id IN (SELECT id FROM symbols WHERE file_path = @file_path)
                OR to_symbol_id IN (SELECT id FROM symbols WHERE file_path = @file_path);
            """;
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.ExecuteNonQuery();
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
                Kind = Enum.Parse<RelationshipKind>(reader.GetString(reader.GetOrdinal("kind")), ignoreCase: true),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
