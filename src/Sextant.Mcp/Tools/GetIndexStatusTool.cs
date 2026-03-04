using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetIndexStatusTool
{
    [McpServerTool(Name = "get_index_status"), Description("Check what projects are indexed, symbol/reference counts, and index freshness. Call this first to see what data is available.")]
    public static string GetIndexStatus(DatabaseProvider dbProvider)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();

        var results = new List<object>();
        long freshness = 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.canonical_id, p.git_remote_url, p.repo_relative_path,
                   p.assembly_name, p.is_test_project, p.last_indexed_at,
                   (SELECT COUNT(*) FROM symbols WHERE project_id = p.id) as symbol_count,
                   (SELECT COUNT(*) FROM "references" WHERE in_project_id = p.id) as reference_count
            FROM projects p
            ORDER BY p.last_indexed_at DESC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var lastIndexed = reader.GetInt64(reader.GetOrdinal("last_indexed_at"));
            if (freshness == 0 || lastIndexed < freshness)
                freshness = lastIndexed;

            results.Add(new
            {
                canonical_id = reader.GetString(reader.GetOrdinal("canonical_id")),
                git_remote_url = reader.GetString(reader.GetOrdinal("git_remote_url")),
                repo_relative_path = reader.GetString(reader.GetOrdinal("repo_relative_path")),
                assembly_name = reader.IsDBNull(reader.GetOrdinal("assembly_name")) ? null : reader.GetString(reader.GetOrdinal("assembly_name")),
                is_test_project = reader.GetInt64(reader.GetOrdinal("is_test_project")) != 0,
                last_indexed_at = lastIndexed,
                symbol_count = reader.GetInt64(reader.GetOrdinal("symbol_count")),
                reference_count = reader.GetInt64(reader.GetOrdinal("reference_count"))
            });
        }

        return ResponseBuilder.Build(results, freshness);
    }
}
