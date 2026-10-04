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
        if (!SymbolKindNames.TryParse(kind, out var kinds, out _, out var kindError))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, kindError!, readContext.Provenance);
        string? accessibilityName = null;
        if (!string.IsNullOrWhiteSpace(accessibility))
        {
            accessibilityName = accessibility.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (!AccessibilityNames.Contains(accessibilityName))
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                    $"Unknown accessibility '{accessibility.Trim()}'. Use one of: {string.Join(", ", AccessibilityNames)}.",
                    readContext.Provenance);
        }

        using var conn = db.OpenReadConnection();
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };

        // Resolve project canonical ID to DB ID if provided
        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                    $"Unknown project_id '{project_id}'. Use a project canonical ID as listed by get_index_status.",
                    readContext.Provenance);
            projectDbId = proj.Value.id;
        }

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var namer = new SymbolNamer(symbolStore);

        var symbols = symbolStore.GetUnreferenced(
            projectDbId, kinds is { Count: 1 } ? kinds.Single().ToString() : null, exclude_test_projects, accessibilityName);

        var results = new List<object>();
        long freshness = 0;
        foreach (var s in symbols)
        {
            if (kinds != null && !kinds.Contains(s.Kind))
                continue;
            if (freshness == 0 || s.LastIndexedAt < freshness)
                freshness = s.LastIndexedAt;
            results.Add(FindSymbolTool.MapSymbol(
                s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache), qualifiedName: namer.QualifiedName(s)));
        }

        return ResponseBuilder.Build(results, freshness, provenance: readContext.Provenance,
            message: results.Count == 0 ? "No unreferenced symbol matches the given filters." : null);
    }

    private static readonly string[] AccessibilityNames =
        ["public", "internal", "protected", "private", "protected_internal", "private_protected"];
}
