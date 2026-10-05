using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindBySignatureTool
{
    [McpServerTool(Name = "find_by_signature"),
     Description("Methods by return type, parameter type or count. Use instead of regex over signatures.")]
    public static string FindBySignature(
        DatabaseProvider dbProvider,
        [Description("Partial match, e.g. Task.")]
        string? return_type = null,
        [Description("Partial match, e.g. CancellationToken.")]
        string? parameter_type = null,
        int? parameter_count = null,
        [Description("method or property.")]
        string? kind = null,
        [Description(ToolText.ProjectId)]
        string? project_id = null,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (!SymbolKindNames.TryParse(kind, out var kinds, out _, out var kindError))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, kindError!, readContext.Provenance);
        if (!Paging.TryBegin("find_by_signature", limit, cursor, readContext, out var page, out var cursorError,
                return_type, parameter_type, parameter_count, kind, project_id))
            return cursorError;

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

        // Every match is read (no SQL cap), so meta.total is exact; only this page is mapped.
        var results = kinds == null
            ? symbolStore.SearchBySignature(return_type, parameter_type, null, projectDbId, int.MaxValue)
            : kinds.Order()
                .SelectMany(k => symbolStore.SearchBySignature(return_type, parameter_type, k.ToString(), projectDbId, int.MaxValue))
                .ToList();

        // The store's text match is only a prefilter: the parsed signature decides, so a return type is matched
        // against the return type alone and a parameter type never against a parameter name or default value.
        results = results
            .Where(s => Matches(
                SignatureText.Parse(s.Declaration ?? s.Signature), return_type, parameter_type, parameter_count))
            .OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.LineStart).ThenBy(s => s.Id)
            .ToList();

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var namer = new SymbolNamer(symbolStore);
        object? Summary() => new { ByProject = Paging.CountBy(results, s => FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache)) };
        var mapped = page.Slice(results)
            .Select(s => FindSymbolTool.MapSymbol(
                s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache), qualifiedName: namer.QualifiedName(s)))
            .ToList();
        var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0;
        return ResponseBuilder.BuildPage(mapped, results.Count, page, freshness, provenance: readContext.Provenance,
            summary: Summary, message: results.Count == 0 ? "No symbol matches the given signature filters." : null);
    }

    // Each filter is a case-insensitive partial match on its part; a legacy signature (no return type) never
    // matches a return type.
    private static bool Matches(SignatureText.Parts parts, string? returnType, string? parameterType, int? parameterCount) =>
        (returnType is null || (parts.ReturnType?.Contains(returnType, StringComparison.OrdinalIgnoreCase) ?? false))
        && (parameterType is null
            || parts.ParameterTypes.Any(t => t.Contains(parameterType, StringComparison.OrdinalIgnoreCase)))
        && (parameterCount is null || parts.ParameterTypes.Count == parameterCount);
}
