using System.Text.RegularExpressions;

namespace Sextant.Service.CallerIdentity;

/// <summary>
/// How the service verifies caller assertions (SVC-3): the compact HS256 JWS a trusted caller sends in
/// <see cref="Header"/> to say which tenant and user (or application) a request is for. Bound from
/// <c>SEXTANT_SERVICE_CALLER_*</c>. With no <see cref="Keys"/> the feature is off and any assertion is refused.
/// </summary>
public sealed partial record CallerAssertionOptions
{
    /// <summary>The default assertion header name.</summary>
    public const string DefaultHeader = "X-ProcessStack-Caller";

    /// <summary>The platform's own identity provider: its subjects are platform user ids, never namespaced.</summary>
    public const string PlatformIdp = "processstack";

    /// <summary>The feature-off default: no keys, so no assertion verifies.</summary>
    public static CallerAssertionOptions Disabled { get; } = new();

    /// <summary>The kid → (key, tenant) ring. Empty = the feature is off.</summary>
    public CallerKeyRing Keys { get; init; } = CallerKeyRing.Empty;

    /// <summary>The audience every assertion must name in <c>aud</c>. Required when <see cref="Keys"/> is set.</summary>
    public string? Audience { get; init; }

    /// <summary>The accepted <c>iss</c> values. Empty = any issuer.</summary>
    public IReadOnlySet<string> Issuers { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The request header that carries the assertion.</summary>
    public string Header { get; init; } = DefaultHeader;

    /// <summary>The identity providers whose users may act as <c>act=user</c>. Defaults to the platform's own.</summary>
    public IReadOnlySet<string> Idps { get; init; } = new HashSet<string>(StringComparer.Ordinal) { PlatformIdp };

    /// <summary>The accepted <c>app</c> values. Empty = any application.</summary>
    public IReadOnlySet<string> Apps { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True when at least one key is configured, so assertions can be verified.</summary>
    public bool Enabled => Keys.Count > 0;

    /// <summary>True when <paramref name="idp"/> is a well-formed <c>CALLER_IDPS</c> entry (<c>[a-z0-9-]{1,32}</c>).</summary>
    public static bool IsValidIdp(string? idp) => idp is not null && IdpPattern().IsMatch(idp);

    /// <summary>True when <paramref name="app"/> is a well-formed <c>CALLER_APPS</c> entry (<c>[A-Za-z0-9._-]{1,64}</c>).</summary>
    public static bool IsValidApp(string? app) => app is not null && AppPattern().IsMatch(app);

    /// <summary>True when <paramref name="header"/> is a usable header name: an HTTP token that no other credential or selector uses.</summary>
    public static bool IsValidHeader(string? header) =>
        header is not null
        && HeaderPattern().IsMatch(header)
        && !ReservedHeaders.Contains(header);

    /// <summary>
    /// Throws when the options are not self-consistent: an audience is required once keys are set, and the header,
    /// identity providers and applications must be well-formed. Messages never contain key material.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are invalid.</exception>
    public void Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(Audience))
            throw Invalid("SEXTANT_SERVICE_CALLER_AUDIENCE is required when SEXTANT_SERVICE_CALLER_KEYS is set.");
        if (!IsValidHeader(Header))
            throw Invalid(
                "SEXTANT_SERVICE_CALLER_HEADER must be an HTTP header name ([A-Za-z0-9-]{1,64}) that is not already a " +
                "credential or selector header (Authorization, Proxy-Authorization, Cookie, X-Sextant-Repository).");
        if (Idps.Count == 0 || Idps.Any(idp => !IsValidIdp(idp)))
            throw Invalid("SEXTANT_SERVICE_CALLER_IDPS must list identity providers matching [a-z0-9-]{1,32}.");
        if (Apps.Any(app => !IsValidApp(app)))
            throw Invalid("SEXTANT_SERVICE_CALLER_APPS must list applications matching [A-Za-z0-9._-]{1,64}.");
        if (Issuers.Any(string.IsNullOrWhiteSpace))
            throw Invalid("SEXTANT_SERVICE_CALLER_ISSUERS must not contain a blank issuer.");
    }

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "X-Sextant-Repository"
    };

    private static InvalidOperationException Invalid(string message) =>
        new($"{message} Refusing to start with an ambiguous caller-assertion configuration (fail closed).");

    [GeneratedRegex("^[a-z0-9-]{1,32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdpPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AppPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();
}
