using System.ComponentModel;
using System.Text.Json;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

public static class FindByAttributeTool
{
    [McpServerTool(Name = "find_by_attribute"), Description("Find symbols decorated with a given attribute.")]
    public static string FindByAttribute(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the attribute")] string attribute_fqn,
        [Description("Optional symbol kind filter")] string? kind = null,
        [Description("Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'")] string? scope = null)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        using var cmd = conn.CreateCommand();

        // Query symbols where the attributes JSON array contains the attribute FQN
        var kindClause = kind != null ? " AND kind = @kind" : "";
        cmd.CommandText = $"""
            SELECT * FROM symbols
            WHERE attributes IS NOT NULL
            AND attributes LIKE @pattern{kindClause}
            ORDER BY fully_qualified_name;
            """;
        cmd.Parameters.AddWithValue("@pattern", $"%{attribute_fqn}%");
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);

        var scopeFilter = ScopeResolver.Resolve(scope, conn);

        var results = new List<object>();
        long freshness = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var attrsJson = reader.GetString(reader.GetOrdinal("attributes"));
            // Verify exact match in the JSON array
            try
            {
                var attrs = JsonSerializer.Deserialize<List<string>>(attrsJson);
                if (attrs == null || !attrs.Contains(attribute_fqn))
                    continue;
            }
            catch
            {
                continue;
            }

            var filePath = reader.GetString(reader.GetOrdinal("file_path"));
            var projectId = reader.GetInt64(reader.GetOrdinal("project_id"));

            if (!scopeFilter.IsEmpty)
            {
                if (scopeFilter.FilePath != null && filePath != scopeFilter.FilePath)
                    continue;
                if (scopeFilter.ProjectIds != null && !scopeFilter.ProjectIds.Contains(projectId))
                    continue;
            }

            var lastIndexed = reader.GetInt64(reader.GetOrdinal("last_indexed_at"));
            if (freshness == 0 || lastIndexed < freshness)
                freshness = lastIndexed;

            results.Add(new
            {
                fully_qualified_name = reader.GetString(reader.GetOrdinal("fully_qualified_name")),
                display_name = reader.GetString(reader.GetOrdinal("display_name")),
                kind = reader.GetString(reader.GetOrdinal("kind")),
                file_path = filePath,
                line_start = reader.GetInt32(reader.GetOrdinal("line_start")),
                accessibility = reader.GetString(reader.GetOrdinal("accessibility")),
                attributes = attrsJson
            });
        }

        return ResponseBuilder.Build(results, freshness);
    }
}
