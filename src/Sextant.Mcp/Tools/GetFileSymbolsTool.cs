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
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var projectStore = new ProjectStore(conn);
        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);

        var symbols = symbolStore.GetByFile(file_path);
        var mapped = symbols.Select(s => FindSymbolTool.MapSymbol(s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache))).ToList<object>();
        var freshness = symbols.Count > 0 ? symbols.Min(s => s.LastIndexedAt) : 0;

        return ResponseBuilder.Build(mapped, freshness);
    }
}
