namespace Sextant.Core;

/// <summary>
/// The repository/commit coordinates a full index publishes its snapshot against. The orchestrator
/// resolves this from git for the indexed working tree; tests (and future non-git hosts) may supply it
/// explicitly so an immutable snapshot can be built without a real git checkout. When no context can be
/// resolved (no git root / no commit) the orchestrator falls back to the pre-Phase-9 mutable-row path,
/// so existing single-repo local behavior and the temp-dir test suite are unchanged.
/// </summary>
public sealed record SnapshotContext
{
    /// <summary>The normalized repository remote URL (or a <c>local://</c> identity).</summary>
    public required string RepositoryRemoteUrl { get; init; }

    /// <summary>The commit SHA the working tree is at.</summary>
    public required string CommitSha { get; init; }

    /// <summary>The commit's tree SHA (part of snapshot identity; null when unavailable).</summary>
    public string? TreeSha { get; init; }

    /// <summary>The branch name whose pointer this index advances (e.g. the checked-out branch).</summary>
    public required string BranchName { get; init; }

    /// <summary>Whether <see cref="BranchName"/> is the repository's default/current-selected branch.</summary>
    public bool IsDefaultBranch { get; init; } = true;

    /// <summary>
    /// The Phase-15 worker-capability fingerprint of the worker producing this snapshot, or <c>null</c>
    /// for a local/single-node run that does not route. When set it is folded into
    /// <see cref="SnapshotIdentity.CapabilityFingerprint"/> and recorded in provenance; when null the
    /// snapshot identity stays byte-identical to the pre-Phase-15 path (CRITICAL 2: the local stdio/MCP
    /// and single-node paths require zero routing infrastructure).
    /// </summary>
    public string? CapabilityFingerprint { get; init; }

    /// <summary>
    /// The service worker's non-default <c>global.json</c> SDK-pin policy (issue #113), or <c>null</c>. When
    /// set it is folded into <see cref="SnapshotIdentity.SdkPinPolicy"/> of every snapshot this run publishes
    /// (the repository snapshot and its submodule provider snapshots). That way a snapshot built with the
    /// override disabled is never reused once it is enabled, nor the other way round. <c>null</c> for the
    /// default override-on policy and for every local CLI/daemon run, which keeps the identity byte-identical.
    /// </summary>
    public string? SdkPinPolicy { get; init; }

    /// <summary>
    /// An OPTIONAL monotonic per-branch head sequence for the SERVICE ensure path (issue #84). When set,
    /// <c>AdvanceBranchToSnapshot</c> advances the branch pointer only when this value is strictly greater
    /// than the sequence already recorded for the branch (forward-only), so an out-of-order/older service
    /// ensure never regresses the pointer. <c>null</c> (the local CLI/daemon path, and every non-service
    /// caller) preserves the unconditional-advance behavior byte-for-byte.
    /// </summary>
    public long? BranchHeadSequence { get; init; }

    /// <summary>
    /// The checkout coverage the producing worker computed for this snapshot (issue #119), or <c>null</c>
    /// when the caller does not compute coverage (the local CLI/daemon path). For a baseless remote-base
    /// overlay (#108) the local reconciler sets it to the peer-reported coverage of the committed base.
    /// When set, the orchestrator
    /// records it (migration 022) inside the SAME transaction that publishes the snapshot, so a published
    /// service snapshot is never observable without its coverage. Re-selecting an already-published
    /// snapshot never backfills or rewrites its coverage (the snapshot is immutable). Not part of snapshot
    /// identity: coverage is derived from the same commit + configuration the identity already pins.
    /// </summary>
    public SnapshotCoverage? Coverage { get; init; }

    /// <summary>
    /// The coverage of each Phase-12 PROVIDER subtree the producing worker computed (issue #162), keyed by
    /// the submodule path relative to the checkout root with <c>/</c> separators (the same form
    /// <c>git submodule status --recursive</c> reports), or <c>null</c> when the caller computes no coverage
    /// (the local CLI/daemon path). The orchestrator records the entry for each provider snapshot it
    /// publishes inside the SAME publish transaction, so a directly-ensured provider reports an honest
    /// verdict instead of "not recorded". A provider this run publishes whose path has no entry is recorded
    /// as partial ("not computed"). An already-complete provider that is reused keeps whatever row it has
    /// (never backfilled). Not part of snapshot identity.
    /// </summary>
    public IReadOnlyDictionary<string, SnapshotCoverage>? ProviderCoverage { get; init; }
}
