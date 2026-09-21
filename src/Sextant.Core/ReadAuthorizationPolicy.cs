using System.Security.Cryptography;
using System.Text;

namespace Sextant.Core;

/// <summary>
/// One authenticated read principal (a bearer token) and the set of repository remote URLs it may read
/// (Phase 17, criterion 1). <c>"*"</c> in <see cref="Repositories"/> grants every repository (a trusted
/// admin/pilot-wide principal). The token is a shared secret compared in constant time so a policy check
/// never leaks token length/content via timing.
/// </summary>
public sealed record ReadPrincipal
{
    public required string Token { get; init; }

    /// <summary>Allowed repository remote URLs; the single entry <c>"*"</c> grants all repositories.</summary>
    public required IReadOnlySet<string> Repositories { get; init; }

    /// <summary>True when this principal is granted every repository.</summary>
    public bool AllowsAll => Repositories.Contains("*");

    /// <summary>True when this principal may read the given repository remote URL.</summary>
    public bool Allows(string remoteUrl) => AllowsAll || Repositories.Contains(remoteUrl);
}

/// <summary>
/// The enforced read-authorization policy for the standalone service's query plane (Phase 17, criterion 1).
/// It turns the permissive-by-default <c>AllowAllReadAuthorizer</c> seam into a real fail-closed decision:
/// a configured policy maps each principal token to the repositories it may read, and any read of a
/// repository the principal is not granted is DENIED with a single generic reason that reveals nothing
/// about whether the repository or its data exists. When <see cref="Enabled"/> is false (no policy
/// configured) the service behaves exactly as before — zero-friction, byte-identical single-node local
/// operation.
/// </summary>
public sealed record ReadAuthorizationPolicy
{
    /// <summary>False for the default open posture (no policy) — the authorizer allows everything.</summary>
    public bool Enabled { get; init; }

    /// <summary>The configured principals; empty when the policy is disabled.</summary>
    public IReadOnlyList<ReadPrincipal> Principals { get; init; } = [];

    /// <summary>The default disabled policy (no enforcement; single-node local default).</summary>
    public static ReadAuthorizationPolicy Disabled { get; } = new();

    /// <summary>
    /// Resolves the principal for a bearer token in constant time over the whole principal set, so a caller
    /// cannot use response timing to discover which (or whether a) token matched. Returns null for an
    /// unknown/empty token.
    /// </summary>
    public ReadPrincipal? Find(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var provided = Encoding.UTF8.GetBytes(token);
        ReadPrincipal? match = null;
        // Iterate ALL principals (no early break) and use a fixed-time compare so neither the match position
        // nor the token contents are observable through timing.
        foreach (var principal in Principals)
        {
            var expected = Encoding.UTF8.GetBytes(principal.Token);
            if (CryptographicOperations.FixedTimeEquals(provided, expected))
                match = principal;
        }
        return match;
    }

    /// <summary>True when the token identifies a configured principal (authentication, not per-repo authz).</summary>
    public bool IsKnownPrincipal(string? token) => Find(token) is not null;

    /// <summary>True when the token's principal is granted read access to the given repository remote URL.</summary>
    public bool Allows(string? token, string remoteUrl) => Find(token) is { } principal && principal.Allows(remoteUrl);

    /// <summary>
    /// Parses the compact wire form <c>token=url1|url2;token2=*</c> into a policy. An empty/blank spec is the
    /// <see cref="Disabled"/> policy. Entries without a token or without any repository are skipped; a policy
    /// that ends up with no valid principals is treated as disabled rather than a lock-everyone-out policy.
    /// </summary>
    public static ReadAuthorizationPolicy Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return Disabled;

        var principals = new List<ReadPrincipal>();
        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0)
                continue;

            var token = entry[..eq].Trim();
            var repos = entry[(eq + 1)..]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            if (token.Length == 0 || repos.Count == 0)
                continue;

            principals.Add(new ReadPrincipal { Token = token, Repositories = repos });
        }

        return principals.Count == 0
            ? Disabled
            : new ReadAuthorizationPolicy { Enabled = true, Principals = principals };
    }
}
