using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ArgumentFlowStore(SqliteConnection connection)
{
    public long Insert(long callGraphId, int parameterOrdinal, string parameterName,
                       string argumentExpression, string argumentKind, string? sourceSymbolFqn,
                       long lastIndexedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO argument_flow (call_graph_id, parameter_ordinal, parameter_name,
                argument_expression, argument_kind, source_symbol_fqn, last_indexed_at)
            VALUES (@call_graph_id, @ordinal, @name, @expression, @kind, @source_fqn, @last_indexed_at)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@call_graph_id", callGraphId);
        cmd.Parameters.AddWithValue("@ordinal", parameterOrdinal);
        cmd.Parameters.AddWithValue("@name", parameterName);
        cmd.Parameters.AddWithValue("@expression", argumentExpression);
        cmd.Parameters.AddWithValue("@kind", argumentKind);
        cmd.Parameters.AddWithValue("@source_fqn", (object?)sourceSymbolFqn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_indexed_at", lastIndexedAt);

        return (long)cmd.ExecuteScalar()!;
    }

    public List<ArgumentFlowInfo> GetByCallGraphId(long callGraphId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM argument_flow WHERE call_graph_id = @id ORDER BY parameter_ordinal;";
        cmd.Parameters.AddWithValue("@id", callGraphId);
        return ReadAll(cmd);
    }

    public List<ArgumentFlowInfo> GetByCallGraphIds(IEnumerable<long> callGraphIds)
    {
        var ids = callGraphIds.ToList();
        if (ids.Count == 0) return new List<ArgumentFlowInfo>();

        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"SELECT * FROM argument_flow WHERE call_graph_id IN ({placeholders}) ORDER BY call_graph_id, parameter_ordinal;";
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        return ReadAll(cmd);
    }

    public void DeleteByCallGraphId(long callGraphId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM argument_flow WHERE call_graph_id = @id;";
        cmd.Parameters.AddWithValue("@id", callGraphId);
        cmd.ExecuteNonQuery();
    }

    private static List<ArgumentFlowInfo> ReadAll(SqliteCommand cmd)
    {
        var results = new List<ArgumentFlowInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ArgumentFlowInfo
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                CallGraphId = reader.GetInt64(reader.GetOrdinal("call_graph_id")),
                ParameterOrdinal = reader.GetInt32(reader.GetOrdinal("parameter_ordinal")),
                ParameterName = reader.GetString(reader.GetOrdinal("parameter_name")),
                ArgumentExpression = reader.GetString(reader.GetOrdinal("argument_expression")),
                ArgumentKind = reader.GetString(reader.GetOrdinal("argument_kind")),
                SourceSymbolFqn = reader.IsDBNull(reader.GetOrdinal("source_symbol_fqn")) ? null : reader.GetString(reader.GetOrdinal("source_symbol_fqn")),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
