using System.Security.Cryptography;
using System.Text;

namespace Sextant.Core;

/// <summary>
/// The identity of a Phase-9 immutable repository snapshot. A snapshot is uniquely identified — for
/// idempotency and for compatibility gating — by the tuple below, reduced to a single stable
/// <see cref="Hash"/>:
/// <list type="bullet">
///   <item><see cref="RepositoryRemoteUrl"/> — the normalized git remote (or local:// identity).</item>
///   <item><see cref="CommitSha"/> + <see cref="TreeSha"/> — the committed state indexed.</item>
///   <item><see cref="SchemaVersion"/> — the on-disk store schema version.</item>
///   <item><see cref="AnalyzerVersion"/> — the extraction-logic version
///   (<see cref="IndexConfigurationHash.AnalyzerVersion"/>).</item>
///   <item><see cref="ConfigHash"/> — the Phase-8 profile/feature configuration hash
///   (<see cref="IndexProfileDescriptor.ConfigurationHash"/>).</item>
///   <item><see cref="ToolchainFingerprint"/> — the runtime that produced it
///   (<see cref="Core.ToolchainFingerprint.Current"/>).</item>
/// </list>
/// Two runs with the same tuple produce the same <see cref="Hash"/>, so a duplicate publish attaches
/// to the existing snapshot instead of creating a second one (acceptance criterion 3). A difference in
/// schema / analyzer / config / toolchain yields a different hash, which blocks reuse of an
/// incompatible snapshot (acceptance criterion 4).
/// </summary>
public sealed record SnapshotIdentity
{
    public required string RepositoryRemoteUrl { get; init; }
    public required string CommitSha { get; init; }
    public string? TreeSha { get; init; }
    public required int SchemaVersion { get; init; }
    public required string AnalyzerVersion { get; init; }
    public string? ConfigHash { get; init; }
    public required string ToolchainFingerprint { get; init; }

    /// <summary>
    /// Phase-10 working-tree delta digest. A stable hash over the dirty working tree's
    /// changed/renamed/deleted/untracked paths and their content hashes. <c>null</c> for a clean
    /// checkout (the identity is then the bare committed state). When set it makes a DIRTY working
    /// tree's snapshot identity <em>commit + delta</em> — never the bare commit — so a dirty tree
    /// under an otherwise-clean HEAD is never mis-identified as the clean committed snapshot (issue
    /// #43), and an identical dirty tree recomputes the identical digest so a restart/periodic pass
    /// idempotently re-selects the same overlay instead of rebuilding it (acceptance criterion 3).
    /// </summary>
    public string? WorkingTreeDelta { get; init; }

    /// <summary>
    /// The stable idempotency/compatibility hash over the identity tuple. Deterministic across
    /// machines and runs: a fixed, ordered <c>key=value;</c> pre-image hashed with SHA-256 (hex).
    /// </summary>
    public string Hash
    {
        get
        {
            var canonical =
                $"v=1;repo={RepositoryRemoteUrl};commit={CommitSha};tree={TreeSha ?? string.Empty};" +
                $"schema={SchemaVersion};analyzer={AnalyzerVersion};config={ConfigHash ?? string.Empty};" +
                $"toolchain={ToolchainFingerprint};delta={WorkingTreeDelta ?? string.Empty}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
