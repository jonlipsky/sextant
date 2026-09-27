namespace Sextant.Service.SdkPin;

/// <summary>
/// Reads the commit a checkout's <c>HEAD</c> points at straight from its git metadata (no git process):
/// a detached <c>HEAD</c> (how the service's own checkouts are provisioned), a loose ref, or a packed ref,
/// following a <c>.git</c> file (<c>gitdir:</c>) and a worktree's <c>commondir</c>. Used by the SDK-pin
/// journal (issue #113) to tell a checkout that was RE-CHECKED-OUT or replaced since a crash (its
/// <c>global.json</c> legitimately changed) from one whose file was modified in place (which must not be
/// indexed). Returns null whenever the commit cannot be determined; callers must then fail closed.
/// </summary>
internal static class CheckoutHead
{
    private const string RefPrefix = "ref:";

    public static string? TryRead(string checkoutDir)
    {
        try
        {
            var gitDir = GitDirectory(checkoutDir);
            if (gitDir is null)
                return null;

            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            if (IsObjectId(head))
                return head.ToLowerInvariant();
            if (!head.StartsWith(RefPrefix, StringComparison.Ordinal))
                return null;

            var refName = head[RefPrefix.Length..].Trim();
            if (!IsSafeRefName(refName))
                return null;

            var commonDir = CommonDirectory(gitDir);
            string[] refDirs = commonDir is null ? [gitDir] : [gitDir, commonDir];
            foreach (var dir in refDirs)
            {
                var loose = Path.Combine(dir, refName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose) && File.ReadAllText(loose).Trim() is var sha && IsObjectId(sha))
                    return sha.ToLowerInvariant();
            }

            var packed = Path.Combine(commonDir ?? gitDir, "packed-refs");
            if (!File.Exists(packed))
                return null;
            foreach (var line in File.ReadLines(packed))
            {
                var space = line.IndexOf(' ');
                if (space > 0 && string.Equals(line[(space + 1)..].Trim(), refName, StringComparison.Ordinal)
                    && IsObjectId(line[..space]))
                    return line[..space].ToLowerInvariant();
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? GitDirectory(string checkoutDir)
    {
        var dotGit = Path.Combine(checkoutDir, ".git");
        if (Directory.Exists(dotGit))
            return dotGit;
        if (!File.Exists(dotGit))
            return null;

        // A worktree / submodule: ".git" is a file "gitdir: <path>".
        var content = File.ReadAllText(dotGit).Trim();
        const string prefix = "gitdir:";
        if (!content.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var target = content[prefix.Length..].Trim();
        var full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(checkoutDir, target));
        return Directory.Exists(full) ? full : null;
    }

    private static string? CommonDirectory(string gitDir)
    {
        var file = Path.Combine(gitDir, "commondir");
        if (!File.Exists(file))
            return null;
        var target = File.ReadAllText(file).Trim();
        var full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(gitDir, target));
        return Directory.Exists(full) ? full : null;
    }

    private static bool IsSafeRefName(string refName) =>
        refName.StartsWith("refs/", StringComparison.Ordinal)
        && !refName.Contains("..", StringComparison.Ordinal)
        && !refName.Contains('\\')
        && !Path.IsPathRooted(refName);

    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
}
