using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindSymbolTool
{
    [McpServerTool(Name = "find_symbol"), Description("Instant indexed lookup of a symbol by name. Faster than grep/file reading for .NET codebases. Use fuzzy=true for FTS5 search.")]
    public static string FindSymbol(
        DatabaseProvider dbProvider,
        [Description("The symbol name or fully qualified name to search for")] string name,
        [Description("Optional symbol kind filter (class, method, property, etc.)")] string? kind = null,
        [Description("Optional project canonical ID filter")] string? project_id = null,
        [Description("Use FTS5 fuzzy search instead of exact match")] bool fuzzy = false,
        [Description("Include the symbol's source declaration")] bool include_source = false,
        [Description("Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'")] string? scope = null)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var projectStore = new ProjectStore(conn);

        var canonicalIdCache = BuildCanonicalIdCache(projectStore);

        // Resolve scope to project filter
        var scopeFilter = ScopeResolver.Resolve(scope, conn);

        if (fuzzy)
        {
            var config = SextantConfiguration.FromEnvironment();
            var results = symbolStore.SearchFts(name, config.FtsMaxResults, kind);

            if (!scopeFilter.IsEmpty)
            {
                if (scopeFilter.FilePath != null)
                    results = results.Where(s => s.FilePath == scopeFilter.FilePath).ToList();
                else if (scopeFilter.ProjectIds != null)
                    results = results.Where(s => scopeFilter.ProjectIds.Contains(s.ProjectId)).ToList();
            }

            var mapped = results.Select(s => MapSymbol(s, ResolveCanonicalId(s.ProjectId, canonicalIdCache), include_source)).ToList();
            var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0;
            return ResponseBuilder.Build(mapped, freshness);
        }
        else
        {
            long? projectDbId = null;
            if (project_id != null)
            {
                var proj = projectStore.GetByCanonicalId(project_id);
                if (proj == null)
                    return ResponseBuilder.BuildEmpty("Project not found.");
                projectDbId = proj.Value.id;
            }

            var symbol = symbolStore.GetByFqn(name, projectDbId);
            if (symbol == null)
                return ResponseBuilder.BuildEmpty("Symbol not found.");

            if (!scopeFilter.IsEmpty)
            {
                if (scopeFilter.FilePath != null && symbol.FilePath != scopeFilter.FilePath)
                    return ResponseBuilder.BuildEmpty("Symbol not in scope.");
                if (scopeFilter.ProjectIds != null && !scopeFilter.ProjectIds.Contains(symbol.ProjectId))
                    return ResponseBuilder.BuildEmpty("Symbol not in scope.");
            }

            var mapped = new List<object> { MapSymbol(symbol, ResolveCanonicalId(symbol.ProjectId, canonicalIdCache), include_source) };
            return ResponseBuilder.Build(mapped, symbol.LastIndexedAt);
        }
    }

    internal static Dictionary<long, string> BuildCanonicalIdCache(ProjectStore projectStore)
    {
        var cache = new Dictionary<long, string>();
        foreach (var (id, project) in projectStore.GetAll())
            cache[id] = project.CanonicalId;
        return cache;
    }

    internal static string ResolveCanonicalId(long projectId, Dictionary<long, string> cache)
        => cache.TryGetValue(projectId, out var cid) ? cid : projectId.ToString();

    internal static object MapSymbol(SymbolInfo s, string? canonicalId = null, bool includeSource = false)
    {
        var result = new Dictionary<string, object?>
        {
            ["fully_qualified_name"] = s.FullyQualifiedName,
            ["display_name"] = s.DisplayName,
            ["kind"] = s.Kind.ToString().ToLowerInvariant(),
            ["project_id"] = canonicalId ?? s.ProjectId.ToString(),
            ["file_path"] = s.FilePath,
            ["line_start"] = s.LineStart,
            ["line_end"] = s.LineEnd,
            ["accessibility"] = SymbolStore.FormatAccessibility(s.Accessibility),
            ["signature"] = s.Signature
        };

        if (includeSource)
            result["source_context"] = SourceReader.ReadDeclaration(s.FilePath, s.LineStart, s.LineEnd);

        return result;
    }
}
