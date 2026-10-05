using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetTypeHierarchyTool
{
    [McpServerTool(Name = "get_type_hierarchy"), Description("Base and/or derived types. Use instead of grepping for : Base.")]
    public static string GetTypeHierarchy(
        DatabaseProvider dbProvider,
        [Description("Type, fully qualified.")] string symbol_fqn,
        [Description("up, down or both.")] string direction = "both")
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var relationshipStore = new RelationshipStore(conn);
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        var directionName = string.IsNullOrWhiteSpace(direction) ? "both" : direction.Trim().ToLowerInvariant();
        if (directionName is not ("up" or "down" or "both"))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"Unknown direction '{direction}'. Use 'up' (base types), 'down' (derived types) or 'both'.",
                readContext.Provenance);

        var lookup = SymbolResolver.Lookup(symbolStore, projectStore, symbol_fqn, SymbolLookupOptions.Types);
        if (lookup.Status != SymbolLookupStatus.Resolved)
            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
        var rootSymbol = lookup.Symbol!;
        var namer = new SymbolNamer(symbolStore);

        var results = new List<object>();

        if (directionName is "up" or "both")
        {
            CollectHierarchy(symbolStore, relationshipStore, namer, rootSymbol.Id, "up", results, new HashSet<long>());
        }

        if (directionName is "down" or "both")
        {
            CollectHierarchy(symbolStore, relationshipStore, namer, rootSymbol.Id, "down", results, new HashSet<long>());
        }

        var empty = results.Count == 0
            ? $"{SymbolResolver.Describe(namer, rootSymbol)} has no indexed " + directionName switch
            {
                "up" => "base types.",
                "down" => "derived types.",
                _ => "base or derived types."
            }
            : null;
        var freshness = rootSymbol.LastIndexedAt;
        return ResponseBuilder.BuildBounded(results, readContext, freshness, lookup.Ambiguity, readContext.Provenance,
            message: ResponseBuilder.JoinMessages(SymbolResolver.ResolutionNote(symbolStore, lookup), empty));
    }

    private static void CollectHierarchy(
        SymbolStore symbolStore, RelationshipStore relationshipStore, SymbolNamer namer,
        long symbolId, string dir, List<object> results, HashSet<long> visited, int depth = 0)
    {
        if (!visited.Add(symbolId) || depth > 20) return;

        List<RelationshipInfo> rels;
        if (dir == "up")
            rels = relationshipStore.GetByFromSymbol(symbolId, RelationshipKind.Inherits);
        else
            rels = relationshipStore.GetByToSymbol(symbolId, RelationshipKind.Inherits);

        foreach (var rel in rels)
        {
            var targetId = dir == "up" ? rel.ToSymbolId : rel.FromSymbolId;
            var target = symbolStore.GetById(targetId);
            if (target == null) continue;

            results.Add(new
            {
                fully_qualified_name = namer.QualifiedName(target),
                display_name = target.DisplayName,
                kind = target.Kind.ToString().ToLowerInvariant(),
                file_path = target.FilePath,
                line_start = target.LineStart,
                direction = dir,
                depth = depth + 1
            });

            CollectHierarchy(symbolStore, relationshipStore, namer, targetId, dir, results, visited, depth + 1);
        }
    }
}
