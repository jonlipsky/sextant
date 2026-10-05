using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetImplementorsTool
{
    [McpServerTool(Name = "get_implementors"), Description("Implementations of an interface or overrides of a member. Use instead of grepping for : IFoo.")]
    public static string GetImplementors(
        DatabaseProvider dbProvider,
        [Description("Interface or member, fully qualified.")] string symbol_fqn,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (!Paging.TryBegin("get_implementors", limit, cursor, readContext, out var page, out var cursorError, symbol_fqn))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var relationshipStore = new RelationshipStore(conn);
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        var lookup = SymbolResolver.Lookup(symbolStore, projectStore, symbol_fqn);
        if (lookup.Status != SymbolLookupStatus.Resolved)
            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
        var targetSymbol = lookup.Symbol!;
        var namer = new SymbolNamer(symbolStore);

        // Find types that implement this interface or override this member
        var implementsRels = relationshipStore.GetByToSymbol(targetSymbol.Id, RelationshipKind.Implements);
        var overridesRels = relationshipStore.GetByToSymbol(targetSymbol.Id, RelationshipKind.Overrides);

        var implementors = new List<(SymbolInfo Symbol, RelationshipKind Kind)>();
        var allRels = implementsRels.Concat(overridesRels);

        foreach (var rel in allRels)
        {
            var implementor = symbolStore.GetById(rel.FromSymbolId);
            if (implementor == null) continue;
            implementors.Add((implementor, rel.Kind));
        }

        // A stable order, so a cursor resumes exactly where the previous page ended.
        implementors = implementors
            .OrderBy(i => i.Symbol.FilePath, StringComparer.Ordinal).ThenBy(i => i.Symbol.LineStart)
            .ThenBy(i => i.Symbol.Id).ThenBy(i => i.Kind)
            .ToList();

        object? Summary() => new { ByFile = Paging.CountBy(implementors, i => i.Symbol.FilePath) };

        var results = page.Slice(implementors).Select(i => (object)new
        {
            fully_qualified_name = namer.QualifiedName(i.Symbol),
            display_name = i.Symbol.DisplayName,
            kind = i.Symbol.Kind.ToString().ToLowerInvariant(),
            file_path = i.Symbol.FilePath,
            line_start = i.Symbol.LineStart,
            relationship = i.Kind.ToString().ToLowerInvariant()
        }).ToList();

        string? empty = null;
        if (implementors.Count == 0)
        {
            empty = $"{SymbolResolver.Describe(namer, targetSymbol)} has no indexed implementors or overrides.";
            if (targetSymbol.Kind is SymbolKind.Class or SymbolKind.Record)
                empty += " For classes deriving from it, use get_type_hierarchy with direction 'down'.";
        }
        var freshness = implementors.Count > 0 ? targetSymbol.LastIndexedAt : 0;
        return ResponseBuilder.BuildPage(results, implementors.Count, page, freshness, lookup.Ambiguity, readContext.Provenance,
            Summary, message: ResponseBuilder.JoinMessages(SymbolResolver.ResolutionNote(symbolStore, lookup), empty));
    }
}
