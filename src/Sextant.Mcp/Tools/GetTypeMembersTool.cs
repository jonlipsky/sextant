using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetTypeMembersTool
{
    private static readonly HashSet<SymbolKind> MemberKinds =
    [
        SymbolKind.Method, SymbolKind.Constructor, SymbolKind.Property,
        SymbolKind.Field, SymbolKind.Event, SymbolKind.Indexer
    ];

    [McpServerTool(Name = "get_type_members"), Description("List all members of a type (methods, properties, fields, events) with signatures. Faster than reading the source file — includes inherited members.")]
    public static string GetTypeMembers(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the type")] string symbol_fqn,
        [Description("Include inherited members")] bool include_inherited = false)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var relationshipStore = new RelationshipStore(conn);
        var projectStore = new ProjectStore(conn);
        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);

        var typeSymbol = symbolStore.GetByFqn(symbol_fqn);
        if (typeSymbol == null)
            return ResponseBuilder.BuildEmpty("Type not found.");

        // Get members from the same file that have the type's FQN as a prefix
        var fileSymbols = symbolStore.GetByFile(typeSymbol.FilePath);
        var members = fileSymbols
            .Where(s => s.FullyQualifiedName.StartsWith(typeSymbol.FullyQualifiedName + ".") && MemberKinds.Contains(s.Kind))
            .ToList();

        if (include_inherited)
        {
            var baseRelationships = relationshipStore.GetByFromSymbol(typeSymbol.Id, RelationshipKind.Inherits);
            foreach (var rel in baseRelationships)
            {
                // Find base type symbols and get their members recursively
                var baseFileSymbols = GetMembersForSymbolId(symbolStore, rel.ToSymbolId);
                members.AddRange(baseFileSymbols.Where(s => MemberKinds.Contains(s.Kind)));
            }
        }

        var mapped = members.Select(s => FindSymbolTool.MapSymbol(s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache))).ToList<object>();
        var freshness = members.Count > 0 ? members.Min(s => s.LastIndexedAt) : typeSymbol.LastIndexedAt;
        return ResponseBuilder.Build(mapped, freshness);
    }

    private static List<SymbolInfo> GetMembersForSymbolId(SymbolStore store, long symbolId)
    {
        var baseType = store.GetById(symbolId);
        if (baseType == null)
            return [];

        var fileSymbols = store.GetByFile(baseType.FilePath);
        return fileSymbols
            .Where(s => s.FullyQualifiedName.StartsWith(baseType.FullyQualifiedName + ".") && MemberKinds.Contains(s.Kind))
            .ToList();
    }
}
