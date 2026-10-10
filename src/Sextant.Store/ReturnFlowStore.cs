using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ReturnFlowStore(SqliteConnection connection)
{
    private const string InsertSql = """
        INSERT INTO return_flow (occurrence_id, destination_kind, destination_variable,
            destination_symbol_fqn, last_indexed_at)
        VALUES (?1, ?2, ?3, ?4, ?5)
        RETURNING id;
        """;

    public PreparedInsert CreateInsertCommand() => new(connection, InsertSql);

    public long Insert(long callGraphId, string destinationKind, string? destinationVariable,
                       string? destinationSymbolFqn, long lastIndexedAt)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, callGraphId, destinationKind, destinationVariable, destinationSymbolFqn, lastIndexedAt);
    }

    public long Insert(PreparedInsert cmd, long callGraphId, string destinationKind, string? destinationVariable,
                       string? destinationSymbolFqn, long lastIndexedAt)
    {
        cmd.Bind(1, callGraphId);
        cmd.Bind(2, destinationKind);
        cmd.Bind(3, destinationVariable);
        cmd.Bind(4, destinationSymbolFqn);
        cmd.Bind(5, lastIndexedAt);
        return cmd.ExecuteReturningId();
    }

    public List<ReturnFlowInfo> GetByCallGraphId(long callGraphId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM return_flow WHERE occurrence_id = @id;";
        cmd.Parameters.AddWithValue("@id", callGraphId);
        return ReadAll(cmd);
    }

    public List<ReturnFlowInfo> GetByCallGraphIds(IEnumerable<long> callGraphIds)
    {
        var ids = callGraphIds.ToList();
        if (ids.Count == 0) return new List<ReturnFlowInfo>();

        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"SELECT * FROM return_flow WHERE occurrence_id IN ({placeholders});";
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        return ReadAll(cmd);
    }

    public void DeleteByCallGraphId(long callGraphId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM return_flow WHERE occurrence_id = @id;";
        cmd.Parameters.AddWithValue("@id", callGraphId);
        cmd.ExecuteNonQuery();
    }

    private static List<ReturnFlowInfo> ReadAll(SqliteCommand cmd)
    {
        var results = new List<ReturnFlowInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ReturnFlowInfo
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                CallGraphId = reader.GetInt64(reader.GetOrdinal("occurrence_id")),
                DestinationKind = reader.GetString(reader.GetOrdinal("destination_kind")),
                DestinationVariable = reader.IsDBNull(reader.GetOrdinal("destination_variable")) ? null : reader.GetString(reader.GetOrdinal("destination_variable")),
                DestinationSymbolFqn = reader.IsDBNull(reader.GetOrdinal("destination_symbol_fqn")) ? null : reader.GetString(reader.GetOrdinal("destination_symbol_fqn")),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
