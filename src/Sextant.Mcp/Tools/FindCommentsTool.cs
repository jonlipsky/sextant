using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindCommentsTool
{
    [McpServerTool(Name = "find_comments"),
     Description("Find TODO, HACK, FIXME, BUG, and NOTE comments in the codebase.")]
    public static string FindComments(
        DatabaseProvider dbProvider,
        [Description("Filter by tag: 'TODO', 'HACK', 'FIXME', 'BUG', 'NOTE', or 'all' (default)")]
        string tag = "all",
        [Description("Search within comment text")]
        string? search = null,
        [Description("Optional project canonical ID filter")]
        string? project_id = null,
        [Description("FQN of enclosing symbol — find comments within a specific method/class")]
        string? in_symbol = null,
        [Description("Maximum results (default 50)")]
        int max_results = 50)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        if (!CapabilityGate.Ensure(db, Core.IndexFeature.Comments, "comments", out var unavailable,
                readContext.SelectedSnapshotId, dbProvider.Authorizer.IsEnforcing))
            return unavailable;

        using var conn = db.OpenReadConnection();
        var snapshotScope = readContext.Scope;
        var commentStore = new CommentStore(conn) { Scope = snapshotScope };
        var symbolStore = new SymbolStore(conn) { Scope = snapshotScope };
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

        var allTags = string.IsNullOrWhiteSpace(tag) || tag.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        var tagName = allTags ? null : tag.Trim().ToUpperInvariant();
        if (tagName != null && !KnownTags.Contains(tagName))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"Unknown tag '{tag}'. Use one of: {string.Join(", ", KnownTags)}, or 'all'.", readContext.Provenance);

        List<Core.CommentInfo> comments;
        SymbolAmbiguity? ambiguity = null;
        string? note = null;
        var namer = new SymbolNamer(symbolStore);

        if (in_symbol != null)
        {
            var lookup = SymbolResolver.Lookup(symbolStore, projectStore, in_symbol);
            if (lookup.Status != SymbolLookupStatus.Resolved)
                return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
            ambiguity = lookup.Ambiguity;
            note = SymbolResolver.ResolutionNote(symbolStore, lookup);
            comments = commentStore.GetBySymbol(lookup.Symbol!.Id);
            if (projectDbId != null)
                comments = comments.Where(c => c.ProjectId == projectDbId.Value).ToList();
        }
        else if (!string.IsNullOrEmpty(search))
        {
            comments = commentStore.Search(search, projectDbId);
        }
        else if (tagName != null)
        {
            comments = commentStore.GetByTag(tagName, projectDbId);
        }
        else
        {
            comments = commentStore.GetAll(projectDbId);
        }

        // Apply tag filter if search or in_symbol was primary filter
        if (tagName != null && (search != null || in_symbol != null))
            comments = comments.Where(c => c.Tag.Equals(tagName, StringComparison.OrdinalIgnoreCase)).ToList();

        comments = comments.Take(max_results).ToList();

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var results = comments.Select(c =>
        {
            string? enclosingSymbolFqn = null;
            if (c.EnclosingSymbolId.HasValue)
            {
                var s = symbolStore.GetById(c.EnclosingSymbolId.Value);
                enclosingSymbolFqn = s is null ? null : namer.QualifiedName(s);
            }

            return (object)new
            {
                tag = c.Tag,
                text = c.Text,
                file_path = c.FilePath,
                line = c.Line,
                enclosing_symbol = enclosingSymbolFqn,
                project_id = FindSymbolTool.ResolveCanonicalId(c.ProjectId, canonicalIdCache)
            };
        }).ToList();

        var freshness = comments.Count > 0 ? comments.Min(c => c.LastIndexedAt) : 0;
        return ResponseBuilder.Build(results, freshness, ambiguity, readContext.Provenance, message: note);
    }

    private static readonly string[] KnownTags = ["TODO", "HACK", "FIXME", "BUG", "NOTE", "UNDONE"];
}
