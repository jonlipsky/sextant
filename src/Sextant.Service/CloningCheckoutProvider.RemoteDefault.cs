namespace Sextant.Service;

// Issue #199: the clone-mode lookup of a repository's default branch, over the same hardened, token-scoped git
// environment as the top-level fetch (credential helpers reset, the token only as a host-scoped header, no
// redirects while it is attached). It runs in a throwaway repository under the checkout root, so git never
// discovers and reads the configuration of an enclosing repository, and a leaked directory is reclaimed by the
// startup temp-clone sweep.
public sealed partial class CloningCheckoutProvider : IRemoteDefaultBranchResolver
{
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
