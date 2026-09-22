using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// The Phase-11 federated read planner, resolved ONCE per MCP request. It pins a single
/// (base snapshot, overlay generation) pair by reading the selected generation exactly once (issue #42),
/// derives the effective <see cref="SnapshotReadScope"/> for the requested <see cref="FederationMode"/>,
/// runs the read-time compatibility gate against that pinned base (issue #41), and captures the read
/// authorization decision (criterion 6). Every store a tool opens is scoped with the SAME
/// <see cref="Scope"/>, and every response stamps the SAME <see cref="Provenance"/>, so a concurrent
/// publish mid-request can never splice two states into one answer (criteria 1/2/5).
/// </summary>
public sealed class FederatedReadContext
{
    private FederatedReadContext(
        SnapshotReadScope scope, SnapshotProvenance? provenance, ReadAuthorization authorization)
    {
        Scope = scope;
        Provenance = provenance;
        Authorization = authorization;
    }

    /// <summary>The pinned read scope reused by every store in this request.</summary>
    public SnapshotReadScope Scope { get; }

    /// <summary>
    /// The provenance stamped into every response's <c>meta.snapshot</c> for this request, or null for a
    /// pure legacy/direct-seed database with no selected snapshot (so its meta stays byte-identical to
    /// pre-Phase-11 — the Phase-9 legacy-parity invariant).
    /// </summary>
    public SnapshotProvenance? Provenance { get; }

    /// <summary>The authorization verdict for this read (criterion 6). Denials become a structured error.</summary>
    public ReadAuthorization Authorization { get; }

    /// <summary>
    /// Resolves the read context for one MCP request. Reads the selected generation once and reuses it for
    /// scope, provenance, and the compatibility verdict. <paramref name="mode"/> selects the federation
    /// partition (default transparent federation); <paramref name="authorizer"/> and
    /// <paramref name="compatibility"/> are injectable for tests and future remote enforcement.
    /// </summary>
    public static FederatedReadContext Resolve(
        IndexDatabase db,
        FederationMode mode = FederationMode.Federated,
        IReadAuthorizer? authorizer = null,
        CompatibilityInputs? compatibility = null)
    {
        var conn = db.GetConnection();
        var snapshots = new SnapshotStore(conn);

        // ONE read of the selected generation pins this request (issue #42).
        var selected = snapshots.GetSelectedSnapshotRow();
        var authorization = (authorizer ?? AllowAllReadAuthorizer.Instance).Authorize(selected);

        // Fail closed: on denial the read gets a DENY-ALL scope (matches no rows), so even a caller that
        // reads Scope without consulting Authorization leaks nothing; the gate additionally turns the
        // denial into a structured meta.error. No provenance/scope work is done for a denied read.
        if (!authorization.Allowed)
            return new FederatedReadContext(SnapshotReadScope.DenyAll, provenance: null, authorization);

        var scope = ResolveScope(snapshots, selected, mode);
        var provenance = selected == null ? null : BuildProvenance(snapshots, selected, mode, compatibility);
        return new FederatedReadContext(scope, provenance, authorization);
    }

    private static SnapshotReadScope ResolveScope(
        SnapshotStore snapshots, SnapshotRow? selected, FederationMode mode)
    {
        if (selected == null)
        {
            // Reproduce ForSelected's legacy fallback WITHOUT a second selected-id read: a single-repo DB
            // mid-rebuild pins to legacy rows, a pure legacy/multi-repo DB stays unscoped (byte-identical).
            return snapshots.HasUnselectedSnapshotProjectRows()
                ? SnapshotReadScope.LegacyPinned
                : SnapshotReadScope.Unscoped;
        }

        return mode switch
        {
            // The committed base the overlay layers on (its own id when the selected row is itself a base).
            FederationMode.BaseOnly => SnapshotReadScope.ForBaseOf(
                selected.IsOverlay ? selected.BaseSnapshotId : selected.Id),

            // Only the project-versions freshly re-extracted into this generation (the working-tree delta).
            FederationMode.OverlayOnly => SnapshotReadScope.ForOverlayLocalOnly(selected.Id),

            // Transparent federation: the selected generation's full membership (fresh overlay rows +
            // shared unchanged base rows via snapshot_projects) — Phase 10 already shadows touched projects.
            _ => new SnapshotReadScope(selected.Id)
        };
    }

    private static SnapshotProvenance BuildProvenance(
        SnapshotStore snapshots, SnapshotRow selected, FederationMode mode, CompatibilityInputs? compatibility)
    {
        // The committed base the read rests on: the overlay's base, or the selected row itself.
        var baseId = selected.IsOverlay ? selected.BaseSnapshotId : selected.Id;
        var baseRow = selected.IsOverlay && selected.BaseSnapshotId is { } bid
            ? snapshots.GetById(bid) ?? selected
            : selected;

        // #41: check the committed base (the generation that can predate the running binary) at READ time.
        var incompatibilities = ReadCompatibility.Evaluate(baseRow, compatibility ?? CompatibilityInputs.Current);

        return new SnapshotProvenance
        {
            BaseSnapshotId = baseId,
            BaseCommit = snapshots.GetCommitSha(baseRow.CommitId),
            OverlayGeneration = selected.IsOverlay ? selected.Id : null,
            IsOverlay = selected.IsOverlay,
            Completeness = selected.Status,
            Scope = mode switch
            {
                FederationMode.BaseOnly => "base_only",
                FederationMode.OverlayOnly => "overlay_only",
                _ => "federated"
            },
            Dirty = selected.WorkingTreeDelta is { Length: > 0 },
            FallbackReason = selected.FallbackReason,
            Compatible = incompatibilities.Count == 0,
            Incompatibilities = incompatibilities.Count == 0 ? null : incompatibilities,
            Freshness = selected.PublishedAt ?? selected.CreatedAt
        };
    }
}
