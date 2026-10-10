using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ArgumentFlowStore(SqliteConnection connection)
{
    private const string InsertSql = """
        INSERT INTO argument_flow (occurrence_id, parameter_ordinal, parameter_name,
            argument_expression, argument_kind, source_symbol_fqn, last_indexed_at)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
        RETURNING id;
        """;

    public PreparedInsert CreateInsertCommand() => new(connection, InsertSql);

    public long Insert(long callGraphId, int parameterOrdinal, string parameterName,
                       string argumentExpression, string argumentKind, string? sourceSymbolFqn,
                       long lastIndexedAt)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, callGraphId, parameterOrdinal, parameterName, argumentExpression,
            argumentKind, sourceSymbolFqn, lastIndexedAt);
    }

    public long Insert(PreparedInsert cmd, long callGraphId, int parameterOrdinal, string parameterName,
                       string argumentExpression, string argumentKind, string? sourceSymbolFqn,
                       long lastIndexedAt)
    {
        cmd.Bind(1, callGraphId);
        cmd.Bind(2, parameterOrdinal);
        cmd.Bind(3, parameterName);
        cmd.Bind(4, argumentExpression);
        cmd.Bind(5, argumentKind);
        cmd.Bind(6, sourceSymbolFqn);
        cmd.Bind(7, lastIndexedAt);
        return cmd.ExecuteReturningId();
    }

    public List<ArgumentFlowInfo> GetByCallGraphId(long callGraphId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM argument_flow WHERE occurrence_id = @id ORDER BY parameter_ordinal;";
        cmd.Parameters.AddWithValue("@id", callGraphId);
        return ReadAll(cmd);
    }

    public List<ArgumentFlowInfo> GetByCallGraphIds(IEnumerable<long> callGraphIds)
    {
        var ids = callGraphIds.ToList();
        if (ids.Count == 0) return new List<ArgumentFlowInfo>();

        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"SELECT * FROM argument_flow WHERE occurrence_id IN ({placeholders}) ORDER BY occurrence_id, parameter_ordinal;";
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        return ReadAll(cmd);
    }

    public void DeleteByCallGraphId(long callGraphId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM argument_flow WHERE occurrence_id = @id;";
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
                CallGraphId = reader.GetInt64(reader.GetOrdinal("occurrence_id")),
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
