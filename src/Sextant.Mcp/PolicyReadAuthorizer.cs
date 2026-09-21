using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// The real enforced read authorizer (Phase 17, criterion 1). It replaces the permissive
/// <see cref="AllowAllReadAuthorizer"/> on the service's query plane, deciding every read against a
/// configured <see cref="ReadAuthorizationPolicy"/> and the ambient authenticated principal. It is
/// deliberately framework-agnostic — the principal token and the repository-URL lookup are injected as
/// delegates — so it lives in <c>Sextant.Mcp</c> (ProcessStack-agnostic) and is unit-testable without a
/// web host; the HTTP host supplies an <c>IHttpContextAccessor</c>-backed token accessor and a catalog
/// repository resolver.
/// <para>
/// Fail-closed and information-leakage discipline: when the policy is enabled, ANY read the principal is
/// not explicitly granted is denied with ONE generic reason. The reason never names the repository, never
/// reports counts, and is byte-identical whether the repository exists, is empty, or is simply
/// unauthorized — so an unauthorized caller cannot use the response as an existence/cardinality oracle. A
/// null selected snapshot (an unidentifiable repository) is denied, never allowed. When the policy is
/// disabled the authorizer allows everything, so single-node local operation is unchanged.
/// </para>
/// </summary>
public sealed class PolicyReadAuthorizer : IReadAuthorizer
{
    // A single generic reason for every denial: it must reveal nothing about existence, identity, or size.
    private const string DeniedReason = "The current principal is not authorized to read the requested index.";

    private readonly ReadAuthorizationPolicy _policy;
    private readonly Func<string?> _principalToken;
    private readonly Func<long, string?> _repositoryUrl;

    /// <param name="policy">The configured allowlist policy. A disabled policy allows everything.</param>
    /// <param name="principalTokenAccessor">Resolves the ambient request principal's bearer token (null when absent).</param>
    /// <param name="repositoryUrlResolver">Maps a repository row id to its remote URL (null when unknown).</param>
    public PolicyReadAuthorizer(
        ReadAuthorizationPolicy policy,
        Func<string?> principalTokenAccessor,
        Func<long, string?> repositoryUrlResolver)
    {
        _policy = policy;
        _principalToken = principalTokenAccessor;
        _repositoryUrl = repositoryUrlResolver;
    }

    /// <summary>True iff the configured policy is enabled — see <see cref="IReadAuthorizer.IsEnforcing"/>.</summary>
    public bool IsEnforcing => _policy.Enabled;

    public ReadAuthorization Authorize(SnapshotRow? selected)
    {
        if (!_policy.Enabled)
            return ReadAuthorization.Allow;

        // No identifiable repository → fail closed. Under an enabled (multi-tenant) policy a legitimate read
        // always resolves to a selected snapshot; a null one cannot be attributed to an authorized
        // repository, so it is denied rather than waved through.
        if (selected is null)
            return ReadAuthorization.Deny(DeniedReason);

        var url = _repositoryUrl(selected.RepositoryId);
        if (url is null)
            return ReadAuthorization.Deny(DeniedReason);

        return _policy.Allows(_principalToken(), url)
            ? ReadAuthorization.Allow
            : ReadAuthorization.Deny(DeniedReason);
    }

    public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl)
    {
        if (!_policy.Enabled)
            return ReadAuthorization.Allow;

        // Cross-repository candidate check: an unauthorized consumer repository is dropped SILENTLY by the
        // resolver (no result, no count, no name), so the denial reason here is never surfaced to the caller.
        return _policy.Allows(_principalToken(), remoteUrl)
            ? ReadAuthorization.Allow
            : ReadAuthorization.Deny(DeniedReason);
    }
}
