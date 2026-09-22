using Sextant.Store;

namespace Sextant.Service;

/// <summary>
/// A request to ingest a client/CI contribution artifact (Phase 16). The artifact is the content-addressed
/// container (manifest + compact semantic payload) the client produced for an exact clean commit. The
/// service authenticates, authorizes, hash/capability-verifies, imports, and (when finalizing) publishes it
/// — attaching to the ONE capability-less assembly snapshot for that committed state so multiple
/// capability-specific contributions assemble into one repository snapshot (acceptance criterion 4).
/// </summary>
public sealed record IngestContributionRequest
{
    /// <summary>The raw contribution-artifact bytes (magic + manifest + payload).</summary>
    public required byte[] Artifact { get; init; }

    /// <summary>The authenticated contributor token/principal (null when the service runs auth-open/dev).</summary>
    public string? Token { get; init; }

    /// <summary>
    /// Whether this contribution FINALIZES (publishes) the assembly snapshot. A single-environment
    /// contribution (the common case, criterion 1) finalizes immediately. A multi-environment assembly
    /// (criterion 4) uploads intermediate contributions with <c>false</c> and finalizes with the last one,
    /// so the snapshot stays PENDING (never a mutated complete snapshot — immutability) until assembly is
    /// declared done.
    /// </summary>
    public bool Finalize { get; init; } = true;

    /// <summary>The branch to advance to the published snapshot (null leaves branch pointers untouched).</summary>
    public string? BranchName { get; init; }

    /// <summary>Whether <see cref="BranchName"/> is the repository's default branch (drives sole-default promotion).</summary>
    public bool IsDefaultBranch { get; init; }
}

/// <summary>The terminal disposition of a contribution ingest.</summary>
public static class ContributionIngestStatus
{
    /// <summary>Accepted and published (or attached to an already-published assembly snapshot).</summary>
    public const string Complete = "complete";

    /// <summary>Accepted and imported, but the assembly snapshot is still pending more contributions.</summary>
    public const string Assembling = "assembling";

    /// <summary>A content-addressed no-op: this exact artifact was already accepted (criterion 2).</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Rejected by the supply-chain gate; never published (criterion 3).</summary>
    public const string Rejected = "rejected";
}

/// <summary>The outcome of ingesting a contribution, with structured diagnostics on rejection (criterion 3).</summary>
public sealed record IngestContributionResult
{
    public required bool Accepted { get; init; }
    public required string Status { get; init; }
    public string? RejectionCode { get; init; }
    public string? Message { get; init; }
    public long? JobId { get; init; }
    public long? SnapshotId { get; init; }
    public string? ContentHash { get; init; }
    public string IdentityHash { get; init; } = string.Empty;
    public IReadOnlyList<SnapshotJobDiagnostic> Diagnostics { get; init; } = [];
}
