using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindReferencesTool
{
    [McpServerTool(Name = "find_references"), Description("Find all usages of a symbol across the entire solution instantly. More accurate than text search — uses Roslyn semantic analysis.")]
    public static string FindReferences(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the symbol")] string symbol_fqn,
        [Description("Comma-separated canonical IDs to filter results to specific projects")] string? include_projects = null,
        [Description("Group results by: 'project', 'file', 'kind', or comma-separated combination (e.g. 'project,file'). Default: flat list.")] string? group_by = null,
        [Description("Include source code lines around each reference")] bool include_source = false,
        [Description("Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'")] string? scope = null,
        [Description("Filter by access kind: 'read', 'write', 'readwrite', or null for all")] string? access_kind = null,
        [Description("Federation partition (Phase 11 diagnostic): 'federated' (default, overlay over base), 'base_only', or 'overlay_only'")] string? federation = null)
    {
        var db = dbProvider.GetReadyDatabase(out var notReady);
        if (db == null)
            return ResponseBuilder.BuildEmpty(notReady);

        var mode = FederationModes.Parse(federation);
        if (!ReadContextGate.TryResolve(db, out var readContext, out var authError, mode, dbProvider.Authorizer))
            return authError;

        using var conn = db.OpenReadConnection();
        var snapshotScope = readContext.Scope;
        var symbolStore = new SymbolStore(conn) { Scope = snapshotScope };
        var referenceStore = new ReferenceStore(conn) { Scope = snapshotScope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var contextRetriever = new SourceContextRetriever(new FileStore(conn));

        var resolution = SymbolResolver.Resolve(symbolStore, projectStore, symbol_fqn);
        if (resolution.Symbol == null)
            return ResponseBuilder.BuildEmpty("Symbol not found.", readContext.Provenance);
        var symbol = resolution.Symbol;

        var refs = referenceStore.GetBySymbolId(symbol.Id);

        // Filter by project if include_projects is specified
        if (!string.IsNullOrEmpty(include_projects))
        {
            var projectIds = new HashSet<long>();
            foreach (var canonicalId in include_projects.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var proj = projectStore.GetByCanonicalId(canonicalId);
                if (proj != null)
                    projectIds.Add(proj.Value.id);
            }
            refs = refs.Where(r => projectIds.Contains(r.InProjectId)).ToList();
        }

        // Apply scope filter
        var scopeFilter = ScopeResolver.Resolve(scope, conn, readContext.Scope);
        if (!scopeFilter.IsEmpty)
        {
            if (scopeFilter.FilePath != null)
                refs = refs.Where(r => r.FilePath == scopeFilter.FilePath).ToList();
            else if (scopeFilter.ProjectIds != null)
                refs = refs.Where(r => scopeFilter.ProjectIds.Contains(r.InProjectId)).ToList();
        }

        // Filter by access kind
        if (!string.IsNullOrEmpty(access_kind))
        {
            if (Enum.TryParse<AccessKind>(access_kind, ignoreCase: true, out var ak))
                refs = refs.Where(r => r.AccessKind == ak).ToList();
        }

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var mapped = refs.Select(r =>
        {
            var result = new Dictionary<string, object?>
            {
                ["file_path"] = r.FilePath,
                ["line"] = r.Line,
                ["reference_kind"] = r.ReferenceKind.ToString().ToLowerInvariant(),
                ["context_snippet"] = contextRetriever.GetLineSnippet(r.InProjectId, r.FilePath, r.Line),
                ["in_project_id"] = FindSymbolTool.ResolveCanonicalId(r.InProjectId, canonicalIdCache),
                ["access_kind"] = r.AccessKind?.ToString().ToLowerInvariant()
            };

            if (include_source)
                result["source_context"] = contextRetriever.GetContext(r.InProjectId, r.FilePath, r.Line, 2);

            return result;
        }).ToList<object>();

        if (!string.IsNullOrEmpty(group_by))
        {
            var groups = group_by.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var grouped = GroupReferences(mapped, groups);
            return ResponseBuilder.Build(grouped, symbol.LastIndexedAt, resolution.Ambiguity, readContext.Provenance);
        }

        return ResponseBuilder.Build(mapped, symbol.LastIndexedAt, resolution.Ambiguity, readContext.Provenance);
    }

    private static List<object> GroupReferences(List<object> refs, string[] groupKeys)
    {
        if (groupKeys.Length == 0 || refs.Count == 0)
            return refs;

        var currentKey = groupKeys[0];
        var remainingKeys = groupKeys[1..];

        var groups = refs.GroupBy(r => GetGroupValue(r, currentKey));

        return groups.Select(g =>
        {
            var items = g.ToList();
            var grouped = remainingKeys.Length > 0
                ? GroupReferences(items, remainingKeys)
                : items;

            return (object)new Dictionary<string, object?>
            {
                ["group_key"] = g.Key,
                ["group_type"] = currentKey,
                ["count"] = items.Count,
                ["items"] = grouped
            };
        }).ToList();
    }

    private static string GetGroupValue(object item, string groupKey)
    {
        if (item is not Dictionary<string, object?> dict)
            return "unknown";

        return groupKey switch
        {
            "project" => dict.TryGetValue("in_project_id", out var pid) ? pid?.ToString() ?? "unknown" : "unknown",
            "file" => dict.TryGetValue("file_path", out var fp) ? fp?.ToString() ?? "unknown" : "unknown",
            "kind" => dict.TryGetValue("reference_kind", out var rk) ? rk?.ToString() ?? "unknown" : "unknown",
            _ => "unknown"
        };
    }
}
