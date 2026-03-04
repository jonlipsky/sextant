using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetImplementorsTool
{
    [McpServerTool(Name = "get_implementors"), Description("Find all types implementing an interface or overriding a virtual/abstract member. Resolves across the entire solution instantly.")]
    public static string GetImplementors(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the interface or member")] string symbol_fqn)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var relationshipStore = new RelationshipStore(conn);

        var targetSymbol = symbolStore.GetByFqn(symbol_fqn);
        if (targetSymbol == null)
            return ResponseBuilder.BuildEmpty("Symbol not found.");

        // Find types that implement this interface or override this member
        var implementsRels = relationshipStore.GetByToSymbol(targetSymbol.Id, RelationshipKind.Implements);
        var overridesRels = relationshipStore.GetByToSymbol(targetSymbol.Id, RelationshipKind.Overrides);

        var results = new List<object>();
        var allRels = implementsRels.Concat(overridesRels);

        foreach (var rel in allRels)
        {
            var implementor = symbolStore.GetById(rel.FromSymbolId);
            if (implementor == null) continue;

            results.Add(new
            {
                fully_qualified_name = implementor.FullyQualifiedName,
                display_name = implementor.DisplayName,
                kind = implementor.Kind.ToString().ToLowerInvariant(),
                file_path = implementor.FilePath,
                line_start = implementor.LineStart,
                relationship = rel.Kind.ToString().ToLowerInvariant()
            });
        }

        var freshness = results.Count > 0 ? targetSymbol.LastIndexedAt : 0;
        return ResponseBuilder.Build(results, freshness);
    }
}
