namespace Sextant.Indexer;

/// <summary>
/// Instructs <see cref="IndexOrchestrator.IndexSolutionAsync"/> to stage the invalidated closure as an
/// OVERLAY generation layered on an existing committed base snapshot (Phase 10, issue #44), rather than
/// mutating the base in place. The overlay:
/// <list type="bullet">
/// <item>gets a fresh snapshot whose identity = the base commit identity + <see cref="WorkingTreeDelta"/>
/// (so a dirty tree is never mis-identified as the clean base — issue #43);</item>
/// <item>re-extracts only the closure projects into FRESH per-overlay project-version rows;</item>
/// <item>SHARES every out-of-closure project by mapping the base snapshot's existing (unchanged) row into
/// the overlay's <c>snapshot_projects</c> — never re-extracting or mutating it, so the base snapshot's
/// rows stay byte-identical (issue #44);</item>
/// <item>publishes atomically and advances the branch pointer WITHOUT superseding the base snapshot.</item>
/// </list>
/// The undirected connected-component closure guarantees no reference edge crosses the boundary between
/// re-extracted and shared projects, so sharing base rows is fully correct (Phase-4 invariant).
/// </summary>
public sealed record OverlayContext
{
    /// <summary>The committed base snapshot this overlay layers on (must be a Complete snapshot).</summary>
    public required long BaseSnapshotId { get; init; }

    /// <summary>
    /// The working-tree delta digest folded into the overlay's identity (issue #43), or null when the
    /// tree is clean (in which case the caller re-selects the base directly rather than staging an
    /// overlay). Non-null for every real overlay.
    /// </summary>
    public string? WorkingTreeDelta { get; init; }
}
