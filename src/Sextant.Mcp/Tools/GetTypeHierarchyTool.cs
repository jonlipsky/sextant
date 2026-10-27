using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetTypeHierarchyTool
{
    [McpServerTool(Name = "get_type_hierarchy"), Description("Bases, interfaces, subtypes and implementers. Use instead of grepping for : Base.")]
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
        var cap = RowCap(readContext.MaxResponseChars);
        var truncated = false;

        if (directionName is "up" or "both")
        {
            truncated |= CollectHierarchy(symbolStore, relationshipStore, namer, rootSymbol.Id, "up", results, new HashSet<long>(), cap);
        }

        if ((directionName is "down" or "both") && results.Count < cap)
        {
            truncated |= CollectHierarchy(symbolStore, relationshipStore, namer, rootSymbol.Id, "down", results, new HashSet<long>(), cap);
        }
        else if (directionName is "down" or "both")
        {
            truncated = true;
        }

        var capped = truncated
            ? $"Stopped collecting at {cap} rows; the hierarchy is larger. Narrow with direction 'up' or 'down', or use get_implementors (paged) for the full list of implementers."
            : null;

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
            message: ResponseBuilder.JoinMessages(SymbolResolver.ResolutionNote(symbolStore, lookup), empty, capped),
            warning: lookup.Warning);
    }

    // A row is roughly 100+ chars of JSON, so the budget cannot show more than this many anyway.
    internal static int RowCap(int maxResponseChars) => Math.Max(20, maxResponseChars / 100);

    /// <returns>True when collection stopped at <paramref name="cap"/> with rows left out.</returns>
    private static bool CollectHierarchy(
        SymbolStore symbolStore, RelationshipStore relationshipStore, SymbolNamer namer,
        long symbolId, string dir, List<object> results, HashSet<long> visited, int cap, int depth = 0)
    {
        if (!visited.Add(symbolId) || depth > 20) return false;

        var rels = new List<(RelationshipInfo Rel, string Kind)>();
        foreach (var kind in new[] { RelationshipKind.Inherits, RelationshipKind.Implements })
        {
            var ofKind = dir == "up"
                ? relationshipStore.GetByFromSymbol(symbolId, kind)
                : relationshipStore.GetByToSymbol(symbolId, kind);
            rels.AddRange(ofKind.Select(r => (r, kind == RelationshipKind.Inherits ? "inherits" : "implements")));
        }

        foreach (var (rel, relationship) in rels)
        {
            var targetId = dir == "up" ? rel.ToSymbolId : rel.FromSymbolId;
            var target = symbolStore.GetById(targetId);
            if (target == null) continue;
            if (results.Count >= cap) return true;

            results.Add(new
            {
                fully_qualified_name = namer.QualifiedName(target),
                display_name = target.DisplayName,
                kind = target.Kind.ToString().ToLowerInvariant(),
                file_path = target.FilePath,
                line_start = target.LineStart,
                direction = dir,
                relationship,
                depth = depth + 1
            });

            if (CollectHierarchy(symbolStore, relationshipStore, namer, targetId, dir, results, visited, cap, depth + 1))
                return true;
        }

        return false;
    }
}
