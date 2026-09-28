using Sextant.Core;

namespace Sextant.Service.Grants;

/// <summary>
/// The repository key of the grant model (SVC-4): the match key that grants, catalog repositories and job rows are
/// compared by. For a URL the SVC-5 <see cref="RepositoryUrlPolicy"/> shape rules accept, it is the policy's
/// canonical <c>https://{host}/{owner}/{repo}</c> (folded by <see cref="RemoteUrlIdentity.Normalize"/>), whatever
/// the configured host and owner allow-lists say, so a catalog repository keeps its key when an operator narrows
/// the allow-list. Any other URL (for example a test fixture's <c>file://</c> repository) falls back to
/// <see cref="RemoteUrlIdentity.Normalize"/>, which is also what the test-only file transport of the policy
/// canonicalizes to.
/// </summary>
public static class RepositoryGrantKey
{
    private static readonly RepositoryUrlPolicy AnyHost = new([RepositoryUrlPolicy.Wildcard]);

    /// <summary>The key of <paramref name="remoteUrl"/>; empty for a null or blank URL.</summary>
    public static string Of(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return string.Empty;
        return AnyHost.Evaluate(remoteUrl) is { Ok: true, Canonical: { } canonical }
            ? canonical
            : RemoteUrlIdentity.Normalize(remoteUrl);
    }

    /// <summary>
    /// The URL-policy decision for REVOKING a grant: <paramref name="policy"/>'s decision, except that a URL refused
    /// only because its host or owner is no longer allow-listed is still accepted when it passes the shape rules, so a
    /// grant made before an operator narrowed the allow-list can always be revoked.
    /// </summary>
    public static RepositoryUrlDecision EvaluateForRevocation(RepositoryUrlPolicy policy, string? remoteUrl)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var decision = policy.Evaluate(remoteUrl);
        if (decision.Ok || decision.Reason is not (RepositoryUrlRejection.HostNotAllowed or RepositoryUrlRejection.OwnerNotAllowed))
            return decision;
        var shape = AnyHost.Evaluate(remoteUrl);
        return shape.Ok ? shape : decision;
    }
}
