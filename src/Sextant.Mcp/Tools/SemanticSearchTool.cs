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

        // An unknown project/solution or an unrecognized scope is an error, never a silently unfiltered query.
        var scopeFilter = ScopeResolver.Resolve(scope, conn, readContext.Scope);
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
                results = results.Where(s => s.FilePath == scopeFilter.FilePath).ToList();
            else if (scopeFilter.ProjectIds != null)
                results = results.Where(s => scopeFilter.ProjectIds.Contains(s.ProjectId)).ToList();
        }

        var namer = new SymbolNamer(symbolStore);
        var mapped = results.Select(s => FindSymbolTool.MapSymbol(
            s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache), qualifiedName: namer.QualifiedName(s))).ToList<object>();
        var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0;
        return ResponseBuilder.Build(mapped, freshness, provenance: readContext.Provenance);
    }
}
