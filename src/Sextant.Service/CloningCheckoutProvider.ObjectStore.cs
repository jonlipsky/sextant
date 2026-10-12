using System.Globalization;

namespace Sextant.Service;

/// <summary>
/// Incremental fetch through a persistent per-repository object store (issue #272). Without it every new commit of
/// a repository is a fresh <c>git init</c> plus a depth-1 fetch of the whole tree (and of every submodule). With
/// it, the commit is first fetched into a bare store kept on the cache volume, whose last fetched commit git offers
/// the server as a <c>have</c>, so only objects the store lacks cross the network; the staged checkout then copies
/// the commit from the store with a local depth-1 fetch, and everything after that (checkout, verify, submodules,
/// scrub, atomic publish) is unchanged. The published checkout never refers to the store, so deleting a store only
/// costs the next fetch its head start.
/// <para>
/// Isolation: a store belongs to ONE repository (<see cref="ServicePaths.RepoDirectoryName"/>), and a submodule's
/// store lives under its superproject's, keyed by the submodule's clean URL, so no repository's fetches ever land
/// in, or are served from, another repository's store.
/// </para>
/// <para>
/// Persistent metadata: before every use the store's config is rewritten to the service's own minimal bare config and
/// any alternates/<c>commondir</c> file is deleted, so nothing planted in it between jobs (a proxy, an ssh command, TLS
/// settings, an include, objects borrowed from elsewhere) is read by the next fetch.
/// </para>
/// <para>
/// Credentials: the store is fetched from by URL with the SAME environment as the direct fetch it replaces (the
/// token, when sent at all, only as the env-scoped host header with redirects off), so no remote, URL or token is
/// ever written into its config. After each fetch its <c>FETCH_HEAD</c> is deleted and, with a token configured,
/// every non-object file is scanned for it; a hit discards the store (fail closed). The local copy into the staged
/// checkout uses an environment with no token at all and only the <c>file</c> transport. Hooks and fsmonitor are
/// disabled for every store command, and git's automatic maintenance never runs detached.
/// </para>
/// <para>
/// Self-healing and bounded: a failure of a store step discards the store (renamed aside, then deleted) and falls back
/// to the direct fetch, so a corrupt store can never wedge a repository; a remote timeout or an unambiguous remote
/// refusal (not found, authentication) keeps the store and is classified exactly as before, without a second fetch. After each fetch the store moves its one ref,
/// <c>refs/sextant/head</c>, to the new commit and runs <c>git gc --auto</c> with immediate pruning, so it holds about
/// one shallow commit plus at most <see cref="StoreAutoPackLimit"/> incremental packs.
/// </para>
/// </summary>
public sealed partial class CloningCheckoutProvider
{
    /// <summary>
    /// When true (the default) commits are fetched through the per-repository object store; when false every commit
    /// is fetched from the remote afresh (<c>SEXTANT_SERVICE_GIT_OBJECT_STORE</c>).
    /// </summary>
    public bool UseObjectStore { get; init; } = true;

    /// <summary>The ref a store keeps on its last fetched commit (the negotiation tip of the next fetch).</summary>
    internal const string StoreHeadRef = "refs/sextant/head";

    /// <summary>Incremental packs a store accumulates before <c>gc --auto</c> consolidates it and prunes old objects.</summary>
    internal const int StoreAutoPackLimit = 10;

    private const string StoreFetchRefPrefix = "refs/sextant/fetch/";
    private const string DiscardedStorePrefix = ".discarded-";

    // Applied to every store command on top of the fetch's own environment: nothing planted in a store (or
    // inherited) may run a hook or an fsmonitor, and git never starts background maintenance that would outlive the
    // job (the store is gc'd explicitly, in the foreground, after each fetch).
    private static readonly KeyValuePair<string, string>[] StoreHardeningConfig =
    [
        new("core.hooksPath", "/dev/null"),
        new("core.fsmonitor", "false"),
        new("maintenance.auto", "false"),
        new("gc.auto", "0"),
    ];

    private static readonly KeyValuePair<string, string>[] StoreGcConfig =
    [
        .. StoreHardeningConfig[..2],
        new("gc.autoDetach", "false"),
        new("gc.pruneExpire", "now"),
        new("gc.autoPackLimit", StoreAutoPackLimit.ToString(CultureInfo.InvariantCulture)),
        new("gc.auto", "1000"),
    ];

