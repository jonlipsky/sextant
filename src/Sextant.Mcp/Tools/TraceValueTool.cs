using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
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
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        if (!CapabilityGate.Ensure(db, Core.IndexFeature.Dataflow, "dataflow", out var unavailable,
                readContext.SelectedSnapshotId, dbProvider.Authorizer.IsEnforcing))
            return unavailable;

        using var conn = db.OpenReadConnection();
        var snapshotScope = readContext.Scope;
        var symbolStore = new SymbolStore(conn) { Scope = snapshotScope };
        var callGraphStore = new CallGraphStore(conn) { Scope = snapshotScope };
        var argumentFlowStore = new ArgumentFlowStore(conn);
        var returnFlowStore = new ReturnFlowStore(conn);
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        var directionName = direction?.Trim().ToLowerInvariant();
        if (directionName is not ("origins" or "destinations"))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"Unknown direction '{direction}'. Use 'origins' or 'destinations'.", readContext.Provenance);

        var lookup = SymbolResolver.Lookup(symbolStore, projectStore, method_fqn, MethodOptions);
        if (lookup.Status != SymbolLookupStatus.Resolved)
            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
        var methodSymbol = lookup.Symbol!;
        var namer = new SymbolNamer(symbolStore);
        var note = SymbolResolver.ResolutionNote(symbolStore, lookup);

        if (directionName == "origins")
            return TraceOrigins(methodSymbol, parameter, depth, symbolStore, namer, callGraphStore, argumentFlowStore, lookup.Ambiguity, readContext.Provenance, note);
        return TraceDestinations(methodSymbol, depth, symbolStore, namer, callGraphStore, returnFlowStore, lookup.Ambiguity, readContext.Provenance, note);
    }

    private static readonly SymbolLookupOptions MethodOptions = new()
    {
        Kinds = new HashSet<Core.SymbolKind> { Core.SymbolKind.Method, Core.SymbolKind.Constructor },
        KindDescription = "method or constructor"
    };

    private static string TraceOrigins(
        Core.SymbolInfo method, string? parameter, int depth,
        SymbolStore symbolStore, SymbolNamer namer, CallGraphStore callGraphStore, ArgumentFlowStore argumentFlowStore,
        SymbolAmbiguity? ambiguity, SnapshotProvenance? provenance, string? note)
    {
        // Find all call graph edges where this method is the callee
        var callerEdges = callGraphStore.GetByCallee(method.Id);
        var edgeIds = callerEdges.Select(e => e.Id).ToList();
        var allArgFlows = argumentFlowStore.GetByCallGraphIds(edgeIds);

        // Group argument flows by parameter
        var allGroups = allArgFlows.GroupBy(a => a.ParameterName).ToList();
        var paramGroups = allGroups
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
                    caller_fqn = callerSymbol is null ? "unknown" : namer.QualifiedName(callerSymbol),
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

        string? empty = null;
        if (results.Count == 0)
        {
            var target = SymbolResolver.Describe(namer, method);
            empty = allGroups.Count == 0
                ? $"No argument flows into {target} were found (it has no indexed callers passing arguments)."
                : $"{target} has no traced parameter '{parameter}'. Traced parameters: " +
                  $"{string.Join(", ", allGroups.Select(g => $"{g.Key} ({g.First().ParameterOrdinal})"))}.";
        }
        return ResponseBuilder.Build(results, method.LastIndexedAt, ambiguity, provenance,
            message: ResponseBuilder.JoinMessages(note, empty));
    }

    private static string TraceDestinations(
        Core.SymbolInfo method, int depth,
        SymbolStore symbolStore, SymbolNamer namer, CallGraphStore callGraphStore, ReturnFlowStore returnFlowStore,
        SymbolAmbiguity? ambiguity, SnapshotProvenance? provenance, string? note)
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
                caller_fqn = callerSymbol is null ? "unknown" : namer.QualifiedName(callerSymbol),
                destination_kind = returnFlow?.DestinationKind ?? "unknown",
                destination_variable = returnFlow?.DestinationVariable,
                destination_symbol_fqn = returnFlow?.DestinationSymbolFqn,
                call_site_file = edge.CallSiteFile,
                call_site_line = edge.CallSiteLine
            };
        }).ToList();

        var empty = results.Count == 0
            ? $"{SymbolResolver.Describe(namer, method)} has no indexed callers, so its return value flows nowhere in the index."
            : null;
        return ResponseBuilder.Build(results, method.LastIndexedAt, ambiguity, provenance,
            message: ResponseBuilder.JoinMessages(note, empty));
    }
}
