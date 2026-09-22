using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// The federation view a read is served under (Phase 11 diagnostic modes). <see cref="Federated"/> is the
/// default transparent union of the local overlay over its committed base; the other two are explicit
/// diagnostic partitions used to prove shadowing in tests and to answer "what did my working tree change".
/// </summary>
public enum FederationMode
{
    /// <summary>Overlay generation shadowing its committed base — the transparent default.</summary>
    Federated,

    /// <summary>Only the committed base snapshot (the pre-edit state the overlay layers on).</summary>
    BaseOnly,

    /// <summary>Only the project-versions freshly re-extracted into the overlay (the working-tree delta).</summary>
    OverlayOnly
}

/// <summary>Parses the optional MCP <c>federation</c> diagnostic parameter into a <see cref="FederationMode"/>.</summary>
public static class FederationModes
{
    /// <summary>
    /// Maps the wire value to a mode; null/empty/unknown falls back to transparent <see
    /// cref="FederationMode.Federated"/> so an unrecognized value never silently changes result semantics.
    /// </summary>
    public static FederationMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "base_only" or "base" => FederationMode.BaseOnly,
        "overlay_only" or "overlay" or "local" or "local_only" => FederationMode.OverlayOnly,
        _ => FederationMode.Federated
    };
}

/// <summary>
/// One dimension on which a served snapshot's build inputs disagree with the running binary/config
/// (issue #41). Surfaced in <c>meta.snapshot.incompatibilities</c> so a federated read across a base
/// snapshot built by an older toolchain is annotated rather than silently mixed (criterion 4 provenance,
/// criterion 3 offline freshness).
/// </summary>
public sealed record IncompatibilityInfo(string Dimension, string Expected, string Actual);

/// <summary>
/// The running binary/config fingerprint a served snapshot is checked against at READ time (issue #41).
/// Defaults to the live process values; tests inject fabricated values to simulate binary/config drift
/// without rebuilding an index. Read-time enforcement complements the Phase-8 index-time config hash:
/// the index-time hash only guards the generation being written, so a later read federating an older base
/// snapshot needs its own compatibility verdict.
/// </summary>
public sealed record CompatibilityInputs(int SchemaVersion, string AnalyzerVersion, string ToolchainFingerprint)
{
    public static CompatibilityInputs Current => new(
        IndexDatabase.LatestSchemaVersion,
        IndexConfigurationHash.AnalyzerVersion,
        Core.ToolchainFingerprint.Current);
}

/// <summary>
/// Evaluates read-time compatibility of a committed base snapshot against the running binary/config
/// (issue #41). A schema DOWNGRADE (snapshot newer than the binary) or an analyzer/toolchain mismatch is
/// reported as a structured <see cref="IncompatibilityInfo"/> so a federated read annotates rather than
/// hides the drift. A schema that is merely OLDER than the current binary is caught earlier by the
/// Phase-7 rebuild gate (<see cref="IndexDatabase.CheckReadiness"/>); here we only flag a snapshot the
/// running binary could MISread. A null stored dimension (a pre-fingerprint snapshot) is treated as
/// unknown, not a mismatch, so older complete generations keep serving without crying wolf.
/// </summary>
public static class ReadCompatibility
{
    public static IReadOnlyList<IncompatibilityInfo> Evaluate(SnapshotRow snapshot, CompatibilityInputs running)
    {
        var issues = new List<IncompatibilityInfo>();

        // A snapshot claiming a NEWER schema than this binary understands cannot be read correctly.
        if (snapshot.SchemaVersion > running.SchemaVersion)
            issues.Add(new IncompatibilityInfo(
                "schema", running.SchemaVersion.ToString(), snapshot.SchemaVersion.ToString()));

        if (!string.Equals(snapshot.AnalyzerVersion, running.AnalyzerVersion, StringComparison.Ordinal))
            issues.Add(new IncompatibilityInfo(
                "analyzer", running.AnalyzerVersion, snapshot.AnalyzerVersion));

        if (snapshot.ToolchainFingerprint is { Length: > 0 } toolchain &&
            !string.Equals(toolchain, running.ToolchainFingerprint, StringComparison.Ordinal))
            issues.Add(new IncompatibilityInfo(
                "toolchain", running.ToolchainFingerprint, toolchain));

        return issues;
    }
}

/// <summary>The outcome of a read authorization decision (criterion 6).</summary>
public sealed record ReadAuthorization(bool Allowed, string? Reason)
{
    public static readonly ReadAuthorization Allow = new(true, null);
    public static ReadAuthorization Deny(string reason) => new(false, reason);
}

/// <summary>
/// Authorizes a read against the selected generation (criterion 6: fail CLOSED). The default local
/// implementation allows everything; the seam exists so a later remote/multi-tenant phase (13/17) can
/// deny a read and have the gate surface a structured <c>meta.error</c> — never an empty successful
/// result an agent would misread as "no matches".
/// </summary>
public interface IReadAuthorizer
{
    ReadAuthorization Authorize(SnapshotRow? selected);
}

/// <summary>The permissive default authorizer for a single-tenant local index.</summary>
public sealed class AllowAllReadAuthorizer : IReadAuthorizer
{
    public static readonly AllowAllReadAuthorizer Instance = new();
    public ReadAuthorization Authorize(SnapshotRow? selected) => ReadAuthorization.Allow;
}

/// <summary>
/// The resolved provenance of a federated read, stamped into every MCP response's <c>meta.snapshot</c>
/// block (Phase 11, criterion 4). Records the committed base snapshot and its commit, the overlay
/// generation (when the read rests on a Phase-10 overlay), completeness, the federation partition the
/// read used, working-tree dirtiness, any full-local fallback reason, the read-time compatibility verdict
/// (issue #41), and the served generation's freshness. Produced ONCE per MCP request by
/// <see cref="FederatedReadContext"/> so every sub-query shares one pinned generation (issue #42).
/// </summary>
public sealed record SnapshotProvenance
{
    public long? BaseSnapshotId { get; init; }
    public string? BaseCommit { get; init; }
    public long? OverlayGeneration { get; init; }
    public bool IsOverlay { get; init; }
    public string Completeness { get; init; } = "complete";
    public string Scope { get; init; } = "federated";
    public bool Dirty { get; init; }
    public string? FallbackReason { get; init; }
    public bool Compatible { get; init; } = true;
    public IReadOnlyList<IncompatibilityInfo>? Incompatibilities { get; init; }
    public long Freshness { get; init; }
}
