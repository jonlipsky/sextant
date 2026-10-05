using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindReferencesTool
{

    [McpServerTool(Name = "find_references"), Description("Every use of a symbol, including the uses grep misses (qualified new Ns.Type(...), target-typed new(...), calls through interfaces) and none of its false matches (comments, strings, namesakes). Use instead of grep.")]
    public static string FindReferences(
        DatabaseProvider dbProvider,
        [Description(ToolText.SymbolFqn)] string symbol_fqn,
        [Description("Comma-separated project_ids from find_symbol.")] string? include_projects = null,
        [Description("project, file and/or kind, e.g. project,file.")] string? group_by = null,
        bool include_source = false,
        [Description(ToolText.Scope)] string? scope = null,
        [Description("read, write or readwrite.")] string? access_kind = null,
        [Description(ToolText.Federation)] string? federation = null,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        var mode = FederationModes.Parse(federation);
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError, mode))
            return authError;
        // include_source only adds each row's surrounding lines, so a cursor carries over when it is toggled.
        if (!Paging.TryBegin("find_references", limit, cursor, readContext, out var page, out var cursorError,
                symbol_fqn, include_projects, group_by, scope, access_kind, federation))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var snapshotScope = readContext.Scope;
        var symbolStore = new SymbolStore(conn) { Scope = snapshotScope };
        var referenceStore = new ReferenceStore(conn) { Scope = snapshotScope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var contextRetriever = new SourceContextRetriever(new FileStore(conn));

        var lookup = SymbolResolver.Lookup(symbolStore, projectStore, symbol_fqn);
        if (lookup.Status != SymbolLookupStatus.Resolved)
            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
        var symbol = lookup.Symbol!;
        var namer = new SymbolNamer(symbolStore);

        AccessKind? accessFilter = null;
        if (!string.IsNullOrEmpty(access_kind))
        {
            var accessText = access_kind.Trim().Replace("_", string.Empty).Replace("-", string.Empty);
            if (!accessText.All(char.IsLetter) || !Enum.TryParse<AccessKind>(accessText, ignoreCase: true, out var ak))
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                    $"Unknown access_kind '{access_kind}'. Use 'read', 'write' or 'readwrite', or omit it for all.",
                    readContext.Provenance);
            accessFilter = ak;
        }

        HashSet<long>? includedProjectIds = null;
        if (!string.IsNullOrEmpty(include_projects))
        {
            includedProjectIds = [];
            foreach (var canonicalId in include_projects.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var proj = projectStore.GetByCanonicalId(canonicalId);
                if (proj == null)
                    return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                        $"Unknown project '{canonicalId}' in include_projects. Use project_ids from find_symbol results.",
                        readContext.Provenance);
                includedProjectIds.Add(proj.Value.id);
            }
        }

        // An unknown project/solution or an unrecognized scope is an error, never a silently unfiltered query.
        var scopeFilter = ScopeResolver.Resolve(scope, conn, readContext.Scope, readContext.Paths);
        if (scopeFilter.Error != null)
            return scopeFilter.ErrorResponse(readContext.Provenance);

        var refs = referenceStore.GetBySymbolId(symbol.Id);

        if (includedProjectIds != null)
            refs = refs.Where(r => includedProjectIds.Contains(r.InProjectId)).ToList();

        if (!scopeFilter.IsEmpty)
        {
            if (scopeFilter.FilePath != null)
                refs = refs.Where(r => scopeFilter.MatchesFile(r.FilePath)).ToList();
            else if (scopeFilter.ProjectIds != null)
                refs = refs.Where(r => scopeFilter.ProjectIds.Contains(r.InProjectId)).ToList();
        }

        if (accessFilter != null)
            refs = refs.Where(r => r.AccessKind == accessFilter).ToList();

        var filtered = includedProjectIds != null || !scopeFilter.IsEmpty || accessFilter != null;
        var message = ResponseBuilder.JoinMessages(
            SymbolResolver.ResolutionNote(symbolStore, lookup),
            refs.Count == 0
                ? $"No references to {SymbolResolver.Describe(namer, symbol)} were found{(filtered ? " with the given filters" : string.Empty)}."
                : null);

        // A stable order, so a cursor resumes exactly where the previous page ended.
        refs = refs.OrderBy(r => r.FilePath, StringComparer.Ordinal).ThenBy(r => r.Line)
            .ThenBy(r => r.InProjectId).ThenBy(r => r.Id).ToList();

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        object? Summary() => new
        {
            ByProject = Paging.CountBy(refs, r => FindSymbolTool.ResolveCanonicalId(r.InProjectId, canonicalIdCache)),
            ByFile = Paging.CountBy(refs, r => r.FilePath)
        };

        // Only this page's rows are mapped, so snippets are read for at most `limit` references.
        var pageRefs = page.Slice(refs);
        var mapped = pageRefs.Select(r =>
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

            // The site did not bind exactly (for example a call whose argument type is unresolved on the
            // indexer); this symbol is one of the compiler's candidates for it.
            if (r.IsCandidate)
                result["candidate"] = true;

            if (include_source)
                result["source_context"] = contextRetriever.GetContext(r.InProjectId, r.FilePath, r.Line, 2);

            return result;
        }).ToList<object>();

        // Grouping shapes the rows the page keeps, so a page cut by size groups only the rows it returns.
        string[] groups = string.IsNullOrEmpty(group_by)
            ? []
            : group_by.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return ResponseBuilder.BuildPage(mapped, refs.Count, page, symbol.LastIndexedAt, lookup.Ambiguity,
            readContext.Provenance, Summary, message,
            shape: groups.Length == 0 ? null : rows => GroupReferences(rows, groups));
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
