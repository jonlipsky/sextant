using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>
/// Resolves the on-disk checkout + solution to index for an ensure-snapshot request. Automated checkout
/// PROVISIONING (git clone/fetch/worktree of the requested commit) is ProcessStack's job in Phase 14; the
/// data plane only needs to LOCATE an already-provisioned checkout on its persistent checkout volume. This
/// seam keeps that boundary explicit and lets tests supply a checkout without a real git host.
/// </summary>
public interface ICheckoutProvider
{
    bool TryResolve(EnsureSnapshotRequest request, out string checkoutDir, out string solutionPath);
}

/// <summary>
/// Locates a checkout under the persistent checkout volume by a sanitized repository-url directory name,
/// then finds a single <c>.slnx</c>/<c>.sln</c> to index. Returns false (→ the job is
/// <see cref="SnapshotJobStatus.Unsupported"/>) when no checkout or solution is present, so a query-only
/// node degrades cleanly instead of failing hard.
/// </summary>
public sealed class PersistentVolumeCheckoutProvider(ServicePaths paths) : ICheckoutProvider
{
    public bool TryResolve(EnsureSnapshotRequest request, out string checkoutDir, out string solutionPath)
    {
        checkoutDir = string.Empty;
        solutionPath = string.Empty;

        var dirName = SanitizeRepo(request.RepositoryRemoteUrl);
        var root = Path.GetFullPath(paths.CheckoutRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, dirName));
        // Containment guard (defense in depth with SanitizeRepo): a crafted repository URL must never
        // resolve a checkout OUTSIDE the persistent checkout volume, so a rogue ensure request can neither
        // read nor (via later cleanup) delete arbitrary host paths.
        if (!IsContainedIn(root, candidate) || !Directory.Exists(candidate))
            return false;

        var solution =
            Directory.EnumerateFiles(candidate, "*.slnx", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.EnumerateFiles(candidate, "*.sln", SearchOption.AllDirectories).FirstOrDefault();
        if (solution is null)
            return false;

        checkoutDir = candidate;
        solutionPath = solution;
        return true;
    }

    // True when <paramref name="candidate"/> is the root itself or a descendant of it, computed on the
    // normalized full paths so "..", symlink-free traversal, and separator differences cannot escape.
    private static bool IsContainedIn(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        return rel != ".."
            && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(rel);
    }

    private static string SanitizeRepo(string url)
    {
        var trimmed = url.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        var chars = name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray();
        // Trim separators AND leading/trailing dots so "." / ".." (traversal) collapse to the safe default.
        var safe = new string(chars).Trim('_', '.');
        return safe.Length == 0 ? "repo" : safe;
    }
}

/// <summary>
/// The production single-node worker: it runs the in-process Roslyn indexer over an already-provisioned
/// checkout and lets the orchestrator publish the immutable snapshot through the Phase-9 catalog. The
/// service validates + records the terminal status; this worker just drives extraction. It shares the
/// service's writer <see cref="IndexDatabase"/> (the service serializes worker execution behind its write
/// gate + single-writer lease), so publication is atomic and never races another writer. It stays entirely
/// decoupled from ProcessStack — Phase 14 will instead register a worker that schedules remote execution.
/// </summary>
public sealed class LocalIndexerSnapshotWorker(
    IndexDatabase database,
    SextantConfiguration configuration,
    ICheckoutProvider checkoutProvider,
    Action<string>? log = null) : ISnapshotWorker
{
    public async Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
    {
        if (!checkoutProvider.TryResolve(request, out _, out var solutionPath))
        {
            return SnapshotWorkResult.Unsupported(
                $"No provisioned checkout with a solution found for '{request.RepositoryRemoteUrl}'. " +
                "Checkout provisioning is orchestrated separately (Phase 14); this node indexes existing checkouts only.");
        }

        var context = new SnapshotContext
        {
            RepositoryRemoteUrl = request.RepositoryRemoteUrl,
            CommitSha = request.CommitSha,
            TreeSha = request.TreeSha,
            BranchName = request.BranchName ?? "main",
            IsDefaultBranch = request.BranchName is null
        };

        try
        {
            var solution = await SolutionLoader.LoadSolutionAsync(solutionPath).ConfigureAwait(false);
            var orchestrator = new IndexOrchestrator(
                database, log, configuration.DocumentExtractor,
                ExtractionParallelismOptions.FromConfiguration(configuration),
                IndexProfileDescriptor.FromConfiguration(configuration));

            await orchestrator.IndexSolutionAsync(
                solution, progress: null, metrics: null, cancellationToken: cancellationToken,
                snapshotContext: context).ConfigureAwait(false);

            var published = new SnapshotStore(database.GetConnection()).GetByIdentityHash(identityHash);
            if (published is { Status: SnapshotStatus.Complete })
                return SnapshotWorkResult.Complete(published.Id);

            return SnapshotWorkResult.Failed(
                "indexing finished but no complete snapshot was published for the requested identity " +
                "(the checkout's committed state may differ from the requested commit).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SnapshotWorkResult.Failed(ex.Message);
        }
    }
}
