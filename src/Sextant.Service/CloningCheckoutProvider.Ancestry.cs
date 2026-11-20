namespace Sextant.Service;

public sealed partial class CloningCheckoutProvider : ICommitAncestryChecker
{
    internal const long DefaultAncestryCacheMaxBytes = 1024L * 1024 * 1024;
    internal const string AncestryHeadRef = "refs/sextant/ancestry-head";

    private static GitEnvironment AncestryGcEnvironment() =>
        new("https", HardeningConfig.Concat(StoreGcConfig)
            .Append(new KeyValuePair<string, string>("gc.auto", "1"))
            .Append(new KeyValuePair<string, string>("gc.cruftPacks", "false")).ToArray());

    /// <summary>Maximum persistent ancestry graph size; an oversized cache is rotated before the next check.</summary>
    public long AncestryCacheMaxBytes { get; init; } = DefaultAncestryCacheMaxBytes;

    public bool? IsAncestor(string repositoryRemoteUrl, string ancestor, string descendant, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(repositoryRemoteUrl) || UrlHasUserInfo(repositoryRemoteUrl)
            || ancestor.Length is not (40 or 64) || descendant.Length is not (40 or 64)
            || !IsHexObjectId(ancestor) || !IsHexObjectId(descendant))
            return null;

        var dirName = ServicePaths.RepoDirectoryName(repositoryRemoteUrl);
        var root = Path.GetFullPath(UseObjectStore ? _paths.GitObjectStoreRoot : _paths.CheckoutRoot);
        var graph = Path.GetFullPath(UseObjectStore
            ? Path.Combine(root, dirName, "ancestry.git")
            : Path.Combine(root, $"{TempClonePrefix}{Guid.NewGuid():N}"));
        if (!IsContainedIn(root, graph))
            return null;

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(CommitReachabilityTimeout);
        lock (RepoGate(dirName))
        {
            try
            {
                if (UseObjectStore && Directory.Exists(graph))
                {
                    var size = DirectorySizeBytes(graph);
                    if (size is null)
                    {
                        _log?.Invoke("Ancestry check unavailable: history store size could not be determined.");
                        return null;
                    }
                    if (size > AncestryCacheMaxBytes)
                    {
                        _log?.Invoke("Ancestry history store exceeded its size limit and was rotated.");
                        DiscardStore(graph);
                        return null;
                    }
                }
                if (!TryPrepareStore(graph, bound.Token) || !EnvironmentConfigSupported(graph, bound.Token))
                {
                    _log?.Invoke("Ancestry check unavailable: history store initialization failed.");
                    return null;
                }
                var local = StoreEnvironment(LocalGitEnvironment);
                local = local with { Config = [.. local.Config, new("core.logAllRefUpdates", "false")] };
                var authority = SubmoduleUrlPolicy.HttpsAuthority(repositoryRemoteUrl);
                var remote = StoreEnvironment(TopLevelEnvironment(_token is not null ? authority : null, repositoryRemoteUrl));
                var previousHead = RunGit(graph, local, CommitReachabilityTimeout, bound.Token,
                    "--git-dir", graph, "rev-parse", "--verify", "--quiet", AncestryHeadRef + "^{commit}");
                var previousHeadId = previousHead.Ok && IsHexObjectId(previousHead.Stdout.Trim())
                    ? previousHead.Stdout.Trim()
                    : null;
                var fetchedAny = false;
                foreach (var commit in new[] { descendant, ancestor }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var id = commit.ToLowerInvariant();
                    if (!RunGit(graph, local, CommitReachabilityTimeout, bound.Token,
                            "--git-dir", graph, "cat-file", "-e", id + "^{commit}").Ok)
                    {
                        var fetched = RunGit(graph, remote, CommitReachabilityTimeout, bound.Token,
                            "--git-dir", graph, "fetch", "--quiet", "--no-tags", "--filter=tree:0",
                            "--end-of-options", repositoryRemoteUrl, id);
                        if (!fetched.Ok)
                        {
                            _log?.Invoke($"Ancestry check unavailable: history fetch failed ({ShortReason(fetched)}).");
                            return null;
                        }
                        fetchedAny = true;
                    }
                }
                if (!KeepOnlyAncestryHead(graph, descendant, local, bound.Token))
                {
                    DiscardStore(graph);
                    return null;
                }
                if (fetchedAny)
                {
                    if (!ScrubStore(graph))
                    {
                        DiscardStore(graph);
                        return null;
                    }
                    var expire = RunGit(graph, local, CommitReachabilityTimeout, bound.Token,
                        "--git-dir", graph, "reflog", "expire", "--expire=now", "--all");
                    if (!expire.Ok)
                    {
                        _log?.Invoke($"Ancestry history reflog cleanup failed ({ShortReason(expire)}); rotating the cache.");
                        DiscardStore(graph);
                        return null;
                    }
                    var maintenance = RunGit(graph, AncestryGcEnvironment(),
                        CommitReachabilityTimeout, bound.Token, "--git-dir", graph, "gc", "--auto", "--quiet");
                    if (!maintenance.Ok)
                        _log?.Invoke($"Ancestry history maintenance failed ({ShortReason(maintenance)}).");
                    var prune = RunGit(graph, AncestryGcEnvironment(),
                        CommitReachabilityTimeout, bound.Token, "--git-dir", graph, "gc", "--prune=now", "--quiet");
                    if (!prune.Ok)
                    {
                        _log?.Invoke($"Ancestry history pruning failed ({ShortReason(prune)}); rotating the cache.");
                        DiscardStore(graph);
                        return null;
                    }
                    if (!ScrubStore(graph))
                    {
                        DiscardStore(graph);
                        return null;
                    }
                    var updatedSize = DirectorySizeBytes(graph);
                    if (updatedSize is null || updatedSize > AncestryCacheMaxBytes)
                    {
                        _log?.Invoke("Ancestry history store exceeded its size limit and was rotated.");
                        DiscardStore(graph);
                        return null;
                    }
                    if (maintenance.Ok && prune.Ok)
                        _log?.Invoke("Ancestry history store maintenance completed.");
                }
                // Never interpret a shallow boundary as proof that an ancestor is absent.
                if (File.Exists(Path.Combine(graph, "shallow")))
                {
                    _log?.Invoke("Ancestry check unavailable: shallow history.");
                    return null;
                }
                var result = RunGit(graph, local, CommitReachabilityTimeout, bound.Token,
                    "--git-dir", graph, "merge-base", "--is-ancestor", ancestor.ToLowerInvariant(), descendant.ToLowerInvariant());
                if (!ScrubStore(graph))
                {
                    DiscardStore(graph);
                    return null;
                }
                if (!result.Ok && result.ExitCode != 1)
                    _log?.Invoke($"Ancestry check unavailable: merge-base failed ({ShortReason(result)}).");
                if (previousHeadId is not null && (result.Ok || result.ExitCode == 1))
                {
                    var followsCachedHead = RunGit(graph, local, CommitReachabilityTimeout, bound.Token,
                        "--git-dir", graph, "merge-base", "--is-ancestor", previousHeadId, descendant.ToLowerInvariant());
                    if (!followsCachedHead.Ok && followsCachedHead.Kind == GitFailureKind.NonZeroExit
                        && followsCachedHead.ExitCode == 1)
                    {
                        _log?.Invoke("Ancestry history diverged from its cached head; rebuilding the graph.");
                        if (!RebuildAncestryGraph(graph, repositoryRemoteUrl, descendant, remote, local, bound.Token))
                            return null;
                    }
                    else if (!followsCachedHead.Ok)
                    {
                        _log?.Invoke($"Ancestry check unavailable: cached history comparison failed ({ShortReason(followsCachedHead)}).");
                        return null;
                    }
                }
                return result.Ok ? true
                    : result.Kind == GitFailureKind.NonZeroExit && result.ExitCode == 1 ? false : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransientProvisioningException)
            {
                _log?.Invoke($"Ancestry check failed ({ex.GetType().Name}).");
                return null;
            }
            finally
            {
                if (!ScrubStore(graph))
                    DiscardStore(graph);
                if (!UseObjectStore)
                    TryDeleteDirectory(graph);
            }
        }
    }

    private bool RebuildAncestryGraph(
        string graph, string repositoryRemoteUrl, string descendant,
        GitEnvironment remote, GitEnvironment local, CancellationToken cancellationToken)
    {
        DiscardStore(graph);
        if (Directory.Exists(graph) || !TryPrepareStore(graph, cancellationToken)
            || !EnvironmentConfigSupported(graph, cancellationToken))
            return false;

        var fetch = RunGit(graph, remote, CommitReachabilityTimeout, cancellationToken,
            "--git-dir", graph, "fetch", "--quiet", "--no-tags", "--end-of-options",
            repositoryRemoteUrl, descendant.ToLowerInvariant());
        if (!fetch.Ok || !KeepOnlyAncestryHead(graph, descendant, local, cancellationToken)
            || !ScrubStore(graph))
        {
            DiscardStore(graph);
            return false;
        }

        var maintenance = RunGit(graph, AncestryGcEnvironment(),
            CommitReachabilityTimeout, cancellationToken, "--git-dir", graph, "gc", "--auto", "--quiet");
        var prune = RunGit(graph, AncestryGcEnvironment(),
            CommitReachabilityTimeout, cancellationToken, "--git-dir", graph, "gc", "--prune=now", "--quiet");
        if (!maintenance.Ok || !prune.Ok || !ScrubStore(graph))
        {
            _log?.Invoke("Ancestry history graph rebuild maintenance failed; rotating the cache.");
            DiscardStore(graph);
            return false;
        }
        var size = DirectorySizeBytes(graph);
        if (size is null || size > AncestryCacheMaxBytes)
        {
            _log?.Invoke("Ancestry history store exceeded its size limit and was rotated.");
            DiscardStore(graph);
            return false;
        }
        _log?.Invoke("Ancestry history store maintenance completed.");
        return true;
    }

    private bool KeepOnlyAncestryHead(
        string graph, string descendant, GitEnvironment local, CancellationToken cancellationToken)
    {
        var resolved = RunGit(graph, local, CommitReachabilityTimeout, cancellationToken,
            "--git-dir", graph, "rev-parse", "--verify", "--quiet", descendant + "^{commit}");
        if (!resolved.Ok || !IsHexObjectId(resolved.Stdout.Trim()))
        {
            _log?.Invoke($"Ancestry history head could not be resolved ({ShortReason(resolved)}).");
            return false;
        }
        var update = RunGit(graph, local, CommitReachabilityTimeout, cancellationToken,
            "--git-dir", graph, "update-ref", AncestryHeadRef, resolved.Stdout.Trim());
        if (!update.Ok)
        {
            _log?.Invoke($"Ancestry history head ref could not be updated ({ShortReason(update)}).");
            return false;
        }

        var refs = RunGit(graph, local, CommitReachabilityTimeout, cancellationToken,
            "--git-dir", graph, "for-each-ref", "--format=%(refname)");
        if (!refs.Ok)
        {
            _log?.Invoke($"Ancestry history refs could not be listed ({ShortReason(refs)}).");
            return false;
        }
        foreach (var name in refs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.Equals(name, AncestryHeadRef, StringComparison.Ordinal))
            {
                var delete = RunGit(graph, local, CommitReachabilityTimeout, cancellationToken,
                    "--git-dir", graph, "update-ref", "-d", name);
                if (!delete.Ok)
                {
                    _log?.Invoke($"Ancestry history ref could not be removed ({ShortReason(delete)}).");
                    return false;
                }
            }
        }
        return true;
    }

    private static long? DirectorySizeBytes(string directory)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return null;
            var pending = new Stack<string>();
            pending.Push(directory);
            long total = 0;
            while (pending.TryPop(out var current))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return null;
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                    else
                        total = checked(total + new FileInfo(entry).Length);
                }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }
}
