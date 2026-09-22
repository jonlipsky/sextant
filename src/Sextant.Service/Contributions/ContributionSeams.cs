namespace Sextant.Service.Contributions;

/// <summary>
/// The policy knobs that decide how STRICT the service is when accepting a client/CI contribution
/// (Phase 16). The safe production posture requires authorization and Git-content verification against
/// provider blobs; the defaults are dev-OPEN so a single-node development service (and the hermetic tests)
/// accept a contribution with no auth server and no Git provider wired — matching CRITICAL 2 (zero external
/// dependency for local operation). A real multi-tenant deployment sets <see cref="RequireAuthorization"/>
/// and <see cref="RequireGitContentVerification"/> true with a real <see cref="IContributionAuthorizer"/>
/// and <see cref="IGitContentProvider"/>.
/// </summary>
public sealed record ContributionPolicy
{
    /// <summary>When true, a contribution the authorizer does not explicitly allow is rejected (fail-closed).</summary>
    public bool RequireAuthorization { get; init; }

    /// <summary>
    /// When true, every declared source/import/reference fingerprint MUST verify against provider Git
    /// content — an <see cref="GitContentCheck.Unavailable"/> answer is treated as a rejection (fail-closed).
    /// When false (dev default) an unavailable provider answer skips the content check.
    /// </summary>
    public bool RequireGitContentVerification { get; init; }

    /// <summary>The maximum accepted artifact size in bytes (defense against oversized uploads).</summary>
    public long MaxArtifactBytes { get; init; } = 512L * 1024 * 1024;

    public static ContributionPolicy Default { get; } = new();
}

/// <summary>The result of verifying one declared file fingerprint against provider Git content.</summary>
public enum GitContentCheck
{
    /// <summary>The provider confirms the path at the commit has exactly the declared blob hash.</summary>
    Match,

    /// <summary>The provider has the path at the commit but with a DIFFERENT blob hash (tampered/mismatched input).</summary>
    Mismatch,

    /// <summary>The provider cannot answer (no checkout / unknown commit / not wired). Policy decides if fatal.</summary>
    Unavailable
}

/// <summary>
/// The seam the service uses to VERIFY that a contribution's declared source/import/reference blobs match
/// the authoritative Git content for the exact repository + commit (CRITICAL 1: a contribution is an
/// untrusted supply-chain input — its inputs must be verified against provider Git content before
/// publication). A production deployment implements this over the ProcessStack/Git provider; a single-node
/// dev service leaves it unwired (<see cref="UnavailableGitContentProvider"/>) and relies on the policy
/// default that treats "unavailable" as "not verified, dev-open".
/// </summary>
public interface IGitContentProvider
{
    /// <summary>Verifies that <paramref name="repoRelativePath"/> at the commit has the declared git blob hash.</summary>
    GitContentCheck VerifyBlob(string repositoryRemoteUrl, string commitSha, string repoRelativePath, string expectedBlobHash);
}

/// <summary>The dev-default provider: it cannot verify anything, so every check is <see cref="GitContentCheck.Unavailable"/>.</summary>
public sealed class UnavailableGitContentProvider : IGitContentProvider
{
    public static UnavailableGitContentProvider Instance { get; } = new();
    public GitContentCheck VerifyBlob(string repositoryRemoteUrl, string commitSha, string repoRelativePath, string expectedBlobHash)
        => GitContentCheck.Unavailable;
}

/// <summary>The authenticated context a contribution arrives with (tenant identity + declared target).</summary>
public sealed record ContributionAuthContext
{
    /// <summary>The bearer token/principal the contributor authenticated with (null when auth is open/dev).</summary>
    public string? Token { get; init; }

    public required string Tenant { get; init; }
    public required string RepositoryRemoteUrl { get; init; }
    public required string CommitSha { get; init; }
    public IReadOnlyList<string> ProjectCanonicalIds { get; init; } = [];
}

/// <summary>The authorizer's decision, with a structured reason when denied (recorded in diagnostics).</summary>
public sealed record ContributionAuthorization(bool Allowed, string? Reason)
{
    public static ContributionAuthorization Allow() => new(true, null);
    public static ContributionAuthorization Deny(string reason) => new(false, reason);
}

/// <summary>
/// The seam the service uses to AUTHORIZE a contribution: does this authenticated tenant own the repository
/// and is it allowed to contribute this exact commit/projects (CRITICAL 1). A production deployment checks
/// the ProcessStack/GitHub tenant's repository grant; the dev default (<see cref="OpenContributionAuthorizer"/>)
/// allows everything, which is safe only because <see cref="ContributionPolicy.RequireAuthorization"/>
/// defaults false for single-node dev.
/// </summary>
public interface IContributionAuthorizer
{
    ContributionAuthorization Authorize(ContributionAuthContext context);
}

/// <summary>The dev-default authorizer: allows every contribution (single-node dev has no tenant boundary).</summary>
public sealed class OpenContributionAuthorizer : IContributionAuthorizer
{
    public static OpenContributionAuthorizer Instance { get; } = new();
    public ContributionAuthorization Authorize(ContributionAuthContext context) => ContributionAuthorization.Allow();
}
