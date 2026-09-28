using Sextant.Mcp;
using Sextant.Store;

namespace Sextant.Service.Grants;

/// <summary>
/// The read authorizer for a verified caller (SVC-4): a repository is readable iff the caller's tenant holds a
/// grant on it for the caller's exact subject or tenant-wide (<c>'*'</c>); an application caller counts only the
/// tenant-wide grants. Visibility is repository-level, so any indexed branch of a visible repository is readable.
/// Like <see cref="PolicyReadAuthorizer"/> it is built from delegates and has no HTTP dependency: the host supplies
/// the current request's visible repository keys (computed once per request, never cached across requests, so a
/// revoked grant takes effect on the next call) and the catalog's repository-URL lookup.
/// <para>
/// Always enforcing: every denial carries ONE generic reason and collapses to the uniform not-found, so an
/// ungranted repository is indistinguishable from an absent one. A request with no caller (a null key set), a null
/// selection or an unknown repository is denied. <see cref="AuthorizeRepository"/> applies the same rule, so the
/// cross-repository tools drop an ungranted consumer silently.
/// </para>
/// </summary>
public sealed class GrantReadAuthorizer : IReadAuthorizer
{
    private const string DeniedReason = "The current principal is not authorized to read the requested index.";

    private static readonly ReadAuthorization Denied = ReadAuthorization.Deny(DeniedReason);

    private readonly Func<IReadOnlySet<string>?> _visibleKeys;
    private readonly Func<long, string?> _repositoryUrl;

    /// <param name="visibleKeys">
    /// The current caller's visible <see cref="RepositoryGrantKey"/>s, or null when the request has no verified
    /// caller (deny everything).
    /// </param>
    /// <param name="repositoryUrlResolver">Maps a repository row id to its remote URL (null when unknown).</param>
    public GrantReadAuthorizer(Func<IReadOnlySet<string>?> visibleKeys, Func<long, string?> repositoryUrlResolver)
    {
        _visibleKeys = visibleKeys;
        _repositoryUrl = repositoryUrlResolver;
    }

    public bool IsEnforcing => true;

    public ReadAuthorization Authorize(SnapshotRow? selected) =>
        selected is not null && IsVisible(_repositoryUrl(selected.RepositoryId)) ? ReadAuthorization.Allow : Denied;

    public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) =>
        IsVisible(remoteUrl) ? ReadAuthorization.Allow : Denied;

    /// <summary>Whether the current caller may read <paramref name="remoteUrl"/> (any spelling of the repository).</summary>
    public bool IsVisible(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || _visibleKeys() is not { Count: > 0 } keys)
            return false;
        return keys.Contains(RepositoryGrantKey.Of(remoteUrl));
    }
}
