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
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var callGraphStore = new CallGraphStore(conn);

        var config = SextantConfiguration.FromEnvironment();
        depth = Math.Min(depth, config.MaxCallHierarchyDepth);

        var rootSymbol = symbolStore.GetByFqn(symbol_fqn);
        if (rootSymbol == null)
            return ResponseBuilder.BuildEmpty("Symbol not found.");

        var results = new List<object>();
        var visited = new HashSet<long>();
        var queue = new Queue<(long symbolId, int currentDepth)>();
        queue.Enqueue((rootSymbol.Id, 0));
        visited.Add(rootSymbol.Id);

        while (queue.Count > 0)
        {
            var (currentId, currentDepth) = queue.Dequeue();

            List<CallGraphEdge> edges;
            if (direction == "callees")
                edges = callGraphStore.GetByCaller(currentId);
            else
                edges = callGraphStore.GetByCallee(currentId);

            foreach (var edge in edges)
            {
                var targetId = direction == "callees" ? edge.CalleeSymbolId : edge.CallerSymbolId;
                var targetSymbol = symbolStore.GetById(targetId);
                if (targetSymbol == null) continue;

                var entry = new Dictionary<string, object?>
                {
                    ["fully_qualified_name"] = targetSymbol.FullyQualifiedName,
                    ["display_name"] = targetSymbol.DisplayName,
                    ["kind"] = targetSymbol.Kind.ToString().ToLowerInvariant(),
                    ["file_path"] = targetSymbol.FilePath,
                    ["line_start"] = targetSymbol.LineStart,
                    ["call_site_file"] = edge.CallSiteFile,
                    ["call_site_line"] = edge.CallSiteLine,
                    ["depth"] = currentDepth + 1
                };

                if (include_source)
                    entry["source_context"] = SourceReader.ReadContext(edge.CallSiteFile, edge.CallSiteLine, 2);

                results.Add(entry);

                if (currentDepth + 1 < depth && visited.Add(targetId))
                {
                    queue.Enqueue((targetId, currentDepth + 1));
                }
            }
        }

        var freshness = rootSymbol.LastIndexedAt;
        return ResponseBuilder.Build(results, freshness);
    }
}
