using System.Net;
using Sextant.Core;

namespace Sextant.ProcessStack.Activities;

/// <summary>
/// The verdict of <see cref="RepositoryUrlShape.Evaluate"/>. When <see cref="Ok"/>, <see cref="Canonical"/> is
/// <c>https://{host}/{owner}/{repo}</c> folded with <see cref="RemoteUrlIdentity.Normalize"/> (the service's
/// SVC-5 policy key), <see cref="Owner"/>/<see cref="Repo"/> are its folded path segments and
/// <see cref="SpelledOwner"/>/<see cref="SpelledRepo"/> are the segments as submitted (without <c>.git</c>).
/// When refused, only <see cref="Reason"/> is set.
/// </summary>
internal readonly record struct RepositoryUrlVerdict(
    bool Ok, string Canonical, string Host, string Owner, string Repo, string SpelledOwner, string SpelledRepo,
    string Reason)
{
    public static RepositoryUrlVerdict Reject(string reason) => new(false, "", "", "", "", "", "", reason);
}

/// <summary>
/// The shape rules of the service's SVC-5 repository URL policy (<c>Sextant.Service.RepositoryUrlPolicy</c>)
/// with every host allowed: <c>https</c> only; no userinfo, query, fragment, non-443 port, whitespace,
/// control or backslash characters; a multi-label DNS host that is not an IP literal or <c>localhost</c>;
/// and a path of exactly <c>/{owner}/{repo}[.git]</c> with safe segments. The service stays authoritative
/// (its host and owner allow-lists apply on top); this lets a flow refuse an obviously unusable URL without
/// a round trip. The reason codes are the service's. A parity test pins this to the service's policy.
/// </summary>
internal static class RepositoryUrlShape
{
    public const string SchemeNotAllowed = "scheme_not_allowed";
    public const string UrlComponentNotAllowed = "url_component_not_allowed";
    public const string HostNotAllowed = "host_not_allowed";
    public const string PathNotAllowed = "path_not_allowed";

    public const int MaxUrlLength = 2048;

    private const int MaxSegmentLength = 100;
    private const int MaxHostLength = 253;
    private const int MaxLabelLength = 63;
    private const string SchemeSeparator = "://";
    private const string HttpsPrefix = "https://";
    private const string GitSuffix = ".git";
    private const string Localhost = "localhost";

    /// <summary>Evaluates an untrusted repository URL against the SVC-5 shape rules.</summary>
    public static RepositoryUrlVerdict Evaluate(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return RepositoryUrlVerdict.Reject(SchemeNotAllowed);
        var schemeEnd = url.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (schemeEnd <= 0 || !url[..schemeEnd].Equals("https", StringComparison.OrdinalIgnoreCase))
            return RepositoryUrlVerdict.Reject(SchemeNotAllowed);
        if (url.Length > MaxUrlLength || HasForbiddenCharacter(url) || url.AsSpan().IndexOfAny('?', '#') >= 0)
            return RepositoryUrlVerdict.Reject(UrlComponentNotAllowed);

        var rest = url[(schemeEnd + SchemeSeparator.Length)..];
        var slash = rest.IndexOf('/');
        var authority = slash >= 0 ? rest[..slash] : rest;
        var path = slash >= 0 ? rest[slash..] : string.Empty;
        if (!TryParseHost(authority, out var host, out var hostReason))
            return RepositoryUrlVerdict.Reject(hostReason);
        if (!IsHostShape(host))
            return RepositoryUrlVerdict.Reject(HostNotAllowed);
        if (!TrySplitRepositoryPath(path, out var owner, out var repo))
            return RepositoryUrlVerdict.Reject(PathNotAllowed);

        var canonical = RemoteUrlIdentity.Normalize($"{HttpsPrefix}{host}/{owner}/{repo}");
        var folded = canonical[(HttpsPrefix.Length + host.Length + 1)..];
        var separator = folded.IndexOf('/');
        return new RepositoryUrlVerdict(
            true, canonical, host, folded[..separator], folded[(separator + 1)..], owner, repo, "");
    }

    /// <summary>
    /// True for a lower-case DNS host with at least two labels that is not <c>localhost</c> (or a subdomain of
    /// it) and cannot be read as an IP address (a host whose last label is numeric, such as <c>127.1</c> or
    /// <c>0x7f.1</c>, is one).
    /// </summary>
    public static bool IsHostShape(string host)
    {
        if (host.Length is 0 or > MaxHostLength || !IsDnsName(host))
            return false;
        var lastDot = host.LastIndexOf('.');
        if (lastDot < 0)
            return false;
        if (host == Localhost || host.EndsWith("." + Localhost, StringComparison.Ordinal))
            return false;
        return !IsNumericLabel(host[(lastDot + 1)..]) && !IPAddress.TryParse(host, out _);
    }

    /// <summary>True for an owner or repository segment: 1-100 of <c>[A-Za-z0-9._-]</c>, not starting with <c>-</c>, not <c>.</c>/<c>..</c>.</summary>
    public static bool IsSegment(string segment)
    {
        if (segment.Length is 0 or > MaxSegmentLength || segment[0] == '-' || segment is "." or "..")
            return false;
        foreach (var c in segment)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                return false;
        }
        return true;
    }

    // The lower-cased host of an https authority: no userinfo, no bracketed (IPv6) literal, and no port other
    // than the default 443 (which is dropped).
    private static bool TryParseHost(string authority, out string host, out string reason)
    {
        host = string.Empty;
        reason = UrlComponentNotAllowed;
        if (authority.Contains('@'))
            return false;
        if (authority.StartsWith('['))
        {
            reason = HostNotAllowed;
            return false;
        }
        var colon = authority.LastIndexOf(':');
        if (colon >= 0 && authority[(colon + 1)..] != "443")
            return false;
        host = (colon >= 0 ? authority[..colon] : authority).ToLowerInvariant();
        return true;
    }

    // Exactly `/{owner}/{repo}` or `/{owner}/{repo}.git`. A repo that still ends in `.git` after the one
    // optional suffix is stripped is refused (the canonical form would no longer name the same path).
    private static bool TrySplitRepositoryPath(string path, out string owner, out string repo)
    {
        owner = repo = string.Empty;
        if (path.Length < 2 || path[0] != '/')
            return false;
        var parts = path[1..].Split('/');
        if (parts.Length != 2)
            return false;
        owner = parts[0];
        repo = StripGitSuffix(parts[1]);
        return IsSegment(owner) && IsSegment(repo) && !repo.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes one trailing <c>.git</c> (any case).</summary>
    public static string StripGitSuffix(string repo) =>
        repo.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase) ? repo[..^GitSuffix.Length] : repo;

    // Dot-separated labels of [a-z0-9-], 1-63 long, neither starting nor ending with '-'.
    private static bool IsDnsName(string host)
    {
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > MaxLabelLength || label[0] == '-' || label[^1] == '-')
                return false;
            foreach (var c in label)
            {
                if (!char.IsAsciiDigit(c) && !char.IsAsciiLetterLower(c) && c != '-')
                    return false;
            }
        }
        return true;
    }

    // A label a URL parser reads as a number: decimal digits, or `0x` followed by hex digits.
    private static bool IsNumericLabel(string label)
    {
        if (label.StartsWith("0x", StringComparison.Ordinal))
            return label[2..].All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
        return label.Length > 0 && label.All(char.IsAsciiDigit);
    }

    private static bool HasForbiddenCharacter(string url)
    {
        foreach (var c in url)
        {
            if (c <= ' ' || c == '\u007f' || c == '\\')
                return true;
        }
        return false;
    }
}
