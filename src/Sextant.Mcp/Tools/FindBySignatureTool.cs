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
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (!SymbolKindNames.TryParse(kind, out var kinds, out _, out var kindError))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, kindError!, readContext.Provenance);

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                    $"Unknown project_id '{project_id}'. Use a project canonical ID as listed by get_index_status.",
                    readContext.Provenance);
            projectDbId = proj.Value.id;
        }

        var results = kinds == null
            ? symbolStore.SearchBySignature(return_type, parameter_type, null, projectDbId, max_results * 2)
            : kinds.Order()
                .SelectMany(k => symbolStore.SearchBySignature(return_type, parameter_type, k.ToString(), projectDbId, max_results * 2))
                .ToList();

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
        var namer = new SymbolNamer(symbolStore);
        var mapped = results.Select(s => FindSymbolTool.MapSymbol(
            s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache), qualifiedName: namer.QualifiedName(s))).ToList<object>();
        var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0;
        return ResponseBuilder.Build(mapped, freshness, provenance: readContext.Provenance,
            message: mapped.Count == 0 ? "No symbol matches the given signature filters." : null);
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
