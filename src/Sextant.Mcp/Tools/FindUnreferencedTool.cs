using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindUnreferencedTool
{
    [McpServerTool(Name = "find_unreferenced"), Description("Symbols nothing references: dead-code candidates (grep cannot prove absence).")]
    public static string FindUnreferenced(
        DatabaseProvider dbProvider,
        [Description(ToolText.Kind)] string? kind = null,
        [Description(ToolText.ProjectId)] string? project_id = null,
        bool exclude_test_projects = true,
        [Description("public, internal, protected, private, ...")] string? accessibility = null,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (!SymbolKindNames.TryParse(kind, out var kinds, out _, out var kindError))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, kindError!, readContext.Provenance);
        string? accessibilityName = null;
        if (!string.IsNullOrWhiteSpace(accessibility))
        {
            accessibilityName = accessibility.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (!AccessibilityNames.Contains(accessibilityName))
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                    $"Unknown accessibility '{accessibility.Trim()}'. Use one of: {string.Join(", ", AccessibilityNames)}.",
                    readContext.Provenance);
        }
        if (!Paging.TryBegin("find_unreferenced", limit, cursor, readContext, out var page, out var cursorError,
                kind, project_id, exclude_test_projects, accessibility))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };

        // Resolve project canonical ID to DB ID if provided
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

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var namer = new SymbolNamer(symbolStore);

        var symbols = symbolStore.GetUnreferenced(
                projectDbId, kinds is { Count: 1 } ? kinds.Single().ToString() : null, exclude_test_projects, accessibilityName)
            .Where(s => kinds == null || kinds.Contains(s.Kind))
            .OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.LineStart).ThenBy(s => s.Id)
            .ToList();

        object? summary = page.IsTruncatedFirstPage(symbols.Count)
            ? new
            {
                ByKind = Paging.CountBy(symbols, s => s.Kind.ToString().ToLowerInvariant()),
                ByProject = Paging.CountBy(symbols, s => FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache))
            }
            : null;

        var results = page.Slice(symbols)
            .Select(s => FindSymbolTool.MapSymbol(
                s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache), qualifiedName: namer.QualifiedName(s)))
            .ToList();
        var freshness = symbols.Count > 0 ? symbols.Min(s => s.LastIndexedAt) : 0L;

        return ResponseBuilder.BuildPage(results, symbols.Count, page, freshness, provenance: readContext.Provenance,
            summary: summary, message: symbols.Count == 0 ? "No unreferenced symbol matches the given filters." : null);
    }

    private static readonly string[] AccessibilityNames =
        ["public", "internal", "protected", "private", "protected_internal", "private_protected"];
}
