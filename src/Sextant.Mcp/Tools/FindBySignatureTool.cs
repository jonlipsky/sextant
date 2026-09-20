using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindBySignatureTool
{
    [McpServerTool(Name = "find_by_signature"),
     Description("Find methods/properties by their signature characteristics: return type, parameter types, parameter count.")]
    public static string FindBySignature(
        DatabaseProvider dbProvider,
        [Description("Return type to match (e.g., 'Task', 'IEnumerable<Order>', 'void'). Partial match supported.")]
        string? return_type = null,
        [Description("Parameter type to match — finds methods with at least one parameter of this type (e.g., 'CancellationToken', 'HttpClient'). Partial match supported.")]
        string? parameter_type = null,
        [Description("Exact number of parameters to match")]
        int? parameter_count = null,
        [Description("Symbol kind filter (default: method). Use 'property' for property type matching.")]
        string? kind = null,
        [Description("Optional project canonical ID filter")]
        string? project_id = null,
        [Description("Maximum results (default 50)")]
        int max_results = 50)
    {
        var db = dbProvider.GetReadyDatabase(out var notReady);
        if (db == null)
            return ResponseBuilder.BuildEmpty(notReady);

        if (!ReadContextGate.TryResolve(db, out var readContext, out var authError))
            return authError;

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildEmpty("Project not found.", readContext.Provenance);
            projectDbId = proj.Value.id;
        }

        var results = symbolStore.SearchBySignature(return_type, parameter_type, kind, projectDbId, max_results * 2);

        // Post-filter by parameter count if specified
        if (parameter_count != null)
        {
            results = results.Where(s =>
            {
                var sig = s.Signature ?? "";
                var parenStart = sig.IndexOf('(');
                var parenEnd = sig.LastIndexOf(')');
                if (parenStart < 0 || parenEnd < 0) return parameter_count == 0;
                var paramSection = sig[(parenStart + 1)..parenEnd].Trim();
                if (string.IsNullOrEmpty(paramSection)) return parameter_count == 0;
                return CountParameters(paramSection) == parameter_count;
            }).ToList();
        }

        results = results.Take(max_results).ToList();

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var mapped = results.Select(s => FindSymbolTool.MapSymbol(s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache))).ToList<object>();
        var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0;
        return ResponseBuilder.Build(mapped, freshness, provenance: readContext.Provenance);
    }

    internal static int CountParameters(string paramSection)
    {
        int count = 1, angleDepth = 0;
        foreach (var ch in paramSection)
        {
            if (ch == '<') angleDepth++;
            else if (ch == '>') angleDepth--;
            else if (ch == ',' && angleDepth == 0) count++;
        }
        return count;
    }
}
