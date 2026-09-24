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

        Assert.IsTrue(provider.TryResolve(request, out var dir, out var sln),
            "a clone-on-miss should provision the checkout and then locate its solution");

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

        Assert.IsTrue(provider.TryResolve(request, out var dir, out var sln),
            "an already-provisioned checkout resolves with no clone");
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

        Assert.IsFalse(provider.TryResolve(request, out var dir, out var sln),
            "locate mode must not clone on a miss");
        Assert.AreEqual(string.Empty, dir);
        Assert.AreEqual(string.Empty, sln);
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

        Assert.IsFalse(provider.TryResolve(request, out var dir, out var sln),
            "an unfetchable commit must degrade cleanly (no checkout published)");
        Assert.AreEqual(string.Empty, dir);
        Assert.AreEqual(string.Empty, sln);
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

        Assert.IsFalse(provider.TryResolve(request, out _, out _),
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
        var t1 = Task.Run(() => { var ok = provider.TryResolve(request, out var d, out _); return (ok, d); });
        var t2 = Task.Run(() => { var ok = provider.TryResolve(request, out var d, out _); return (ok, d); });
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

        var request = ServiceTestFixtures.Request("https://host/repo/..", new string('b', 40));

        Assert.IsFalse(provider.TryResolve(request, out var dir, out var sln),
            "a traversal repository url must never resolve or provision outside the checkout volume");
        Assert.AreEqual(string.Empty, dir);
        Assert.AreEqual(string.Empty, sln);
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

        Assert.IsTrue(provider.TryResolve(request, out var dir, out _),
            "a token-authenticated clone of a reachable commit still resolves");

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
    public void AuthenticatedUrl_InjectsTokenOnlyForHttpsWithoutUserInfo()
    {
        Assert.AreEqual("https://x-access-token:tok@github.com/o/r.git",
            CloningCheckoutProvider.AuthenticatedUrl("https://github.com/o/r.git", "tok"));
        // No token → unchanged.
        Assert.AreEqual("https://github.com/o/r.git",
            CloningCheckoutProvider.AuthenticatedUrl("https://github.com/o/r.git", null));
        // Non-https → unchanged (public/local remotes need no credential).
        Assert.AreEqual("git@github.com:o/r.git",
            CloningCheckoutProvider.AuthenticatedUrl("git@github.com:o/r.git", "tok"));
        // Already carries userinfo → don't double-inject.
        Assert.AreEqual("https://user@github.com/o/r.git",
            CloningCheckoutProvider.AuthenticatedUrl("https://user@github.com/o/r.git", "tok"));
    }

    [TestMethod]
    public void TryResolve_CachedCheckoutAtDifferentCommit_ReprovisionsToRequestedCommit()
    {
        var (remoteUrl, first) = NewRemoteRepo(out var repoDir);
        var paths = NewPaths();
        var provider = NewCloneProvider(paths);

        // First ensure clones the repo at its initial commit.
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(remoteUrl, first), out var dir1, out _),
            "the first ensure clones the repo at the initial commit");
        Assert.AreEqual(first, GitHead(dir1));

        // The branch advances on the remote; a later ensure asks for the NEW commit against the SAME repo.
        var second = CommitMore(repoDir);
        Assert.AreNotEqual(first, second);

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(remoteUrl, second), out var dir2, out var sln2),
            "an ensure for a different commit must re-provision rather than reuse the stale checkout");
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
        Assert.IsTrue(NewCloneProvider(paths).TryResolve(ServiceTestFixtures.Request(remoteUrl, commit), out _, out _));

        var cacheOnly = new CloningCheckoutProvider(
            new PersistentVolumeCheckoutProvider(paths), paths, token: null, gitExecutable: "definitely-not-git");
        Assert.IsTrue(cacheOnly.TryResolve(ServiceTestFixtures.Request(remoteUrl, commit), out var dir, out var sln),
            "a same-commit re-ensure resolves from cache");
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

        Assert.IsFalse(provider.TryResolve(request, out var dir, out var sln),
            "an http(s) URL embedding userinfo must be refused");
        Assert.AreEqual(string.Empty, dir);
        Assert.AreEqual(string.Empty, sln);
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

    // ---- helpers ---------------------------------------------------------------------------------

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
