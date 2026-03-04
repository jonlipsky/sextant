using System.Text.RegularExpressions;

namespace Sextant.Core;

public static partial class GitRemoteNormalizer
{
    // Matches SSH URLs like git@github.com:org/repo or ssh://git@github.com/org/repo
    [GeneratedRegex(@"^(?:ssh://)?([^@]+)@([^:/]+)[:/](.+)$")]
    private static partial Regex SshPattern();

    public static string Normalize(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return remoteUrl;

        var url = remoteUrl.Trim();

        // Convert SSH to HTTPS
        var sshMatch = SshPattern().Match(url);
        if (sshMatch.Success && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                             && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var host = sshMatch.Groups[2].Value;
            var path = sshMatch.Groups[3].Value;
            url = $"https://{host}/{path}";
        }

        // Strip trailing .git
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // Parse as URI to strip credentials and lowercase host
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            // Rebuild without credentials, with lowered host
            url = $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath}";

            // Remove trailing slash if present
            if (url.EndsWith('/'))
                url = url[..^1];
        }

        return url;
    }
}
