using Sextant.Core;
using Sextant.Store;
using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>The decision a <see cref="LocalOverlayReconciler"/> reconciliation pass reached.</summary>
public enum OverlayReconcileKind
{
    /// <summary>Clean working tree with an exact committed base — the base is selected, no overlay (criterion 1).</summary>
    CleanBase,

    /// <summary>Clean tree with no committed base yet — a normal full index built the base.</summary>
    FullInitial,

    /// <summary>Dirty tree over a compatible base — a working-tree-delta overlay was staged (criterion 2).</summary>
    Overlay,

    /// <summary>Dirty tree with no compatible base — a full LOCAL index carrying the delta + reason (criterion 5).</summary>
    FullFallback,

    /// <summary>
    /// The git HEAD or working tree moved mid-pass (issue #49): the pass was aborted BEFORE publishing so
    /// no mixed-state generation was committed. A subsequent pass over the now-stable tree publishes the
    /// correct single-state snapshot (self-heal / bounded retry).
    /// </summary>
    Aborted
}

/// <summary>The outcome of a reconciliation pass.</summary>
public sealed record OverlayReconcileResult
{
    public required OverlayReconcileKind Kind { get; init; }

    /// <summary>The explicit fallback reason when <see cref="Kind"/> is <see cref="OverlayReconcileKind.FullFallback"/>.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>The number of working-tree changes discovered from git (0 for a clean tree).</summary>
    public int ChangeCount { get; init; }
}

/// <summary>
/// The AUTHORITATIVE Git-aware local reconciliation pass (Phase 10). Run at startup (before the file
/// watcher is enabled) and periodically, it reconstructs the working-tree state purely from git —
/// independent of any file-watcher events (criterion 3) — and decides how to represent it:
/// <list type="bullet">
/// <item>a clean tree over an exact committed base selects the base with no overlay (criterion 1);</item>
/// <item>a dirty tree over a compatible base stages a working-tree-delta overlay via the SAME
/// contribution pipeline as full indexing (criterion 2), never mutating the base (issue #44);</item>
/// <item>a dirty tree with no compatible base falls back to a full LOCAL index that records an EXPLICIT
/// reason and carries the delta digest so it never claims identity with the clean base (criterion 5,
/// issue #43);</item>
/// <item>project/import/SDK changes escalate to project-level invalidation through the incremental
/// closure's evaluation-fingerprint check (criterion 4).</item>
/// </list>
/// Publication is atomic (the Phase-9 one-transaction publish + branch-pointer advance), so MCP never
/// observes a half-updated overlay.
/// </summary>
public sealed class LocalOverlayReconciler
{
    /// <summary>
    /// Stable sentinel working-tree delta used when git status is UNAVAILABLE (a HEAD resolved, but the
    /// working-tree diff could not be enumerated). Folding it into the snapshot identity keeps the
    /// resulting full index DISTINCT from the clean base commit's snapshot (so a possibly-dirty tree is
    /// never re-selected as the clean base — issue #43) while staying constant so repeated
    /// status-unavailable passes re-select idempotently. It is not a real digest, so it can never
    /// collide with a genuine working-tree delta.
    /// </summary>
    public const string StatusUnavailableDeltaSentinel = "git-status-unavailable";

    private readonly IndexDatabase _db;
    private readonly Action<string>? _log;
    private readonly bool _useDocumentExtractor;
    private readonly ExtractionParallelismOptions _parallelism;
    private readonly IndexProfileDescriptor? _profile;
    private readonly IGitStateProbe _gitStateProbe;

    public LocalOverlayReconciler(
        IndexDatabase db,
        Action<string>? log = null,
        bool useDocumentExtractor = false,
        ExtractionParallelismOptions? parallelism = null,
        IndexProfileDescriptor? profile = null,
        IGitStateProbe? gitStateProbe = null)
    {
        _db = db;
        _log = log;
        _useDocumentExtractor = useDocumentExtractor;
        _parallelism = parallelism ?? ExtractionParallelismOptions.Default;
        _profile = profile;
        // Issue #49: the git-state probe pins HEAD/tree/status for the whole pass. Captured HERE (before
        // the working-tree status read below) so a move any time between this capture and the pre-publish
        // re-verification aborts the pass. Defaults to the real git probe; tests inject a fake.
        _gitStateProbe = gitStateProbe ?? GitStateProbe.Default;
    }

