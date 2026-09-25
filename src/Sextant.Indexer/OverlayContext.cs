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
/// <para>
/// Issue #108 (REMOTE base): when the compatible committed base is NOT in the local catalog but a
/// configured peer publishes it, <see cref="BaseSnapshotId"/> is null. The overlay then maps ONLY the
/// touched-closure projects into its <c>snapshot_projects</c>; every out-of-closure project is OMITTED
/// entirely (not shared, not re-extracted) because there is no local base row to share — the federated
/// read planner unions those unchanged projects from the remote base at query time. A thin machine thus
/// indexes ONLY its working-tree diff and never performs a full local index.
/// </para>
/// </summary>
public sealed record OverlayContext
{
    /// <summary>
    /// The committed base snapshot this overlay layers on (a Complete, local snapshot), or null when the
    /// compatible base lives only on a configured remote peer (issue #108). A null base makes this a
    /// REMOTE-base overlay: out-of-closure projects are omitted from the overlay and federated from the
    /// peer at read time instead of shared from a local base row.
    /// </summary>
    public long? BaseSnapshotId { get; init; }

    /// <summary>
    /// The working-tree delta digest folded into the overlay's identity (issue #43), or null when the
    /// tree is clean (in which case the caller re-selects the base directly rather than staging an
    /// overlay). Non-null for every real overlay.
    /// </summary>
    public string? WorkingTreeDelta { get; init; }
}
