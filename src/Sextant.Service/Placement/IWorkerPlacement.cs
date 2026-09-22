using Sextant.Core.Platform;

namespace Sextant.Service.Placement;

/// <summary>
/// The ProcessStack-AGNOSTIC seam between the routing DECISION (made in <c>Sextant.Core</c>'s
/// <see cref="CapabilityRouter"/>) and whatever actually EXECUTES an index on a worker of a given
/// capability (Phase 15). A placement advertises its <see cref="Capability"/> — matched against a
/// project's requirement to decide routing — and produces a snapshot when selected.
///
/// The Phase-13 in-process indexer is the DEFAULT (Linux) placement behind this seam
/// (<see cref="LocalPlacement"/>). The ACTUAL native Windows/macOS worker execution is ProcessStack's
/// trusted-placement job (Phase 14, DEFERRED) — a Phase-14 placement implements this SAME interface
/// without the core routing contract ever depending on ProcessStack. Because the seam is substitutable,
/// the routing DECISION, fail-closed behaviour, and capability-fingerprint logic are fully covered on a
/// Linux CI by injecting a fake native placement (acceptance criterion 6).
/// </summary>
public interface IWorkerPlacement
{
    /// <summary>The capabilities this placement can faithfully evaluate (its routing advertisement).</summary>
    WorkerCapability Capability { get; }

    /// <summary>Whether this is the default (Linux, in-process) placement — the no-routing target.</summary>
    bool IsDefault { get; }

    /// <summary>
    /// Produces a snapshot for the request on this placement's worker, publishing it under
    /// <paramref name="identityHash"/> (the service's expected identity). Mirrors
    /// <see cref="ISnapshotWorker.ProduceAsync"/> so the routing worker can drive any placement uniformly.
    /// </summary>
    Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request,
        string identityHash,
        string scratchDir,
        CancellationToken cancellationToken);
}

/// <summary>
/// The DEFAULT placement (Phase 15): the Phase-13 in-process Roslyn indexer, advertising the node's own
/// default capability (Linux on the service). It simply delegates to the wrapped
/// <see cref="ISnapshotWorker"/> (the <see cref="LocalIndexerSnapshotWorker"/>), which stamps the node's
/// capability fingerprint into the published snapshot. This is what makes single-node/local operation
/// require ZERO routing infrastructure (CRITICAL 2): with only this placement registered the routing
/// worker never escalates and behaves exactly like the Phase-13 direct worker.
/// </summary>
public sealed class LocalPlacement(WorkerCapability capability, ISnapshotWorker worker) : IWorkerPlacement
{
    public WorkerCapability Capability { get; } = capability;

    public bool IsDefault => true;

    public Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken) =>
        worker.ProduceAsync(request, identityHash, scratchDir, cancellationToken);
}
