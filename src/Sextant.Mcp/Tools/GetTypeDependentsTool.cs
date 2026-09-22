using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetTypeDependentsTool
{
    [McpServerTool(Name = "get_type_dependents"),
     Description("Find all types that depend on a given type — through fields, properties, method parameters, return types, or inheritance.")]
    public static string GetTypeDependents(
        DatabaseProvider dbProvider,
        [Description("Fully qualified name of the type to find dependents of")]
        string symbol_fqn,
        [Description("Filter by dependency kind: 'inherits', 'implements', 'returns', 'parameter_of', 'instantiates', or 'all' (default)")]
        string dependency_kind = "all")
    {
        var db = dbProvider.GetReadyDatabase(out var notReady);
        if (db == null)
            return ResponseBuilder.BuildEmpty(notReady);

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn) { Scope = SnapshotReadScope.ForSelected(conn) };
        var relationshipStore = new RelationshipStore(conn);
        var projectStore = new ProjectStore(conn);

        var resolution = SymbolResolver.Resolve(symbolStore, projectStore, symbol_fqn);
        if (resolution.Symbol == null)
            return ResponseBuilder.BuildEmpty("Symbol not found.");
        var targetSymbol = resolution.Symbol;

        RelationshipKind? kindFilter = null;
        if (dependency_kind != "all")
        {
            if (!Enum.TryParse<RelationshipKind>(dependency_kind, ignoreCase: true, out var parsed))
                return ResponseBuilder.BuildEmpty($"Invalid dependency_kind: {dependency_kind}");
            kindFilter = parsed;
        }

        var rels = relationshipStore.GetByToSymbol(targetSymbol.Id, kindFilter);

        // Group relationships by containing type
        var dependentMap = new Dictionary<string, DependentInfo>();

        foreach (var rel in rels)
        {
            var fromSymbol = symbolStore.GetById(rel.FromSymbolId);
            if (fromSymbol == null) continue;

            var containingTypeFqn = GetContainingTypeFqn(fromSymbol);
            var containingType = containingTypeFqn != fromSymbol.FullyQualifiedName
                ? symbolStore.GetByFqn(containingTypeFqn, fromSymbol.ProjectId)
                : fromSymbol;

            var groupSymbol = containingType ?? fromSymbol;
            // Key by project + FQN so same-named types in different projects are not merged into one
            // dependent. The containing-type lookup is scoped to the dependent's own project above.
            var key = $"{groupSymbol.ProjectId}:{groupSymbol.FullyQualifiedName}";

            if (!dependentMap.TryGetValue(key, out var info))
            {
                var s = groupSymbol;
                info = new DependentInfo
                {
                    DependentType = s.FullyQualifiedName,
                    DisplayName = s.DisplayName,
                    Kind = s.Kind.ToString().ToLowerInvariant(),
                    FilePath = s.FilePath,
                    LineStart = s.LineStart,
                    Relationships = new List<object>()
                };
                dependentMap[key] = info;
            }

            info.Relationships.Add(new
            {
                kind = rel.Kind.ToString().ToLowerInvariant(),
                via_member = fromSymbol.FullyQualifiedName != groupSymbol.FullyQualifiedName ? fromSymbol.DisplayName : null,
                file_path = fromSymbol.FilePath,
                line_start = fromSymbol.LineStart
            });
        }

        var results = dependentMap.Values.Select(d => (object)new
        {
            dependent_type = d.DependentType,
            display_name = d.DisplayName,
            kind = d.Kind,
            file_path = d.FilePath,
            line_start = d.LineStart,
            relationships = d.Relationships
        }).ToList();

        return ResponseBuilder.Build(results, targetSymbol.LastIndexedAt, resolution.Ambiguity);
    }

    private static string GetContainingTypeFqn(SymbolInfo symbol)
    {
        // For member symbols (method, property, field), extract containing type from FQN
        if (symbol.Kind is SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Property
            or SymbolKind.Field or SymbolKind.Event or SymbolKind.Indexer)
        {
            var fqn = symbol.FullyQualifiedName;
            // Remove method parameters if present: "global::Ns.Type.Method(params)" -> "global::Ns.Type"
            var parenIdx = fqn.IndexOf('(');
            var nameOnly = parenIdx >= 0 ? fqn[..parenIdx] : fqn;
            var lastDot = nameOnly.LastIndexOf('.');
            if (lastDot > 0)
                return nameOnly[..lastDot];
        }
        return symbol.FullyQualifiedName;
    }

    private sealed class DependentInfo
    {
        public required string DependentType { get; init; }
        public required string DisplayName { get; init; }
        public required string Kind { get; init; }
        public required string FilePath { get; init; }
        public int LineStart { get; init; }
        public required List<object> Relationships { get; set; }
    }
}
