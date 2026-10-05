using Sextant.Core;
using Sextant.Store;
using System.Text.Json.Serialization;

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
    /// OPTIONAL control-plane assertion of whether <see cref="BranchName"/> is the repository's default
    /// branch (issue #104). Decoupled from the legacy "no branch name ⇒ default" heuristic: a coordinator
    /// (ProcessStack's <c>SextantEnsureSnapshot</c>) names the branch it advances even in the NORMAL
    /// indexing case, so deriving default-ness from a null branch name never marks that named branch
    /// default — and the multi-tenant read selector (<c>GetSelectedSnapshotIdForRepository</c>), which
    /// requires <c>is_default = 1</c>, then fails closed on a fully populated catalog. When present this
    /// value is authoritative for which branch owns the repository's default pointer. When <c>null</c> (the
    /// local CLI/daemon path and any caller that omits it) it falls back via
    /// <see cref="ResolveIsDefaultBranch"/> to the historical <c>BranchName is null</c> rule, so the local
    /// flow is byte-identical. Deliberately NOT folded into <see cref="ToIdentity"/> — the same committed
    /// state is the SAME immutable snapshot regardless of default designation. Serializes as
    /// <c>default_branch</c> (matching the contribute path's query parameter).
    /// </summary>
    [JsonPropertyName("default_branch")]
    public bool? IsDefaultBranch { get; init; }

    /// <summary>
    /// Resolves whether this ensure advances the repository's default branch: the explicit
    /// <see cref="IsDefaultBranch"/> when supplied, else the legacy <c>BranchName is null</c> heuristic so
    /// an omitted flag preserves the pre-#104 behavior byte-for-byte (the local/single-repo path).
    /// </summary>
    public bool ResolveIsDefaultBranch() => IsDefaultBranch ?? (BranchName is null);

    /// <summary>
    /// An OPTIONAL compare-and-swap guard on the branch pointer (SVC-6): the commit the caller expects the
    /// branch to point at right now, typically a push's <c>before</c>. <c>""</c> or an all-zero SHA means
    /// "the branch must have no pointer yet" (a branch create). The branch advances only when the pointer's
    /// commit equals this value, when the pointer's target is unusable (not complete, or reclaimed by
    /// retention), or when the branch has no pointer and this value is empty/all zeros. Otherwise the
    /// snapshot is attached and the branch is left untouched. It cannot be combined with
    /// <see cref="BranchHeadSequence"/> (<c>conflicting_branch_guards</c>). Deliberately NOT folded into
    /// <see cref="ToIdentity"/>. Serializes as <c>expected_head_commit</c>.
    /// </summary>
    public string? ExpectedHeadCommit { get; init; }

    /// <summary>
    /// OPTIONAL and informational (SVC-6): the caller reports that the push was a force-push. It is recorded
    /// in the ensure audit row and does NOT bypass the <see cref="ExpectedHeadCommit"/> CAS, because the CAS
    /// on the push's <c>before</c> already orders the events. Deliberately NOT folded into
    /// <see cref="ToIdentity"/>. Serializes as <c>forced</c>.
    /// </summary>
    public bool? Forced { get; init; }

    /// <summary>
    /// OPTIONAL branch-pointer mode (SVC-7): <c>"advance"</c> (the default, also when omitted) or
    /// <c>"none"</c>. <c>none</c> publishes or attaches the snapshot but creates or moves NO branch pointer
    /// (for example a pull-request head or a historical commit), and wins over every other branch guard.
    /// Matched case-insensitively; any other value is refused with <c>invalid_branch_update</c>.
    /// Deliberately NOT folded into <see cref="ToIdentity"/>. Serializes as <c>branch_update</c>.
    /// </summary>
    public string? BranchUpdate { get; init; }

    /// <summary>True when <see cref="BranchUpdate"/> is <c>none</c>: no branch pointer is created or moved.</summary>
    [JsonIgnore]
    public bool SuppressesBranchUpdate =>
        string.Equals(BranchUpdate, BranchUpdateMode.None, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The intake reason code when the branch guards are malformed, or <c>null</c> when they are valid:
    /// <c>invalid_branch_update</c> for an unknown <see cref="BranchUpdate"/>, and
    /// <c>conflicting_branch_guards</c> when both <see cref="ExpectedHeadCommit"/> and
    /// <see cref="BranchHeadSequence"/> are present. <c>branch_update: none</c> takes precedence over both
    /// guards (SVC-6/7 precedence row 0: they are ignored), so it is never a conflict.
    /// </summary>
    public string? BranchGuardProblem()
    {
        if (BranchUpdate is not null
            && !string.Equals(BranchUpdate, BranchUpdateMode.Advance, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(BranchUpdate, BranchUpdateMode.None, StringComparison.OrdinalIgnoreCase))
            return BranchGuardReason.InvalidBranchUpdate;
        if (!SuppressesBranchUpdate && ExpectedHeadCommit is not null && BranchHeadSequence is not null)
            return BranchGuardReason.ConflictingBranchGuards;
        return null;
    }

    /// <summary>
    /// The intake reason code when this ensure asks for more branch control than a user caller (a verified
    /// <c>act=user</c> assertion) may have, or <c>null</c> when it stays within the user bounds (SX-6d, issue #198).
    /// A user may never claim the default branch (<c>default_branch: true</c>:
    /// <see cref="BranchGuardReason.DefaultBranchNotAllowed"/>) or send a head sequence (a large one would move the
    /// pointer and block every later sequenced advance: <see cref="BranchGuardReason.HeadSequenceNotAllowed"/>). Unless
    /// the ensure is <c>branch_update: none</c>, it must carry the <see cref="ExpectedHeadCommit"/> CAS
    /// (<see cref="BranchGuardReason.BranchGuardRequired"/>) and name its branch
    /// (<see cref="BranchGuardReason.BranchRequired"/>), because an ensure that names no branch claims the default by
    /// the legacy <see cref="ResolveIsDefaultBranch"/> heuristic. The checks read the request only, never the catalog,
    /// in this fixed order. Application callers and assertion-less control calls are not subject to them.
    /// </summary>
    public string? UserCallerBranchProblem()
    {
        if (IsDefaultBranch == true)
            return BranchGuardReason.DefaultBranchNotAllowed;
        if (BranchHeadSequence is not null)
            return BranchGuardReason.HeadSequenceNotAllowed;
        if (SuppressesBranchUpdate)
            return null;
        if (ExpectedHeadCommit is null)
            return BranchGuardReason.BranchGuardRequired;
        if (string.IsNullOrWhiteSpace(BranchName))
            return BranchGuardReason.BranchRequired;
        return null;
    }

    /// <summary>
    /// Set by <see cref="SnapshotService"/> — never bound from the wire (internal + ignored) — when this run is
    /// the LAST provisioning attempt the job-wide bound allows (issue #125). A clone-mode checkout then degrades
    /// a still-TRANSIENT submodule failure (an unreachable host, a persistent 5xx) to an unpopulated submodule
    /// (coverage partial with the reason) instead of failing the whole checkout, and a failed upgrade of a
    /// cached pre-#125 checkout keeps serving the cached tree. Earlier attempts keep the transient retry.
    /// Deliberately NOT folded into <see cref="ToIdentity"/>.
    /// </summary>
    [JsonIgnore]
    internal bool IsFinalProvisioningAttempt { get; init; }

    /// <summary>
    /// Set by the host — never bound from the wire — for a caller that may not pick the repository's default
    /// branch (a verified <c>act=user</c> caller; issue #199). Such an ensure gets the issue #104 first-branch
    /// default (the repository has no default yet) only for the branch the REMOTE names as its default, which
    /// the service looks up itself (<see cref="IRemoteDefaultBranchResolver"/>) and never takes from the request;
    /// when that lookup is unavailable or fails, the branch is created without the default (fail closed).
    /// <c>false</c> (application callers, assertion-less control calls, direct callers) keeps today's behavior.
    /// Deliberately NOT folded into <see cref="ToIdentity"/>.
    /// </summary>
    [JsonIgnore]
    public bool RestrictsImplicitDefault { get; init; }

    /// <summary>
    /// The remote's default branch as the service itself resolved it for a <see cref="RestrictsImplicitDefault"/>
    /// ensure, or <c>null</c> when it was not needed or could not be determined. Set by <see cref="SnapshotService"/>
    /// only (internal + ignored), which overwrites any value a direct caller supplied.
    /// </summary>
    [JsonIgnore]
    internal string? VerifiedRemoteDefaultBranch { get; init; }

    /// <summary>
    /// Whether the issue #104 first-branch safety net may make <paramref name="branchName"/> its repository's
    /// default for this ensure: always, unless <see cref="RestrictsImplicitDefault"/>, in which case only when the
    /// service verified that <paramref name="branchName"/> is the remote's default branch (issue #199).
    /// </summary>
    public bool AllowsImplicitDefault(string branchName) =>
        !RestrictsImplicitDefault
        || (VerifiedRemoteDefaultBranch is { } remoteDefault
            && string.Equals(remoteDefault, branchName, StringComparison.Ordinal));

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
    /// <paramref name="sdkPinPolicy"/> is the node's non-default SDK-pin policy component (issue #113,
    /// <see cref="ServiceOptions.SdkPinIdentityComponent"/>), the SAME value the worker publishes under. Null
    /// (the default override-on policy, tests, local) leaves the identity byte-identical.
    /// <paramref name="restorePolicy"/> is the node's non-default NuGet restore policy component
    /// (<see cref="ServiceOptions.RestoreIdentityComponent"/>), again the SAME value the worker publishes under.
    /// </summary>
    public SnapshotIdentity ToIdentity(
        string? fallbackConfigHash = null, string? fallbackCapability = null, string? sdkPinPolicy = null,
        string? restorePolicy = null) => new()
    {
        RepositoryRemoteUrl = RepositoryRemoteUrl,
        CommitSha = CommitSha,
        TreeSha = TreeSha,
        SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = ConfigHash ?? fallbackConfigHash,
        ToolchainFingerprint = ToolchainFingerprint.Current,
        CapabilityFingerprint = fallbackCapability,
        SdkPinPolicy = sdkPinPolicy,
        RestorePolicy = restorePolicy
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

    /// <summary>
    /// Why the job is not complete (the job's recorded reason): the partial-coverage reasons for a
    /// <c>partial</c> job, the error for a <c>failed</c>/<c>unsupported</c> one; null for a complete job.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The durable checkout coverage recorded for the snapshot (issue #119); null when none was recorded
    /// (no snapshot, or a snapshot from a path that does not compute coverage).
    /// </summary>
    public SnapshotCoverage? Coverage { get; init; }

    /// <summary>
    /// Whether THIS ensure moved the requested branch's pointer onto <see cref="SnapshotId"/> (SVC-6):
    /// <c>true</c> when the pointer now targets the snapshot and targeted something else (or nothing) before
    /// the decision, <c>false</c> when the branch guards left it where it was (a CAS mismatch, a lower
    /// sequence, <c>branch_update: none</c>, or the pointer already targeted the snapshot). <c>null</c> when
    /// the ensure made no branch decision (a non-terminal or failed job, or an attach to another caller's
    /// in-flight production).
    /// </summary>
    public bool? BranchAdvanced { get; init; }
}

/// <summary>The <see cref="EnsureSnapshotRequest.BranchUpdate"/> values (SVC-7).</summary>
public static class BranchUpdateMode
{
    public const string Advance = "advance";
    public const string None = "none";
}

/// <summary>Reason codes for refused branch-guard requests and branch retirement (SVC-6/7).</summary>
public static class BranchGuardReason
{
    public const string ConflictingBranchGuards = "conflicting_branch_guards";
    public const string InvalidBranchUpdate = "invalid_branch_update";
    public const string BranchRequired = "branch_required";
    public const string HeadMismatch = "head_mismatch";
    public const string DefaultBranch = "default_branch";

    /// <summary>A user caller's ensure sent <c>default_branch: true</c> (SX-6d).</summary>
    public const string DefaultBranchNotAllowed = "default_branch_not_allowed";

    /// <summary>A user caller's ensure sent a <c>branch_head_sequence</c> (SX-6d).</summary>
    public const string HeadSequenceNotAllowed = "branch_head_sequence_not_allowed";

    /// <summary>A user caller's ensure carried neither <c>expected_head_commit</c> nor <c>branch_update: none</c> (SX-6d).</summary>
    public const string BranchGuardRequired = "branch_guard_required";
}

/// <summary>A request to retire (delete) a repository branch's pointer (SVC-6).</summary>
public sealed record RetireBranchRequest
{
    public required string Repository { get; init; }
    public required string Branch { get; init; }

    /// <summary>
    /// OPTIONAL compare-and-swap guard: retire only while the branch still points at this commit (for
    /// example a delete push's <c>before</c>), so a newer push that re-created the branch is never
    /// retired. <c>""</c>/all zeros means "the branch has no pointer". Same rules as
    /// <see cref="EnsureSnapshotRequest.ExpectedHeadCommit"/>.
    /// </summary>
    public string? ExpectedHeadCommit { get; init; }
}

/// <summary>The outcome of a branch retirement (SVC-6).</summary>
public sealed record RetireBranchResult
{
    /// <summary>True when the branch row was deleted; false when it did not exist (idempotent) or was refused.</summary>
    public required bool Retired { get; init; }

    /// <summary>
    /// The refusal reason (<see cref="BranchGuardReason.HeadMismatch"/> or
    /// <see cref="BranchGuardReason.DefaultBranch"/>), or <c>null</c> when the request was not refused.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// A resolved branch head for <c>/control/resolve</c> (SVC-6): the published snapshot plus the branch it
/// was resolved through and the snapshot's commit.
/// </summary>
public sealed record ResolvedBranchHead
{
    public required SnapshotRow Snapshot { get; init; }
    public required string Branch { get; init; }
    public required bool IsDefault { get; init; }
    public long? HeadSequence { get; init; }
    public string? CommitSha { get; init; }

    /// <summary>The snapshot's durable coverage record (issue #119), or null when none was recorded.</summary>
    public SnapshotCoverage? Coverage { get; init; }

    /// <summary>
    /// The snapshot identity hash an ensure of this branch's commit would compute on this node NOW — the default
    /// profile/configuration (no <c>config_hash</c>), no tree sha, and the node's present schema, analyzer,
    /// toolchain, capability, SDK-pin and restore components — or null when it cannot be computed (the snapshot
    /// records no commit). The repository URL is spelled as the snapshot's own ensure, the resolve request, or the
    /// catalog spelled it, whichever reproduces the snapshot's identity (a spelling variant alone is never stale);
    /// when none does, as the resolve request spelled it. Serialized as <c>current_identity_hash</c>.
    /// </summary>
    public string? CurrentIdentityHash { get; init; }

    /// <summary>
    /// Whether the pointed snapshot is current under this node's present identity: its identity hash equals
    /// <see cref="CurrentIdentityHash"/>. False after an identity change (an <c>AnalyzerVersion</c> or schema bump, a
    /// toolchain or policy change), when an ensure of the same commit would build a NEW snapshot; an
    /// <c>expected_head_commit</c> CAS ensure of that commit then re-points the branch at it. Null when
    /// <see cref="CurrentIdentityHash"/> is (serialized as an absent <c>identity_current</c>, never <c>null</c>).
    /// </summary>
    public bool? IdentityCurrent => CurrentIdentityHash is { } current
        ? string.Equals(current, Snapshot.IdentityHash, StringComparison.Ordinal)
        : null;
}

/// <summary>
/// The full status of a snapshot job for the status API, including per-project diagnostics
/// (acceptance criterion 5) so a partial/failed/unsupported outcome is explainable.
/// </summary>
public sealed record JobStatusResult
{
    public required SnapshotJobRow Job { get; init; }
    public IReadOnlyList<SnapshotJobDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>The durable checkout coverage recorded for the job's snapshot (issue #119), or null.</summary>
    public SnapshotCoverage? Coverage { get; init; }
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

    /// <summary>
    /// The checkout coverage of the published snapshot (issue #119), or null when the worker does not
    /// compute coverage. Informational on the result: the durable record is the one the orchestrator
    /// persisted in the publish transaction.
    /// </summary>
    public SnapshotCoverage? Coverage { get; init; }

    public static SnapshotWorkResult Complete(
        long snapshotId, IReadOnlyList<ProjectOutcome>? projects = null, SnapshotCoverage? coverage = null) =>
        new() { Status = SnapshotJobStatus.Complete, SnapshotId = snapshotId, Projects = projects ?? [], Coverage = coverage };

    /// <summary>
    /// A servable-but-incomplete outcome (issues #109/#119): a COMPLETE snapshot was published (the loadable
    /// projects across every selected solution were indexed), but the checkout is not fully covered —
    /// solutions/projects/submodules were left out or skipped with a reason. The validator still requires
    /// that a complete snapshot was actually published — Partial never masks an empty/absent publish — so
    /// PARTIAL coverage is recorded honestly instead of being reported as COMPLETE.
    /// </summary>
    public static SnapshotWorkResult Partial(
        long snapshotId, string message, IReadOnlyList<ProjectOutcome>? projects = null, SnapshotCoverage? coverage = null) =>
        new()
        {
            Status = SnapshotJobStatus.Partial, SnapshotId = snapshotId, Error = message, Projects = projects ?? [],
            Coverage = coverage
        };

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
