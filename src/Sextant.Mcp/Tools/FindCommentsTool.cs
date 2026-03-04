using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

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
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var commentStore = new CommentStore(conn);
        var symbolStore = new SymbolStore(conn);
        var projectStore = new ProjectStore(conn);

        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildEmpty("Project not found.");
            projectDbId = proj.Value.id;
        }

        List<Core.CommentInfo> comments;

        if (in_symbol != null)
        {
            var symbol = symbolStore.GetByFqn(in_symbol);
            if (symbol == null)
                return ResponseBuilder.BuildEmpty("Symbol not found.");
            comments = commentStore.GetBySymbol(symbol.Id);
        }
        else if (!string.IsNullOrEmpty(search))
        {
            comments = commentStore.Search(search, projectDbId);
        }
        else if (tag != "all")
        {
            comments = commentStore.GetByTag(tag.ToUpperInvariant(), projectDbId);
        }
        else
        {
            comments = commentStore.GetAll(projectDbId);
        }

        // Apply tag filter if search or in_symbol was primary filter
        if (tag != "all" && (search != null || in_symbol != null))
            comments = comments.Where(c => c.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)).ToList();

        comments = comments.Take(max_results).ToList();

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var results = comments.Select(c =>
        {
            string? enclosingSymbolFqn = null;
            if (c.EnclosingSymbolId.HasValue)
            {
                var s = symbolStore.GetById(c.EnclosingSymbolId.Value);
                enclosingSymbolFqn = s?.FullyQualifiedName;
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
        return ResponseBuilder.Build(results, freshness);
    }
}
