using Sextant.Core;
using Sextant.Core.Platform;
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
    private readonly IBaseSnapshotSource? _remoteBaseSource;

    public LocalOverlayReconciler(
        IndexDatabase db,
        Action<string>? log = null,
        bool useDocumentExtractor = false,
        ExtractionParallelismOptions? parallelism = null,
        IndexProfileDescriptor? profile = null,
        IGitStateProbe? gitStateProbe = null,
        IBaseSnapshotSource? remoteBaseSource = null)
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
        // Issue #108: when configured, the committed base can be resolved from a remote peer instead of a
        // LOCAL complete snapshot, so a thin machine that indexed only its working-tree diff builds a
        // baseless overlay (base_snapshot_id NULL) that the read path federates over the peer. Null keeps
        // the pre-#108 behavior (a missing local base falls back to a full local index).
        _remoteBaseSource = remoteBaseSource;
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
        if (pin == null)
        {
            // Fail-closed (issue #49): a git-backed context resolved, but the git state could not be
            // pinned, so we cannot prove HEAD/status/sources are one consistent state. Abort rather than
            // publish an unverifiable generation; the next periodic pass self-heals once git is readable.
            _log?.Invoke("Overlay reconcile aborted: git state could not be pinned for a git-backed context (issue #49).");
            return new OverlayReconcileResult
            {
                Kind = OverlayReconcileKind.Aborted,
                FallbackReason = "git state could not be pinned for a git-backed context",
                ChangeCount = 0
            };
        }
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
            // Issue #108: no LOCAL committed base for HEAD. Before indexing the whole tree locally, ask a
            // configured peer whether it publishes the exact committed base (by the same identity hash a
            // local base would use). If it does, stage a BASELESS overlay (base_snapshot_id NULL) carrying
            // only the working-tree delta; the read path federates overlay ⊕ remote-base transparently. A
            // thin machine thus indexes only its diff and never builds the unchanged base locally.
            var remoteBase = _remoteBaseSource != null
                ? await ProbeRemoteBaseAsync(BuildRemoteBaseIdentity(ctx).Hash, cancellationToken).ConfigureAwait(false)
                : default;
            if (remoteBase.Published)
            {
                _log?.Invoke($"Overlay reconcile: {changeSet.Changes.Count} working-tree change(s) over a REMOTE base for HEAD {ctx.CommitSha}; staging baseless overlay.");
                var remoteOverlay = new OverlayContext { BaseSnapshotId = null, WorkingTreeDelta = delta };
                // Issue #119: a baseless overlay has no local base row to read coverage from, so the peer's
                // (immutable) base coverage is recorded on the overlay itself at publish. Local hits and
                // get_index_status then report the remote base's partiality instead of "complete".
                var overlayCtx = ctx with { Coverage = remoteBase.Coverage };
                await new IncrementalIndexer(_db, _log, _useDocumentExtractor, _parallelism, _profile, _gitStateProbe)
                    .IndexChangedFilesAsync(solution, changeSet.TouchedAbsolutePaths(), cancellationToken, remoteOverlay, overlayCtx, pin);
                return new OverlayReconcileResult
                {
                    Kind = OverlayReconcileKind.Overlay,
                    ChangeCount = changeSet.Changes.Count
                };
            }

            var reason = _remoteBaseSource != null
                ? $"no compatible committed base snapshot for HEAD {ctx.CommitSha} in the local catalog or any configured peer " +
                  "(base not indexed, unreachable, or schema/analyzer/config/toolchain mismatch); indexed the dirty working tree fully"
                : $"no compatible committed base snapshot for HEAD {ctx.CommitSha} " +
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
        var row = snapshotStore.GetByIdentityHash(BuildBaseIdentity(ctx).Hash);
        if (row is { Status: SnapshotStatus.Complete, IsOverlay: false })
            return row.Id;
        return null;
    }

    /// <summary>
    /// The clean-HEAD committed base identity for LOCAL resolution: the commit/tree/schema/analyzer/config/
    /// toolchain with NO working-tree delta and not an overlay, and — like every locally-produced snapshot
    /// (<c>TryResolveSnapshotContext</c> leaves it unset) — NO capability fingerprint. Its
    /// <see cref="SnapshotIdentity.Hash"/> is the key a LOCALLY-built base is looked up by
    /// (<see cref="ResolveCompatibleBaseId"/>); keeping it capability-free preserves the pure-local write/read
    /// identity byte-for-byte (criterion 4). The peer-addressable variant is <see cref="BuildRemoteBaseIdentity"/>.
    /// </summary>
    private SnapshotIdentity BuildBaseIdentity(SnapshotContext ctx) => new()
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

    /// <summary>
    /// The peer-addressable committed base identity (issue #108): identical to <see cref="BuildBaseIdentity"/>
    /// but folding this machine's <see cref="WorkerCapability.LocalDefault"/> fingerprint, because a peer
    /// (the standalone index service) publishes its committed bases under the PRODUCING node's default
    /// capability (<c>ServiceContracts.ToIdentity</c> ← <c>ServiceOptions.DefaultCapabilityFingerprint</c> =
    /// <see cref="WorkerCapability.LocalDefault"/>). A capability-free hash would therefore never match a real
    /// service base. <see cref="WorkerCapability.LocalDefault"/> is deterministic per (OS, architecture), so a
    /// SAME-PLATFORM peer's base is addressable; a genuinely different-capability base (e.g. a Linux service
    /// base fetched by a Windows client) yields a distinct hash and safely falls through to the full-local
    /// build — a capability incompatibility, not a mere addressing miss. This MUST stay byte-identical to the
    /// read-side <c>BaseSnapshotIdentity.ForRemoteOverlay</c> reconstruction, which folds the same value.
    /// </summary>
    private SnapshotIdentity BuildRemoteBaseIdentity(SnapshotContext ctx) => new()
    {
        RepositoryRemoteUrl = ctx.RepositoryRemoteUrl,
        CommitSha = ctx.CommitSha,
        TreeSha = ctx.TreeSha,
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = _profile?.ConfigurationHash,
        ToolchainFingerprint = ToolchainFingerprint.Current,
        WorkingTreeDelta = null,
        CapabilityFingerprint = WorkerCapability.LocalDefault.Fingerprint
    };

    /// <summary>
    /// Probes a configured remote peer for the committed base addressed by <paramref name="baseIdentityHash"/>
    /// (issue #108). Returns true only when a peer definitively PUBLISHES the base (a non-empty first page, or
    /// an explicit <c>published</c> flag from a current peer — a published base may be legitimately empty);
    /// a reachable peer that does not publish it, or an unreachable/timed-out peer, returns false so the
    /// caller falls back to a full local index. Never throws — a transport failure is a "no" here, and the
    /// read path re-attempts the fetch (with its own cache-first fallback) at query time. When the base is
    /// published, also returns the peer's recorded checkout coverage for it (issue #119; null from a
    /// pre-#119 peer or when none was recorded).
    /// </summary>
    private async Task<(bool Published, SnapshotCoverage? Coverage)> ProbeRemoteBaseAsync(
        string baseIdentityHash, CancellationToken cancellationToken)
    {
        if (_remoteBaseSource is null)
            return (false, null);
        try
        {
            var page = await _remoteBaseSource.FetchSymbolsAsync(
                new SnapshotPageRequest { IdentityHash = baseIdentityHash, Cursor = null, Limit = 1 },
                cancellationToken).ConfigureAwait(false);
            return page.Symbols.Count > 0 || page.IsProvenPublished ? (true, page.Coverage) : (false, null);
        }
        catch (Exception ex) when (ex is RemoteSnapshotUnavailableException or HttpRequestException)
        {
            _log?.Invoke($"Overlay reconcile: remote base probe failed ({ex.Message}); using full local fallback.");
            return (false, null);
        }
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
