using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

public static class FindUnreferencedTool
{
    [McpServerTool(Name = "find_unreferenced"), Description("Find symbols that have zero references — useful for dead-code detection.")]
    public static string FindUnreferenced(
        DatabaseProvider dbProvider,
        [Description("Optional symbol kind filter (class, method, property, etc.)")] string? kind = null,
        [Description("Optional project canonical ID to scope to a single project")] string? project_id = null,
        [Description("Exclude symbols defined in test projects (default: true)")] bool exclude_test_projects = true,
        [Description("Optional accessibility filter (public, internal, etc.)")] string? accessibility = null)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var projectStore = new ProjectStore(conn);

        // Resolve project canonical ID to DB ID if provided
        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildEmpty("Project not found.");
            projectDbId = proj.Value.id;
        }

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);

        using var cmd = conn.CreateCommand();

        var clauses = new List<string> { "r.id IS NULL" };
        if (kind != null)
        {
            clauses.Add("s.kind = @kind");
            cmd.Parameters.AddWithValue("@kind", kind);
        }
        if (projectDbId != null)
        {
            clauses.Add("s.project_id = @project_db_id");
            cmd.Parameters.AddWithValue("@project_db_id", projectDbId.Value);
        }
        if (exclude_test_projects)
        {
            clauses.Add("p.is_test_project = 0");
        }
        if (accessibility != null)
        {
            clauses.Add("s.accessibility = @accessibility");
            cmd.Parameters.AddWithValue("@accessibility", accessibility);
        }

        var whereClause = string.Join(" AND ", clauses);

        cmd.CommandText = $"""
            SELECT s.fully_qualified_name, s.display_name, s.kind, s.project_id,
                   s.file_path, s.line_start, s.line_end, s.accessibility,
                   s.signature, s.last_indexed_at
            FROM symbols s
            LEFT JOIN "references" r ON r.symbol_id = s.id
            LEFT JOIN projects p ON p.id = s.project_id
            WHERE {whereClause}
            ORDER BY s.file_path, s.line_start;
            """;

        var results = new List<object>();
        long freshness = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var lastIndexed = reader.GetInt64(reader.GetOrdinal("last_indexed_at"));
            if (freshness == 0 || lastIndexed < freshness)
                freshness = lastIndexed;

            var projId = reader.GetInt64(reader.GetOrdinal("project_id"));
            var canonicalId = FindSymbolTool.ResolveCanonicalId(projId, canonicalIdCache);

            results.Add(new
            {
                fully_qualified_name = reader.GetString(reader.GetOrdinal("fully_qualified_name")),
                display_name = reader.GetString(reader.GetOrdinal("display_name")),
                kind = reader.GetString(reader.GetOrdinal("kind")),
                project_id = canonicalId,
                file_path = reader.GetString(reader.GetOrdinal("file_path")),
                line_start = reader.GetInt32(reader.GetOrdinal("line_start")),
                line_end = reader.GetInt32(reader.GetOrdinal("line_end")),
                accessibility = reader.GetString(reader.GetOrdinal("accessibility")),
                signature = reader.IsDBNull(reader.GetOrdinal("signature")) ? null : reader.GetString(reader.GetOrdinal("signature"))
            });
        }

        return ResponseBuilder.Build(results, freshness);
    }
}
