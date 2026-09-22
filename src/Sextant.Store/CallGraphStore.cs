using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class CallGraphStore(SqliteConnection connection)
{
    public long Insert(CallGraphEdge edge)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO call_graph (caller_symbol_id, callee_symbol_id, call_site_file, call_site_line, last_indexed_at)
            VALUES (@caller, @callee, @file, @line, @last_indexed_at)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@caller", edge.CallerSymbolId);
        cmd.Parameters.AddWithValue("@callee", edge.CalleeSymbolId);
        cmd.Parameters.AddWithValue("@file", edge.CallSiteFile);
        cmd.Parameters.AddWithValue("@line", edge.CallSiteLine);
        cmd.Parameters.AddWithValue("@last_indexed_at", edge.LastIndexedAt);

        return (long)cmd.ExecuteScalar()!;
    }

    public List<CallGraphEdge> GetByCaller(long callerSymbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM call_graph WHERE caller_symbol_id = @caller;";
        cmd.Parameters.AddWithValue("@caller", callerSymbolId);
        return ReadAll(cmd);
    }

    public List<CallGraphEdge> GetByCallee(long calleeSymbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM call_graph WHERE callee_symbol_id = @callee;";
        cmd.Parameters.AddWithValue("@callee", calleeSymbolId);
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM call_graph WHERE call_site_file = @file;";
        cmd.Parameters.AddWithValue("@file", filePath);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: call_graph rows have no project column, so scope through the caller
    // symbol's project. This clears only the calls made from one logical (per-TFM) project's code in
    // the shared source file, leaving the sibling framework's call edges for that file intact.
    public void DeleteByFile(string filePath, long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM call_graph
            WHERE call_site_file = @file
              AND caller_symbol_id IN (SELECT id FROM symbols WHERE project_id = @project_id);
            """;
        cmd.Parameters.AddWithValue("@file", filePath);
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.ExecuteNonQuery();
    }

    private static List<CallGraphEdge> ReadAll(SqliteCommand cmd)
    {
        var results = new List<CallGraphEdge>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CallGraphEdge
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                CallerSymbolId = reader.GetInt64(reader.GetOrdinal("caller_symbol_id")),
                CalleeSymbolId = reader.GetInt64(reader.GetOrdinal("callee_symbol_id")),
                CallSiteFile = reader.GetString(reader.GetOrdinal("call_site_file")),
                CallSiteLine = reader.GetInt32(reader.GetOrdinal("call_site_line")),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
