using System.Net;
using System.Text.RegularExpressions;
using Sextant.Core;

namespace Sextant.Service;

/// <summary>The stable, wire-safe reason codes <see cref="RepositoryUrlPolicy.Evaluate"/> refuses a URL with.</summary>
public static class RepositoryUrlRejection
{
    /// <summary>The URL is not <c>https://</c> (includes http, ssh, git, scp-like <c>git@host:o/r</c>, and <c>file://</c>).</summary>
    public const string SchemeNotAllowed = "scheme_not_allowed";

    /// <summary>The URL carries userinfo, a query, a fragment, a non-443 port, or whitespace/control/backslash characters, or is too long.</summary>
    public const string UrlComponentNotAllowed = "url_component_not_allowed";

    /// <summary>The host is not a plain multi-label DNS name (an IP literal, <c>localhost</c>, a single label, a trailing dot) or is not allow-listed.</summary>
    public const string HostNotAllowed = "host_not_allowed";

    /// <summary>The path is not exactly <c>/{owner}/{repo}</c> (optionally <c>.git</c>) with safe segments.</summary>
    public const string PathNotAllowed = "path_not_allowed";

    /// <summary>An owner allow-list is configured and the URL's <c>host/owner</c> is not on it.</summary>
    public const string OwnerNotAllowed = "owner_not_allowed";

    /// <summary>An <c>owner/repo</c> selector was sent, but the policy has no single host to expand it with.</summary>
    public const string HostRequired = "host_required";
}

/// <summary>
/// The verdict for one repository URL. When <see cref="Ok"/>, <see cref="Canonical"/> is
/// <c>https://{host}/{owner}/{repo}</c> folded with <see cref="RemoteUrlIdentity.Normalize"/>, and
/// <see cref="Owner"/>/<see cref="Repo"/> are its (equally folded) path segments. It is a POLICY and grant key
/// only: callers keep using the submitted spelling for snapshot identity. When refused, only
/// <see cref="Reason"/> is set (a <see cref="RepositoryUrlRejection"/> code that never contains any part of
/// the URL).
/// </summary>
public readonly record struct RepositoryUrlDecision(
    bool Ok, string? Canonical, string? Host, string? Owner, string? Repo, string? Reason)
{
    internal static RepositoryUrlDecision Reject(string reason) => new(false, null, null, null, null, reason);
}

/// <summary>
/// Pure, git-free SSRF policy for an UNTRUSTED top-level repository URL (SVC-5), applied at service intake
/// (<c>POST /control/ensure</c>) before any job row exists, so the service never clones or locates a
/// repository an operator did not allow. It mirrors <see cref="SubmoduleUrlPolicy"/> but is stricter: only
/// <c>https://{host}/{owner}/{repo}[.git]</c> on an allow-listed host is accepted.
/// <list type="bullet">
///   <item>scheme <c>https</c> only; <c>file://</c> only under the test-only
///   <see cref="AllowFileTransportForTesting"/>;</item>
///   <item>no userinfo, query, fragment, non-443 port, whitespace/control/backslash, or over-long URL;</item>
///   <item>host: at least two DNS labels, no trailing dot, no IP literal (including the WHATWG
///   "ends in a number" forms such as <c>127.1</c> or <c>0x7f.1</c>), not <c>localhost</c> or
///   <c>*.localhost</c>, and on the host allow-list (<c>*</c> = any host that passes the shape rules);</item>
///   <item>path: exactly <c>/{owner}/{repo}</c> with an optional <c>.git</c>, each segment
///   <c>[A-Za-z0-9._-]{1,100}</c>, not starting with <c>-</c>, not <c>.</c>/<c>..</c>;</item>
///   <item>optional owner allow-list of <c>host/owner</c> (or <c>host/*</c>) entries.</item>
/// </list>
/// </summary>
public sealed partial class RepositoryUrlPolicy
{
    /// <summary>The host allow-list when none is configured.</summary>
    public const string DefaultHost = "github.com";

