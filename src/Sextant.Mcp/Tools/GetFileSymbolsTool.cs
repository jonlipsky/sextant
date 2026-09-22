using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetFileSymbolsTool
{
    [McpServerTool(Name = "get_file_symbols"), Description("Get all symbols defined in a source file with their kinds, signatures, and line numbers. Use before reading a file to understand its structure.")]
    public static string GetFileSymbols(
        DatabaseProvider dbProvider,
        [Description("The source file path")] string file_path)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);

        var symbols = symbolStore.GetByFile(file_path);
        var mapped = symbols.Select(s => FindSymbolTool.MapSymbol(s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache))).ToList<object>();
        var freshness = symbols.Count > 0 ? symbols.Min(s => s.LastIndexedAt) : 0;

        return ResponseBuilder.Build(mapped, freshness, provenance: readContext.Provenance);
    }
}
