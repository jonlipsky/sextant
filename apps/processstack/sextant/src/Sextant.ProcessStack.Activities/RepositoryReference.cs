using Sextant.Core;

namespace Sextant.ProcessStack.Activities;

/// <summary>Repository URL helpers built on <c>Sextant.Core</c>'s normalizers, the same code the service uses.</summary>
internal static class RepositoryReference
{
    /// <summary>The host an <c>owner/repo</c> reference names when none is given.</summary>
    public const string DefaultHost = "github.com";

    private const string HttpsPrefix = "https://";
    private const string HttpPrefix = "http://";
    private const string GitSuffix = ".git";

    /// <summary>
    /// The join key for a repository URL: the SVC-5 canonical form (<c>https://{host}/{owner}/{repo}</c>,
    /// folded by <see cref="RemoteUrlIdentity.Normalize"/>) when the URL has an acceptable shape, otherwise the
    /// <see cref="GitRemoteNormalizer"/> form (ssh to https, no credentials) folded the same way. Spellings of
    /// one repository that differ only in case (on case-insensitive hosts), a trailing <c>.git</c> or
    /// <c>/</c>, a default port or ssh-versus-https share a key. <c>""</c> for an empty URL.
    /// </summary>
    public static string KeyFor(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        var verdict = RepositoryUrlShape.Evaluate(trimmed);
        return verdict.Ok ? verdict.Canonical : RemoteUrlIdentity.Normalize(GitRemoteNormalizer.Normalize(trimmed));
    }

    /// <summary>
    /// The URL to send to the service for a repository remote: an <c>http(s)</c> URL is returned trimmed but
    /// otherwise verbatim (snapshot identity hashes the submitted spelling), and an ssh or scp-like remote
    /// (<c>git@host:owner/repo.git</c>) becomes <c>https://host/owner/repo.git</c>, the spelling of GitHub's
    /// <c>clone_url</c>. Anything else is returned trimmed, for the shape check to refuse.
    /// </summary>
    public static string ToCloneUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var converted = GitRemoteNormalizer.Normalize(trimmed);
        return converted.StartsWith(HttpsPrefix, StringComparison.Ordinal) ? converted + GitSuffix : trimmed;
    }

    /// <summary><c>https://{host}/{owner}/{repo}.git</c>, the spelling of GitHub's <c>clone_url</c>.</summary>
    public static string CloneUrlFor(string host, string owner, string repo) =>
        $"{HttpsPrefix}{host}/{owner}/{RepositoryUrlShape.StripGitSuffix(repo)}{GitSuffix}";

    /// <summary>
    /// Parses a repository reference typed by a user: an https URL, an ssh or scp-like remote,
    /// <c>host/owner/repo</c> (the first segment contains a dot) or <c>owner/repo</c> on
    /// <paramref name="defaultHost"/>. The result is checked with the SVC-5 shape rules.
    /// </summary>
    public static RepositoryUrlVerdict Parse(string reference, string defaultHost)
    {
        if (reference.Contains("://", StringComparison.Ordinal) || reference.Contains('@'))
            return RepositoryUrlShape.Evaluate(ToCloneUrl(reference));

        var parts = reference.Split('/');
        return parts.Length switch
        {
            2 => RepositoryUrlShape.Evaluate(CloneUrlFor(defaultHost, parts[0], parts[1])),
            3 when parts[0].Contains('.') =>
                RepositoryUrlShape.Evaluate(CloneUrlFor(parts[0].ToLowerInvariant(), parts[1], parts[2])),
            _ => RepositoryUrlVerdict.Reject(RepositoryUrlShape.PathNotAllowed),
        };
    }
}
