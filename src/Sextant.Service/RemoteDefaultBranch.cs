namespace Sextant.Service;

/// <summary>
/// Looks up a repository's default branch from the REMOTE itself (the branch its <c>HEAD</c> names), never from a
/// request (issue #199). The service consults it only for an ensure whose caller may not pick the repository's
/// default (<see cref="EnsureSnapshotRequest.RestrictsImplicitDefault"/>), and only while the repository has no
/// default branch yet: such a caller's first branch becomes the default only when it is this one.
/// </summary>
public interface IRemoteDefaultBranchResolver
{
    /// <summary>
    /// The remote's default branch name (for example <c>main</c>), or <c>null</c> when it cannot be determined
    /// (unreachable, unauthorized, no symbolic <c>HEAD</c>, a refused URL). Never throws for such a remote; the
    /// service treats a throw like <c>null</c> (fail closed: the branch is created without the default).
    /// </summary>
    string? ResolveDefaultBranch(string repositoryRemoteUrl);
}

/// <summary>Parsing of <c>git ls-remote --symref &lt;url&gt; HEAD</c> for <see cref="IRemoteDefaultBranchResolver"/>.</summary>
public static class RemoteDefaultBranch
{
    private const string BranchRefPrefix = "refs/heads/";

    /// <summary>Longest branch name accepted from a remote (a sanity bound; git's own ref limits are larger).</summary>
    internal const int MaxBranchNameLength = 255;

    /// <summary>
    /// The branch named by the <c>ref: refs/heads/&lt;name&gt;&#9;HEAD</c> line of <c>ls-remote --symref</c> output,
    /// or <c>null</c> when there is none, more than one, or the name is empty, too long, or contains whitespace or a
    /// control character.
    /// </summary>
    public static string? ParseSymref(string lsRemoteOutput)
    {
        string? branch = null;
        foreach (var raw in lsRemoteOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("ref: ", StringComparison.Ordinal))
                continue;
            var tab = line.IndexOf('\t');
            if (tab < 0 || !string.Equals(line[(tab + 1)..], "HEAD", StringComparison.Ordinal))
                continue;
            var target = line["ref: ".Length..tab];
            if (!target.StartsWith(BranchRefPrefix, StringComparison.Ordinal))
                return null;
            var name = target[BranchRefPrefix.Length..];
            if (branch is not null || !IsPlausibleBranchName(name))
                return null;
            branch = name;
        }
        return branch;
    }

    private static bool IsPlausibleBranchName(string name) =>
        name.Length is > 0 and <= MaxBranchNameLength
        && !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
