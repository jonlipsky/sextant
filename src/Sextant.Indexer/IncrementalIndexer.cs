using System.Security.Cryptography;
using Sextant.Core;
using Sextant.Store;
using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// Drives incremental re-indexing by computing the invalidated project closure for a set of changed
/// files/config and delegating the actual rebuild to <see cref="IndexOrchestrator"/> with a
/// canonical-id filter. Because both the full and incremental paths run the same extraction code, an
/// incremental rebuild of a closure is canonically identical to the same projects in a full index —
/// there is no second, divergent per-file extraction path to keep in sync (Phase 4).
/// </summary>
public sealed class IncrementalIndexer
{
    private readonly IndexDatabase _db;
    private readonly Action<string>? _log;
    private readonly bool _useDocumentExtractor;

    public IncrementalIndexer(IndexDatabase db, Action<string>? log = null, bool useDocumentExtractor = false)
    {
        _db = db;
        _log = log;
        _useDocumentExtractor = useDocumentExtractor;
    }

    /// <summary>
    /// Reindexes the invalidated project closure implied by <paramref name="changedFilePaths"/> plus
    /// any project whose evaluation fingerprint changed. Returns an empty list: the closure rebuild is
    /// complete on return, so callers have no signature-changed follow-up set to re-enqueue (the whole
    /// dependency closure was already rebuilt). The list return type is retained for API compatibility.
    /// </summary>
    public Task<List<string>> IndexChangedFilesAsync(
        Solution solution,
        IReadOnlyList<string> changedFilePaths)
        => IndexChangedFilesAsync(solution, changedFilePaths, default);

