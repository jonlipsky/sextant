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
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var dependencyStore = new ProjectDependencyStore(conn);
        var referenceStore = new ReferenceStore(conn);
        var apiSurfaceStore = new ApiSurfaceStore(conn);
        var projectStore = new ProjectStore(conn);

        var symbol = symbolStore.GetByFqn(symbol_fqn);
        if (symbol == null)
            return ResponseBuilder.BuildEmpty($"Symbol not found: {symbol_fqn}");

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

        // Check if this symbol is part of the API surface
        var latestSnapshots = apiSurfaceStore.GetLatestByProject(symbol.ProjectId);
        var isApiSurface = latestSnapshots.Any(s => s.SymbolId == symbol.Id);

        // Determine change classification
        string? changeClassification = null;
        if (isApiSurface && latestSnapshots.Count > 0)
        {
            var currentCommit = latestSnapshots.First().GitCommit;
            var previousCommit = apiSurfaceStore.GetPreviousCommit(symbol.ProjectId, currentCommit);
            if (previousCommit != null)
            {
                var oldSnapshots = apiSurfaceStore.GetByProjectAndCommit(symbol.ProjectId, previousCommit);
                var oldSurface = BuildSurface(oldSnapshots, symbolStore);
                var newSurface = BuildSurface(latestSnapshots, symbolStore);

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

        return ResponseBuilder.Build(result, symbol.LastIndexedAt);
    }

    private static List<(string fqn, string signatureHash, string accessibility)> BuildSurface(
        List<Sextant.Core.ApiSurfaceSnapshot> snapshots,
        SymbolStore symbolStore)
    {
        var surface = new List<(string, string, string)>();
        foreach (var snapshot in snapshots)
        {
            var sym = symbolStore.GetById(snapshot.SymbolId);
            if (sym != null)
            {
                surface.Add((
                    sym.FullyQualifiedName,
                    snapshot.SignatureHash,
                    SymbolStore.FormatAccessibility(sym.Accessibility)
                ));
            }
        }
        return surface;
    }
}
