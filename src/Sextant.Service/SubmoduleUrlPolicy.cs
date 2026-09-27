using System.Text.RegularExpressions;

namespace Sextant.Service;

/// <summary>
/// The verdict for one <c>.gitmodules</c> URL: either a CLEAN (credential-free) URL to fetch plus whether the
/// configured checkout token may be sent to it, or a refusal reason. A refusal leaves the submodule
/// unpopulated (the checkout still succeeds and coverage reports it partial with this reason).
/// </summary>
/// <param name="CleanUrl">The URL to fetch (never carries userinfo); null when refused.</param>
/// <param name="Authority">The lower-case <c>host[:port]</c> the URL targets (null for <c>file://</c>).</param>
/// <param name="SendToken">True only when the URL targets the top-level repository's own https host.</param>
/// <param name="Refusal">Why the URL was refused (safe to log: never contains userinfo); null when allowed.</param>
internal readonly record struct SubmoduleUrlDecision(string? CleanUrl, string? Authority, bool SendToken, string? Refusal)
{
    public bool Allowed => CleanUrl is not null;

    public static SubmoduleUrlDecision Refuse(string reason) => new(null, null, false, reason);
}

/// <summary>
/// Pure, git-free resolution of an UNTRUSTED <c>.gitmodules</c> URL into the URL the service may fetch
/// (issue #125). The checkout token is only ever sent to the top-level repository's https host; other
/// hosts are fetched only when an operator allowlisted them (<c>SEXTANT_SERVICE_SUBMODULE_HOSTS</c>) and then
/// anonymously. Shapes handled:
/// <list type="bullet">
///   <item>absolute <c>https://</c> — allowed for the repository host (token) or an allowlisted host (anonymous);
///   a username-only userinfo (<c>https://user@host/…</c>, not a secret) is stripped; a password/token
///   userinfo (<c>user:secret@</c>) is refused;</item>
///   <item>relative <c>./x</c> / <c>../x</c> — resolved against the parent's CLEAN URL with git's semantics,
///   refusing any resolution that climbs past the parent's host;</item>
///   <item>scp-like <c>git@host:org/repo.git</c> and <c>ssh://[user@]host[:port]/org/repo.git</c> — rewritten
///   to <c>https://</c> ONLY when the host is the repository host (the ssh user/port are dropped);</item>
///   <item>everything else — <c>http://</c>, <c>git://</c>, helper transports (<c>ext::</c>), local paths and
///   <c>file://</c> — is refused. <c>file://</c> is allowed only under a test-only flag.</item>
/// </list>
/// Argument-injection / smuggling hardening: a URL with a leading <c>-</c>, whitespace, control or backslash
/// characters, a percent-encoded NUL/CR/LF, a query/fragment, or dot segments is refused outright.
/// </summary>
internal static partial class SubmoduleUrlPolicy
{
    /// <summary>Longest URL accepted from <c>.gitmodules</c>.</summary>
    internal const int MaxUrlLength = 2048;

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostPattern();

    /// <summary>
    /// Resolves <paramref name="rawUrl"/> declared by a repository whose clean URL is
    /// <paramref name="parentCleanUrl"/>.
    /// </summary>
    /// <param name="rawUrl">The <c>submodule.&lt;name&gt;.url</c> value (untrusted).</param>
    /// <param name="parentCleanUrl">The declaring repository's clean URL (relative URLs resolve against it).</param>
    /// <param name="repositoryAuthority">The top-level repository's https <c>host[:port]</c> (the only host the
    /// token is sent to); null when the top-level repository is not https.</param>
    /// <param name="anonymousAuthorities">Operator-allowlisted extra https authorities, fetched anonymously.</param>
    /// <param name="allowFileTransport">Test-only: permit <c>file://</c> URLs (never set in production).</param>
    public static SubmoduleUrlDecision Resolve(
        string? rawUrl,
        string parentCleanUrl,
        string? repositoryAuthority,
        IReadOnlySet<string> anonymousAuthorities,
        bool allowFileTransport)
    {
        if (string.IsNullOrEmpty(rawUrl))
            return SubmoduleUrlDecision.Refuse("no url is declared");
        if (rawUrl.Length > MaxUrlLength)
            return SubmoduleUrlDecision.Refuse("the url is too long");
        foreach (var c in rawUrl)
        {
            if (c <= ' ' || c == '\u007f' || c == '\\')
                return SubmoduleUrlDecision.Refuse("the url contains whitespace, control or backslash characters");
        }
        if (rawUrl[0] == '-')
            return SubmoduleUrlDecision.Refuse("the url starts with '-'");
        if (ContainsEncodedControl(rawUrl))
            return SubmoduleUrlDecision.Refuse("the url contains a percent-encoded control character");
        if (rawUrl.IndexOfAny(['?', '#']) >= 0)
            return SubmoduleUrlDecision.Refuse("the url carries a query or fragment");

        var url = rawUrl;
        if (url.StartsWith("./", StringComparison.Ordinal) || url.StartsWith("../", StringComparison.Ordinal))
        {
            var resolved = ResolveRelative(parentCleanUrl, url);
            if (resolved is null)
                return SubmoduleUrlDecision.Refuse("the relative url cannot be resolved inside the parent repository's host");
            url = resolved;
        }

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return ResolveSchemeless(url, repositoryAuthority);

        var scheme = url[..schemeEnd].ToLowerInvariant();
        var rest = url[(schemeEnd + 3)..];
        switch (scheme)
        {
            case "https":
                return ResolveHttps(rest, repositoryAuthority, anonymousAuthorities);
            case "ssh" or "git+ssh" or "ssh+git":
                return ResolveSsh(rest, repositoryAuthority);
            case "file":
                return allowFileTransport
                    ? new SubmoduleUrlDecision(url, null, false, null)
                    : SubmoduleUrlDecision.Refuse("file:// submodule urls are not allowed");
            case "http":
                return SubmoduleUrlDecision.Refuse("plain http:// submodule urls are not allowed (use https)");
            default:
                return SubmoduleUrlDecision.Refuse($"the '{Truncate(scheme, 16)}' transport is not allowed");
        }
    }

