using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
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
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };

        // Resolve project canonical ID to DB ID if provided
        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildEmpty("Project not found.", readContext.Provenance);
            projectDbId = proj.Value.id;
        }

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);

        var symbols = symbolStore.GetUnreferenced(projectDbId, kind, exclude_test_projects, accessibility);

        var results = new List<object>();
        long freshness = 0;
        foreach (var s in symbols)
        {
            if (freshness == 0 || s.LastIndexedAt < freshness)
                freshness = s.LastIndexedAt;
            results.Add(FindSymbolTool.MapSymbol(s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache)));
        }

        return ResponseBuilder.Build(results, freshness, provenance: readContext.Provenance);
    }
}
