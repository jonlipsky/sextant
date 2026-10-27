namespace Sextant.Service;

// Issue #199: the clone-mode lookup of a repository's default branch, over the same hardened, token-scoped git
// environment as the top-level fetch (credential helpers reset, the token only as a host-scoped header, no
// redirects while it is attached). It runs in a throwaway repository under the checkout root, so git never
// discovers and reads the configuration of an enclosing repository, and a leaked directory is reclaimed by the
// startup temp-clone sweep.
public sealed partial class CloningCheckoutProvider : IRemoteDefaultBranchResolver, IRemoteBranchLister, IRemoteBranchHeadResolver, IRemoteCommitBranchChecker
{
    /// <summary>Upper bound on the history fetch of <see cref="IsCommitOnBranch"/>; a large repository's commit graph can take a while.</summary>
    private static readonly TimeSpan CommitReachabilityTimeout = TimeSpan.FromMinutes(2);

    // Issue #209: whether the commit is reachable from a refs/heads/* branch of the repository. Only the branch
    // heads are fetched (commits and trees, no blobs, no tags, no pull refs), so a commit that exists in the remote's
    // object database only through refs/pull/* (a fork-network commit) is never transferred, and so never found.
    // Anything short of a clear "reachable" answer is false (fail closed).
    public bool IsCommitOnBranch(string repositoryRemoteUrl, string commit, CancellationToken cancellationToken = default)
    {
        var id = commit?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(repositoryRemoteUrl) || UrlHasUserInfo(repositoryRemoteUrl)
            || id.Length is not (40 or 64) || !id.All(Uri.IsHexDigit))
            return false;

        var root = Path.GetFullPath(_paths.CheckoutRoot);
        var temp = Path.GetFullPath(Path.Combine(root, $"{TempClonePrefix}{Guid.NewGuid():N}"));
        if (!IsContainedIn(root, temp))
            return false;

