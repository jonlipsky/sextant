using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetTypeDependentsTool
{
    [McpServerTool(Name = "get_type_dependents"),
     Description("Types that depend on a type, by kind. Use instead of grepping its name.")]
    public static string GetTypeDependents(
        DatabaseProvider dbProvider,
        [Description("Type, fully qualified.")]
        string symbol_fqn,
        [Description("inherits, implements, returns, parameter_of, instantiates or all.")]
        string dependency_kind = "all",
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (!Paging.TryBegin("get_type_dependents", limit, cursor, readContext, out var page, out var cursorError,
                symbol_fqn, dependency_kind))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var relationshipStore = new RelationshipStore(conn);
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        RelationshipKind? kindFilter = null;
        var dependencyText = dependency_kind?.Trim().Replace("_", string.Empty).Replace("-", string.Empty);
        if (!string.IsNullOrEmpty(dependencyText) && !dependencyText.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!dependencyText.All(char.IsLetter)
                || !Enum.TryParse<RelationshipKind>(dependencyText, ignoreCase: true, out var parsed))
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                    $"Unknown dependency_kind '{dependency_kind}'. Use 'inherits', 'implements', 'overrides', " +
                    "'returns', 'parameter_of', 'instantiates', or 'all'.", readContext.Provenance);
            kindFilter = parsed;
        }

        var lookup = SymbolResolver.Lookup(symbolStore, projectStore, symbol_fqn, SymbolLookupOptions.Types);
        if (lookup.Status != SymbolLookupStatus.Resolved)
            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
        var targetSymbol = lookup.Symbol!;
        var namer = new SymbolNamer(symbolStore);

        var rels = relationshipStore.GetByToSymbol(targetSymbol.Id, kindFilter);

        // Group relationships by containing type
        var dependentMap = new Dictionary<string, DependentInfo>();
        // The kinds of the relationships whose source this read can see (the summary counts only these).
        var readableKinds = new List<string>();

        foreach (var rel in rels)
        {
            var fromSymbol = symbolStore.GetById(rel.FromSymbolId);
            if (fromSymbol == null) continue;
            readableKinds.Add(rel.Kind.ToString().ToLowerInvariant());

            var groupSymbol = ContainingType(symbolStore, namer, fromSymbol) ?? fromSymbol;
            // Key by project + symbol key so same-named types in different projects are not merged into one
            // dependent. The containing type is looked up in the dependent's own project.
            var key = $"{groupSymbol.ProjectId}:{groupSymbol.SymbolKey}";

            if (!dependentMap.TryGetValue(key, out var info))
            {
                var s = groupSymbol;
                info = new DependentInfo
                {
                    DependentType = namer.QualifiedName(s),
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
                via_member = fromSymbol.Id != groupSymbol.Id ? fromSymbol.DisplayName : null,
                file_path = fromSymbol.FilePath,
                line_start = fromSymbol.LineStart
            });
        }

        // A stable order, so a cursor resumes exactly where the previous page ended (the project-qualified key
        // breaks a tie between one type's per-TFM rows).
        var dependents = dependentMap
            .OrderBy(d => d.Value.DependentType, StringComparer.Ordinal).ThenBy(d => d.Value.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Value.LineStart).ThenBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => d.Value)
            .ToList();
        object? Summary() => new
        {
            ByRelationship = Paging.CountBy(readableKinds, k => k),
            ByFile = Paging.CountBy(dependents, d => d.FilePath)
        };

        var results = page.Slice(dependents).Select(d => (object)new
        {
            dependent_type = d.DependentType,
            display_name = d.DisplayName,
            kind = d.Kind,
            file_path = d.FilePath,
            line_start = d.LineStart,
            relationships = d.Relationships
        }).ToList();

        var empty = dependents.Count == 0
            ? $"No indexed type depends on {SymbolResolver.Describe(namer, targetSymbol)}" +
              (kindFilter is null ? "." : $" through '{dependency_kind!.Trim()}'.")
            : null;
        return ResponseBuilder.BuildPage(results, dependents.Count, page, targetSymbol.LastIndexedAt, lookup.Ambiguity,
            readContext.Provenance, Summary,
            message: ResponseBuilder.JoinMessages(SymbolResolver.ResolutionNote(symbolStore, lookup), empty));
    }

    // The type declaring a member (from its documentation-ID key; for an index from before those keys, from its fully
    // qualified name), or null for a type.
    private static SymbolInfo? ContainingType(SymbolStore store, SymbolNamer namer, SymbolInfo symbol)
    {
        if (symbol.Kind is not (SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Property
            or SymbolKind.Field or SymbolKind.Event or SymbolKind.Indexer))
            return null;
        if (namer.ContainingType(symbol) is { } type)
            return type;
        var containingTypeFqn = GetContainingTypeFqn(symbol);
        return containingTypeFqn != symbol.FullyQualifiedName
            ? store.GetByFqn(containingTypeFqn, symbol.ProjectId)
            : null;
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
