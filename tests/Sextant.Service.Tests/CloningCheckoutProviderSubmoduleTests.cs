using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #125: <see cref="CloningCheckoutProvider"/> recursively initializes submodules at their PINNED
/// gitlink commits, credential-safely. Every fixture is a set of sibling LOCAL git repositories served over
/// <c>file://</c> (no network); submodules are declared the way a real superproject records them — a
/// <c>.gitmodules</c> entry plus a mode-160000 gitlink in the tree. <c>file://</c> submodule URLs are refused
/// in production, so these tests opt in with the test-only <see cref="CloningCheckoutProvider.AllowFileTransportForTesting"/>.
/// </summary>
[TestClass]
public class CloningCheckoutProviderSubmoduleTests
{
    private const string Sentinel = "SENTINEL-TOKEN-7b3e9d51c0";

    private readonly List<string> _tempDirs = [];
    private readonly List<string> _log = [];
    private string _base = null!;

    [TestInitialize]
    public void Init() => _base = Temp("sextant_subfix");

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
            DeleteTree(dir);
    }

    [TestMethod]
    public async Task TryResolve_PopulatesAbsoluteRelativeAndNestedSubmodules_AtPinnedCommits()
    {
        var (_, libUrl, libPinned, libTip) = NewLibRepo("lib");
        var (_, deepUrl, deepPinned) = NewLeafRepo("deep", "Deep.cs");
        var (_, midUrl, midPinned) = NewRepoWithSubmodules("mid", ("deep", "deep", "../deep", deepPinned));
        var (_, appUrl, appCommit) = NewRepoWithSubmodules("app",
            ("abs", "libs/abs", libUrl, libPinned),
            ("rel", "libs/rel", "../lib", libPinned),
            ("mid", "libs/mid", midUrl, midPinned));
        Assert.AreNotEqual(libPinned, libTip, "the fixture pins a commit that is NOT the branch tip");

        var paths = NewPaths();
        var provider = NewProvider(paths);
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out var resolution));
        var checkout = resolution.CheckoutDir;

        Assert.AreEqual(appCommit, GitHead(checkout));
        Assert.AreEqual(libPinned, GitHead(Path.Combine(checkout, "libs", "abs")), "absolute url, pinned commit (not the tip)");
        Assert.AreEqual(libPinned, GitHead(Path.Combine(checkout, "libs", "rel")), "relative url resolves against the parent");
        Assert.AreEqual(midPinned, GitHead(Path.Combine(checkout, "libs", "mid")));
        Assert.AreEqual(deepPinned, GitHead(Path.Combine(checkout, "libs", "mid", "deep")), "nested submodule is recursive");
        Assert.AreEqual("true", GitOut(Path.Combine(checkout, "libs", "abs"), "rev-parse", "--is-shallow-repository").Trim(),
            "a pinned commit is fetched shallow (--depth 1) where the server allows fetch-by-sha");

        var outcomes = resolution.SubmoduleProvisioning;
        CollectionAssert.AreEqual(
            new[] { "libs/abs", "libs/mid", "libs/mid/deep", "libs/rel" }, outcomes.Select(o => o.Path).ToArray());
        Assert.IsTrue(outcomes.All(o => o.IsPopulated), string.Join("; ", outcomes.Select(o => $"{o.Path}:{o.Status}:{o.Reason}")));
        Assert.AreEqual(libUrl.TrimEnd('/'), outcomes.Single(o => o.Path == "libs/rel").Url, "the relative url was resolved");
        Assert.AreEqual(deepPinned, outcomes.Single(o => o.Path == "libs/mid/deep").Commit);

        // Canonical git layout: git dirs absorbed under .git/modules (a gitdir LINK file in each work tree).
        Assert.IsTrue(File.Exists(Path.Combine(checkout, "libs", "abs", ".git")), "the submodule's .git is a gitdir link");
        Assert.IsTrue(Directory.Exists(Path.Combine(checkout, ".git", "modules", "abs")));
        Assert.IsTrue(Directory.Exists(Path.Combine(checkout, ".git", "modules", "mid", "modules", "deep")));
        Assert.AreEqual(CloningCheckoutProvider.CheckoutLayoutVersion, CloningCheckoutProvider.ReadMarker(checkout)!.Layout);

        // The coverage scan sees every declared submodule populated …
        var inventory = SnapshotCoverageBuilder.Inventory.Scan(checkout);
        Assert.AreEqual(4, inventory.Submodules.Count);
        Assert.IsTrue(inventory.Submodules.All(s => s.Populated));

        // … and Phase-12 discovery sees them initialized + CLEAN at their pins with their OWN remotes (the
        // shape that makes provider snapshots / cross-repository edges fire).
        var discovered = await SubmoduleDiscovery.DiscoverAsync(checkout);
        Assert.AreEqual(4, discovered.Count, string.Join("; ", discovered.Select(d => d.Path)));
        Assert.IsTrue(discovered.All(d => !d.IsDirty), "every submodule is checked out exactly at its gitlink");
        var abs = discovered.Single(d => d.Path == "libs/abs");
        Assert.AreEqual(libPinned, abs.CommitSha);
        Assert.AreEqual(GitRemoteNormalizer.Normalize(libUrl), abs.RemoteUrl);
        Assert.AreEqual(deepPinned, discovered.Single(d => d.Path == "libs/mid/deep").CommitSha);
    }

    [TestMethod]
    public void TryResolve_UnfetchableSubmodules_CheckoutSucceedsAndCoverageIsPartialWithReasons()
    {
        var (_, libUrl, libPinned, _) = NewLibRepo("lib");
        var missingUrl = new Uri(Path.Combine(_base, "does-not-exist")).AbsoluteUri;
        var (_, appUrl, appCommit) = NewRepoWithSubmodules("app",
            [
                ("good", "libs/good", libUrl, libPinned),
                ("missing", "libs/missing", missingUrl, libPinned),
                ("badsha", "libs/badsha", libUrl, new string('1', 40)),
                ("refused", "libs/refused", "https://example.invalid/org/x.git", libPinned),
                ("ssh", "libs/ssh", "git@example.invalid:org/x.git", libPinned),
            ],
            extraGitmodules: "[submodule \"ghost\"]\n\tpath = libs/ghost\n\turl = ../lib\n");

        var paths = NewPaths();
        var provider = NewProvider(paths);
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out var resolution),
            "an unfetchable submodule must NOT fail the whole checkout");
        var checkout = resolution.CheckoutDir;

        var byPath = resolution.SubmoduleProvisioning.ToDictionary(o => o.Path);
        Assert.AreEqual(SubmoduleProvisioningStatus.Populated, byPath["libs/good"].Status);
        Assert.AreEqual(SubmoduleProvisioningStatus.FetchFailed, byPath["libs/missing"].Status);
        Assert.AreEqual(SubmoduleProvisioningStatus.CheckoutFailed, byPath["libs/badsha"].Status);
        Assert.AreEqual(SubmoduleProvisioningStatus.UrlRefused, byPath["libs/refused"].Status);
        Assert.AreEqual(SubmoduleProvisioningStatus.UrlRefused, byPath["libs/ssh"].Status);
        Assert.AreEqual(SubmoduleProvisioningStatus.NoGitlink, byPath["libs/ghost"].Status);
        foreach (var failed in byPath.Values.Where(o => !o.IsPopulated))
            Assert.IsFalse(string.IsNullOrWhiteSpace(failed.Reason), $"{failed.Path} must carry a reason");
        StringAssert.Contains(byPath["libs/missing"].Reason, "does not appear to be a git repository");

        // Failed submodules stay UNPOPULATED (empty directory, no partial git dir left behind).
        foreach (var path in new[] { "libs/missing", "libs/badsha", "libs/refused", "libs/ssh" })
        {
            var dir = Path.Combine(checkout, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(Directory.Exists(dir), path);
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(dir).Any(), $"{path} must be left empty");
        }
        Assert.AreEqual(libPinned, GitHead(Path.Combine(checkout, "libs", "good")));

        // Coverage is PARTIAL with a concrete per-submodule reason.
        var appProject = Path.Combine(checkout, "src", "App", "App.csproj");
        var load = new MultiSolutionLoadResult(
            new AdhocWorkspace().CurrentSolution, [], [new SolutionCoverage(resolution.SelectedSolutions[0], 1, 1, [])])
        {
            DeclaredProjects = [appProject]
        };
        var coverage = SnapshotCoverageBuilder.Build(checkout, resolution, load, SnapshotCoverageBuilder.Inventory.Scan(checkout));
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Coverage.Verdict);
        Assert.AreEqual(5, coverage.Coverage.SubmodulesUnpopulated, "missing, badsha, refused, ssh, ghost");
        var reason = coverage.Coverage.Reasons.Single(r => r.Contains("submodule", StringComparison.Ordinal));
        StringAssert.Contains(reason, "libs/missing (fetch failed");
        StringAssert.Contains(reason, "libs/badsha (pinned commit not found");
        StringAssert.Contains(reason, "libs/refused (url refused: host 'example.invalid'");
        StringAssert.Contains(reason, "libs/ghost (no gitlink");
        var diag = coverage.Diagnostics.Single(d => d.Code == "submodule_unpopulated" && d.ProjectPath == "libs/ssh");
        StringAssert.Contains(diag.Message, "url refused");

        // A re-ensure of a checkout WITH a current marker is a cache hit even though submodules failed (the
        // failure is recorded, not retried on every ensure) — the published tree is not replaced.
        var keep = Path.Combine(checkout, "keep.txt");
        File.WriteAllText(keep, "x");
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out var again));
        Assert.IsTrue(File.Exists(keep), "a marker'd checkout is reused, not re-cloned");
        Assert.AreEqual(SubmoduleProvisioningStatus.FetchFailed,
            again.SubmoduleProvisioning.Single(o => o.Path == "libs/missing").Status, "outcomes are re-read from the marker");
    }

    [TestMethod]
    public void TryResolve_WithSentinelToken_NoCredentialPersistsAnywhereInThePublishedCheckout()
    {
        // Route https://git.test/org/{app,lib}.git to the local fixtures with url.<file>.insteadOf supplied
        // through the INHERITED environment config (what an operator could do), so the provider runs its REAL
        // authenticated path: the repository authority is git.test, the token rides the env-scoped
        // extraheader on every fetch, the scp-style and relative submodule urls are rewritten onto
        // https://git.test, and the same-host submodules are fetched with the token env. Then prove the
        // sentinel token appears NOWHERE on disk (work tree, every git dir incl. .git/modules, objects).
        var (lib, _, libPinned, _) = NewLibRepo("lib");
        var (app, _, appCommit) = NewRepoWithSubmodules("app",
            ("scp", "libs/scp", "git@git.test:org/lib.git", libPinned),
            ("rel", "libs/rel", "../lib.git", libPinned));

        using var env = new EnvironmentScope(
            ("GIT_CONFIG_COUNT", "2"),
            ("GIT_CONFIG_KEY_0", $"url.{new Uri(app).AbsoluteUri}.insteadOf"),
            ("GIT_CONFIG_VALUE_0", "https://git.test/org/app.git"),
            ("GIT_CONFIG_KEY_1", $"url.{new Uri(lib).AbsoluteUri}.insteadOf"),
            ("GIT_CONFIG_VALUE_1", "https://git.test/org/lib.git"));

        const string appUrl = "https://git.test/org/app.git";
        var paths = NewPaths();
        var provider = NewProvider(paths, token: Sentinel);
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out var resolution),
            string.Join(Environment.NewLine, _log));
        var checkout = resolution.CheckoutDir;

        var outcomes = resolution.SubmoduleProvisioning;
        Assert.IsTrue(outcomes.All(o => o.IsPopulated), string.Join("; ", outcomes.Select(o => $"{o.Path}:{o.Status}:{o.Reason}")));
        Assert.AreEqual("https://git.test/org/lib.git", outcomes.Single(o => o.Path == "libs/scp").Url, "scp url → https (same host)");
        Assert.AreEqual("https://git.test/org/lib.git", outcomes.Single(o => o.Path == "libs/rel").Url);
        Assert.AreEqual(libPinned, GitHead(Path.Combine(checkout, "libs", "scp")));

        // The persisted remotes are the CLEAN https urls.
        Assert.AreEqual(appUrl, GitOut(checkout, "config", "--get", "remote.origin.url").Trim());
        Assert.AreEqual("https://git.test/org/lib.git", GitOut(checkout, "config", "--get", "submodule.scp.url").Trim());
        Assert.AreEqual("https://git.test/org/lib.git",
            GitOut(Path.Combine(checkout, "libs", "scp"), "config", "--get", "remote.origin.url").Trim());

        var needles = new[]
        {
            Sentinel,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Sentinel)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + Sentinel)),
        };
        var files = Directory.EnumerateFiles(checkout, "*", SearchOption.AllDirectories).ToList();
        Assert.IsTrue(files.Any(f => f.Contains(Path.Combine(".git", "modules", "scp"), StringComparison.Ordinal)),
            "the scan covers the absorbed submodule git dirs");
        foreach (var file in files)
        {
            Assert.AreNotEqual("FETCH_HEAD", Path.GetFileName(file), $"no fetch record may be published: {file}");
            var bytes = File.ReadAllBytes(file);
            foreach (var needle in needles)
                Assert.IsFalse(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0,
                    $"the token (in some form) leaked into {Path.GetRelativePath(checkout, file)}");
        }
        foreach (var line in _log)
            Assert.IsFalse(needles.Any(n => line.Contains(n, StringComparison.Ordinal)), $"the token leaked into a log line: {line}");
    }

    [TestMethod]
    public void TryResolve_PreChangeCachedCheckout_IsUpgradedInPlaceOfTheStaleTree()
    {
        var (_, libUrl, libPinned, _) = NewLibRepo("lib");
        var (_, appUrl, appCommit) = NewRepoWithSubmodules("app", ("abs", "libs/abs", libUrl, libPinned));
        var paths = NewPaths();
        var canonical = PreChangeCheckout(paths, appUrl, appCommit);
        Assert.IsTrue(Directory.Exists(Path.Combine(canonical, "libs", "abs")), "a plain clone leaves the gitlink dir empty");
        Assert.IsFalse(File.Exists(Path.Combine(canonical, "libs", "abs", ".git")));
        Assert.IsNull(CloningCheckoutProvider.ReadMarker(canonical), "a pre-#125 checkout has no provisioning marker");
        var stale = Path.Combine(canonical, "stale.txt");
        File.WriteAllText(stale, "pre-change");

        var provider = NewProvider(paths);
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out var resolution));

        Assert.AreEqual(Path.GetFullPath(canonical), Path.GetFullPath(resolution.CheckoutDir));
        Assert.AreEqual(appCommit, GitHead(canonical));
        Assert.AreEqual(libPinned, GitHead(Path.Combine(canonical, "libs", "abs")), "the cache hit did NOT skip submodule init");
        Assert.IsFalse(File.Exists(stale), "the upgrade swaps in a whole fresh clone (never populates the served tree in place)");
        Assert.AreEqual(CloningCheckoutProvider.CheckoutLayoutVersion, CloningCheckoutProvider.ReadMarker(canonical)!.Layout);
        Assert.IsTrue(resolution.SubmoduleProvisioning.Single().IsPopulated);
        Assert.IsFalse(Directory.EnumerateDirectories(paths.CheckoutRoot, ".tmp-clone-*").Any(),
            "the upgrade leaves no temp or retired directories behind");

        // The upgraded checkout now carries a marker ⇒ a later re-ensure is a plain cache hit.
        var keep = Path.Combine(canonical, "keep.txt");
        File.WriteAllText(keep, "x");
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out _));
        Assert.IsTrue(File.Exists(keep));
    }

    [TestMethod]
    public void TryResolve_PreChangeCachedCheckoutWithoutSubmodules_IsReusedAsIs()
    {
        var (_, appUrl, appCommit) = NewRepoWithSubmodules("app");
        var paths = NewPaths();
        var canonical = PreChangeCheckout(paths, appUrl, appCommit);
        var stale = Path.Combine(canonical, "stale.txt");
        File.WriteAllText(stale, "pre-change");

        Assert.IsTrue(NewProvider(paths).TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out _));
        Assert.IsTrue(File.Exists(stale), "nothing to upgrade ⇒ the pre-change checkout is a plain cache hit");
    }

    [TestMethod]
    public void TryResolve_PreChangeCachedCheckout_UpgradeFailsDeterministically_KeepsServingTheCachedTree()
    {
        var (_, libUrl, libPinned, _) = NewLibRepo("lib");
        var (app, appUrl, appCommit) = NewRepoWithSubmodules("app", ("abs", "libs/abs", libUrl, libPinned));
        var paths = NewPaths();
        var canonical = PreChangeCheckout(paths, appUrl, appCommit);
        var stale = Path.Combine(canonical, "stale.txt");
        File.WriteAllText(stale, "pre-change");
        DeleteTree(app); // the repository is no longer fetchable (a permanent failure)

        Assert.IsTrue(NewProvider(paths).TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out var resolution),
            "a failed upgrade must not make a previously-indexable checkout unusable");
        Assert.AreEqual(Path.GetFullPath(canonical), Path.GetFullPath(resolution.CheckoutDir));
        Assert.IsTrue(File.Exists(stale), "the cached tree is served unchanged");
        Assert.IsFalse(File.Exists(Path.Combine(canonical, "libs", "abs", ".git")), "its submodule stays unpopulated (coverage partial)");
        Assert.IsTrue(_log.Any(l => l.Contains("reusing it as-is", StringComparison.Ordinal)), string.Join(Environment.NewLine, _log));
    }

    [TestMethod]
    public void TryResolve_FileSubmoduleWithoutTheTestFlag_IsRefused()
    {
        var (_, libUrl, libPinned, _) = NewLibRepo("lib");
        var (_, appUrl, appCommit) = NewRepoWithSubmodules("app", ("abs", "libs/abs", libUrl, libPinned));
        var paths = NewPaths();
        // Production configuration: an untrusted .gitmodules may never read a local repository on the host.
        var provider = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, log: _log.Add);

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out var resolution));
        var outcome = resolution.SubmoduleProvisioning.Single();
        Assert.AreEqual(SubmoduleProvisioningStatus.UrlRefused, outcome.Status);
        StringAssert.Contains(outcome.Reason, "file://");
        Assert.IsFalse(File.Exists(Path.Combine(resolution.CheckoutDir, "libs", "abs", ".git")));
    }

    [TestMethod]
    public void TryResolve_TransientSubmoduleFailure_RetriesUntilTheFinalAttempt_ThenDegradesToPartial()
    {
        // A connection-refused endpoint is a TRANSIENT failure (not a recognized permanent git error). Before
        // the service's last attempt it discards the staged clone and throws (retry); ON the last attempt it
        // must not make the whole repository un-indexable — the submodule is left unpopulated with a reason.
        var (_, libUrl, libPinned, _) = NewLibRepo("lib");
        const string unreachable = "https://127.0.0.1:1/org/down.git";
        var (_, appUrl, appCommit) = NewRepoWithSubmodules("app",
            ("good", "libs/good", libUrl, libPinned),
            ("down", "libs/down", unreachable, libPinned));
        var paths = NewPaths();
        var provider = NewProvider(paths, token: Sentinel, submoduleHosts: ["127.0.0.1:1"]);
        var request = ServiceTestFixtures.Request(appUrl, appCommit);

        var ex = Assert.ThrowsExactly<TransientProvisioningException>(() => provider.TryResolve(request, out _));
        Assert.IsFalse(ex.Message.Contains(Sentinel, StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(appUrl))),
            "a non-final transient failure publishes nothing");

        Assert.IsTrue(provider.TryResolve(request with { IsFinalProvisioningAttempt = true }, out var resolution),
            "the final attempt degrades the transient submodule failure instead of failing the checkout");
        var down = resolution.SubmoduleProvisioning.Single(o => o.Path == "libs/down");
        Assert.AreEqual(SubmoduleProvisioningStatus.FetchFailed, down.Status);
        StringAssert.Contains(down.Reason, "final provisioning attempt");
        Assert.IsTrue(resolution.SubmoduleProvisioning.Single(o => o.Path == "libs/good").IsPopulated);
        var downDir = Path.Combine(resolution.CheckoutDir, "libs", "down");
        Assert.IsFalse(Directory.Exists(downDir) && Directory.EnumerateFileSystemEntries(downDir).Any(),
            "the degraded submodule is left EMPTY (no partial git dir)");
        Assert.AreEqual(1, SnapshotCoverageBuilder.Inventory.Scan(resolution.CheckoutDir).Submodules.Count(s => !s.Populated),
            "coverage reports the degraded submodule unpopulated");
        foreach (var line in _log)
            Assert.IsFalse(line.Contains(Sentinel, StringComparison.Ordinal), line);
    }

    [TestMethod]
    public void TryResolve_PreChangeCachedCheckout_TransientUpgradeFailure_RetriesThenServesTheCachedTreeOnTheFinalAttempt()
    {
        var (_, libUrl, libPinned, _) = NewLibRepo("lib");
        var (app, _, appCommit) = NewRepoWithSubmodules("app", ("abs", "libs/abs", libUrl, libPinned));
        // The cached checkout was published for an https remote that is now unreachable (transient).
        const string appUrl = "https://127.0.0.1:1/org/app.git";
        var paths = NewPaths();
        Directory.CreateDirectory(paths.CheckoutRoot);
        var canonical = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(appUrl));
        Git(paths.CheckoutRoot, "clone", "--quiet", "--no-recurse-submodules", new Uri(app).AbsoluteUri, canonical);
        Git(canonical, "checkout", "--quiet", "--detach", appCommit);
        var stale = Path.Combine(canonical, "stale.txt");
        File.WriteAllText(stale, "pre-change");
        var provider = NewProvider(paths);
        var request = ServiceTestFixtures.Request(appUrl, appCommit);

        Assert.ThrowsExactly<TransientProvisioningException>(() => provider.TryResolve(request, out _),
            "before the final attempt a transient upgrade failure is retried");
        Assert.IsTrue(File.Exists(stale), "the failed upgrade never touched the served tree");

        Assert.IsTrue(provider.TryResolve(request with { IsFinalProvisioningAttempt = true }, out var resolution),
            "on the final attempt the cached checkout keeps being served");
        Assert.AreEqual(Path.GetFullPath(canonical), Path.GetFullPath(resolution.CheckoutDir));
        Assert.IsTrue(File.Exists(stale));
        Assert.IsTrue(_log.Any(l => l.Contains("reusing it as-is", StringComparison.Ordinal)), string.Join(Environment.NewLine, _log));
    }

    [TestMethod]
    public void TryResolve_TokenWithAHostItCannotBeScopedTo_IsNotSent_AndSaysWhy()
    {
        var paths = NewPaths();
        var provider = NewProvider(paths, token: Sentinel);
        // An IPv6 literal is a valid https host but not a plain DNS name, so the token cannot be host-scoped to
        // it. (A literal, not a name: no DNS lookup — the connection is refused/unreachable, i.e. transient.)
        var request = ServiceTestFixtures.Request("https://[::1]:1/org/app.git", new string('a', 40));

        Assert.ThrowsExactly<TransientProvisioningException>(() => provider.TryResolve(request, out _));
        Assert.IsTrue(_log.Any(l => l.Contains("is not sent to", StringComparison.Ordinal)),
            "an unscoped token is never dropped silently: " + string.Join(Environment.NewLine, _log));
        foreach (var line in _log)
            Assert.IsFalse(line.Contains(Sentinel, StringComparison.Ordinal), line);
    }

    [TestMethod]
    public void TryResolve_GitNeverInheritsTheServiceTokenOrAnySextantVariable()
    {
        // A fake git that dumps the environment it was started with: git — and anything IT launches
        // (transports, smudge filters, hooks) — must never see SEXTANT_SERVICE_CHECKOUT_TOKEN (the token
        // reaches git ONLY as the host-scoped header), the service's other SEXTANT_* secrets, or any other
        // variable that happens to carry the raw token.
        var fakeDir = Temp("sextant_fakegit");
        var dump = Path.Combine(fakeDir, "env.txt");
        string fakeGit;
        if (OperatingSystem.IsWindows())
        {
            fakeGit = Path.Combine(fakeDir, "git.cmd");
            File.WriteAllText(fakeGit, "@set > \"%~dp0env.txt\"\r\n@exit /b 1\r\n");
        }
        else
        {
            fakeGit = Path.Combine(fakeDir, "git");
            File.WriteAllText(fakeGit, "#!/bin/sh\nenv > \"$(dirname \"$0\")/env.txt\"\nexit 1\n");
            File.SetUnixFileMode(fakeGit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var env = new EnvironmentScope(
            ("SEXTANT_SERVICE_CHECKOUT_TOKEN", Sentinel),
            ("SEXTANT_SERVICE_CONTROL_TOKEN", "control-secret-4d1f"),
            ("UNRELATED_VARIABLE_WITH_TOKEN", "prefix-" + Sentinel));

        var paths = NewPaths();
        var provider = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths,
            token: Sentinel, gitExecutable: fakeGit, log: _log.Add);
        Assert.IsFalse(provider.TryResolve(
            ServiceTestFixtures.Request("https://git.test/org/app.git", new string('a', 40)), out _));

        Assert.IsTrue(File.Exists(dump), "the fake git ran: " + string.Join(Environment.NewLine, _log));
        var lines = File.ReadAllLines(dump);
        Assert.IsTrue(lines.Contains("GIT_TERMINAL_PROMPT=0"), "the dump is of the provider's git environment");
        foreach (var line in lines)
        {
            Assert.IsFalse(line.StartsWith("SEXTANT_", StringComparison.OrdinalIgnoreCase), $"inherited: {line}");
            Assert.IsFalse(line.Contains(Sentinel, StringComparison.Ordinal), $"the raw token reached git: {line}");
            Assert.IsFalse(line.Contains("control-secret-4d1f", StringComparison.Ordinal), line);
        }
    }

    [TestMethod]
    public void TryResolve_SubmoduleCheckoutFailsHalfway_LeavesItsDirectoryEmpty()
    {
        // A submodule commit whose checkout fails AFTER writing some files (a required smudge filter that
        // fails on the LAST path): the partially written work tree — including a project file the solution
        // loader would otherwise pick up into the PARENT snapshot — must not survive; the directory is reset to
        // empty and the submodule reported unpopulated.
        var lib = InitRepo("boomlib");
        Write(lib, ".gitattributes", "*.boom filter=boom\n");
        Write(lib, "A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write(lib, "Z.boom", "payload");
        var libPinned = CommitAll(lib, "boom");
        var (_, appUrl, appCommit) = NewRepoWithSubmodules("app",
            ("boom", "libs/boom", new Uri(lib).AbsoluteUri, libPinned));

        using var env = new EnvironmentScope(
            ("GIT_CONFIG_COUNT", "2"),
            ("GIT_CONFIG_KEY_0", "filter.boom.smudge"),
            ("GIT_CONFIG_VALUE_0", "false"),
            ("GIT_CONFIG_KEY_1", "filter.boom.required"),
            ("GIT_CONFIG_VALUE_1", "true"));

        var paths = NewPaths();
        Assert.IsTrue(NewProvider(paths).TryResolve(ServiceTestFixtures.Request(appUrl, appCommit), out var resolution),
            string.Join(Environment.NewLine, _log));

        var outcome = resolution.SubmoduleProvisioning.Single();
        Assert.AreEqual(SubmoduleProvisioningStatus.CheckoutFailed, outcome.Status, outcome.Reason);
        var dir = Path.Combine(resolution.CheckoutDir, "libs", "boom");
        Assert.IsTrue(Directory.Exists(dir));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(dir).Any(),
            "no partial work-tree file (or git dir) of the failed submodule survives: "
            + string.Join(", ", Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName)));
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private ServicePaths NewPaths() => new(ServiceVolumes.Rooted(Temp("sextant_subfix_data")));

    private CloningCheckoutProvider NewProvider(ServicePaths paths, string? token = null, string[]? submoduleHosts = null) =>
        new(new PersistentVolumeCheckoutProvider(paths), paths, token: token, log: _log.Add, submoduleHosts: submoduleHosts)
        {
            AllowFileTransportForTesting = true
        };

    /// <summary>A checkout exactly as the pre-#125 provider published it: the commit, no submodules, no marker.</summary>
    private static string PreChangeCheckout(ServicePaths paths, string url, string commit)
    {
        Directory.CreateDirectory(paths.CheckoutRoot);
        var canonical = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(url));
        Git(paths.CheckoutRoot, "clone", "--quiet", "--no-recurse-submodules", url, canonical);
        Git(canonical, "checkout", "--quiet", "--detach", commit);
        return canonical;
    }

    // A leaf library with TWO commits: the first (pinned by superprojects) and a later tip.
    private (string dir, string url, string pinned, string tip) NewLibRepo(string name)
    {
        var dir = InitRepo(name);
        Write(dir, "Lib.cs", "namespace Lib; public class Library { }");
        var pinned = CommitAll(dir, "lib v1");
        Write(dir, "Later.cs", "namespace Lib; public class Later { }");
        var tip = CommitAll(dir, "lib v2");
        return (dir, new Uri(dir).AbsoluteUri, pinned, tip);
    }

    private (string dir, string url, string commit) NewLeafRepo(string name, string file)
    {
        var dir = InitRepo(name);
        Write(dir, file, $"namespace {Path.GetFileNameWithoutExtension(file)}; public class C {{ }}");
        return (dir, new Uri(dir).AbsoluteUri, CommitAll(dir, name));
    }

    private (string dir, string url, string commit) NewRepoWithSubmodules(
        string name, params (string Name, string Path, string Url, string Sha)[] submodules) =>
        NewRepoWithSubmodules(name, submodules, extraGitmodules: null);

    /// <summary>
    /// A superproject: <c>App.slnx</c> → <c>src/App/App.csproj</c>, plus one <c>.gitmodules</c> entry and one
    /// mode-160000 gitlink per submodule (recorded directly in the index, exactly what <c>git submodule add</c>
    /// leaves in the tree — without cloning anything).
    /// </summary>
    private (string dir, string url, string commit) NewRepoWithSubmodules(
        string name, (string Name, string Path, string Url, string Sha)[] submodules, string? extraGitmodules)
    {
        var dir = InitRepo(name);
        Write(dir, "App.slnx", "<Solution>\n  <Project Path=\"src/App/App.csproj\" />\n</Solution>\n");
        Write(dir, "src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write(dir, "src/App/App.cs", "namespace App; public class Program { }");
        var gitmodules = new StringBuilder();
        foreach (var s in submodules)
            gitmodules.Append($"[submodule \"{s.Name}\"]\n\tpath = {s.Path}\n\turl = {s.Url}\n");
        gitmodules.Append(extraGitmodules);
        if (gitmodules.Length > 0)
            Write(dir, ".gitmodules", gitmodules.ToString());
        Git(dir, "add", "-A");
        foreach (var s in submodules)
            Git(dir, "update-index", "--add", "--cacheinfo", $"160000,{s.Sha},{s.Path}");
        Git(dir, "commit", "--quiet", "-m", name);
        return (dir, new Uri(dir).AbsoluteUri, GitHead(dir));
    }

    private string InitRepo(string name)
    {
        var dir = Path.Combine(_base, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "--quiet", "--initial-branch", "main");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Sextant Test");
        Git(dir, "config", "commit.gpgsign", "false");
        // Allow fetch-by-sha of the pinned (non-tip) commit from this non-bare file:// remote.
        Git(dir, "config", "uploadpack.allowReachableSHA1InWant", "true");
        Git(dir, "config", "uploadpack.allowAnySHA1InWant", "true");
        return dir;
    }

    private static string CommitAll(string dir, string message)
    {
        Git(dir, "add", "-A");
        Git(dir, "commit", "--quiet", "-m", message);
        return GitHead(dir);
    }

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Temp(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void DeleteTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // git object files are read-only on Windows
            Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    private static string GitHead(string dir) => GitOut(dir, "rev-parse", "HEAD").Trim();

    private static void Git(string dir, params string[] args) => GitOut(dir, args);

    private static string GitOut(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, $"git {string.Join(' ', args)} failed in {dir}: {stderr}");
        return stdout.GetAwaiter().GetResult();
    }

    /// <summary>Sets process environment variables for one test and restores the previous values.</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly List<(string Name, string? Previous)> _previous = [];

        public EnvironmentScope(params (string Name, string Value)[] variables)
        {
            foreach (var (name, value) in variables)
            {
                _previous.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, previous) in _previous)
                Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
