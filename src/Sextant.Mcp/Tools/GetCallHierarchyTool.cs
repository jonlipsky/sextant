using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetCallHierarchyTool
{
    [McpServerTool(Name = "get_call_hierarchy"), Description("Trace method call chains (callers or callees) with depth control. Instantly resolves what would take multiple grep/read cycles.")]
    public static string GetCallHierarchy(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the method")] string symbol_fqn,
        [Description("Direction: 'callers' or 'callees'")] string direction,
        [Description("Maximum depth to traverse")] int depth = 5,
        [Description("Include source code snippet at each call site")] bool include_source = false)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var snapshotScope = readContext.Scope;
        var symbolStore = new SymbolStore(conn) { Scope = snapshotScope };
        var callGraphStore = new CallGraphStore(conn) { Scope = snapshotScope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        var config = SextantConfiguration.FromEnvironment();
        depth = Math.Min(depth, config.MaxCallHierarchyDepth);

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

        var results = new List<object>();
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

            foreach (var edge in edges)
            {
                var targetId = callees ? edge.CalleeSymbolId : edge.CallerSymbolId;
                var targetSymbol = symbolStore.GetById(targetId);
                if (targetSymbol == null) continue;

                var entry = new Dictionary<string, object?>
                {
                    ["fully_qualified_name"] = namer.QualifiedName(targetSymbol),
                    ["display_name"] = targetSymbol.DisplayName,
                    ["kind"] = targetSymbol.Kind.ToString().ToLowerInvariant(),
                    ["file_path"] = targetSymbol.FilePath,
                    ["line_start"] = targetSymbol.LineStart,
                    ["call_site_file"] = edge.CallSiteFile,
                    ["call_site_line"] = edge.CallSiteLine,
                    ["depth"] = currentDepth + 1
                };

                // Raw host-filesystem read (no content-hash verification): suppress under an enforced
                // multi-tenant policy so a reconstructed absolute call-site path cannot expose a file
                // outside the caller's authorized repository. Location-only under enforcement; the
                // zero-policy local path is byte-identical (hardening review, criteria 1 & 2).
                if (include_source && !dbProvider.Authorizer.IsEnforcing)
                    entry["source_context"] = SourceReader.ReadContext(edge.CallSiteFile, edge.CallSiteLine, 2);

                results.Add(entry);

                if (currentDepth + 1 < depth && visited.Add(targetId))
                {
                    queue.Enqueue((targetId, currentDepth + 1));
                }
            }
        }

        var freshness = rootSymbol.LastIndexedAt;
        var message = ResponseBuilder.JoinMessages(
            SymbolResolver.ResolutionNote(symbolStore, lookup),
            results.Count == 0
                ? $"{SymbolResolver.Describe(namer, rootSymbol)} has no indexed {(callees ? "callees" : "callers")}."
                : null);
        return ResponseBuilder.Build(results, freshness, lookup.Ambiguity, readContext.Provenance, message: message);
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
