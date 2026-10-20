namespace Sextant.Service;

/// <summary>
/// Lists the branches a repository has on its REMOTE (issue #338), for the maintenance loop's upstream-branch
/// reconcile. Credentials are the service's own checkout token, never a request's.
/// </summary>
public interface IRemoteBranchLister
{
    /// <summary>
    /// The complete set of branch names (<c>refs/heads/*</c>, without the prefix) the remote has, or <c>null</c> when
    /// the listing is not complete or trustworthy (unreachable, unauthorized, timeout, non-zero exit, unparseable
    /// output, a refused URL). Never a partial set. The service treats a throw like <c>null</c>.
    /// </summary>
    IReadOnlySet<string>? ListBranches(string repositoryRemoteUrl);
}

/// <summary>Parsing of <c>git ls-remote --heads</c> output for <see cref="IRemoteBranchLister"/>.</summary>
public static class RemoteBranchHeads
{
    private const string BranchRefPrefix = "refs/heads/";

    /// <summary>
    /// The branch names of <c>ls-remote --heads</c> output (<c>&lt;object id&gt;&#9;refs/heads/&lt;name&gt;</c> per
    /// line; the name may contain slashes). Refs outside <c>refs/heads/</c> are ignored. Returns <c>null</c> for output
    /// that is not ls-remote output (a line without an object id and a tab, an implausible branch name), so a damaged
    /// listing is never read as a shorter one. Empty output is an empty set.
    /// </summary>
    public static IReadOnlySet<string>? Parse(string lsRemoteOutput)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lsRemoteOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var tab = line.IndexOf('\t');
            if (tab < 1 || !IsObjectId(line.AsSpan(0, tab)))
                return null;
            var reference = line[(tab + 1)..];
            if (!reference.StartsWith(BranchRefPrefix, StringComparison.Ordinal))
                continue;
            var name = reference[BranchRefPrefix.Length..];
            if (!IsPlausibleBranchName(name))
                return null;
            names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// True when a stored branch name is a plain <c>refs/heads/</c> short name that a listing can be compared against: no
    /// <c>refs/</c> prefix and nothing git ref names cannot contain. Anything else is never read as absent (issue #338).
    /// </summary>
    public static bool IsVerifiableBranchName(string name)
    {
        if (!IsPlausibleBranchName(name) || name.StartsWith("refs/", StringComparison.Ordinal)
            || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.') || name.StartsWith('-')
            || name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal) || name == "@")
            return false;
        if (name.Any(c => c is '~' or '^' or ':' or '?' or '*' or '[' or '\\' or '\u007f'))
            return false;
        return name.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
    }

    private static bool IsObjectId(ReadOnlySpan<char> id)
    {
        if (id.Length is not (40 or 64))
            return false;
        foreach (var c in id)
            if (!char.IsAsciiHexDigit(c))
                return false;
        return true;
    }

    private static bool IsPlausibleBranchName(string name) =>
        name.Length is > 0 and <= RemoteDefaultBranch.MaxBranchNameLength
        && !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
