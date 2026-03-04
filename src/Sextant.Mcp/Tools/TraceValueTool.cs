using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

public static class TraceValueTool
{
    [McpServerTool(Name = "trace_value"),
     Description("Trace the flow of a value through method calls. Given a method parameter, find what arguments callers pass. Given a method return value, find where the result goes.")]
    public static string TraceValue(
        DatabaseProvider dbProvider,
        [Description("FQN of the method to trace")]
        string method_fqn,
        [Description("Direction: 'origins' (what values flow IN to this method's parameters) or 'destinations' (where this method's return value flows)")]
        string direction,
        [Description("For 'origins': parameter name or index to trace. Omit to trace all parameters.")]
        string? parameter = null,
        [Description("Maximum depth of transitive tracing (default 2)")]
        int depth = 2)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var callGraphStore = new CallGraphStore(conn);
        var argumentFlowStore = new ArgumentFlowStore(conn);
        var returnFlowStore = new ReturnFlowStore(conn);

        var methodSymbol = symbolStore.GetByFqn(method_fqn);
        if (methodSymbol == null)
            return ResponseBuilder.BuildEmpty("Method not found.");

        if (direction == "origins")
            return TraceOrigins(methodSymbol, parameter, depth, symbolStore, callGraphStore, argumentFlowStore);
        else if (direction == "destinations")
            return TraceDestinations(methodSymbol, depth, symbolStore, callGraphStore, returnFlowStore);
        else
            return ResponseBuilder.BuildEmpty("Invalid direction. Use 'origins' or 'destinations'.");
    }

    private static string TraceOrigins(
        Core.SymbolInfo method, string? parameter, int depth,
        SymbolStore symbolStore, CallGraphStore callGraphStore, ArgumentFlowStore argumentFlowStore)
    {
        // Find all call graph edges where this method is the callee
        var callerEdges = callGraphStore.GetByCallee(method.Id);
        var edgeIds = callerEdges.Select(e => e.Id).ToList();
        var allArgFlows = argumentFlowStore.GetByCallGraphIds(edgeIds);

        // Group argument flows by parameter
        var paramGroups = allArgFlows
            .GroupBy(a => a.ParameterName)
            .Where(g =>
            {
                if (parameter == null) return true;
                if (int.TryParse(parameter, out var idx))
                    return g.Any(a => a.ParameterOrdinal == idx);
                return g.Key == parameter;
            });

        var results = paramGroups.Select(g =>
        {
            var callers = g.Select(af =>
            {
                var edge = callerEdges.FirstOrDefault(e => e.Id == af.CallGraphId);
                var callerSymbol = edge != null ? symbolStore.GetById(edge.CallerSymbolId) : null;

                return (object)new
                {
                    caller_fqn = callerSymbol?.FullyQualifiedName ?? "unknown",
                    argument_expression = af.ArgumentExpression,
                    argument_kind = af.ArgumentKind,
                    source_symbol_fqn = af.SourceSymbolFqn,
                    call_site_file = edge?.CallSiteFile,
                    call_site_line = edge?.CallSiteLine ?? 0
                };
            }).ToList();

            return (object)new
            {
                parameter_name = g.Key,
                parameter_ordinal = g.First().ParameterOrdinal,
                callers
            };
        }).ToList();

        return ResponseBuilder.Build(results, method.LastIndexedAt);
    }

    private static string TraceDestinations(
        Core.SymbolInfo method, int depth,
        SymbolStore symbolStore, CallGraphStore callGraphStore, ReturnFlowStore returnFlowStore)
    {
        // Find all call graph edges where this method is the callee
        var callerEdges = callGraphStore.GetByCallee(method.Id);
        var edgeIds = callerEdges.Select(e => e.Id).ToList();
        var allReturnFlows = returnFlowStore.GetByCallGraphIds(edgeIds);

        var results = callerEdges.Select(edge =>
        {
            var callerSymbol = symbolStore.GetById(edge.CallerSymbolId);
            var returnFlow = allReturnFlows.FirstOrDefault(r => r.CallGraphId == edge.Id);

            return (object)new
            {
                caller_fqn = callerSymbol?.FullyQualifiedName ?? "unknown",
                destination_kind = returnFlow?.DestinationKind ?? "unknown",
                destination_variable = returnFlow?.DestinationVariable,
                destination_symbol_fqn = returnFlow?.DestinationSymbolFqn,
                call_site_file = edge.CallSiteFile,
                call_site_line = edge.CallSiteLine
            };
        }).ToList();

        return ResponseBuilder.Build(results, method.LastIndexedAt);
    }
}
