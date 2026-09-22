using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindByAttributeTool
{
    [McpServerTool(Name = "find_by_attribute"), Description("Find symbols decorated with a given attribute.")]
    public static string FindByAttribute(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the attribute")] string attribute_fqn,
        [Description("Optional symbol kind filter")] string? kind = null,
        [Description("Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'")] string? scope = null)
    {
        var db = dbProvider.GetReadyDatabase(out var notReady);
        if (db == null)
            return ResponseBuilder.BuildEmpty(notReady);

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn) { Scope = SnapshotReadScope.ForSelected(conn) };

        // GetByAttribute does the substring pre-filter AND the exact JSON-array membership check.
        var matches = symbolStore.GetByAttribute(attribute_fqn);

        var scopeFilter = ScopeResolver.Resolve(scope, conn);

        var results = new List<object>();
        long freshness = 0;
        foreach (var s in matches)
        {
            if (kind != null && !string.Equals(s.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!scopeFilter.IsEmpty)
            {
                if (scopeFilter.FilePath != null && s.FilePath != scopeFilter.FilePath)
                    continue;
                if (scopeFilter.ProjectIds != null && !scopeFilter.ProjectIds.Contains(s.ProjectId))
                    continue;
            }

            if (freshness == 0 || s.LastIndexedAt < freshness)
                freshness = s.LastIndexedAt;

            results.Add(new
            {
                fully_qualified_name = s.FullyQualifiedName,
                display_name = s.DisplayName,
                kind = s.Kind.ToString().ToLowerInvariant(),
                file_path = s.FilePath,
                line_start = s.LineStart,
                accessibility = SymbolStore.FormatAccessibility(s.Accessibility),
                attributes = s.Attributes
            });
        }

        return ResponseBuilder.Build(results, freshness);
    }
}