    /// <summary>The bare store of the repository whose checkout directory is <paramref name="dirName"/>, or null.</summary>
    private string? RepositoryStore(string dirName)
    {
        if (!UseObjectStore)
            return null;
        var root = Path.GetFullPath(_paths.GitObjectStoreRoot);
        var store = Path.GetFullPath(Path.Combine(root, dirName, "repository.git"));
        return IsContainedIn(root, store) ? store : null;
    }

    /// <summary>The bare store of one submodule, under its superproject's store directory, or null.</summary>
    private string? SubmoduleStore(string? repositoryStore, string submoduleCleanUrl)
    {
        if (repositoryStore is null)
            return null;
        var root = Path.GetFullPath(_paths.GitObjectStoreRoot);
        var store = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(repositoryStore)!, "submodules", ServicePaths.RepoDirectoryName(submoduleCleanUrl) + ".git"));
        return IsContainedIn(root, store) ? store : null;
    }

    /// <summary>
    /// Fetches <paramref name="commit"/> of <paramref name="url"/> into the freshly initialized repository
    /// <paramref name="repoDir"/>: a shallow fetch of the exact commit, falling back to a full fetch when the server
    /// refuses fetch-by-sha (HEAD for a top-level repository, every branch for a submodule). Through
    /// <paramref name="store"/> when it is usable; directly otherwise. Returns both attempts so the caller classifies
    /// a failure exactly as before (<c>Full</c> is <c>default</c> when the shallow attempt succeeded).
    /// </summary>
    private (GitResult Shallow, GitResult Full) FetchCommit(
        string repoDir, GitEnvironment env, string url, string commit, bool submodule, string? store)
    {
        if (store is not null && TryPrepareStore(store))
        {
            var (shallowViaStore, fullViaStore, state) = FetchIntoStore(store, env, url, commit, submodule);
            switch (state)
            {
                case StoreFetch.RemoteFailed when RemoteCausedFailure(shallowViaStore, fullViaStore):
                    // A timeout, or an unambiguous remote refusal (not found, authentication): the remote, not the
                    // store, failed, so the store is kept and the caller classifies the attempts exactly as before.
                    return (shallowViaStore, fullViaStore);
                case StoreFetch.RemoteFailed:
                    // Anything else may be the store itself (a corrupt pack or ref): discard it and fetch directly once;
                    // a remote that really is failing fails that fetch too, which the caller classifies as before.
                    DiscardStore(store);
                    break;
                case StoreFetch.CommitAbsent:
                    // The fetch succeeded but did not bring the commit: nothing is copied, so the caller's checkout
                    // fails deterministically, exactly as after a direct fetch that did not bring it.
                    return (shallowViaStore, fullViaStore);
                case StoreFetch.Fetched when CopyFromStore(store, repoDir):
                    return (shallowViaStore, fullViaStore);
                default:
                    DiscardStore(store);
                    break;
            }
        }

        // Direct fetch (no store, or a store step failed): today's shape.
        string[] source = submodule ? ["origin"] : [url];
        var shallow = RunGit(repoDir, env, ["fetch", "--depth", "1", "--no-tags", "--end-of-options", .. source, commit]);
        if (shallow.Ok)
            return (shallow, default);
        var full = RunGit(repoDir, env, ["fetch", "--no-tags", "--end-of-options", .. source]);
        return (shallow, full);
    }

    private enum StoreFetch { Fetched, RemoteFailed, CommitAbsent, Unusable }

    // A failed store fetch is the remote's doing when an attempt timed out or every attempt failed deterministically.
    private static bool RemoteCausedFailure(GitResult shallow, GitResult full)
    {
        if (shallow.Kind == GitFailureKind.Timeout || full.Kind == GitFailureKind.Timeout)
            return true;
        return !IsTransientGitFailure(GitStage.Fetch, shallow.Kind, shallow.Stderr)
            && !IsTransientGitFailure(GitStage.Fetch, full.Kind, full.Stderr);
    }

    // The ONLY repository config a store ever has. It persists on the cache volume between jobs and is read by
    // token-bearing fetches, so it is rewritten before every use: nothing planted in it (a proxy, an ssh command, TLS
    // settings, an include) survives to the next fetch.
    private static string StoreConfig => OperatingSystem.IsWindows()
        ? "[core]\n\trepositoryformatversion = 0\n\tfilemode = false\n\tbare = true\n"
        : "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = true\n";

    // Files that would make git read objects or a git dir from outside the store.
    private static readonly string[] StoreRedirectFiles =
        [Path.Combine("objects", "info", "alternates"), Path.Combine("objects", "info", "http-alternates"), "commondir", "gitdir"];

    /// <summary>Creates <paramref name="store"/> as a bare repository when it does not hold one. False when it cannot.</summary>
    private bool TryPrepareStore(string store)
    {
        try
        {
            if (File.Exists(Path.Combine(store, "HEAD")) && Directory.Exists(Path.Combine(store, "objects")))
                return ResetStoreMetadata(store);
            if (Directory.Exists(store))
                DiscardStore(store);
            Directory.CreateDirectory(store);
            var init = RunGit(store, StoreEnvironment(LocalGitEnvironment), "init", "--bare", "--quiet");
            if (init.Ok)
                return ResetStoreMetadata(store);
            _log?.Invoke($"Object store could not be created ({ShortReason(init)}); fetching directly.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Object store could not be created ({ex.GetType().Name}); fetching directly.");
        }
        DiscardStore(store);
        return false;
    }

    /// <summary>
    /// Rewrites the store's config to <see cref="StoreConfig"/> and deletes any file that would redirect its object or
    /// git directory, so a store's metadata is the service's own at every fetch. Throws on an I/O failure (the caller
    /// discards the store).
    /// </summary>
    private static bool ResetStoreMetadata(string store)
    {
        File.WriteAllText(Path.Combine(store, "config"), StoreConfig);
        foreach (var relative in StoreRedirectFiles)
        {
            var file = Path.Combine(store, relative);
            if (File.Exists(file))
                File.Delete(file);
            if (File.Exists(file) || Directory.Exists(file))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Fetches the commit into the store, moves <see cref="StoreHeadRef"/> to it, scrubs the store, logs what was
    /// transferred and gc's it. <see cref="StoreFetch.Unusable"/> when a local step failed (the caller then discards
    /// the store and fetches directly).
    /// </summary>
    private (GitResult Shallow, GitResult Full, StoreFetch State) FetchIntoStore(
        string store, GitEnvironment env, string url, string commit, bool submodule)
    {
        var storeEnv = StoreEnvironment(env);
        var local = StoreEnvironment(LocalGitEnvironment);
        var before = StoreObjectStats(store);

        var shallow = RunGit(store, storeEnv, "fetch", "--depth", "1", "--no-tags", "--end-of-options", url, commit);
        var full = default(GitResult);
        if (!shallow.Ok)
        {
            // A submodule's pinned commit may be on any branch; keep them under a temporary namespace only long
            // enough to move the head ref, so they never keep other branches' objects in the store.
            full = submodule
                ? RunGit(store, storeEnv, "fetch", "--no-tags", "--end-of-options", url, $"+refs/heads/*:{StoreFetchRefPrefix}*")
                : RunGit(store, storeEnv, "fetch", "--no-tags", "--end-of-options", url);
            if (!full.Ok)
                return (shallow, full, StoreFetch.RemoteFailed);
        }

        // The requested id may be abbreviated (it is a validated hex id, never an option); the head ref always names
        // the full commit.
        var resolved = RunGit(store, local, "rev-parse", "--verify", "--quiet", commit + "^{commit}");
        var state = StoreFetch.Fetched;
        if (!resolved.Ok || !IsHexObjectId(resolved.Stdout.Trim()))
            state = resolved.Kind is GitFailureKind.NonZeroExit ? StoreFetch.CommitAbsent : StoreFetch.Unusable;
        else if (!RunGit(store, local, "update-ref", StoreHeadRef, resolved.Stdout.Trim()).Ok)
            state = StoreFetch.Unusable;
        if (submodule && !shallow.Ok)
            DeleteFetchRefs(store);

        if (!ScrubStore(store))
        {
            // Fail closed: a credential (or an undeletable fetch record) in the store discards it; the direct fetch
            // that follows is verified again before publish.
            _log?.Invoke("Object store discarded: a credential or unscrubbable fetch record remained in it after a fetch.");
            return (shallow, full, StoreFetch.Unusable);
        }
        if (state != StoreFetch.Fetched)
            return (shallow, full, state);

        if (before is { } b && StoreObjectStats(store) is { } a)
        {
            _log?.Invoke(
                $"Object store for '{SanitizeUrlForLog(url)}': fetched {Math.Max(0, a.Objects - b.Objects)} new object(s) " +
                $"({Math.Max(0, a.KiB - b.KiB)} KiB) for {commit[..Math.Min(8, commit.Length)]}.");
        }

        var gc = RunGit(store, new GitEnvironment("https", [.. HardeningConfig, .. StoreGcConfig]), "gc", "--auto", "--quiet");
        if (!gc.Ok)
            _log?.Invoke($"Object store gc failed (kept as is): {ShortReason(gc)}");
        return (shallow, full, StoreFetch.Fetched);
    }

    /// <summary>
    /// Copies the store's head commit into <paramref name="repoDir"/> with a local depth-1 fetch (no token, only the
    /// <c>file</c> transport). False when the copy fails; the caller then fetches directly.
    /// </summary>
    private bool CopyFromStore(string store, string repoDir)
    {
        var copy = RunGit(repoDir, new GitEnvironment("file", HardeningConfig),
            "fetch", "--depth", "1", "--no-tags", "--end-of-options", store, StoreHeadRef);
        if (copy.Ok)
            return true;
        _log?.Invoke($"Object store copy failed ({ShortReason(copy)}); fetching directly.");
        return false;
    }

    /// <summary>The store environment: the fetch's own environment plus <see cref="StoreHardeningConfig"/>.</summary>
    private static GitEnvironment StoreEnvironment(GitEnvironment env) =>
        env with { Config = [.. env.Config, .. StoreHardeningConfig] };

    private void DeleteFetchRefs(string store)
    {
        var refs = RunGit(store, StoreEnvironment(LocalGitEnvironment), "for-each-ref", "--format=%(refname)", StoreFetchRefPrefix);
        if (!refs.Ok)
            return;
        foreach (var name in refs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.StartsWith(StoreFetchRefPrefix, StringComparison.Ordinal))
                RunGit(store, StoreEnvironment(LocalGitEnvironment), "update-ref", "-d", name);
        }
    }

    /// <summary>
    /// Deletes the store's <c>FETCH_HEAD</c> and, with a token configured, scans its non-object files for the token in
    /// any form. False (and the store discarded) when a record cannot be deleted or a credential is found.
    /// </summary>
    private bool ScrubStore(string store)
    {
        var needles = CredentialNeedles();
        try
        {
            foreach (var file in EnumerateGitMetadataFiles(store).ToList())
            {
                if (string.Equals(Path.GetFileName(file), "FETCH_HEAD", StringComparison.Ordinal))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* checked below */ }
                    if (!File.Exists(file))
                        continue;
                    DiscardStore(store);
                    return false;
                }
                if (needles.Count > 0 && FileContainsAny(file, needles))
                {
                    DiscardStore(store);
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiscardStore(store);
            return false;
        }
    }

    /// <summary>Objects and KiB a store holds (loose plus packed), or null when git cannot count them.</summary>
    private (long Objects, long KiB)? StoreObjectStats(string store)
    {
        var result = RunGit(store, StoreEnvironment(LocalGitEnvironment), "count-objects", "-v");
        if (!result.Ok)
            return null;
        long objects = 0, kib = 0;
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !long.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                continue;
            switch (line[..colon])
            {
                case "count" or "in-pack":
                    objects += value;
                    break;
                case "size" or "size-pack":
                    kib += value;
                    break;
            }
        }
        return (objects, kib);
    }

    /// <summary>Renames a store aside (so no later fetch sees it half-deleted) and deletes it, best effort.</summary>
    private void DiscardStore(string store)
    {
        if (!Directory.Exists(store))
            return;
        var root = Path.GetFullPath(_paths.GitObjectStoreRoot);
        var aside = Path.Combine(root, $"{DiscardedStorePrefix}{Guid.NewGuid():N}");
        try
        {
            Directory.Move(store, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            aside = store;
        }
        TryDeleteDirectory(aside);
    }

    /// <summary>Reclaims stores a previous run renamed aside but could not delete.</summary>
    private void SweepDiscardedStores()
    {
        try
        {
            if (!Directory.Exists(_paths.GitObjectStoreRoot))
                return;
            foreach (var dir in Directory.EnumerateDirectories(_paths.GitObjectStoreRoot, DiscardedStorePrefix + "*"))
                TryDeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }
}
