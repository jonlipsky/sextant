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
}
