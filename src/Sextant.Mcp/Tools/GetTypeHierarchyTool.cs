using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetTypeHierarchyTool
{
    [McpServerTool(Name = "get_type_hierarchy"), Description("Get the full inheritance chain (base types and/or derived types) for a type. Resolves across projects instantly.")]
    public static string GetTypeHierarchy(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the type")] string symbol_fqn,
        [Description("Direction: 'up' (base types), 'down' (derived types), or 'both'")] string direction = "both")
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var relationshipStore = new RelationshipStore(conn);

        var rootSymbol = symbolStore.GetByFqn(symbol_fqn);
        if (rootSymbol == null)
            return ResponseBuilder.BuildEmpty("Symbol not found.");

        var results = new List<object>();

        if (direction is "up" or "both")
        {
            CollectHierarchy(symbolStore, relationshipStore, rootSymbol.Id, "up", results, new HashSet<long>());
        }

        if (direction is "down" or "both")
        {
            CollectHierarchy(symbolStore, relationshipStore, rootSymbol.Id, "down", results, new HashSet<long>());
        }

        var freshness = rootSymbol.LastIndexedAt;
        return ResponseBuilder.Build(results, freshness);
    }

    private static void CollectHierarchy(
        SymbolStore symbolStore, RelationshipStore relationshipStore,
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
                fully_qualified_name = target.FullyQualifiedName,
                display_name = target.DisplayName,
                kind = target.Kind.ToString().ToLowerInvariant(),
                file_path = target.FilePath,
                line_start = target.LineStart,
                direction = dir,
                depth = depth + 1
            });

            CollectHierarchy(symbolStore, relationshipStore, targetId, dir, results, visited, depth + 1);
        }
    }
}
