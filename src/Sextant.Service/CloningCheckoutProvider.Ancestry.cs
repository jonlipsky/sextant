using System.Collections.Concurrent;

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

    /// <summary>Independent deadline for building and maintaining a history graph.</summary>
    public TimeSpan AncestryBuildTimeout { get; init; } = TimeSpan.FromMinutes(15);

    private sealed class AncestryWork
    {
        public readonly Dictionary<(string Ancestor, string Descendant), bool> Results = [];
        public Task<AncestryResult>? Pending;
        public (string Ancestor, string Descendant) PendingKey;
    }

    private readonly ConcurrentDictionary<string, AncestryWork> _ancestryWork = new(StringComparer.Ordinal);

    public bool? IsAncestor(string repositoryRemoteUrl, string ancestor, string descendant, CancellationToken cancellationToken = default) =>
        CheckAncestry(repositoryRemoteUrl, ancestor, descendant, cancellationToken).IsAncestor;

    public AncestryResult CheckAncestry(string repositoryRemoteUrl, string ancestor, string descendant, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(repositoryRemoteUrl) || UrlHasUserInfo(repositoryRemoteUrl)
            || ancestor.Length is not (40 or 64) || descendant.Length is not (40 or 64)
            || !IsHexObjectId(ancestor) || !IsHexObjectId(descendant))
            return new(null, cancellationToken.IsCancellationRequested ? "timeout" : "invalid ancestry input");

        var dirName = ServicePaths.RepoDirectoryName(repositoryRemoteUrl);
        var key = (ancestor.ToLowerInvariant(), descendant.ToLowerInvariant());
        var work = _ancestryWork.GetOrAdd(dirName, _ => new AncestryWork());
        lock (work)
        {
            if (work.Pending is { IsCompleted: true } completed)
            {
                var result = completed.GetAwaiter().GetResult();
                if (result.IsAncestor is bool known)
                {
                    if (work.Results.Count >= 4096)
                        work.Results.Clear();
                    work.Results[work.PendingKey] = known;
                }
                work.Pending = null;
                if (work.PendingKey == key && result.IsAncestor is null)
                    return result;
            }
            if (work.Results.TryGetValue(key, out var cached))
                return new(cached);
            if (work.Pending is not null)
                return new(null, "rebuild in progress");
            work.PendingKey = key;
            work.Pending = Task.Run(() => BuildAncestryResult(repositoryRemoteUrl, key.Item1, key.Item2), CancellationToken.None);
            return new(null, "rebuild in progress");
        }
    }

    private AncestryResult BuildAncestryResult(string repositoryRemoteUrl, string ancestor, string descendant)
    {
        try
        {
            using var bound = new CancellationTokenSource(AncestryBuildTimeout);
            var result = IsAncestorCore(repositoryRemoteUrl, ancestor, descendant, bound.Token, out var detail);
            if (result is not null)
                return new(result);
            return new(null, bound.IsCancellationRequested ? "timeout" : detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransientProvisioningException or InvalidOperationException)
        {
            _log?.Invoke($"Ancestry check failed ({ex.GetType().Name}).");
            return new(null, "history store unavailable");
        }
    }

    private bool? IsAncestorCore(string repositoryRemoteUrl, string ancestor, string descendant, CancellationToken cancellationToken, out string detail)
    {
        detail = "history store unavailable";
        var dirName = ServicePaths.RepoDirectoryName(repositoryRemoteUrl);
        var root = Path.GetFullPath(UseObjectStore ? _paths.GitObjectStoreRoot : _paths.CheckoutRoot);
        var graph = Path.GetFullPath(UseObjectStore
            ? Path.Combine(root, dirName, "ancestry.git")
            : Path.Combine(root, $"{TempClonePrefix}{Guid.NewGuid():N}"));
        if (!IsContainedIn(root, graph))
            return null;

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (UseObjectStore && !Directory.Exists(graph))
        {
            var local = StoreEnvironment(LocalGitEnvironment);
            local = local with { Config = [.. local.Config, new("core.logAllRefUpdates", "false")] };
            var authority = SubmoduleUrlPolicy.HttpsAuthority(repositoryRemoteUrl);
            var remote = StoreEnvironment(TopLevelEnvironment(_token is not null ? authority : null, repositoryRemoteUrl));
            if (!RebuildAncestryGraph(graph, repositoryRemoteUrl, descendant, remote, local, bound.Token, out detail))
                return null;
        }
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
            var previousHead = RunGit(graph, local, AncestryBuildTimeout, bound.Token,
                "--git-dir", graph, "rev-parse", "--verify", "--quiet", AncestryHeadRef + "^{commit}");
            var previousHeadId = previousHead.Ok && IsHexObjectId(previousHead.Stdout.Trim())
                ? previousHead.Stdout.Trim()
                : null;
            var fetchedAny = false;
            foreach (var commit in new[] { descendant, ancestor }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var id = commit.ToLowerInvariant();
                if (!RunGit(graph, local, AncestryBuildTimeout, bound.Token,
                        "--git-dir", graph, "cat-file", "-e", id + "^{commit}").Ok)
                {
                    var fetched = RunGit(graph, remote, AncestryBuildTimeout, bound.Token,
                        "--git-dir", graph, "fetch", "--quiet", "--no-tags", "--filter=tree:0",
                        "--end-of-options", repositoryRemoteUrl, id);
                    if (!fetched.Ok)
                    {
                        _log?.Invoke($"Ancestry check unavailable: history fetch failed ({ShortReason(fetched)}).");
                        detail = fetched.Kind == GitFailureKind.Timeout ? "timeout" :
                            $"fetch failed: exit {fetched.ExitCode}: {ShortReason(fetched)}";
                        return null;
                    }
                    fetchedAny = true;
                }
            }
            if (File.Exists(Path.Combine(graph, "shallow")))
            {
                detail = "shallow history";
                return null;
            }
            var result = RunGit(graph, local, AncestryBuildTimeout, bound.Token,
                "--git-dir", graph, "merge-base", "--is-ancestor", ancestor.ToLowerInvariant(), descendant.ToLowerInvariant());
            if (!result.Ok && (result.Kind != GitFailureKind.NonZeroExit || result.ExitCode != 1))
            {
                detail = $"merge-base failed: {ShortReason(result)}";
                return null;
            }
            var headToKeep = descendant;
            if (previousHeadId is not null)
            {
                var follows = RunGit(graph, local, AncestryBuildTimeout, bound.Token,
                    "--git-dir", graph, "merge-base", "--is-ancestor", previousHeadId, descendant);
                if (!follows.Ok && follows.Kind == GitFailureKind.NonZeroExit && follows.ExitCode == 1)
                {
                    var older = RunGit(graph, local, AncestryBuildTimeout, bound.Token,
                        "--git-dir", graph, "merge-base", "--is-ancestor", descendant, previousHeadId);
                    if (older.Ok)
                        headToKeep = previousHeadId;
                    else if (older.Kind == GitFailureKind.NonZeroExit && older.ExitCode == 1)
                    {
                        _log?.Invoke("Ancestry history diverged from its cached head; rebuilding the graph.");
                        if (!RebuildAncestryGraph(graph, repositoryRemoteUrl, descendant, remote, local, bound.Token, out detail))
                            return null;
                        return result.Ok;
                    }
                    else
                    {
                        detail = $"cached history comparison failed: {ShortReason(older)}";
                        return null;
                    }
                }
                else if (!follows.Ok)
                {
                    detail = $"cached history comparison failed: {ShortReason(follows)}";
                    return null;
                }
            }
            if (!KeepOnlyAncestryHead(graph, headToKeep, local, bound.Token))
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
                var expire = RunGit(graph, local, AncestryBuildTimeout, bound.Token,
                    "--git-dir", graph, "reflog", "expire", "--expire=now", "--all");
                if (!expire.Ok)
                {
                    _log?.Invoke($"Ancestry history reflog cleanup failed ({ShortReason(expire)}); rotating the cache.");
                    DiscardStore(graph);
                    return null;
                }
                var maintenance = RunGit(graph, AncestryGcEnvironment(),
                    AncestryBuildTimeout, bound.Token, "--git-dir", graph, "gc", "--auto", "--quiet");
                if (!maintenance.Ok)
                    _log?.Invoke($"Ancestry history maintenance failed ({ShortReason(maintenance)}).");
                var prune = RunGit(graph, AncestryGcEnvironment(),
                    AncestryBuildTimeout, bound.Token, "--git-dir", graph, "gc", "--prune=now", "--quiet");
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
            if (!ScrubStore(graph))
            {
                DiscardStore(graph);
                return null;
            }
            return result.Ok;
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

    private bool RebuildAncestryGraph(
        string graph, string repositoryRemoteUrl, string descendant,
        GitEnvironment remote, GitEnvironment local, CancellationToken cancellationToken, out string detail)
    {
        detail = "history store unavailable";
        var staged = graph + ".building-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!TryPrepareStore(staged, cancellationToken) || !EnvironmentConfigSupported(staged, cancellationToken))
                return false;

            var fetch = RunGit(staged, remote, AncestryBuildTimeout, cancellationToken,
                "--git-dir", staged, "fetch", "--quiet", "--no-tags", "--filter=tree:0", "--end-of-options",
                repositoryRemoteUrl, descendant.ToLowerInvariant());
            if (!fetch.Ok)
            {
                detail = fetch.Kind == GitFailureKind.Timeout ? "timeout" : $"fetch failed: exit {fetch.ExitCode}: {ShortReason(fetch)}";
                return false;
            }
            if (File.Exists(Path.Combine(staged, "shallow")))
            {
                detail = "shallow history";
                return false;
            }
            if (!KeepOnlyAncestryHead(staged, descendant, local, cancellationToken) || !ScrubStore(staged))
                return false;

            var maintenance = RunGit(staged, AncestryGcEnvironment(),
                AncestryBuildTimeout, cancellationToken, "--git-dir", staged, "gc", "--auto", "--quiet");
            var prune = RunGit(staged, AncestryGcEnvironment(),
                AncestryBuildTimeout, cancellationToken, "--git-dir", staged, "gc", "--prune=now", "--quiet");
            if (!maintenance.Ok || !prune.Ok || !ScrubStore(staged))
            {
                _log?.Invoke("Ancestry history graph rebuild maintenance failed; rotating the cache.");
                return false;
            }
            var size = DirectorySizeBytes(staged);
            if (size is null || size > AncestryCacheMaxBytes)
            {
                _log?.Invoke("Ancestry history store exceeded its size limit and was rotated.");
                return false;
            }
            var old = graph + ".old-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(graph))
                Directory.Move(graph, old);
            try
            {
                Directory.Move(staged, graph);
            }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(graph))
                    Directory.Move(old, graph);
                throw;
            }
            DiscardStore(old);
            _log?.Invoke("Ancestry history store maintenance completed.");
            return true;
        }
        finally
        {
            DiscardStore(staged);
        }
    }

    private bool KeepOnlyAncestryHead(
        string graph, string descendant, GitEnvironment local, CancellationToken cancellationToken)
    {
        var resolved = RunGit(graph, local, AncestryBuildTimeout, cancellationToken,
            "--git-dir", graph, "rev-parse", "--verify", "--quiet", descendant + "^{commit}");
        if (!resolved.Ok || !IsHexObjectId(resolved.Stdout.Trim()))
        {
            _log?.Invoke($"Ancestry history head could not be resolved ({ShortReason(resolved)}).");
            return false;
        }
        var update = RunGit(graph, local, AncestryBuildTimeout, cancellationToken,
            "--git-dir", graph, "update-ref", AncestryHeadRef, resolved.Stdout.Trim());
        if (!update.Ok)
        {
            _log?.Invoke($"Ancestry history head ref could not be updated ({ShortReason(update)}).");
            return false;
        }

        var refs = RunGit(graph, local, AncestryBuildTimeout, cancellationToken,
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
                var delete = RunGit(graph, local, AncestryBuildTimeout, cancellationToken,
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