    /// <summary>Runs a single authoritative reconciliation pass over <paramref name="solution"/>.</summary>
    public async Task<OverlayReconcileResult> ReconcileAsync(Solution solution, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var repoRoot = ResolveRepoRoot(solution);
        var ctx = IndexOrchestrator.TryResolveSnapshotContext(repoRoot);

        // No git root / no HEAD commit: nothing to reconcile against, so index the tree fully (this is a
        // normal no-snapshot local index — the pre-Phase-9 mutable path — not a "fallback" state). No git
        // identity is published, so the #49 mid-pass-move guard is inert here (no pin captured).
        if (repoRoot == null || ctx == null)
        {
            await RunFullAsync(solution, cancellationToken, workingTreeDelta: null, fallbackReason: null,
                ctx: null, gitStatePin: null);
            return new OverlayReconcileResult { Kind = OverlayReconcileKind.FullInitial, ChangeCount = 0 };
        }

        // Issue #49: pin the git state BEFORE reading working-tree status below, so the status read, the
        // source reads (during extraction) and the recorded commit are all bound to one state. The
        // orchestrator re-verifies this pin immediately before publishing and throws GitStateMovedException
        // if HEAD/the working tree moved — we translate that into an Aborted result (no snapshot published).
        var pin = _gitStateProbe.Capture(repoRoot);
        try
        {
            return await ReconcileWithContextAsync(solution, repoRoot, ctx, pin, cancellationToken);
        }
        catch (GitStateMovedException ex)
        {
            _log?.Invoke($"Overlay reconcile aborted: {ex.Message}");
            return new OverlayReconcileResult
            {
                Kind = OverlayReconcileKind.Aborted,
                FallbackReason = ex.Message,
                ChangeCount = 0
            };
        }
    }

