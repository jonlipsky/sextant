using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ReturnFlowStore(SqliteConnection connection)
{
    private const string InsertSql = """
        INSERT INTO return_flow (call_graph_id, destination_kind, destination_variable,
            destination_symbol_fqn, last_indexed_at)
        VALUES (@call_graph_id, @kind, @variable, @symbol_fqn, @last_indexed_at)
        RETURNING id;
        """;

    public SqliteCommand CreateInsertCommand()
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = InsertSql;
        return cmd;
    }

    public long Insert(long callGraphId, string destinationKind, string? destinationVariable,
                       string? destinationSymbolFqn, long lastIndexedAt)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, callGraphId, destinationKind, destinationVariable, destinationSymbolFqn, lastIndexedAt);
    }

    public long Insert(SqliteCommand cmd, long callGraphId, string destinationKind, string? destinationVariable,
                       string? destinationSymbolFqn, long lastIndexedAt)
    {
        SqlParam.Set(cmd, "@call_graph_id", callGraphId);
        SqlParam.Set(cmd, "@kind", destinationKind);
        SqlParam.Set(cmd, "@variable", destinationVariable);
        SqlParam.Set(cmd, "@symbol_fqn", destinationSymbolFqn);
        SqlParam.Set(cmd, "@last_indexed_at", lastIndexedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<ReturnFlowInfo> GetByCallGraphId(long callGraphId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM return_flow WHERE call_graph_id = @id;";
        cmd.Parameters.AddWithValue("@id", callGraphId);
        return ReadAll(cmd);
    }

    public List<ReturnFlowInfo> GetByCallGraphIds(IEnumerable<long> callGraphIds)
    {
        var ids = callGraphIds.ToList();
        if (ids.Count == 0) return new List<ReturnFlowInfo>();

        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"SELECT * FROM return_flow WHERE call_graph_id IN ({placeholders});";
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        return ReadAll(cmd);
    }

    public void DeleteByCallGraphId(long callGraphId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM return_flow WHERE call_graph_id = @id;";
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
                CallGraphId = reader.GetInt64(reader.GetOrdinal("call_graph_id")),
                DestinationKind = reader.GetString(reader.GetOrdinal("destination_kind")),
                DestinationVariable = reader.IsDBNull(reader.GetOrdinal("destination_variable")) ? null : reader.GetString(reader.GetOrdinal("destination_variable")),
                DestinationSymbolFqn = reader.IsDBNull(reader.GetOrdinal("destination_symbol_fqn")) ? null : reader.GetString(reader.GetOrdinal("destination_symbol_fqn")),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
