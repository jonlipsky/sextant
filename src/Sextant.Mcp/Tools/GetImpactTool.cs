using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetImpactTool
{
    [McpServerTool(Name = "get_impact"), Description("Projects that break if a symbol changes. Use before refactoring.")]
    public static string GetImpact(
        DatabaseProvider dbProvider,
        [Description(ToolText.SymbolFqn)] string symbol_fqn,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (!Paging.TryBegin("get_impact", limit, cursor, readContext, out var page, out var cursorError, symbol_fqn))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var snapshotScope = readContext.Scope;
        var symbolStore = new SymbolStore(conn) { Scope = snapshotScope };
        var dependencyStore = new ProjectDependencyStore(conn);
        var referenceStore = new ReferenceStore(conn) { Scope = snapshotScope };
        var apiSurfaceStore = new ApiSurfaceStore(conn);
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        var lookup = SymbolResolver.Lookup(symbolStore, projectStore, symbol_fqn);
        if (lookup.Status != SymbolLookupStatus.Resolved)
            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
        var symbol = lookup.Symbol!;
        var namer = new SymbolNamer(symbolStore);

        // Find all projects that depend on this symbol's project
        var consumers = dependencyStore.GetByDependency(symbol.ProjectId)
            .Select(dep => (Dependency: dep, Project: projectStore.GetById(dep.ConsumerProjectId)))
            .Where(c => c.Project != null)
            .OrderBy(c => c.Project!.Value.project.CanonicalId, StringComparer.Ordinal)
            .ThenBy(c => c.Dependency.ConsumerProjectId)
            .ToList();

        // Read the target's references once, not once per consumer.
        var refCountByProject = referenceStore.GetBySymbolId(symbol.Id)
            .GroupBy(r => r.InProjectId)
            .ToDictionary(g => g.Key, g => g.Count());

        var consumerResults = new List<object>();
        foreach (var (dep, consumer) in page.Slice(consumers))
        {
            var consumerProject = consumer!.Value;
            consumerResults.Add((object)new
            {
                project = new
                {
                    canonical_id = consumerProject.project.CanonicalId,
                    git_remote_url = consumerProject.project.GitRemoteUrl,
                    repo_relative_path = consumerProject.project.RepoRelativePath
                },
                reference_count = refCountByProject.GetValueOrDefault(dep.ConsumerProjectId),
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

        // The page's rows are the consumers; the response wraps the ones it keeps in the one symbol row.
        var target = new
        {
            fully_qualified_name = namer.QualifiedName(symbol),
            display_name = symbol.DisplayName,
            kind = symbol.Kind.ToString().ToLowerInvariant(),
            file_path = symbol.FilePath,
            line_start = symbol.LineStart
        };

        return ResponseBuilder.BuildPage(consumerResults, consumers.Count, page, symbol.LastIndexedAt, lookup.Ambiguity,
            readContext.Provenance, message: SymbolResolver.ResolutionNote(symbolStore, lookup), warning: lookup.Warning,
            shape: kept => new List<object>
            {
                new
                {
                    symbol = target,
                    is_api_surface = isApiSurface,
                    change_classification = changeClassification,
                    consumers = kept
                }
            });
    }

    private static List<(string symbolKey, string fqn, string signatureHash, string accessibility)> BuildSurface(
        List<Sextant.Core.ApiSurfaceSnapshot> snapshots)
    {
        // Snapshots are self-contained: fqn/accessibility/signature come from the row itself, not from
        // the live symbol table, so a historical surface reconstructs correctly even after the working
        // symbols were rebuilt (and their ids reassigned) since capture. Keyed by symbol_key so overloads
        // sharing an FQN stay distinct (issue #29).
        var surface = new List<(string, string, string, string)>();
        foreach (var snapshot in snapshots)
        {
            surface.Add((
                snapshot.SymbolKey,
                snapshot.FullyQualifiedName,
                snapshot.SignatureHash,
                snapshot.Accessibility
            ));
        }
        return surface;
    }
}
