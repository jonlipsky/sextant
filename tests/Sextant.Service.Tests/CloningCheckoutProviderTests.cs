using System.Diagnostics;

namespace Sextant.Service.Tests;

/// <summary>
/// Exercises <see cref="CloningCheckoutProvider"/> against a LOCAL git repository served over a
/// <c>file://</c> URL — no network. Each test builds a throwaway "remote" repo containing a minimal
/// <c>.slnx</c> + a <c>.cs</c> file, commits, and clones it into a service checkout volume. Covers the
/// clone-on-miss cache fill, the cache-hit short-circuit, the locate-only default, a bad/unfetchable
/// commit, concurrent ensures for the same repo, and the path-containment guard.
/// </summary>
[TestClass]
public class CloningCheckoutProviderTests
{
    private readonly List<string> _tempDirs = [];

    [TestCleanup]
    public void TestCleanup()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    [TestMethod]
    public void TryResolve_CloneOnMiss_PopulatesVolumeAndResolves()
    {
        var (remoteUrl, commit) = NewRemoteRepo();
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);
        var request = ServiceTestFixtures.Request(remoteUrl, commit);

        Assert.IsTrue(provider.TryResolve(request, out var resolution),
            "a clone-on-miss should provision the checkout and then locate its solution");
        var dir = resolution.CheckoutDir;
        var sln = resolution.PrimarySolution;