    /// <summary>The host (or owner) allow-list entry that matches anything passing the shape rules.</summary>
    public const string Wildcard = "*";

    /// <summary>Longest repository URL accepted.</summary>
    public const int MaxUrlLength = 2048;

    private const int MaxSegmentLength = 100;
    private const string GitSuffix = ".git";
    private const string Localhost = "localhost";

    /// <summary>Only <c>github.com</c>, any owner.</summary>
    public static RepositoryUrlPolicy Default { get; } = new([DefaultHost]);

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("^(?:[0-9]+|0x[0-9a-f]*)$")]
    private static partial Regex NumericLabelPattern();

    private readonly HashSet<string> _hosts;
    private readonly HashSet<string>? _ownerEntries;

    /// <summary>
    /// Builds a policy from host and owner allow-list entries. Host entries are DNS host names (case-folded)
    /// or <c>*</c>; owner entries are <c>host/owner</c> or <c>host/*</c>, and each must name an allow-listed
    /// host. <paramref name="owners"/> null means any owner. Throws <see cref="FormatException"/> on any
    /// malformed or inconsistent entry, or an empty host list.
    /// </summary>
    public RepositoryUrlPolicy(IEnumerable<string> hosts, IEnumerable<string>? owners = null)
    {
        (AllowsAnyHost, var hostList) = ParseHosts(hosts);
        _hosts = new HashSet<string>(hostList, StringComparer.Ordinal);
        Hosts = hostList;
        if (owners is null)
            return;
        var ownerList = ParseOwners(owners);
        _ownerEntries = new HashSet<string>(ownerList, StringComparer.Ordinal);
        Owners = ownerList;
    }

    /// <summary>True when the host allow-list contains <c>*</c>: any host that passes the shape rules.</summary>
    public bool AllowsAnyHost { get; }

    /// <summary>The explicit (case-folded, de-duplicated) allow-listed hosts, excluding <c>*</c>.</summary>
    public IReadOnlyList<string> Hosts { get; }

    /// <summary>The <c>host/owner</c> allow-list (owners folded like <see cref="RepositoryUrlDecision.Owner"/>); null = any owner.</summary>
    public IReadOnlyList<string>? Owners { get; }

    /// <summary>
    /// TEST-ONLY: accept <c>file://</c> repository URLs so HTTP fixtures can ensure local bare repositories
    /// without a network (mirrors <see cref="CloningCheckoutProvider.AllowFileTransportForTesting"/> for
    /// submodules). Never set in production: a caller must not be able to make the service read arbitrary local
    /// repositories on its host. A file URL is accepted as-is with no host/owner/repo.
    /// </summary>
    internal bool AllowFileTransportForTesting { get; init; }

    /// <summary>Evaluates <paramref name="url"/> (untrusted) against this policy.</summary>
    public RepositoryUrlDecision Evaluate(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return RepositoryUrlDecision.Reject(RepositoryUrlRejection.SchemeNotAllowed);
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return RepositoryUrlDecision.Reject(RepositoryUrlRejection.SchemeNotAllowed);
        var scheme = url[..schemeEnd];
        if (AllowFileTransportForTesting && string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase))
            return new RepositoryUrlDecision(true, RemoteUrlIdentity.Normalize(url), null, null, null, null);
        if (!string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
            return RepositoryUrlDecision.Reject(RepositoryUrlRejection.SchemeNotAllowed);
        return EvaluateHttps(url, url[(schemeEnd + "://".Length)..]);
    }

    /// <summary>
    /// Evaluates a repository SELECTOR (a tool's <c>repository</c> argument): a URL as <see cref="Evaluate"/> takes
    /// it, or the short forms <c>host/owner/repo</c> and, when the policy allow-lists exactly one explicit host,
    /// <c>owner/repo</c> (otherwise refused with <see cref="RepositoryUrlRejection.HostRequired"/>).
    /// </summary>
    public RepositoryUrlDecision EvaluateSelector(string selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.Contains("://", StringComparison.Ordinal))
            return Evaluate(selector);

        return selector.Split('/').Length switch
        {
            3 => Evaluate("https://" + selector),
            2 when Hosts.Count == 1 => Evaluate($"https://{Hosts[0]}/{selector}"),
            2 => RepositoryUrlDecision.Reject(RepositoryUrlRejection.HostRequired),
            _ => RepositoryUrlDecision.Reject(RepositoryUrlRejection.PathNotAllowed)
        };
    }

    // `rest` is everything after `https://`.
    private RepositoryUrlDecision EvaluateHttps(string url, string rest)
    {
        if (url.Length > MaxUrlLength || HasForbiddenCharacter(url) || url.AsSpan().IndexOfAny('?', '#') >= 0)
            return RepositoryUrlDecision.Reject(RepositoryUrlRejection.UrlComponentNotAllowed);

        var slash = rest.IndexOf('/');
        var authority = slash >= 0 ? rest[..slash] : rest;
        var path = slash >= 0 ? rest[slash..] : string.Empty;
        if (!TryParseHost(authority, out var host, out var hostReason))
            return RepositoryUrlDecision.Reject(hostReason);
        if (!IsAllowedHostShape(host) || !AllowsHost(host))
            return RepositoryUrlDecision.Reject(RepositoryUrlRejection.HostNotAllowed);

        if (!TrySplitRepositoryPath(path, out var owner, out var repo))
            return RepositoryUrlDecision.Reject(RepositoryUrlRejection.PathNotAllowed);

        var canonical = Canonicalize(host, owner, repo);
        var (foldedOwner, foldedRepo) = SegmentsOf(canonical, host);
        if (_ownerEntries is not null
            && !_ownerEntries.Contains($"{host}/{Wildcard}")
            && !_ownerEntries.Contains($"{host}/{foldedOwner}"))
            return RepositoryUrlDecision.Reject(RepositoryUrlRejection.OwnerNotAllowed);

        return new RepositoryUrlDecision(true, canonical, host, foldedOwner, foldedRepo, null);
    }

    // The lower-cased host of an https authority: no userinfo, no bracketed (IPv6) literal, and no port other
    // than the default 443 (which is dropped).
    private static bool TryParseHost(string authority, out string host, out string reason)
    {
        host = string.Empty;
        reason = RepositoryUrlRejection.UrlComponentNotAllowed;
        if (authority.Contains('@'))
            return false;
        if (authority.StartsWith('['))
        {
            reason = RepositoryUrlRejection.HostNotAllowed;
            return false;
        }
        var colon = authority.LastIndexOf(':');
        if (colon >= 0 && authority[(colon + 1)..] != "443")
            return false;
        host = (colon >= 0 ? authority[..colon] : authority).ToLowerInvariant();
        return true;
    }

    private bool AllowsHost(string host) => AllowsAnyHost || _hosts.Contains(host);

    private static (bool AnyHost, List<string> Hosts) ParseHosts(IEnumerable<string> hosts)
    {
        var anyHost = false;
        var hostList = new List<string>();
        foreach (var raw in hosts)
        {
            var entry = raw.Trim();
            if (entry == Wildcard)
            {
                anyHost = true;
                continue;
            }
            var host = entry.ToLowerInvariant();
            if (!IsAllowedHostShape(host))
                throw new FormatException(
                    $"Repository host entry '{raw}' is invalid. Use '{Wildcard}' or a DNS host name with at least two " +
                    "labels (e.g. 'github.com'): no scheme, port, path, credentials, trailing dot, IP literal or 'localhost'.");
            if (!hostList.Contains(host, StringComparer.Ordinal))
                hostList.Add(host);
        }
        if (!anyHost && hostList.Count == 0)
            throw new FormatException("The repository host allow-list has no entries.");
        return (anyHost, hostList);
    }

    private List<string> ParseOwners(IEnumerable<string> owners)
    {
        var ownerList = new List<string>();
        foreach (var raw in owners)
        {
            var entry = ParseOwnerEntry(raw.Trim())
                ?? throw new FormatException(
                    $"Repository owner entry '{raw}' is invalid. Use 'host/owner' or 'host/{Wildcard}' (e.g. " +
                    "'github.com/my-org'), where host is a DNS host name and owner is 1-100 of [A-Za-z0-9._-] not " +
                    "starting with '-'.");
            if (!AllowsHost(entry.Host))
                throw new FormatException(
                    $"Repository owner entry '{raw}' names host '{entry.Host}', which is not on the repository host allow-list.");
            var key = $"{entry.Host}/{entry.Owner}";
            if (!ownerList.Contains(key, StringComparer.Ordinal))
                ownerList.Add(key);
        }
        if (ownerList.Count == 0)
            throw new FormatException("The repository owner allow-list has no entries.");
        return ownerList;
    }

    // A lower-case DNS host with >= 2 labels that is not loopback-by-name and cannot be read as an IP address
    // (a resolver/URL parser treats a host whose LAST label is numeric — `127.1`, `0x7f.1`, `1.2.3.4` — as IPv4).
    private static bool IsAllowedHostShape(string host)
    {
        if (host.Length is 0 or > 253 || !SubmoduleUrlPolicy.HostPattern().IsMatch(host))
            return false;
        var lastDot = host.LastIndexOf('.');
        if (lastDot < 0)
            return false;
        if (host == Localhost || host.EndsWith("." + Localhost, StringComparison.Ordinal))
            return false;
        return !NumericLabelPattern().IsMatch(host[(lastDot + 1)..]) && !IPAddress.TryParse(host, out _);
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

    // Exactly `/{owner}/{repo}` or `/{owner}/{repo}.git`. A repo name that still ends in `.git` after the one
    // optional suffix is stripped is refused, because RemoteUrlIdentity would strip it again and the canonical
    // form would no longer name the same path.
    private static bool TrySplitRepositoryPath(string path, out string owner, out string repo)
    {
        owner = repo = string.Empty;
        if (path.Length < 2 || path[0] != '/')
            return false;
        var parts = path[1..].Split('/');
        if (parts.Length != 2)
            return false;
        owner = parts[0];
        repo = parts[1].EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase) ? parts[1][..^GitSuffix.Length] : parts[1];
        return IsValidSegment(owner)
            && IsValidSegment(repo)
            && !repo.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidSegment(string segment) =>
        segment.Length is > 0 and <= MaxSegmentLength
        && segment[0] != '-'
        && segment is not ("." or "..")
        && SegmentPattern().IsMatch(segment);

    private static string Canonicalize(string host, string owner, string repo) =>
        RemoteUrlIdentity.Normalize($"https://{host}/{owner}/{repo}");

    private static (string Owner, string Repo) SegmentsOf(string canonical, string host)
    {
        var path = canonical[("https://".Length + host.Length + 1)..];
        var slash = path.IndexOf('/');
        return (path[..slash], path[(slash + 1)..]);
    }

    // `host/owner` or `host/*`, with the owner folded exactly as a URL's owner is (host-aware, via
    // RemoteUrlIdentity), so a case-insensitive host matches regardless of the configured spelling.
    private static (string Host, string Owner)? ParseOwnerEntry(string entry)
    {
        var slash = entry.IndexOf('/');
        if (slash <= 0)
            return null;
        var host = entry[..slash].ToLowerInvariant();
        var owner = entry[(slash + 1)..];
        if (!IsAllowedHostShape(host))
            return null;
        if (owner == Wildcard)
            return (host, Wildcard);
        if (!IsValidSegment(owner))
            return null;
        return (host, SegmentsOf(Canonicalize(host, owner, "_"), host).Owner);
    }
}
