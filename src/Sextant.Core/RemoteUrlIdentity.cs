namespace Sextant.Core;

/// <summary>
/// The ONE canonical rule that folds identity-neutral SPELLING differences of a repository remote URL
/// to a single key: surrounding whitespace, a trailing <c>/</c>, a trailing <c>.git</c>, and letter
/// case. It is the shared identity boundary used by BOTH the on-disk checkout directory
/// (<c>ServicePaths.RepoDirectoryName</c> / <c>ShortUrlHash</c>) and the catalog repository row
/// (<c>SnapshotStore.EnsureRepository</c> / <c>GetRepositoryId</c>), so the "same repository" the
/// checkout provider locates on disk and the "same repository" the catalog deduplicates can never drift
/// (issue #92). Before this helper existed the catalog keyed identity on the raw URL string while the
/// checkout directory already stripped <c>.git</c>/trailing-slash, so trivially-equivalent spellings of
/// one repository (a trailing <c>.git</c>, a trailing slash, or a different case) produced MULTIPLE
/// <c>repositories</c> rows and defeated the cross-repo/submodule deduplication the service exists to
/// provide.
/// <para>
/// It folds ONLY spelling. It never touches the commit, tree, schema, analyzer, config, or toolchain,
/// so content-addressing across genuinely DIFFERENT commits of the same repository is unaffected — two
/// commits still produce distinct snapshots.
/// </para>
/// <para>
/// Case folding is HOST-AWARE, matching the convention already established by
/// <see cref="GitRemoteNormalizer"/> (which lower-cases the host and preserves path case). The URL host
/// is ALWAYS case-folded (DNS host names are case-insensitive). The owner/repo PATH is case-folded only
/// for hosts KNOWN to treat repository paths case-insensitively (github.com, gitlab.com, bitbucket.org,
/// azure devops) — this is what collapses <c>.../MixAndMatch</c> and <c>.../mixandmatch</c> on GitHub,
/// the exact #92 evidence. For any OTHER (possibly self-hosted, possibly case-sensitive) host the path
/// case is PRESERVED, so two genuinely-distinct case-sensitive repositories (<c>.../Repo</c> vs
/// <c>.../repo</c>) are never merged into one catalog row / checkout directory. Different owners
/// (<c>org-a/common</c> vs <c>org-b/common</c>) differ in text, not case, so they are never merged and
/// cross-tenant isolation is preserved regardless of host.
/// </para>
/// <para>
/// This is deliberately distinct from <see cref="GitRemoteNormalizer"/>, which normalizes PROJECT
/// identity more aggressively (SSH→HTTPS rewrite, credential stripping). This helper is the narrower
/// checkout/catalog identity fold that matches what the checkout directory hash already did
/// (trim / trailing-slash / trailing-<c>.git</c>), plus the host-aware case fold.
/// </para>
/// </summary>
public static class RemoteUrlIdentity
{
    /// <summary>
    /// Hosts whose repository PATH (owner/repo) is case-insensitive, so the whole URL key can be safely
    /// lower-cased. Any host NOT in this set is treated as potentially case-sensitive: only its host name
    /// is folded and the path case is preserved.
    /// </summary>
    private static readonly HashSet<string> CaseInsensitivePathHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "www.github.com",
        "gist.github.com",
        "gitlab.com",
        "bitbucket.org",
        "dev.azure.com",
        "ssh.dev.azure.com",
    };

    /// <summary>
    /// Folds identity-neutral spelling differences of <paramref name="repositoryRemoteUrl"/> to a single
    /// canonical key: trims surrounding whitespace, strips a trailing <c>/</c>, strips a trailing
    /// <c>.git</c> (case-insensitively), then case-folds host-awarely (the whole key for a known
    /// case-insensitive host, host-only otherwise). A null/empty input maps to the empty string.
    /// Idempotent: normalizing an already-normalized value returns it unchanged.
    /// </summary>
    public static string Normalize(string? repositoryRemoteUrl)
    {
        var url = (repositoryRemoteUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length == 0)
            return string.Empty;
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        var host = ExtractHost(url);
        if (host is null)
            return url; // Host indeterminable: conservatively fold nothing beyond trim/.git/slash.

        // A known case-insensitive host: the whole key (host AND path) can be lower-cased safely.
        if (CaseInsensitivePathHosts.Contains(host))
            return url.ToLowerInvariant();

        // A possibly case-sensitive host: fold the host name only and PRESERVE the path case so two
        // genuinely-distinct case-sensitive repositories never collapse into one identity.
        return FoldHostOnly(url, host);
    }

    /// <summary>
    /// The lower-cased host of an <c>http(s)</c>/<c>ssh</c>/<c>git</c> URL, or of an scp-like
    /// <c>[user@]host:path</c> remote, or null when the host cannot be determined.
    /// </summary>
    private static string? ExtractHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            return uri.Host.ToLowerInvariant();

        // scp-like git remote: [user@]host:owner/repo (no scheme, so Uri.TryCreate fails above).
        var colon = url.IndexOf(':');
        if (colon > 0)
        {
            var at = url.IndexOf('@');
            var start = at >= 0 && at < colon ? at + 1 : 0;
            if (colon > start)
                return url[start..colon].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Lower-cases the first occurrence of <paramref name="lowerHost"/> in <paramref name="url"/>, leaving the rest untouched.</summary>
    private static string FoldHostOnly(string url, string lowerHost)
    {
        var idx = url.IndexOf(lowerHost, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return url;
        return string.Concat(url.AsSpan(0, idx), lowerHost, url.AsSpan(idx + lowerHost.Length));
    }
}
