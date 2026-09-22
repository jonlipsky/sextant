using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetImpactTool
{
    [McpServerTool(Name = "get_impact"), Description("Assess the impact of changing a symbol — find all cross-project consumers and classify breaking changes. Use before refactoring.")]
    public static string GetImpact(
        DatabaseProvider dbProvider,
        [Description("Fully qualified name of the symbol")] string symbol_fqn)
    {
        var db = dbProvider.GetReadyDatabase(out var notReady);
        if (db == null)
            return ResponseBuilder.BuildEmpty(notReady);

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var dependencyStore = new ProjectDependencyStore(conn);
        var referenceStore = new ReferenceStore(conn);
        var apiSurfaceStore = new ApiSurfaceStore(conn);
        var projectStore = new ProjectStore(conn);

        var resolution = SymbolResolver.Resolve(symbolStore, projectStore, symbol_fqn);
        if (resolution.Symbol == null)
            return ResponseBuilder.BuildEmpty($"Symbol not found: {symbol_fqn}");
        var symbol = resolution.Symbol;

        // Find all projects that depend on this symbol's project
        var consumers = dependencyStore.GetByDependency(symbol.ProjectId);

        var consumerResults = new List<object>();
        foreach (var dep in consumers)
        {
            var consumerProject = projectStore.GetById(dep.ConsumerProjectId);
            if (consumerProject == null) continue;

            // Count references from this consumer to the target symbol
            var refs = referenceStore.GetBySymbolId(symbol.Id);
            var refCount = refs.Count(r => r.InProjectId == dep.ConsumerProjectId);

            consumerResults.Add(new
            {
                project = new
                {
                    canonical_id = consumerProject.Value.project.CanonicalId,
                    git_remote_url = consumerProject.Value.project.GitRemoteUrl,
                    repo_relative_path = consumerProject.Value.project.RepoRelativePath
                },
                reference_count = refCount,
                reference_kind = dep.ReferenceKind,
                submodule_pinned_commit = dep.SubmodulePinnedCommit,
                pin_includes_current = dep.SubmodulePinnedCommit != null
            });
        }

        // Check if this symbol is part of the API surface. Compare by stable semantic key rather than
        // the volatile row id, so a snapshot whose soft symbol_id back-pointer was nulled by a later
        // rebuild is still recognised as covering this symbol.
        var latestSnapshots = apiSurfaceStore.GetLatestByProject(symbol.ProjectId);
        var isApiSurface = latestSnapshots.Any(s => s.SymbolKey == symbol.SymbolKey);

        // Determine change classification
        string? changeClassification = null;
        if (isApiSurface && latestSnapshots.Count > 0)
        {
            var currentCommit = latestSnapshots.First().GitCommit;
            var previousCommit = apiSurfaceStore.GetPreviousCommit(symbol.ProjectId, currentCommit);
            if (previousCommit != null)
            {
                var oldSnapshots = apiSurfaceStore.GetByProjectAndCommit(symbol.ProjectId, previousCommit);
                var oldSurface = BuildSurface(oldSnapshots);
                var newSurface = BuildSurface(latestSnapshots);

                var changes = BreakingChangeDetector.DetectChanges(oldSurface, newSurface);
                var overall = BreakingChangeDetector.GetOverallClassification(changes);
                changeClassification = overall.ToString().ToLowerInvariant();
            }
        }

        var result = new List<object>
        {
            new
            {
                symbol = new
                {
                    fully_qualified_name = symbol.FullyQualifiedName,
                    display_name = symbol.DisplayName,
                    kind = symbol.Kind.ToString().ToLowerInvariant(),
                    file_path = symbol.FilePath,
                    line_start = symbol.LineStart
                },
                is_api_surface = isApiSurface,
                change_classification = changeClassification,
                consumers = consumerResults
            }
        };

        return ResponseBuilder.Build(result, symbol.LastIndexedAt, resolution.Ambiguity);
    }

    private static List<(string fqn, string signatureHash, string accessibility)> BuildSurface(
        List<Sextant.Core.ApiSurfaceSnapshot> snapshots)
    {
        // Snapshots are self-contained: fqn/accessibility/signature come from the row itself, not from
        // the live symbol table, so a historical surface reconstructs correctly even after the working
        // symbols were rebuilt (and their ids reassigned) since capture.
        var surface = new List<(string, string, string)>();
        foreach (var snapshot in snapshots)
        {
            surface.Add((
                snapshot.FullyQualifiedName,
                snapshot.SignatureHash,
                snapshot.Accessibility
            ));
        }
        return surface;
    }
}
