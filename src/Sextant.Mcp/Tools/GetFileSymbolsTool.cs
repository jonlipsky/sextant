using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetFileSymbolsTool
{
    [McpServerTool(Name = "get_file_symbols"), Description("Outline of a file: types and members with signatures and lines. Use before reading it.")]
    public static string GetFileSymbols(
        DatabaseProvider dbProvider,
        [Description("Repo-relative, e.g. src/App/Foo.cs.")] string file_path,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (readContext.Paths.Refuses(file_path))
            return PathPresenter.AbsolutePathRefused("file_path", readContext.Provenance);
        if (!Paging.TryBegin("get_file_symbols", limit, cursor, readContext, out var page, out var cursorError, file_path))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);

        // An absolute path keeps its exact local lookup; a repository-relative one (issue #145) matches the
        // stored file wherever its project's checkout is, including a submodule's (stored relative to the
        // submodule's own root, so every whole-segment suffix of the input is a candidate).
        var symbols = PathPresenter.IsAbsolute(file_path.Trim())
            ? symbolStore.GetByFile(file_path)
            : symbolStore.GetByStoredRelativePaths(PathPresenter.StoredCandidates(file_path))
                .Where(s => readContext.Paths.Matches(s.FilePath, file_path))
                .ToList();
        symbols = symbols.OrderBy(s => s.LineStart).ThenBy(s => s.Id).ToList();

        var mapped = page.Slice(symbols)
            .Select(s => FindSymbolTool.MapSymbol(s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache)))
            .ToList();
        var freshness = symbols.Count > 0 ? symbols.Min(s => s.LastIndexedAt) : 0;

        return ResponseBuilder.BuildPage(mapped, symbols.Count, page, freshness, provenance: readContext.Provenance);
    }
}
