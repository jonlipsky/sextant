using Sextant.Core;
using Sextant.Core.Platform;

namespace Sextant.Store;

/// <summary>
/// Recomputes the committed BASE snapshot identity a remote-base overlay layers on (issue #108), derived
/// purely from the overlay generation's OWN stored fields. A remote-base overlay stores
/// <c>is_overlay = 1</c> with a NULL <c>base_snapshot_id</c> because its base lives on a configured peer,
/// not in the local catalog, so the read planner has no local base row to read the identity from — it
/// reconstructs the peer-addressable identity hash here instead of persisting a second column.
/// <para>
/// This MUST mirror <c>LocalOverlayReconciler.BuildRemoteBaseIdentity</c>, which built the same base
/// identity (from the clean HEAD context) to probe the peer at build time: the committed base is the CLEAN
/// commit state, so <see cref="SnapshotIdentity.WorkingTreeDelta"/> is null and
/// <see cref="SnapshotIdentity.IsOverlay"/> is false, and — like the reconciler's remote probe — the
/// capability fingerprint is this machine's <see cref="WorkerCapability.LocalDefault"/> (a peer service
/// publishes committed bases under the producing node's default capability, so a capability-free hash would
/// never match a real service base; a same-platform peer's base is addressable, a different-capability base
/// falls through to full-local). The schema/analyzer/config/toolchain are taken from the overlay ROW (their
/// build-time values), which equal the running binary's values at build time, so the reconstructed hash is
/// byte-identical to the one the reconciler probed and the peer published.
/// </para>
/// </summary>
public static class BaseSnapshotIdentity
{
    public static SnapshotIdentity ForRemoteOverlay(SnapshotRow overlay, string repositoryRemoteUrl, string commitSha) =>
        new()
        {
            RepositoryRemoteUrl = repositoryRemoteUrl,
            CommitSha = commitSha,
            TreeSha = overlay.TreeSha,
            SchemaVersion = overlay.SchemaVersion,
            AnalyzerVersion = overlay.AnalyzerVersion,
            ConfigHash = overlay.ConfigHash,
            ToolchainFingerprint = overlay.ToolchainFingerprint ?? Core.ToolchainFingerprint.Current,
            // The committed base is the CLEAN commit state; mirroring BuildRemoteBaseIdentity, the peer's
            // committed base is addressed under the producing (same-platform) node's default capability.
            WorkingTreeDelta = null,
            IsOverlay = false,
            CapabilityFingerprint = WorkerCapability.LocalDefault.Fingerprint
        };
}