    private async Task<OverlayReconcileResult> ReconcileWithContextAsync(
        Solution solution, string repoRoot, SnapshotContext ctx, GitStatePin? pin, CancellationToken cancellationToken)
    {
        var changeSet = GitChangeProvider.TryGetChangeSet(repoRoot);

        // Git unavailable for status (but a HEAD resolved): treat as non-reconcilable and index fully.
        if (changeSet == null)
        {
            const string reason = "git working-tree status unavailable; indexed the working tree fully";
            // We could NOT prove the tree is clean, so the full index must NOT be published under the
            // clean base commit's identity (WorkingTreeDelta = null). Doing so would let a genuinely
            // dirty tree be re-selected as — or mis-identified as — the clean committed base snapshot
            // (issue #43): BeginPending dedups by identity hash, so a null delta would collide with an
            // existing clean base and re-select it WITHOUT indexing the possibly-dirty tree, silently
            // dropping the reason too. A stable sentinel delta keeps this fallback a DISTINCT,
            // self-consistent snapshot that always carries its explicit reason yet still re-selects
            // idempotently on a repeated status-unavailable pass.
            await RunFullAsync(solution, cancellationToken,
                workingTreeDelta: StatusUnavailableDeltaSentinel, fallbackReason: reason, ctx: ctx, gitStatePin: pin);
            return new OverlayReconcileResult
            {
                Kind = OverlayReconcileKind.FullFallback,
                FallbackReason = reason,
                ChangeCount = 0
            };
        }

        if (changeSet.IsClean)
        {
            // Clean tree: the full-index path is an idempotent select-or-build. An exact committed base
            // is re-selected without rebuild (empty overlay, criterion 1); if none exists it is built.
            var hadBase = ResolveCompatibleBaseId(ctx) != null;
            await RunFullAsync(solution, cancellationToken, workingTreeDelta: null, fallbackReason: null,
                ctx: ctx, gitStatePin: pin);
            return new OverlayReconcileResult
            {
                Kind = hadBase ? OverlayReconcileKind.CleanBase : OverlayReconcileKind.FullInitial,
                ChangeCount = 0
            };
        }

        // Dirty tree. Fold the delta into the identity (issue #43) so it is never confused with the base.
        var delta = changeSet.ComputeDeltaDigest();
        var baseId = ResolveCompatibleBaseId(ctx);

        if (baseId == null)
        {
            var reason = $"no compatible committed base snapshot for HEAD {ctx.CommitSha} " +
                         "(base not indexed, or schema/analyzer/config/toolchain mismatch); indexed the dirty working tree fully";
            _log?.Invoke($"Overlay reconcile: {reason}.");
            await RunFullAsync(solution, cancellationToken, workingTreeDelta: delta, fallbackReason: reason,
                ctx: ctx, gitStatePin: pin);
            return new OverlayReconcileResult
            {
                Kind = OverlayReconcileKind.FullFallback,
                FallbackReason = reason,
                ChangeCount = changeSet.Changes.Count
            };
        }

        _log?.Invoke($"Overlay reconcile: {changeSet.Changes.Count} working-tree change(s) over base snapshot {baseId}; staging overlay.");
        var overlay = new OverlayContext { BaseSnapshotId = baseId.Value, WorkingTreeDelta = delta };
        await new IncrementalIndexer(_db, _log, _useDocumentExtractor, _parallelism, _profile, _gitStateProbe)
            .IndexChangedFilesAsync(solution, changeSet.TouchedAbsolutePaths(), cancellationToken, overlay, ctx, pin);

        return new OverlayReconcileResult
        {
            Kind = OverlayReconcileKind.Overlay,
            ChangeCount = changeSet.Changes.Count
        };
    }

    /// <summary>
    /// The committed base snapshot compatible with <paramref name="ctx"/>'s clean HEAD — the snapshot
    /// whose identity matches the commit/tree/schema/analyzer/config/toolchain with NO working-tree
    /// delta, that is Complete and not itself an overlay. Null when none exists (criterion 5).
    /// </summary>
    private long? ResolveCompatibleBaseId(SnapshotContext ctx)
    {
        var conn = _db.GetConnection();
        var snapshotStore = new SnapshotStore(conn);
        var baseIdentity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = ctx.RepositoryRemoteUrl,
            CommitSha = ctx.CommitSha,
            TreeSha = ctx.TreeSha,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
            ConfigHash = _profile?.ConfigurationHash,
            ToolchainFingerprint = ToolchainFingerprint.Current,
            WorkingTreeDelta = null
        };
        var row = snapshotStore.GetByIdentityHash(baseIdentity.Hash);
        if (row is { Status: SnapshotStatus.Complete, IsOverlay: false })
            return row.Id;
        return null;
    }

    private Task RunFullAsync(Solution solution, CancellationToken cancellationToken, string? workingTreeDelta,
        string? fallbackReason, SnapshotContext? ctx, GitStatePin? gitStatePin)
        => new IndexOrchestrator(_db, _log, _useDocumentExtractor, _parallelism, _profile, _gitStateProbe).IndexSolutionAsync(
            solution,
            progress: null,
            metrics: new IndexingMetrics { Mode = fallbackReason != null ? "overlay-fallback" : "full" },
            cancellationToken: cancellationToken,
            snapshotContext: ctx,
            workingTreeDelta: workingTreeDelta,
            fallbackReason: fallbackReason,
            gitStatePin: gitStatePin);

    private static string? ResolveRepoRoot(Solution solution)
    {
        var firstProjectPath = solution.Projects.FirstOrDefault(p => p.FilePath != null)?.FilePath;
        return firstProjectPath != null ? GitRemoteResolver.ResolveGitRoot(firstProjectPath) : null;
    }
}
