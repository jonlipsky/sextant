using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>
/// A request to ensure a committed-branch snapshot exists in the service's durable catalog. The service
/// folds these fields with its OWN schema/analyzer/toolchain fingerprint into the Phase-9
/// <see cref="SnapshotIdentity"/>, so two requests for the same committed state attach to the ONE durable
/// job/result (acceptance criterion 1) and a request under an incompatible toolchain resolves to a
/// distinct identity (never a stale reuse).
/// </summary>
public sealed record EnsureSnapshotRequest
{
    public required string RepositoryRemoteUrl { get; init; }
    public required string CommitSha { get; init; }
    public string? TreeSha { get; init; }
    public string? BranchName { get; init; }

    /// <summary>The Phase-8 profile/feature configuration hash the worker will index under.</summary>
    public string? ConfigHash { get; init; }

    /// <summary>
    /// An OPTIONAL monotonic per-branch head sequence supplied by the control plane (issue #84). The
    /// service ensures EVERY delivered commit — including out-of-order/older ones — so an unconditional
    /// branch advance could transiently REGRESS the data-plane branch pointer. When present, the service
    /// ensure path advances the branch pointer only when this sequence is strictly greater than the one
    /// already recorded for the branch (forward-only); a lower/equal sequence still ensures/attaches the
    /// immutable, content-addressed snapshot but never moves the pointer. When <c>null</c> (the local
    /// CLI/daemon path) the advance stays unconditional — byte-identical to the pre-#84 behavior. It is
    /// deliberately NOT folded into <see cref="ToIdentity"/>: the same committed state re-ensured under a
    /// different sequence is the SAME immutable snapshot. Serializes as <c>branch_head_sequence</c>.
    /// </summary>
    public long? BranchHeadSequence { get; init; }

    /// <summary>
    /// Builds the durable identity for this request using the service-side schema/analyzer/toolchain.
    /// A committed-branch ensure is always a clean, non-overlay identity (no working-tree delta). When the
    /// request omits <see cref="ConfigHash"/> the caller's <paramref name="fallbackConfigHash"/> (the
    /// node's own profile/feature hash) is folded in instead, so the identity matches the hash the live
    /// worker's orchestrator publishes under (<c>IndexProfileDescriptor.ConfigurationHash</c>) — otherwise
    /// the service's idempotent lookup would never find the worker's published snapshot.
    /// <paramref name="fallbackCapability"/> is the producing NODE's default worker-capability fingerprint
    /// (Phase 15): the client cannot know the post-routing capability, so the request identity uses the
    /// node's default capability — the SAME value the worker stamps into the published snapshot — keeping
    /// request identity == published identity so idempotent attachment is preserved. Null in tests/local.
    /// </summary>
    public SnapshotIdentity ToIdentity(string? fallbackConfigHash = null, string? fallbackCapability = null) => new()
    {
        RepositoryRemoteUrl = RepositoryRemoteUrl,
        CommitSha = CommitSha,
        TreeSha = TreeSha,
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = ConfigHash ?? fallbackConfigHash,
        ToolchainFingerprint = ToolchainFingerprint.Current,
        CapabilityFingerprint = fallbackCapability
    };
}

/// <summary>The result of an ensure-snapshot request: the attached durable job plus its resolved status.</summary>
public sealed record EnsureSnapshotResult
{
    public required long JobId { get; init; }
    public required string IdentityHash { get; init; }
    public required string Status { get; init; }
    public long? SnapshotId { get; init; }

    /// <summary>True when this request ATTACHED to an already-existing job rather than creating one (criterion 1).</summary>
    public required bool Attached { get; init; }
}

/// <summary>
/// The full status of a snapshot job for the status API, including per-project diagnostics
/// (acceptance criterion 5) so a partial/failed/unsupported outcome is explainable.
/// </summary>
public sealed record JobStatusResult
{
    public required SnapshotJobRow Job { get; init; }
    public IReadOnlyList<SnapshotJobDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>A single per-project outcome a worker reports back for job diagnostics.</summary>
public sealed record ProjectOutcome
{
    public string? ProjectCanonicalId { get; init; }
    public string? ProjectPath { get; init; }
    public required string Severity { get; init; }
    public string? Code { get; init; }
    public required string Message { get; init; }

    public SnapshotJobDiagnostic ToDiagnostic(long jobId) => new()
    {
        JobId = jobId,
        ProjectCanonicalId = ProjectCanonicalId,
        ProjectPath = ProjectPath,
        Severity = Severity,
        Code = Code,
        Message = Message
    };
}

/// <summary>
/// The outcome of a worker producing a snapshot: the terminal job status, the published snapshot id (when
/// the worker published one), an optional overall error, and per-project diagnostics. A worker that cannot
/// support the request returns <see cref="SnapshotJobStatus.Unsupported"/>; a worker that published every
/// project returns <see cref="SnapshotJobStatus.Complete"/>; a worker that published with degraded/skipped
/// projects returns <see cref="SnapshotJobStatus.Partial"/> plus diagnostics (acceptance criterion 5).
/// </summary>
public sealed record SnapshotWorkResult
{
    public required string Status { get; init; }
    public long? SnapshotId { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ProjectOutcome> Projects { get; init; } = [];

    public static SnapshotWorkResult Complete(long snapshotId, IReadOnlyList<ProjectOutcome>? projects = null) =>
        new() { Status = SnapshotJobStatus.Complete, SnapshotId = snapshotId, Projects = projects ?? [] };

    public static SnapshotWorkResult Unsupported(string message, IReadOnlyList<ProjectOutcome>? projects = null) =>
        new() { Status = SnapshotJobStatus.Unsupported, Error = message, Projects = projects ?? [] };

    public static SnapshotWorkResult Failed(string message, IReadOnlyList<ProjectOutcome>? projects = null) =>
        new() { Status = SnapshotJobStatus.Failed, Error = message, Projects = projects ?? [] };
}

/// <summary>
/// The seam between the data plane and whatever actually PRODUCES a snapshot. The service stages worker
/// output, validates it, and publishes it through the catalog; the worker itself is pluggable so the
/// core service stays decoupled from any specific execution strategy (a local in-process indexer today,
/// a ProcessStack-scheduled remote worker in Phase 14 — the dependency points OUTWARD, never into core).
/// Implementations receive a per-job scratch directory that is SEPARATE from the persistent volumes.
/// </summary>
public interface ISnapshotWorker
{
    Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request,
        string identityHash,
        string scratchDir,
        CancellationToken cancellationToken);
}
