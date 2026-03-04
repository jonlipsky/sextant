using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetApiSurfaceTool
{
    [McpServerTool(Name = "get_api_surface"), Description("Get the complete public API surface of a project. Use to understand what a project exposes before making changes. Supports diff against previous commits.")]
    public static string GetApiSurface(
        DatabaseProvider dbProvider,
        [Description("Canonical ID of the project")] string project_id,
        [Description("Git commit to compare against (optional, for diff mode)")] string? compare_to_commit = null)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var symbolStore = new SymbolStore(conn);
        var apiSurfaceStore = new ApiSurfaceStore(conn);

        var project = projectStore.GetByCanonicalId(project_id);
        if (project == null)
            return ResponseBuilder.BuildEmpty($"Project not found: {project_id}");

        var projectId = project.Value.id;

        // Get current public/protected symbols
        var publicSymbols = symbolStore.GetByProjectAndAccessibility(projectId, ["public", "protected"]);

        if (compare_to_commit == null)
        {
            // No diff — just return current API surface
            var results = publicSymbols.Select(s => new
            {
                fully_qualified_name = s.FullyQualifiedName,
                display_name = s.DisplayName,
                kind = s.Kind.ToString().ToLowerInvariant(),
                accessibility = SymbolStore.FormatAccessibility(s.Accessibility),
                signature = s.Signature,
                signature_hash = s.SignatureHash,
                file_path = s.FilePath,
                line_start = s.LineStart
            }).ToList<object>();

            return ResponseBuilder.Build(results, project.Value.lastIndexedAt);
        }

        // Diff mode: compare current symbols against a previous snapshot
        var oldSnapshots = apiSurfaceStore.GetByProjectAndCommit(projectId, compare_to_commit);
        if (oldSnapshots.Count == 0)
            return ResponseBuilder.BuildEmpty($"No snapshot found for commit: {compare_to_commit}");

        var oldSurface = new List<(string fqn, string signatureHash, string accessibility)>();
        foreach (var snapshot in oldSnapshots)
        {
            var sym = symbolStore.GetById(snapshot.SymbolId);
            if (sym != null)
            {
                oldSurface.Add((
                    sym.FullyQualifiedName,
                    snapshot.SignatureHash,
                    SymbolStore.FormatAccessibility(sym.Accessibility)
                ));
            }
        }

        var newSurface = publicSymbols.Select(s => (
            s.FullyQualifiedName,
            s.SignatureHash ?? s.FullyQualifiedName,
            SymbolStore.FormatAccessibility(s.Accessibility)
        )).ToList();

        var changes = BreakingChangeDetector.DetectChanges(oldSurface, newSurface);
        var overall = BreakingChangeDetector.GetOverallClassification(changes);

        var diffResults = new List<object>
        {
            new
            {
                overall_classification = overall.ToString().ToLowerInvariant(),
                compared_against = compare_to_commit,
                changes = changes.Select(c => new
                {
                    symbol_fqn = c.SymbolFqn,
                    classification = c.Classification.ToString().ToLowerInvariant(),
                    reason = c.Reason
                }).ToList()
            }
        };

        return ResponseBuilder.Build(diffResults, project.Value.lastIndexedAt);
    }
}
