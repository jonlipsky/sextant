using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class TraceValueTool
{
    [McpServerTool(Name = "trace_value"),
     Description("Values flowing into a method (origins) or out of it (destinations). Use instead of reading call sites.")]
    public static string TraceValue(
        DatabaseProvider dbProvider,
        [Description("Method, fully qualified.")]
        string method_fqn,
        [Description("origins or destinations.")]
        string direction,
        [Description("origins only: parameter name or index.")]
        string? parameter = null,
        int depth = 2,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        if (!CapabilityGate.Ensure(db, IndexFeature.Dataflow, "dataflow", out var unavailable,
                readContext.SelectedSnapshotId, dbProvider.Authorizer.IsEnforcing))
            return unavailable;
        if (!Paging.TryBegin("trace_value", limit, cursor, readContext, out var page, out var cursorError,
                method_fqn, direction, parameter, depth))
            return cursorError;

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
            return TraceOrigins(methodSymbol, parameter, page, symbolStore, namer, callGraphStore, argumentFlowStore, lookup.Ambiguity, readContext.Provenance, note);
        return TraceDestinations(methodSymbol, page, symbolStore, namer, callGraphStore, returnFlowStore, lookup.Ambiguity, readContext.Provenance, note);
    }

    private static readonly SymbolLookupOptions MethodOptions = new()
    {
        Kinds = new HashSet<Core.SymbolKind> { Core.SymbolKind.Method, Core.SymbolKind.Constructor },
        KindDescription = "method or constructor"
    };

    private static string TraceOrigins(
        SymbolInfo method, string? parameter, PageRequest page,
        SymbolStore symbolStore, SymbolNamer namer, CallGraphStore callGraphStore, ArgumentFlowStore argumentFlowStore,
        SymbolAmbiguity? ambiguity, SnapshotProvenance? provenance, string? note)
    {
        // Find all call graph edges where this method is the callee
        var callerEdges = callGraphStore.GetByCallee(method.Id);
        var edgesById = callerEdges.ToDictionary(e => e.Id);
        var allArgFlows = argumentFlowStore.GetByCallGraphIds(callerEdges.Select(e => e.Id).ToList());

        // One row per (parameter, call site), grouped by parameter: the page is cut from these rows, then
        // regrouped, so a parameter with hundreds of call sites cannot blow the page.
        var allGroups = allArgFlows.GroupBy(a => a.ParameterName).ToList();
        var rows = allGroups
            .Where(g =>
            {
                if (parameter == null) return true;
                if (int.TryParse(parameter, out var idx))
                    return g.Any(a => a.ParameterOrdinal == idx);
                return g.Key == parameter;
            })
            .OrderBy(g => g.First().ParameterOrdinal).ThenBy(g => g.Key, StringComparer.Ordinal)
            .SelectMany(g => g
                .Select(af => (Flow: af, Edge: edgesById.GetValueOrDefault(af.CallGraphId), Ordinal: g.First().ParameterOrdinal))
                .OrderBy(r => r.Edge?.CallSiteFile, StringComparer.Ordinal).ThenBy(r => r.Edge?.CallSiteLine).ThenBy(r => r.Flow.Id))
            .ToList();

        object? summary = page.IsTruncatedFirstPage(rows.Count)
            ? new
            {
                ByParameter = Paging.CountBy(rows, r => r.Flow.ParameterName),
                ByFile = Paging.CountBy(rows, r => r.Edge?.CallSiteFile)
            }
            : null;

        var results = page.Slice(rows)
            .GroupBy(r => r.Flow.ParameterName)
            .Select(g => (object)new
            {
                parameter_name = g.Key,
                parameter_ordinal = g.First().Ordinal,
                callers = g.Select(r =>
                {
                    var callerSymbol = r.Edge != null ? symbolStore.GetById(r.Edge.CallerSymbolId) : null;
                    return (object)new
                    {
                        caller_fqn = callerSymbol is null ? "unknown" : namer.QualifiedName(callerSymbol),
                        argument_expression = r.Flow.ArgumentExpression,
                        argument_kind = r.Flow.ArgumentKind,
                        source_symbol_fqn = r.Flow.SourceSymbolFqn,
                        call_site_file = r.Edge?.CallSiteFile,
                        call_site_line = r.Edge?.CallSiteLine ?? 0
                    };
                }).ToList()
            }).ToList();

        string? empty = null;
        if (rows.Count == 0)
        {
            var target = SymbolResolver.Describe(namer, method);
            empty = allGroups.Count == 0
                ? $"No argument flows into {target} were found (it has no indexed callers passing arguments)."
                : $"{target} has no traced parameter '{parameter}'. Traced parameters: " +
                  $"{string.Join(", ", allGroups.Select(g => $"{g.Key} ({g.First().ParameterOrdinal})"))}.";
        }
        return ResponseBuilder.BuildPage(results, rows.Count, page, method.LastIndexedAt, ambiguity, provenance, summary,
            resultCount: Math.Max(0, Math.Min(page.Limit, rows.Count - page.Offset)),
            message: ResponseBuilder.JoinMessages(note, empty));
    }

    private static string TraceDestinations(
        SymbolInfo method, PageRequest page,
        SymbolStore symbolStore, SymbolNamer namer, CallGraphStore callGraphStore, ReturnFlowStore returnFlowStore,
        SymbolAmbiguity? ambiguity, SnapshotProvenance? provenance, string? note)
    {
        // Find all call graph edges where this method is the callee
        var callerEdges = callGraphStore.GetByCallee(method.Id)
            .OrderBy(e => e.CallSiteFile, StringComparer.Ordinal).ThenBy(e => e.CallSiteLine).ThenBy(e => e.Id)
            .ToList();
        var allReturnFlows = returnFlowStore.GetByCallGraphIds(callerEdges.Select(e => e.Id).ToList());

        object? summary = page.IsTruncatedFirstPage(callerEdges.Count)
            ? new { ByFile = Paging.CountBy(callerEdges, e => e.CallSiteFile) }
            : null;

        var results = page.Slice(callerEdges).Select(edge =>
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

        var empty = callerEdges.Count == 0
            ? $"{SymbolResolver.Describe(namer, method)} has no indexed callers, so its return value flows nowhere in the index."
            : null;
        return ResponseBuilder.BuildPage(results, callerEdges.Count, page, method.LastIndexedAt, ambiguity, provenance, summary,
            message: ResponseBuilder.JoinMessages(note, empty));
    }
}
