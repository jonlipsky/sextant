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
        var db = dbProvider.GetReadyDatabase(out var notReady);
        if (db == null)
            return ResponseBuilder.BuildEmpty(notReady);

        if (!ReadContextGate.TryResolve(db, out var readContext, out var authError, authorizer: dbProvider.Authorizer))
            return authError;

        using var conn = db.OpenReadConnection();
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var apiSurfaceStore = new ApiSurfaceStore(conn);

        var project = projectStore.GetByCanonicalId(project_id);
        if (project == null)
            return ResponseBuilder.BuildEmpty($"Project not found: {project_id}", readContext.Provenance);

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

            return ResponseBuilder.Build(results, project.Value.lastIndexedAt, provenance: readContext.Provenance);
        }

        // Diff mode: compare current symbols against a previous snapshot
        var oldSnapshots = apiSurfaceStore.GetByProjectAndCommit(projectId, compare_to_commit);
        if (oldSnapshots.Count == 0)
            return ResponseBuilder.BuildEmpty($"No snapshot found for commit: {compare_to_commit}", readContext.Provenance);

        var oldSurface = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>();
        foreach (var snapshot in oldSnapshots)
        {
            // Reconstruct the historical surface from the snapshot's own stable fields rather than the
            // live symbol row, which may have been rebuilt (new id) or removed since capture. Keyed by
            // symbol_key so overloads sharing an FQN stay distinct (issue #29).
            oldSurface.Add((
                snapshot.SymbolKey,
                snapshot.FullyQualifiedName,
                snapshot.SignatureHash,
                snapshot.Accessibility
            ));
        }

        var newSurface = publicSymbols.Select(s => (
            s.SymbolKey,
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

        return ResponseBuilder.Build(diffResults, project.Value.lastIndexedAt, provenance: readContext.Provenance);
    }
}
