using System.Text.RegularExpressions;

namespace Sextant.Service.Restore;

/// <summary>
/// An operator-configured credential for one package source HOST (issue #231). The restore hands it to NuGet only
/// through <see cref="FeedCredentialPlugin"/>, which answers for an <c>https</c> request to exactly this host and
/// port (<see cref="Port"/>, or the default 443 when null), never for a source key a repository's <c>nuget.config</c>
/// chooses. <see cref="ToString"/> never includes the password.
/// </summary>
public sealed record PackageSourceCredential(string Host, string Username, string Password)
{
    /// <summary>The exact port the credential is for; null means the https default (443) only.</summary>
    public int? Port { get; init; }

    // A plain host name (dot-separated DNS labels): no scheme, port, path, userinfo or wildcard. Matching is exact.
    private static readonly Regex HostShape = new(
        @"^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="uri"/> is an <c>https</c> request to <see cref="Host"/> on <see cref="Port"/> (the
    /// default port when null) with no userinfo. A host that merely starts or ends with <see cref="Host"/> never matches.
    /// </summary>
    public bool Matches(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri
            && uri.Scheme == Uri.UriSchemeHttps
            && (Port is { } port ? uri.Port == port : uri.IsDefaultPort)
            && uri.UserInfo.Length == 0
            && string.Equals(uri.IdnHost, Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override string ToString() => $"{HostAndPort} (user {Username})";

    /// <summary><see cref="Host"/>, with <c>:port</c> when <see cref="Port"/> is set.</summary>
    public string HostAndPort => Port is { } port ? $"{Host}:{port}" : Host;

    /// <summary>
    /// Parses <c>SEXTANT_SERVICE_PACKAGE_SOURCE_CREDENTIALS</c>: <c>host[:port]=username:password</c> entries separated
    /// by <c>;</c> (the password may contain <c>:</c> but not <c>;</c>). Unset gives none. A set value with no entry, a
    /// malformed entry, a host that is not a plain host name, a bad port, or a host and port listed twice THROWS
    /// <see cref="FormatException"/>, naming the entry by position only and never echoing a value.
    /// </summary>
    public static IReadOnlyList<PackageSourceCredential> ParseList(string? value)
    {
        if (value is null)
            return [];
        var entries = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            throw new FormatException("it is set but lists no entry.");

        var credentials = new List<PackageSourceCredential>(entries.Length);
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var equals = entry.IndexOf('=');
            var colon = equals < 0 ? -1 : entry.IndexOf(':', equals + 1);
            if (equals <= 0 || colon < 0)
                throw new FormatException($"entry #{i + 1} is not host=username:password.");

            var host = entry[..equals].Trim().ToLowerInvariant();
            int? port = null;
            var portSeparator = host.IndexOf(':');
            if (portSeparator >= 0)
            {
                if (!int.TryParse(host[(portSeparator + 1)..], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535)
                    throw new FormatException($"entry #{i + 1} has a port that is not a number from 1 to 65535.");
                // The https default is the same source as no port at all: one credential per (host, port).
                port = parsed == 443 ? null : parsed;
                host = host[..portSeparator];
            }
            var username = entry[(equals + 1)..colon];
            var password = entry[(colon + 1)..];
            if (!HostShape.IsMatch(host))
                throw new FormatException($"entry #{i + 1} names a host that is not a plain host name (no scheme, path, userinfo or wildcard).");
            if (username.Length == 0 || password.Length == 0)
                throw new FormatException($"entry #{i + 1} has an empty username or password.");
            if (username.Any(char.IsControl) || password.Any(char.IsControl) || username.Any(char.IsWhiteSpace))
                throw new FormatException($"entry #{i + 1} has a control or whitespace character in its username or password.");
            var credential = new PackageSourceCredential(host, username, password) { Port = port };
            if (!hosts.Add(credential.HostAndPort))
                throw new FormatException($"entry #{i + 1} repeats a host listed earlier.");
            credentials.Add(credential);
        }
        return credentials;
    }
}