    /// <summary>
    /// Normalizes an operator-configured or URL-derived authority to lower-case <c>host[:port]</c> with the
    /// default https port (443) removed. Returns null when it is not a plain DNS host with an optional port.
    /// </summary>
    public static string? NormalizeAuthority(string? authority)
    {
        if (string.IsNullOrEmpty(authority))
            return null;
        var value = authority.ToLowerInvariant();
        string host;
        string? port = null;
        var colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            host = value[..colon];
            port = value[(colon + 1)..];
            if (port.Length is 0 or > 5 || !port.All(char.IsAsciiDigit) || !int.TryParse(port, out var p) || p is < 1 or > 65535)
                return null;
            if (p == 443)
                port = null;
            else
                port = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            host = value;
        }
        if (host.Length is 0 or > 253 || !HostPattern().IsMatch(host))
            return null;
        return port is null ? host : $"{host}:{port}";
    }

    /// <summary>
    /// The normalized https authority of <paramref name="url"/> — the host the checkout token may be sent to —
    /// or null when the URL is not https, carries userinfo, or has a host that is not a plain DNS name.
    /// </summary>
    public static string? HttpsAuthority(string url)
    {
        const string scheme = "https://";
        if (!url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = url[scheme.Length..];
        var slash = rest.IndexOf('/');
        var authority = slash >= 0 ? rest[..slash] : rest;
        return authority.Contains('@') ? null : NormalizeAuthority(authority);
    }

    private static SubmoduleUrlDecision ResolveHttps(
        string rest, string? repositoryAuthority, IReadOnlySet<string> anonymousAuthorities)
    {
        var slash = rest.IndexOf('/');
        var rawAuthority = slash >= 0 ? rest[..slash] : rest;
        var path = slash >= 0 ? rest[slash..] : string.Empty;

        var at = rawAuthority.LastIndexOf('@');
        if (at >= 0)
        {
            // A username alone is not a secret (e.g. `https://user@github.com/o/r.git`); strip it and fetch
            // with the service's own transient auth. A password/token in the userinfo is a credential we were
            // never told about and cannot redact — refuse it without ever echoing it.
            if (rawAuthority[..at].Contains(':'))
                return SubmoduleUrlDecision.Refuse("the url embeds a password or token in its userinfo");
            rawAuthority = rawAuthority[(at + 1)..];
        }

        var authority = NormalizeAuthority(rawAuthority);
        if (authority is null)
            return SubmoduleUrlDecision.Refuse("the url's host is not a valid DNS host name");
        if (!IsSafeRepositoryPath(path))
            return SubmoduleUrlDecision.Refuse("the url's path is empty or contains dot segments");

        var clean = $"https://{authority}{path}";
        if (repositoryAuthority is not null && string.Equals(authority, repositoryAuthority, StringComparison.Ordinal))
            return new SubmoduleUrlDecision(clean, authority, SendToken: true, null);
        if (anonymousAuthorities.Contains(authority))
            return new SubmoduleUrlDecision(clean, authority, SendToken: false, null);
        return SubmoduleUrlDecision.Refuse(
            $"host '{authority}' is neither the repository's host nor listed in SEXTANT_SERVICE_SUBMODULE_HOSTS");
    }

    private static SubmoduleUrlDecision ResolveSsh(string rest, string? repositoryAuthority)
    {
        var slash = rest.IndexOf('/');
        if (slash <= 0)
            return SubmoduleUrlDecision.Refuse("the ssh url has no repository path");
        var rawAuthority = rest[..slash];
        var path = rest[slash..];

        var at = rawAuthority.LastIndexOf('@');
        if (at >= 0)
        {
            if (rawAuthority[..at].Contains(':'))
                return SubmoduleUrlDecision.Refuse("the url embeds a password or token in its userinfo");
            rawAuthority = rawAuthority[(at + 1)..];
        }
        // The ssh port is meaningless for the https rewrite (the repository's https authority is used).
        var colon = rawAuthority.LastIndexOf(':');
        var host = colon >= 0 ? rawAuthority[..colon] : rawAuthority;
        return RewriteToRepositoryHost(host, path, repositoryAuthority);
    }

    private static SubmoduleUrlDecision ResolveSchemeless(string url, string? repositoryAuthority)
    {
        // `transport::address` names a remote helper (e.g. `ext::sh -c …`), which can run arbitrary commands.
        if (url.Contains("::", StringComparison.Ordinal))
            return SubmoduleUrlDecision.Refuse("remote-helper transports are not allowed");

        // git's scp-like syntax: `[user@]host:path`, recognized when a ':' precedes any '/'.
        var colon = url.IndexOf(':');
        var slash = url.IndexOf('/');
        if (colon <= 0 || (slash >= 0 && slash < colon))
            return SubmoduleUrlDecision.Refuse("local-path submodule urls are not allowed");

        var hostPart = url[..colon];
        var path = url[(colon + 1)..];
        var at = hostPart.LastIndexOf('@');
        var host = at >= 0 ? hostPart[(at + 1)..] : hostPart;
        // A one-letter "host" is a Windows drive letter (`C:/repo`), i.e. a local path.
        if (host.Length <= 1)
            return SubmoduleUrlDecision.Refuse("local-path submodule urls are not allowed");
        if (path.IndexOfAny([':', '@']) >= 0)
            return SubmoduleUrlDecision.Refuse("the scp-style url has an invalid path");
        return RewriteToRepositoryHost(host, "/" + path.TrimStart('/'), repositoryAuthority);
    }

    private static SubmoduleUrlDecision RewriteToRepositoryHost(string host, string path, string? repositoryAuthority)
    {
        var normalizedHost = NormalizeAuthority(host);
        if (normalizedHost is null || normalizedHost.Contains(':'))
            return SubmoduleUrlDecision.Refuse("the ssh url's host is not a valid DNS host name");
        if (repositoryAuthority is null || !string.Equals(HostOf(repositoryAuthority), normalizedHost, StringComparison.Ordinal))
            return SubmoduleUrlDecision.Refuse(
                $"ssh url host '{normalizedHost}' is not the repository's https host (only same-host ssh urls are rewritten to https)");
        if (!IsSafeRepositoryPath(path) || path.StartsWith("/~", StringComparison.Ordinal))
            return SubmoduleUrlDecision.Refuse("the url's path is empty or contains dot segments");
        return new SubmoduleUrlDecision($"https://{repositoryAuthority}{path}", repositoryAuthority, SendToken: true, null);
    }

    /// <summary>
    /// git's relative-URL semantics: the parent URL is treated as a directory, so each leading <c>../</c>
    /// removes one trailing path component (<c>../X.git</c> against <c>https://h/org/app.git</c> →
    /// <c>https://h/org/X.git</c>) and <c>./</c> keeps it. Returns null when the resolution would climb past
    /// the parent's scheme + authority, or the parent is not a <c>scheme://</c> URL.
    /// </summary>
    internal static string? ResolveRelative(string parentCleanUrl, string relative)
    {
        var schemeEnd = parentCleanUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return null;
        var authorityStart = schemeEnd + 3;
        var pathStart = parentCleanUrl.IndexOf('/', authorityStart);
        var prefix = pathStart >= 0 ? parentCleanUrl[..pathStart] : parentCleanUrl;
        var basePath = pathStart >= 0 ? parentCleanUrl[pathStart..] : string.Empty;

        var segments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var parts = relative.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                // Tolerate a single trailing '/', refuse an empty segment anywhere else.
                if (i == parts.Length - 1)
                    continue;
                return null;
            }
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(part);
        }
        return segments.Count == 0 ? null : $"{prefix}/{string.Join('/', segments)}";
    }

    private static bool IsSafeRepositoryPath(string path)
    {
        if (path.Length < 2 || path[0] != '/')
            return false;
        foreach (var segment in path[1..].Split('/'))
        {
            if (segment is "." or "..")
                return false;
        }
        return path.Trim('/').Length > 0;
    }

    private static string HostOf(string authority)
    {
        var colon = authority.LastIndexOf(':');
        return colon >= 0 ? authority[..colon] : authority;
    }

    private static bool ContainsEncodedControl(string url) =>
        url.Contains("%00", StringComparison.Ordinal)
        || url.Contains("%0a", StringComparison.OrdinalIgnoreCase)
        || url.Contains("%0d", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
