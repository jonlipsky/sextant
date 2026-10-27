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

/// <summary>
/// Looks up the commit a branch of a repository points at on the REMOTE itself (issue #203). The service consults it
/// only to check the target of a user caller's branch advance. Implemented by the clone-mode checkout provider;
/// without an implementation (locate mode) the check is skipped.
/// </summary>
public interface IRemoteBranchHeadResolver
{
    /// <summary>
    /// The commit id <c>refs/heads/&lt;branch&gt;</c> names on the remote, or <c>null</c> when it cannot be determined
    /// (unreachable, unauthorized, no such branch, a refused URL or branch name). A throw is treated like <c>null</c>.
    /// Cancelling <paramref name="cancellationToken"/> stops the remote call.
    /// </summary>
    string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides whether a commit is reachable from a <c>refs/heads/*</c> branch of a repository as the REMOTE holds it
/// (issue #209). The service consults it for a user caller's <c>branch_update: none</c> ensure, so fork-network
/// commits (reachable only through <c>refs/pull/*</c> or shared objects) are never fetched into an evaluated checkout.
/// Implemented by the clone-mode checkout provider; without an implementation (locate mode) the check is skipped.
/// </summary>
public interface IRemoteCommitBranchChecker
{
    /// <summary>
    /// <c>true</c> only when <paramref name="commit"/> (a full object id) is reachable from a branch of the remote.
    /// Unreachable, unauthorized, unparseable or a refused URL is <c>false</c>. A throw is treated like <c>false</c>.
    /// Cancelling <paramref name="cancellationToken"/> stops the remote call.
    /// </summary>
    bool IsCommitOnBranch(string repositoryRemoteUrl, string commit, CancellationToken cancellationToken = default);
}

/// <summary>Parsing of <c>git ls-remote &lt;url&gt; refs/heads/&lt;branch&gt;</c> for <see cref="IRemoteBranchHeadResolver"/>.</summary>
public static class RemoteBranchHead
{
    /// <summary>
    /// The object id of exactly <c>refs/heads/&lt;branch&gt;</c> in the output, or <c>null</c> when it is absent, listed
    /// more than once, or any line is not <c>&lt;object id&gt;&#9;&lt;ref&gt;</c>.
    /// </summary>
    public static string? Parse(string lsRemoteOutput, string branch)
    {
        var wanted = "refs/heads/" + branch;
        string? head = null;
        foreach (var raw in lsRemoteOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var tab = line.IndexOf('\t');
            if (tab < 1)
                return null;
            var id = line[..tab];
            if (id.Length is not (40 or 64) || !id.All(Uri.IsHexDigit))
                return null;
            if (!string.Equals(line[(tab + 1)..], wanted, StringComparison.Ordinal))
                continue;
            if (head is not null)
                return null;
            head = id;
        }
        return head;
    }
}
