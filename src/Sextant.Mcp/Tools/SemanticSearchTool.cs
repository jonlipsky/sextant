using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class SemanticSearchTool
{
    [McpServerTool(Name = "semantic_search"), Description("Full-text search over symbol names and documentation. Use instead of grep for .NET symbol discovery — searches the pre-built semantic index.")]
    public static string SemanticSearch(
        DatabaseProvider dbProvider,
        [Description("The search query")] string query,
        [Description("Optional symbol kind filter")] string? kind = null,
        [Description("Maximum number of results")] int max_results = 20,
        [Description("Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'")] string? scope = null)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var projectStore = new ProjectStore(conn);
        var config = SextantConfiguration.FromEnvironment();
        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);

        max_results = Math.Min(max_results, config.FtsMaxResults);
        var results = symbolStore.SearchFts(query, max_results, kind);

        var scopeFilter = ScopeResolver.Resolve(scope, conn);
        if (!scopeFilter.IsEmpty)
        {
            if (scopeFilter.FilePath != null)
                results = results.Where(s => s.FilePath == scopeFilter.FilePath).ToList();
            else if (scopeFilter.ProjectIds != null)
                results = results.Where(s => scopeFilter.ProjectIds.Contains(s.ProjectId)).ToList();
        }

        var mapped = results.Select(s => FindSymbolTool.MapSymbol(s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache))).ToList<object>();
        var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0;
        return ResponseBuilder.Build(mapped, freshness);
    }
}
