using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetApiSurfaceTool
{
    [McpServerTool(Name = "get_api_surface"), Description("Public API of a project, or its breaking changes since compare_to_commit. Use before changing a library.")]
    public static string GetApiSurface(
        DatabaseProvider dbProvider,
        [Description("canonical_id from get_index_status.")] string project_id,
        string? compare_to_commit = null,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (!Paging.TryBegin("get_api_surface", limit, cursor, readContext, out var page, out var cursorError,
                project_id, compare_to_commit))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var apiSurfaceStore = new ApiSurfaceStore(conn);

        var project = projectStore.GetByCanonicalId(project_id);
        if (project == null)
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"Unknown project_id '{project_id}'. Use a project canonical ID as listed by get_index_status.",
                readContext.Provenance);

        var projectId = project.Value.id;

        var namer = new SymbolNamer(symbolStore);

        // Get current public/protected symbols
        var publicSymbols = symbolStore.GetByProjectAndAccessibility(projectId, ["public", "protected"]);

        if (compare_to_commit == null)
        {
            // No diff — return the current API surface, one page at a time.
            var ordered = publicSymbols
                .OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.LineStart).ThenBy(s => s.Id)
                .ToList();
            object? summary = page.IsTruncatedFirstPage(ordered.Count)
                ? new
                {
                    ByKind = Paging.CountBy(ordered, s => s.Kind.ToString().ToLowerInvariant()),
                    ByFile = Paging.CountBy(ordered, s => s.FilePath)
                }
                : null;
            var results = page.Slice(ordered).Select(s => new
            {
                fully_qualified_name = namer.QualifiedName(s),
                display_name = s.DisplayName,
                kind = s.Kind.ToString().ToLowerInvariant(),
                accessibility = SymbolStore.FormatAccessibility(s.Accessibility),
                signature = s.Signature,
                signature_hash = s.SignatureHash,
                file_path = s.FilePath,
                line_start = s.LineStart
            }).ToList<object>();

            return ResponseBuilder.BuildPage(results, ordered.Count, page, project.Value.lastIndexedAt,
                provenance: readContext.Provenance, summary: summary);
        }

        // Diff mode: compare current symbols against a previous snapshot
        var oldSnapshots = apiSurfaceStore.GetByProjectAndCommit(projectId, compare_to_commit);
        if (oldSnapshots.Count == 0)
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"No API surface snapshot was recorded for commit '{compare_to_commit}' in this project. Omit compare_to_commit for the current surface.",
                readContext.Provenance);

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
