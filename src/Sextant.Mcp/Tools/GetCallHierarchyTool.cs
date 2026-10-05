using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetCallHierarchyTool
{
    [McpServerTool(Name = "get_call_hierarchy"), Description("Callers or callees of a method, transitively. Use instead of repeated grep and read.")]
    public static string GetCallHierarchy(
        DatabaseProvider dbProvider,
        [Description("Method, fully qualified.")] string symbol_fqn,
        [Description("callers or callees.")] string direction,
        int depth = 5,
        bool include_source = false,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        var config = SextantConfiguration.FromEnvironment();
        depth = Math.Min(depth, config.MaxCallHierarchyDepth);
        // include_source only adds each call site's surrounding lines, so a cursor carries over when it is toggled.
        if (!Paging.TryBegin("get_call_hierarchy", limit, cursor, readContext, out var page, out var cursorError,
                symbol_fqn, direction, depth))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var snapshotScope = readContext.Scope;
        var symbolStore = new SymbolStore(conn) { Scope = snapshotScope };
        var callGraphStore = new CallGraphStore(conn) { Scope = snapshotScope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        var directionName = direction?.Trim().ToLowerInvariant();
        if (directionName is not ("callers" or "callees"))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"Unknown direction '{direction}'. Use 'callers' or 'callees'.", readContext.Provenance);
        var callees = directionName == "callees";

        var lookup = SymbolResolver.Lookup(symbolStore, projectStore, symbol_fqn, CallableOptions);
        if (lookup.Status != SymbolLookupStatus.Resolved)
            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
        var rootSymbol = lookup.Symbol!;
        var namer = new SymbolNamer(symbolStore);

        var hits = new List<(SymbolInfo Target, CallGraphEdge Edge, int Depth)>();
        var visited = new HashSet<long>();
        var queue = new Queue<(long symbolId, int currentDepth)>();
        queue.Enqueue((rootSymbol.Id, 0));
        visited.Add(rootSymbol.Id);

        while (queue.Count > 0)
        {
            var (currentId, currentDepth) = queue.Dequeue();

            List<CallGraphEdge> edges;
            if (callees)
                edges = callGraphStore.GetByCaller(currentId);
            else
                edges = callGraphStore.GetByCallee(currentId);

            // A stable order within each level, so a cursor resumes exactly where the previous page ended.
            foreach (var edge in edges.OrderBy(e => e.CallSiteFile, StringComparer.Ordinal).ThenBy(e => e.CallSiteLine).ThenBy(e => e.Id))
            {
                var targetId = callees ? edge.CalleeSymbolId : edge.CallerSymbolId;
                var targetSymbol = symbolStore.GetById(targetId);
                if (targetSymbol == null) continue;

                hits.Add((targetSymbol, edge, currentDepth + 1));

                if (currentDepth + 1 < depth && visited.Add(targetId))
                {
                    queue.Enqueue((targetId, currentDepth + 1));
                }
            }
        }

        object? Summary() => new
        {
            ByDepth = Paging.CountBy(hits, h => h.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ByFile = Paging.CountBy(hits, h => h.Edge.CallSiteFile)
        };

        var results = page.Slice(hits).Select(hit =>
        {
            var (targetSymbol, edge, hitDepth) = hit;
            var entry = new Dictionary<string, object?>
            {
                ["fully_qualified_name"] = namer.QualifiedName(targetSymbol),
                ["display_name"] = targetSymbol.DisplayName,
                ["kind"] = targetSymbol.Kind.ToString().ToLowerInvariant(),
                ["file_path"] = targetSymbol.FilePath,
                ["line_start"] = targetSymbol.LineStart,
                ["call_site_file"] = edge.CallSiteFile,
                ["call_site_line"] = edge.CallSiteLine,
                ["depth"] = hitDepth
            };

            // The call did not bind exactly (for example an argument type is unresolved on the indexer); this
            // method is one of the compiler's candidates for it.
            if (edge.IsCandidate)
                entry["candidate"] = true;

            // Raw host-filesystem read (no content-hash verification): suppress under an enforced
            // multi-tenant policy so a reconstructed absolute call-site path cannot expose a file
            // outside the caller's authorized repository. Location-only under enforcement; the
            // zero-policy local path is byte-identical (hardening review, criteria 1 & 2).
            if (include_source && !dbProvider.Authorizer.IsEnforcing)
                entry["source_context"] = SourceReader.ReadContext(edge.CallSiteFile, edge.CallSiteLine, 2);

            return (object)entry;
        }).ToList();

        var freshness = rootSymbol.LastIndexedAt;
        var message = ResponseBuilder.JoinMessages(
            SymbolResolver.ResolutionNote(symbolStore, lookup),
            hits.Count == 0
                ? $"{SymbolResolver.Describe(namer, rootSymbol)} has no indexed {(callees ? "callees" : "callers")}."
                : null);
        return ResponseBuilder.BuildPage(results, hits.Count, page, freshness, lookup.Ambiguity, readContext.Provenance, Summary,
            message: message);
    }

    // The call graph links members with bodies: methods, constructors, and property/indexer/event accessors.
    private static readonly SymbolLookupOptions CallableOptions = new()
    {
        Kinds = new HashSet<SymbolKind>
        {
            SymbolKind.Method, SymbolKind.Constructor, SymbolKind.Property, SymbolKind.Indexer, SymbolKind.Event
        },
        KindDescription = "method, constructor, property, indexer or event"
    };
}
