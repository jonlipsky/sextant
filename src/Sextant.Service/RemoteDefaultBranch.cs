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

    /// <summary>Looks up the default; cancelling <paramref name="cancellationToken"/> stops the remote call where the
    /// resolver can.</summary>
    string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken);
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
/// to check user advances and to recover a published default head from a disconnected pointer CAS.
/// Implemented by the clone-mode checkout provider;
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

    /// <summary>
    /// Returns a verified head, or a verified absence (<c>Head = null</c>); a null observation means the remote could not
    /// be observed.
    /// </summary>
    RemoteBranchHeadObservation? ObserveBranchHead(
        string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Observes the default branch and the requested head in one remote advertisement, or <c>null</c> when the remote could
    /// not be observed. A null answer is never final: the refresh that asked releases its intent with a bounded retry time,
    /// and any later intent of the branch supersedes it, so the branch is never stranded behind it.
    /// </summary>
    RemoteBranchStateObservation? ObserveBranchState(
        string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default);
}

public sealed record RemoteBranchHeadObservation(string? Head);
public sealed record RemoteBranchStateObservation(string DefaultBranch, string Head);

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
    /// Unreachable, unauthorized, unparseable or a refused URL is <c>false</c>.
    /// Cancelling <paramref name="cancellationToken"/> stops the remote call.
    /// </summary>
    bool IsCommitOnBranch(string repositoryRemoteUrl, string commit, CancellationToken cancellationToken = default);

    /// <summary>Returns null when reachability could not be determined; a throw is also unavailable.</summary>
    bool? ObserveCommitOnBranch(string repositoryRemoteUrl, string commit, CancellationToken cancellationToken = default);
}

/// <summary>Checks git ancestry using complete commit history, fetching missing objects without changing the checkout.</summary>
public interface ICommitAncestryChecker
{
    /// <summary>True for an ancestor (including equality), false for unrelated commits, null when history is unknown.</summary>
    bool? IsAncestor(string repositoryRemoteUrl, string ancestor, string descendant, CancellationToken cancellationToken = default);

    /// <summary>Checks ancestry and explains an unknown result without exposing credentials.</summary>
    AncestryResult CheckAncestry(string repositoryRemoteUrl, string ancestor, string descendant, CancellationToken cancellationToken = default) =>
        new(IsAncestor(repositoryRemoteUrl, ancestor, descendant, cancellationToken), "ancestry unavailable");
}

public readonly record struct AncestryResult(bool? IsAncestor, string? Detail = null);

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