        var authority = SubmoduleUrlPolicy.HttpsAuthority(repositoryRemoteUrl);
        var environment = TopLevelEnvironment(_token is not null ? authority : null, repositoryRemoteUrl);
        try
        {
            Directory.CreateDirectory(temp);
            if (!EnvironmentConfigSupported(temp) || !RunGit(temp, environment, "init", "--quiet").Ok)
                return false;
            var fetch = RunGit(temp, environment, CommitReachabilityTimeout, cancellationToken,
                "fetch", "--quiet", "--no-tags", "--filter=blob:none", "--end-of-options", repositoryRemoteUrl,
                "+refs/heads/*:refs/remotes/origin/*");
            if (!fetch.Ok)
                return false;
            var local = LocalGitEnvironment with { AllowedProtocols = AllowedGitProtocols };
            if (!RunGit(temp, local, "rev-parse", "--verify", "--quiet", id + "^{commit}").Ok)
                return false;
            var containing = RunGit(temp, local, "for-each-ref", "--format=%(refname)", "--contains", id, "refs/remotes/origin/");
            return containing.Ok && containing.Stdout.Split('\n').Any(l => l.TrimEnd('\r').StartsWith("refs/remotes/origin/", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransientProvisioningException)
        {
            return false;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    // Issue #203: the commit refs/heads/<branch> names on the remote, over the same hardened environment. Anything
    // short of one clean, exactly matching line is null (fail closed).
    public string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRemoteUrl) || UrlHasUserInfo(repositoryRemoteUrl)
            || !RemoteBranchHeads.IsVerifiableBranchName(branch))
            return null;

        var root = Path.GetFullPath(_paths.CheckoutRoot);
        var temp = Path.GetFullPath(Path.Combine(root, $"{TempClonePrefix}{Guid.NewGuid():N}"));
        if (!IsContainedIn(root, temp))
            return null;

        var authority = SubmoduleUrlPolicy.HttpsAuthority(repositoryRemoteUrl);
        var environment = TopLevelEnvironment(_token is not null ? authority : null, repositoryRemoteUrl);
        try
        {
            Directory.CreateDirectory(temp);
            if (!EnvironmentConfigSupported(temp) || !RunGit(temp, environment, "init", "--quiet").Ok)
                return null;
            var result = RunGit(temp, environment, RemoteDefaultBranchTimeout, cancellationToken,
                "ls-remote", "--end-of-options", repositoryRemoteUrl, "refs/heads/" + branch);
            return result.Ok ? RemoteBranchHead.Parse(result.Stdout, branch) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransientProvisioningException)
        {
            return null;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>Upper bound on the <c>ls-remote --heads</c> call of the upstream-branch reconcile (issue #338); it runs in the maintenance loop, off the writer.</summary>
    private static readonly TimeSpan RemoteBranchListingTimeout = TimeSpan.FromSeconds(30);

    // Issue #338: the complete list of the remote's branches, over the same hardened, token-scoped environment as the
    // default-branch lookup. Anything short of a clean, parsed listing is null (never a partial set).
    public IReadOnlySet<string>? ListBranches(string repositoryRemoteUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryRemoteUrl) || UrlHasUserInfo(repositoryRemoteUrl))
            return null;

        var root = Path.GetFullPath(_paths.CheckoutRoot);
        var temp = Path.GetFullPath(Path.Combine(root, $"{TempClonePrefix}{Guid.NewGuid():N}"));
        if (!IsContainedIn(root, temp))
            return null;

        var authority = SubmoduleUrlPolicy.HttpsAuthority(repositoryRemoteUrl);
        var environment = TopLevelEnvironment(_token is not null ? authority : null, repositoryRemoteUrl);
        try
        {
            Directory.CreateDirectory(temp);
            if (!EnvironmentConfigSupported(temp) || !RunGit(temp, environment, "init", "--quiet").Ok)
                return null;
            var result = RunGit(temp, environment, RemoteBranchListingTimeout,
                "ls-remote", "--heads", "--end-of-options", repositoryRemoteUrl);
            return result.Ok ? RemoteBranchHeads.Parse(result.Stdout) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransientProvisioningException)
        {
            return null;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>Upper bound on the <c>ls-remote</c> call: it runs before an ensure is registered, so it stays short.</summary>
    private static readonly TimeSpan RemoteDefaultBranchTimeout = TimeSpan.FromSeconds(30);

    public string? ResolveDefaultBranch(string repositoryRemoteUrl)
    {
        // Credentials only ever arrive through SEXTANT_SERVICE_CHECKOUT_TOKEN, exactly as for a clone.
        if (string.IsNullOrWhiteSpace(repositoryRemoteUrl) || UrlHasUserInfo(repositoryRemoteUrl))
            return null;

        var root = Path.GetFullPath(_paths.CheckoutRoot);
        var temp = Path.GetFullPath(Path.Combine(root, $"{TempClonePrefix}{Guid.NewGuid():N}"));
        if (!IsContainedIn(root, temp))
            return null;

        var authority = SubmoduleUrlPolicy.HttpsAuthority(repositoryRemoteUrl);
        var environment = TopLevelEnvironment(_token is not null ? authority : null, repositoryRemoteUrl);
        try
        {
            Directory.CreateDirectory(temp);
            if (!EnvironmentConfigSupported(temp) || !RunGit(temp, environment, "init", "--quiet").Ok)
                return null;
            var result = RunGit(temp, environment, RemoteDefaultBranchTimeout,
                "ls-remote", "--symref", "--end-of-options", repositoryRemoteUrl, "HEAD");
            var branch = result.Ok ? RemoteDefaultBranch.ParseSymref(result.Stdout) : null;
            if (branch is null)
                _log?.Invoke($"Could not determine the default branch of '{SanitizeUrlForLog(repositoryRemoteUrl)}'; a restricted ensure will not make its branch the default.");
            return branch;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransientProvisioningException)
        {
            return null;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }
}
