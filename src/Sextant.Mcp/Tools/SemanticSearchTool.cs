using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class SemanticSearchTool
{
    [McpServerTool(Name = "semantic_search"), Description("Symbols matching words in names or XML docs. Use instead of grep to find code by topic.")]
    public static string SemanticSearch(
        DatabaseProvider dbProvider,
        string query,
        [Description(ToolText.Kind)] string? kind = null,
        int max_results = 20,
        [Description(ToolText.Scope)] string? scope = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        if (!CapabilityGate.Ensure(db, IndexFeature.DocumentationSearch, "documentation_search", out var unavailable,
                readContext.SelectedSnapshotId, dbProvider.Authorizer.IsEnforcing))
            return unavailable;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var config = SextantConfiguration.FromEnvironment();
        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);

        if (!SymbolKindNames.TryParse(kind, out var kinds, out _, out var kindError))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, kindError!, readContext.Provenance);

        // An unknown project/solution, an unrecognized scope or (remote) an absolute path is an error, never a
        // silently unfiltered query.
        var scopeFilter = ScopeResolver.Resolve(scope, conn, readContext.Scope, readContext.Paths);
        if (scopeFilter.Error != null)
            return scopeFilter.ErrorResponse(readContext.Provenance);

        max_results = Math.Min(max_results, config.FtsMaxResults);
        // One kind narrows the FTS query itself; a kind family ("type") filters its results.
        var results = symbolStore.SearchFts(query, max_results, kinds is { Count: 1 } ? kinds.First().ToString() : null);
        if (kinds is { Count: > 1 })
            results = results.Where(s => kinds.Contains(s.Kind)).ToList();

        if (!scopeFilter.IsEmpty)
        {
            if (scopeFilter.FilePath != null)
                results = results.Where(s => scopeFilter.MatchesFile(s.FilePath)).ToList();
            else if (scopeFilter.ProjectIds != null)
                results = results.Where(s => scopeFilter.ProjectIds.Contains(s.ProjectId)).ToList();
        }

        var namer = new SymbolNamer(symbolStore);
        var mapped = results.Select(s => FindSymbolTool.MapSymbol(
            s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache), qualifiedName: namer.QualifiedName(s))).ToList<object>();
        var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0;
        return ResponseBuilder.BuildBounded(mapped, readContext, freshness, provenance: readContext.Provenance);
    }
}