        var canonical = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(remoteUrl));
        Assert.AreEqual(Path.GetFullPath(canonical), Path.GetFullPath(dir));
        Assert.IsTrue(dir.StartsWith(paths.CheckoutRoot, StringComparison.Ordinal));
        Assert.IsTrue(sln.EndsWith("App.slnx", StringComparison.Ordinal));
        Assert.AreEqual(commit, GitHead(canonical), "the checked-out HEAD must be the requested commit");
        // No leftover temp clone directories under the checkout root.
        Assert.IsFalse(Directory.EnumerateDirectories(paths.CheckoutRoot, ".tmp-clone-*").Any(),
            "a successful clone must leave no temp directory behind");
    }

    [TestMethod]
    public void TryResolve_CacheHit_DoesNotReClone()
    {
        var (remoteUrl, commit) = NewRemoteRepo();
        var paths = NewPaths();
        var request = ServiceTestFixtures.Request(remoteUrl, commit);

        // Pre-populate the canonical checkout with a solution — a cache hit the inner locate satisfies.
        var canonical = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(remoteUrl));
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(canonical, "App.slnx"), string.Empty);

        // A git executable that would FAIL if invoked, proving the cache-hit path never shells out to git.
        var provider = new CloningCheckoutProvider(
            new PersistentVolumeCheckoutProvider(paths), paths, token: null, gitExecutable: "definitely-not-git");

        Assert.IsTrue(provider.TryResolve(request, out var resolution),
            "an already-provisioned checkout resolves with no clone");
        var dir = resolution.CheckoutDir;
        var sln = resolution.PrimarySolution;
        Assert.AreEqual(Path.GetFullPath(canonical), Path.GetFullPath(dir));
        Assert.IsTrue(sln.EndsWith("App.slnx", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryResolve_LocateMode_NeverClones()
    {
        var (remoteUrl, commit) = NewRemoteRepo();
        var paths = NewPaths();
        var request = ServiceTestFixtures.Request(remoteUrl, commit);

        // The DEFAULT locate-only provider (what the service uses in `locate` mode) must never provision.
        var provider = new PersistentVolumeCheckoutProvider(paths);

        Assert.IsFalse(provider.TryResolve(request, out var resolution),
            "locate mode must not clone on a miss");
        Assert.IsNull(resolution);
        var canonical = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(remoteUrl));
        Assert.IsFalse(Directory.Exists(canonical), "locate mode must not create a checkout directory");
    }

    [TestMethod]
    public void TryResolve_UnfetchableCommit_ReturnsFalseAndLeavesNoCheckout()
    {
        var (remoteUrl, _) = NewRemoteRepo();
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);
        // A well-formed but nonexistent commit id: fetch/checkout fails, so we must not index the wrong commit.
        var request = ServiceTestFixtures.Request(remoteUrl, new string('a', 40));

        Assert.IsFalse(provider.TryResolve(request, out var resolution),
            "an unfetchable commit must degrade cleanly (no checkout published)");
        Assert.IsNull(resolution);
        var canonical = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(remoteUrl));
        Assert.IsFalse(Directory.Exists(canonical), "a failed clone must leave no canonical checkout");
        Assert.IsFalse(Directory.EnumerateDirectories(paths.CheckoutRoot, ".tmp-clone-*").Any(),
            "a failed clone must clean up its temp directory");
    }

    [TestMethod]
    public void TryResolve_NonHexCommit_ReturnsFalseWithoutCloning()
    {
        var (remoteUrl, _) = NewRemoteRepo();
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);
        var request = ServiceTestFixtures.Request(remoteUrl, "main"); // a ref name, not a git object id

        Assert.IsFalse(provider.TryResolve(request, out _),
            "a non-oid commit is rejected (argument-injection hardening) and never provisions");
        var canonical = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(remoteUrl));
        Assert.IsFalse(Directory.Exists(canonical));
    }

    [TestMethod]
    public async Task TryResolve_ConcurrentSameRepo_ClonesOnceAndBothResolve()
    {
        var (remoteUrl, commit) = NewRemoteRepo();
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);
        var request = ServiceTestFixtures.Request(remoteUrl, commit);

        // Two parallel ensures for the SAME repo → the per-repo lock serializes them; one clones, the other
        // re-locates the winner's checkout. Both resolve to the same directory.
        var t1 = Task.Run(() => { var ok = provider.TryResolve(request, out var r); return (ok, d: ok ? r.CheckoutDir : null); });
        var t2 = Task.Run(() => { var ok = provider.TryResolve(request, out var r); return (ok, d: ok ? r.CheckoutDir : null); });
        var results = await Task.WhenAll(t1, t2);

        Assert.IsTrue(results[0].ok && results[1].ok, "both concurrent resolves succeed");
        Assert.AreEqual(results[0].d, results[1].d, "both resolve the same canonical checkout");
        Assert.IsFalse(Directory.EnumerateDirectories(paths.CheckoutRoot, ".tmp-clone-*").Any(),
            "concurrent clones leave no temp directories");
    }

    [TestMethod]
    public void TryResolve_TraversalRepositoryUrl_DoesNotEscapeCheckoutVolume()
    {
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);

        // Plant a solution OUTSIDE the checkout volume; a traversal URL must never resolve or write to it.
        var dataParent = Directory.GetParent(paths.CheckoutRoot)!.FullName;
        var outside = Path.Combine(dataParent, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Escaped.sln"), string.Empty);

        // A crafted traversal URL pointing (via `..`) at that outside directory. It is a REAL local path but
        // not a git repository, so the fetch fails DETERMINISTICALLY ("does not appear to be a git
        // repository") — proving containment without depending on a network/DNS outcome. The canonical
        // checkout directory name is derived through RepoDirectoryName, which neutralizes the traversal, so
        // the sanitized target always stays inside the volume and the outside solution is never located.
        var traversalUrl = new Uri(Path.Combine(dataParent, "repo")).AbsoluteUri + "/../outside";
        var request = ServiceTestFixtures.Request(traversalUrl, new string('b', 40));

        Assert.IsFalse(provider.TryResolve(request, out var resolution),
            "a traversal repository url must never resolve or provision outside the checkout volume");
        Assert.IsNull(resolution);
        Assert.IsFalse(File.Exists(Path.Combine(paths.CheckoutRoot, "Escaped.sln")),
            "the outside solution must never be copied or linked into the checkout volume");
    }

    [TestMethod]
    public void TryResolve_WithToken_LeavesNoCredentialOnPublishedCheckout()
    {
        var (remoteUrl, commit) = NewRemoteRepo();
        var paths = NewPaths();
        // A token is supplied; the local file:// remote does not consume it, but the published checkout must
        // never carry the credential in its git config or fetch record regardless.
        var provider = new CloningCheckoutProvider(
            new PersistentVolumeCheckoutProvider(paths), paths, token: "supersecrettoken");
        var request = ServiceTestFixtures.Request(remoteUrl, commit);

        Assert.IsTrue(provider.TryResolve(request, out var resolution),
            "a token-authenticated clone of a reachable commit still resolves");
        var dir = resolution.CheckoutDir;

        var config = Path.Combine(dir, ".git", "config");
        Assert.IsTrue(File.Exists(config));
        var configText = File.ReadAllText(config);
        Assert.IsFalse(configText.Contains("supersecrettoken", StringComparison.Ordinal),
            "the token must never be persisted in the published checkout's git config");
        Assert.IsFalse(configText.Contains("x-access-token", StringComparison.Ordinal),
            "the published origin remote must be the token-less url");
        var fetchHead = Path.Combine(dir, ".git", "FETCH_HEAD");
        if (File.Exists(fetchHead))
            Assert.IsFalse(File.ReadAllText(fetchHead).Contains("supersecrettoken", StringComparison.Ordinal),
                "the fetch record must not retain the token");
    }

    [TestMethod]
    public void ScrubFetchHead_RemovesTokenBearingFetchRecord()
    {
        // Transport-independent: seed a .git/FETCH_HEAD that echoes an authenticated (token-bearing) fetch
        // URL — exactly what an https clone with a token would leave behind — and assert the scrub removes
        // the credential. The file:// happy-path test never injects a token, so this pins the security-
        // relevant scrub path directly against a record git actually wrote a token into.
        var checkoutDir = Path.Combine(Path.GetTempPath(), "sextant-scrub-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(checkoutDir);
        var gitDir = Path.Combine(checkoutDir, ".git");
        Directory.CreateDirectory(gitDir);
        var fetchHead = Path.Combine(gitDir, "FETCH_HEAD");
        const string token = "supersecrettoken";
        File.WriteAllText(
            fetchHead,
            $"9fceb02d0ae598e95dc970b74767f19372d61af8\t\tbranch 'main' of "
            + $"https://x-access-token:{token}@github.com/o/r\n");

        var scrubbed = CloningCheckoutProvider.ScrubFetchHead(checkoutDir);

        Assert.IsTrue(scrubbed, "the token-bearing fetch record must be scrubbable");
        Assert.IsFalse(File.Exists(fetchHead),
            "FETCH_HEAD must be gone after scrubbing so no token is published on the volume");
        Assert.IsFalse(
            Directory.EnumerateFiles(gitDir).Any(f =>
                File.ReadAllText(f).Contains(token, StringComparison.Ordinal)),
            "no token may remain anywhere in the scrubbed git directory");
    }

    [TestMethod]
    public void TopLevelEnvironment_ScopesTransientTokenHeaderToRepositoryHostOnly()
    {
        var paths = NewPaths();
        var provider = new CloningCheckoutProvider(
            new PersistentVolumeCheckoutProvider(paths), paths, token: "tok");
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:tok"));

        // Token + https repository host → an extraheader scoped to EXACTLY that host: an empty reset entry
        // (clears any inherited header) followed by the Basic credential. The token never appears raw.
        var env = provider.TopLevelEnvironment("github.com", "https://github.com/o/r.git");
        var headers = env.Config.Where(kv => kv.Key.EndsWith(".extraheader", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreEqual(
            new[] { "http.https://github.com/.extraheader", "http.https://github.com/.extraheader" },
            headers.Select(kv => kv.Key).ToArray());
        Assert.AreEqual(string.Empty, headers[0].Value);
        Assert.AreEqual($"AUTHORIZATION: basic {basic}", headers[1].Value);
        Assert.IsFalse(env.Config.Any(kv => kv.Value.Contains("tok", StringComparison.Ordinal) && !kv.Value.Contains(basic, StringComparison.Ordinal)),
            "the raw token must never be handed to git");
        // Credential helpers / askpass are always reset, so no stored credential is consulted or written.
        Assert.IsTrue(env.Config.Any(kv => kv.Key == "credential.helper" && kv.Value.Length == 0));
        Assert.IsTrue(env.Config.Any(kv => kv.Key == "core.askPass" && kv.Value.Length == 0));

        // No authority (non-https repository) → no header at all.
        Assert.IsFalse(provider.TopLevelEnvironment(null, "https://github.com/o/r.git").Config.Any(kv => kv.Key.EndsWith(".extraheader", StringComparison.Ordinal)));
        // No token configured → no header even for an https host.
        Assert.IsFalse(NewCloneProvider(paths).TopLevelEnvironment("github.com", "https://github.com/o/r.git").Config
            .Any(kv => kv.Key.EndsWith(".extraheader", StringComparison.Ordinal)));
        // Submodule fetches are https-only (no ssh/git/http/ext transports from an untrusted .gitmodules).
        Assert.AreEqual("https", provider.SubmoduleFetchEnvironment(null, "https://github.com/o/r.git").AllowedProtocols);
    }

    [TestMethod]
    public void TryResolve_CachedCheckoutAtDifferentCommit_ReprovisionsToRequestedCommit()
    {
        var (remoteUrl, first) = NewRemoteRepo(out var repoDir);
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);

        // First ensure clones the repo at its initial commit.
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(remoteUrl, first), out var resolution1),
            "the first ensure clones the repo at the initial commit");
        var dir1 = resolution1.CheckoutDir;
        Assert.AreEqual(first, GitHead(dir1));

        // The branch advances on the remote; a later ensure asks for the NEW commit against the SAME repo.
        var second = CommitMore(repoDir);
        Assert.AreNotEqual(first, second);

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(remoteUrl, second), out var resolution2),
            "an ensure for a different commit must re-provision rather than reuse the stale checkout");
        var dir2 = resolution2.CheckoutDir;
        var sln2 = resolution2.PrimarySolution;
        Assert.AreEqual(Path.GetFullPath(dir1), Path.GetFullPath(dir2), "the canonical checkout dir is unchanged");
        Assert.AreEqual(second, GitHead(dir2),
            "the cached checkout must be replaced with the requested commit — never index the wrong revision");
        Assert.IsTrue(sln2.EndsWith("App.slnx", StringComparison.Ordinal));
        Assert.IsFalse(Directory.EnumerateDirectories(paths.CheckoutRoot, ".tmp-clone-*").Any(),
            "a re-provision leaves no temp or retired directories behind");
    }

    [TestMethod]
    public void TryResolve_SameCommitReensure_IsCacheHitWithNoGit()
    {
        var (remoteUrl, commit) = NewRemoteRepo(out _);
        var paths = NewPaths();

        // Clone once with a real git provider, then swap in a bogus git exe: a re-ensure for the SAME commit
        // must be a pure cache hit (HEAD already matches) and never shell out to git again.
        Assert.IsTrue(NewCloneProvider(paths).TryResolve(ServiceTestFixtures.Request(remoteUrl, commit), out _));

        var cacheOnly = new CloningCheckoutProvider(
            new PersistentVolumeCheckoutProvider(paths), paths, token: null, gitExecutable: "definitely-not-git");
        Assert.IsTrue(cacheOnly.TryResolve(ServiceTestFixtures.Request(remoteUrl, commit), out var resolution),
            "a same-commit re-ensure resolves from cache");
        var dir = resolution.CheckoutDir;
        var sln = resolution.PrimarySolution;
        Assert.AreEqual(commit, GitHead(dir));
        Assert.IsTrue(sln.EndsWith("App.slnx", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryResolve_RepositoryUrlWithEmbeddedCredentials_IsRefused()
    {
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);
        // A credential embedded in the URL must never be persisted to .git/config or logged: refuse it
        // outright (credentials belong in SEXTANT_SERVICE_CHECKOUT_TOKEN). Returns before any git runs.
        var request = ServiceTestFixtures.Request("https://user:secret@host/o/r.git", new string('a', 40));

        Assert.IsFalse(provider.TryResolve(request, out var resolution),
            "an http(s) URL embedding userinfo must be refused");
        Assert.IsNull(resolution);
        var canonical = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
        Assert.IsFalse(Directory.Exists(canonical), "a refused clone must leave no checkout directory");
    }

    [TestMethod]
    public void Constructor_SweepsOrphanedTempClones_ButSparesRecentOnes()
    {
        var paths = NewPaths();
        Directory.CreateDirectory(paths.CheckoutRoot);

        // A stale temp clone (untouched longer than a git operation can run) is reclaimed on startup.
        var stale = Path.Combine(paths.CheckoutRoot, ".tmp-clone-stale");
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-30));

        // A recent temp clone (possibly an in-flight clone by another process) is spared.
        var recent = Path.Combine(paths.CheckoutRoot, ".tmp-clone-recent");
        Directory.CreateDirectory(recent);
        Directory.SetLastWriteTimeUtc(recent, DateTime.UtcNow);

        // Construction runs the sweep.
        _ = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths);

        Assert.IsFalse(Directory.Exists(stale), "a stale orphaned temp clone must be reclaimed on startup");
        Assert.IsTrue(Directory.Exists(recent), "a recently-touched temp clone must be spared (may be in-flight)");
    }

    // ---- remote default branch (issue #199) ------------------------------------------------------

    [TestMethod]
    public void ResolveDefaultBranch_ReadsTheBranchTheRemotesHeadNames()
    {
        var (remoteUrl, _) = NewRemoteRepo(out var repoDir);
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);

        Assert.AreEqual("main", provider.ResolveDefaultBranch(remoteUrl));

        Git(repoDir, "branch", "trunk");
        Git(repoDir, "symbolic-ref", "HEAD", "refs/heads/trunk");
        Assert.AreEqual("trunk", provider.ResolveDefaultBranch(remoteUrl), "the remote's HEAD decides, not a name convention");
        Assert.IsFalse(Directory.EnumerateDirectories(paths.CheckoutRoot, ".tmp-clone-*").Any(),
            "the lookup leaves no temp directory behind");
    }

    [TestMethod]
    public void ResolveDefaultBranch_WhenItCannotBeDetermined_IsNull()
    {
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);
        var missing = new Uri(Path.Combine(Path.GetTempPath(), $"sextant_missing_{Guid.NewGuid():N}")).AbsoluteUri;

        Assert.IsNull(provider.ResolveDefaultBranch(missing), "an unreachable remote");
        Assert.IsNull(new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, gitExecutable: "definitely-not-git")
            .ResolveDefaultBranch("https://github.com/acme/widgets"), "git cannot run");
        Assert.IsNull(provider.ResolveDefaultBranch("https://user:secret@github.com/acme/widgets"),
            "a URL that embeds credentials is refused before git runs");
    }

    // ---- commits must be on a branch (issue #209) -------------------------------------------------

    [TestMethod]
    public void IsCommitOnBranch_AcceptsBranchCommits_AndRefusesForkNetworkCommits()
    {
        var (remoteUrl, mainCommit) = NewRemoteRepo(out var repoDir);
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);
        var olderMain = mainCommit;
        var newerMain = CommitMore(repoDir);

        Git(repoDir, "checkout", "--quiet", "-b", "feature");
        var featureOnly = CommitFile(repoDir, "Feature.cs");
        Git(repoDir, "checkout", "--quiet", "main");

        // A fork commit fetched into the canonical repository only through a pull ref, as GitHub serves them.
        var fork = Path.Combine(Path.GetTempPath(), $"sextant_fork_{Guid.NewGuid():N}");
        _tempDirs.Add(fork);
        Git(repoDir, "clone", "--quiet", repoDir, fork);
        File.WriteAllText(Path.Combine(fork, "Evil.cs"), "namespace App; public class Evil { }");
        Git(fork, "add", "-A");
        Git(fork, "-c", "user.email=f@example.com", "-c", "user.name=Fork", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "fork");
        var forkCommit = GitHead(fork);
        Git(repoDir, "fetch", "--quiet", fork, "HEAD:refs/pull/1/head");

        // Sanity: the remote really does serve that commit by id, which is the exposure #209 closes.
        var probe = Path.Combine(Path.GetTempPath(), $"sextant_probe_{Guid.NewGuid():N}");
        _tempDirs.Add(probe);
        Directory.CreateDirectory(probe);
        Git(probe, "init", "--quiet");
        Git(probe, "fetch", "--quiet", "--depth", "1", remoteUrl, forkCommit);

        Assert.IsTrue(provider.IsCommitOnBranch(remoteUrl, newerMain), "a branch head");
        Assert.IsTrue(provider.IsCommitOnBranch(remoteUrl, olderMain), "an ancestor of a branch head");
        Assert.IsTrue(provider.IsCommitOnBranch(remoteUrl, newerMain.ToUpperInvariant()), "object ids are case-insensitive");
        Assert.IsTrue(provider.IsCommitOnBranch(remoteUrl, featureOnly), "a commit only on a non-default branch");
        Assert.IsFalse(provider.IsCommitOnBranch(remoteUrl, forkCommit), "a fork commit reachable only through refs/pull/*");
        Assert.IsFalse(Directory.EnumerateDirectories(paths.CheckoutRoot, ".tmp-clone-*").Any(), "no temp directory is left behind");

        // Once a branch contains the fork commit (it was merged), it is on a branch.
        Git(repoDir, "-c", "user.email=m@example.com", "-c", "user.name=M", "-c", "commit.gpgsign=false", "merge", "--quiet", "--no-edit", forkCommit);
        Assert.IsTrue(provider.IsCommitOnBranch(remoteUrl, forkCommit), "a merged fork commit");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("main")]
    [DataRow("abc123")]
    [DataRow("0000000000000000000000000000000000000000")]
    public void IsCommitOnBranch_FailsClosed(string commit)
    {
        var (remoteUrl, _) = NewRemoteRepo();
        var provider = NewCloneProvider(NewPaths());

        Assert.IsFalse(provider.IsCommitOnBranch(remoteUrl, commit), "not a full id, or no such commit");
    }

    [TestMethod]
    public void IsCommitOnBranch_WhenTheRemoteCannotBeRead_IsFalse()
    {
        var (_, commit) = NewRemoteRepo();
        var paths = NewPaths();
        var missing = new Uri(Path.Combine(Path.GetTempPath(), $"sextant_missing_{Guid.NewGuid():N}")).AbsoluteUri;

        Assert.IsFalse(NewCloneProvider(paths).IsCommitOnBranch(missing, commit), "an unreachable remote");
        Assert.IsFalse(new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, gitExecutable: "definitely-not-git")
            .IsCommitOnBranch("https://github.com/acme/widgets", commit), "git cannot run");
        Assert.IsFalse(NewCloneProvider(paths).IsCommitOnBranch("******github.com/acme/widgets", commit),
            "a URL that embeds credentials is refused before git runs");
    }

    [TestMethod]
    public void CancellingARemoteLookup_KillsTheGitProcess_InsteadOfWaitingForItsTimeout()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("uses a shell script as the git executable");
        var (remoteUrl, commit) = NewRemoteRepo();
        var paths = NewPaths();
        var scriptDir = Path.Combine(Path.GetTempPath(), $"sextant_slowgit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(scriptDir);
        _tempDirs.Add(scriptDir);
        var git = Path.Combine(scriptDir, "git");
        File.WriteAllText(git, "#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in fetch|ls-remote) exec sleep 120;; esac; done\nexec git \"$@\"\n");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var provider = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, gitExecutable: git);

        foreach (var lookup in new Func<CancellationToken, object?>[]
                 {
                     ct => provider.IsCommitOnBranch(remoteUrl, commit, ct),
                     ct => provider.ResolveBranchHead(remoteUrl, "main", ct)
                 })
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew();
            var answer = lookup(cts.Token);

            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(20), $"the git call was killed on cancel, not run to its timeout ({watch.Elapsed})");
            Assert.IsTrue(answer is null or false, "a cancelled lookup answers nothing");
        }
        Assert.IsFalse(Directory.EnumerateDirectories(paths.CheckoutRoot, ".tmp-clone-*").Any(), "no temp directory is left behind");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static string CommitFile(string repoDir, string file)
    {
        File.WriteAllText(Path.Combine(repoDir, file), "namespace App; public class Extra { }");
        Git(repoDir, "add", "-A");
        Git(repoDir, "commit", "--quiet", "-m", file);
        return GitHead(repoDir);
    }

    private ServicePaths NewPaths()
    {
        var dataRoot = ServiceTestFixtures.NewDataRoot();
        _tempDirs.Add(dataRoot);
        return new ServicePaths(ServiceVolumes.Rooted(dataRoot));
    }

    private static CloningCheckoutProvider NewCloneProvider(ServicePaths paths) =>
        new(new PersistentVolumeCheckoutProvider(paths), paths);

    private (string url, string commit) NewRemoteRepo() => NewRemoteRepo(out _);

    /// <summary>
    /// Creates a local git repository (the "remote") with a minimal solution + source file, returns its
    /// <c>file://</c> URL and the HEAD commit sha, and exposes the repo directory for advancing the branch.
    /// </summary>
    private (string url, string commit) NewRemoteRepo(out string repoDir)
    {
        repoDir = Path.Combine(Path.GetTempPath(), $"sextant_remote_{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        _tempDirs.Add(repoDir);

        File.WriteAllText(Path.Combine(repoDir, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(repoDir, "App.cs"), "namespace App; public class Program { }");

        Git(repoDir, "init", "--quiet", "--initial-branch", "main");
        Git(repoDir, "config", "user.email", "test@example.com");
        Git(repoDir, "config", "user.name", "Sextant Test");
        Git(repoDir, "config", "commit.gpgsign", "false");
        // Allow fetching an arbitrary reachable sha by object id from this non-bare file:// remote.
        Git(repoDir, "config", "uploadpack.allowReachableSHA1InWant", "true");
        Git(repoDir, "config", "uploadpack.allowAnySHA1InWant", "true");
        Git(repoDir, "add", "-A");
        Git(repoDir, "commit", "--quiet", "-m", "initial");

        var commit = GitHead(repoDir);
        // A file:// URL is a valid git remote and requires no network.
        var url = new Uri(repoDir).AbsoluteUri;
        return (url, commit);
    }

    /// <summary>Adds another commit to the given remote repo and returns the new HEAD sha.</summary>
    private static string CommitMore(string repoDir)
    {
        File.WriteAllText(Path.Combine(repoDir, "More.cs"), "namespace App; public class More { }");
        Git(repoDir, "add", "-A");
        Git(repoDir, "commit", "--quiet", "-m", "more");
        return GitHead(repoDir);
    }

    private static string GitHead(string repoDir)
    {
        var (ok, stdout) = TryGit(repoDir, "rev-parse", "HEAD");
        Assert.IsTrue(ok, "git rev-parse HEAD should succeed in the test repo");
        return stdout.Trim();
    }

    private static void Git(string workingDir, params string[] args)
    {
        var (ok, _) = TryGit(workingDir, args);
        Assert.IsTrue(ok, $"git {string.Join(' ', args)} should succeed");
    }

    private static (bool ok, string stdout) TryGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode == 0, stdout);
    }
}