    /// <inheritdoc cref="IndexChangedFilesAsync(Solution, IReadOnlyList{string})" />
    public async Task<List<string>> IndexChangedFilesAsync(
        Solution solution,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var fileIndexStore = new FileIndexStore(conn);

        // Resolve project identity exactly as IndexOrchestrator does, so the canonical ids we hand it
        // as a filter line up with the ids it computes during registration.
        var (repoRoot, submodules) = await ResolveRepoContextAsync(solution);

        var projectCanonicalById = new Dictionary<ProjectId, string>();
        var dbIdByCanonical = new Dictionary<string, long>(StringComparer.Ordinal);
        var canonicalByDbId = new Dictionary<long, string>();
        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null) continue;
            var identity = ProjectIdentityFactory.Create(project, submodules, repoRoot);
            projectCanonicalById[project.Id] = identity.CanonicalId;
            var existing = projectStore.GetByCanonicalId(identity.CanonicalId);
            if (existing != null)
            {
                dbIdByCanonical[identity.CanonicalId] = existing.Value.id;
                canonicalByDbId[existing.Value.id] = identity.CanonicalId;
            }
        }

        // Map every current on-disk source file to the projects (per-TFM logical ids) that own it, so
        // a shared/linked file invalidates each framework it participates in.
        var fileToCanonical = BuildFileOwnershipMap(solution, projectCanonicalById);

        var affected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawPath in changedFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = rawPath;
            if (string.IsNullOrEmpty(path) || SymbolExtractor.IsGeneratedFile(path))
                continue;

            if (File.Exists(path))
            {
                // A changed or newly-created source file: invalidate every owning project whose stored
                // fingerprint for it is missing or stale. An unchanged file (hash matches file_index)
                // adds nothing, so a spurious watcher event or an unchanged restart schedules no work.
                var hash = ComputeFileHash(path);
                if (fileToCanonical.TryGetValue(path, out var owners))
                {
                    foreach (var canonicalId in owners)
                    {
                        var entry = dbIdByCanonical.TryGetValue(canonicalId, out var dbId)
                            ? fileIndexStore.GetByProjectAndFile(dbId, path)
                            : null;
                        if (entry == null || entry.ContentHash != hash)
                            affected.Add(canonicalId);
                    }
                }
            }
            else
            {
                // A deleted/renamed-away source file no longer has a Roslyn document, so recover its
                // owning projects from the persisted file_index and rebuild them to purge stale rows.
                foreach (var dbId in fileIndexStore.GetProjectIdsByFile(path))
                {
                    if (canonicalByDbId.TryGetValue(dbId, out var canonicalId))
                        affected.Add(canonicalId);
                }
            }
        }

        // Escalate any project whose evaluation inputs (csproj, Directory.Build.*, global.json,
        // analyzer/editor config, restored assets) changed, even when no .cs byte changed.
        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null
                || !projectCanonicalById.TryGetValue(project.Id, out var canonicalId)
                || !dbIdByCanonical.TryGetValue(canonicalId, out var dbId))
                continue;

            var current = EvaluationFingerprint.Compute(project.FilePath, repoRoot);
            var stored = projectStore.GetEvaluationFingerprint(dbId);
            if (!string.Equals(current, stored, StringComparison.Ordinal))
                affected.Add(canonicalId);
        }

        if (affected.Count == 0)
        {
            _log?.Invoke("Incremental: no changed projects — nothing to reindex.");
            return [];
        }

        // Expand to the undirected connected closure over the solution's project references, UNIONED
        // with the previous index's persisted cross-project connectivity. Unioning the old edges is
        // what lets a removed project reference (or a deleted cross-project usage) still pull the
        // now-detached neighbour into the closure: its symbols still own the stale inbound reference
        // rows, and only reprocessing it rebuilds them away. Old edges are read from persisted
        // references (always recorded), so this holds even when project_dependencies is not populated.
        var edges = new List<(string Consumer, string Dependency)>(
            BuildDependencyEdges(solution, projectCanonicalById));
        var referenceStore = new ReferenceStore(conn);
        foreach (var (consumerDbId, dependencyDbId) in referenceStore.GetCrossProjectPairs())
        {
            if (canonicalByDbId.TryGetValue(consumerDbId, out var consumer)
                && canonicalByDbId.TryGetValue(dependencyDbId, out var dependency))
                edges.Add((consumer, dependency));
        }
        var adjacency = ProjectClosure.BuildUndirectedAdjacency(edges, StringComparer.Ordinal);
        var closure = ProjectClosure.Expand(affected, adjacency, StringComparer.Ordinal);

        _log?.Invoke($"Incremental: {affected.Count} changed project(s), rebuilding closure of {closure.Count}.");

        // Delegate to the shared full-index extraction, restricted to the closure. This reuses the
        // Phase 3 write session + index_runs generation ledger inside the orchestrator; the incremental
        // path opens no transaction of its own.
        await new IndexOrchestrator(_db, _log, _useDocumentExtractor).IndexSolutionAsync(
            solution,
            progress: null,
            metrics: new IndexingMetrics { Mode = "incremental", ChangedFileCount = affected.Count },
            cancellationToken: cancellationToken,
            projectCanonicalFilter: closure);

        return [];
    }

    private static async Task<(string? repoRoot, List<SubmoduleInfo> submodules)> ResolveRepoContextAsync(
        Solution solution)
    {
        var submodules = new List<SubmoduleInfo>();
        string? repoRoot = null;
        var firstProjectPath = solution.Projects.FirstOrDefault(p => p.FilePath != null)?.FilePath;
        if (firstProjectPath != null)
        {
            repoRoot = GitRemoteResolver.ResolveGitRoot(firstProjectPath);
            if (repoRoot != null)
                submodules = await SubmoduleDiscovery.DiscoverAsync(repoRoot);
        }
        return (repoRoot, submodules);
    }

    private static Dictionary<string, List<string>> BuildFileOwnershipMap(
        Solution solution,
        Dictionary<ProjectId, string> projectCanonicalById)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (!projectCanonicalById.TryGetValue(project.Id, out var canonicalId))
                continue;
            foreach (var document in project.Documents)
            {
                var filePath = document.FilePath;
                if (string.IsNullOrEmpty(filePath) || SymbolExtractor.IsGeneratedFile(filePath))
                    continue;
                if (!map.TryGetValue(filePath, out var owners))
                {
                    owners = [];
                    map[filePath] = owners;
                }
                if (!owners.Contains(canonicalId, StringComparer.Ordinal))
                    owners.Add(canonicalId);
            }
        }
        return map;
    }

    private static IEnumerable<(string Consumer, string Dependency)> BuildDependencyEdges(
        Solution solution,
        Dictionary<ProjectId, string> projectCanonicalById)
    {
        foreach (var project in solution.Projects)
        {
            if (!projectCanonicalById.TryGetValue(project.Id, out var consumer))
                continue;
            foreach (var reference in project.ProjectReferences)
            {
                if (projectCanonicalById.TryGetValue(reference.ProjectId, out var dependency))
                    yield return (consumer, dependency);
            }
        }
    }

    public static string ComputeFileHash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
