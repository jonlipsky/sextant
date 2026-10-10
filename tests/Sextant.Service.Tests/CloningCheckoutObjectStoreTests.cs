using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #272: <see cref="CloningCheckoutProvider"/> fetches a new commit through a persistent per-repository object
/// store, so only objects the store lacks cross the network. The "remotes" are local repositories served over
/// <c>file://</c>, which speaks the same pack protocol as https; what a fetch transferred is measured as the objects
/// it added to the store (<c>git count-objects</c>) and as the provider's own transfer log line.
/// </summary>
[TestClass]
public class CloningCheckoutObjectStoreTests
{
    private const string Token = "STORE-SENTINEL-TOKEN-5c2a91e7";
    private const int TreeFiles = 200;

    private readonly List<string> _tempDirs = [];
    private readonly List<string> _log = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
            DeleteTree(dir);
    }

    [TestMethod]
    public void NewCommitOfAClonedRepository_TransfersOnlyItsNewObjects()
    {
        var (remote, url) = NewRemote("app");
        var first = CommitTree(remote, TreeFiles);
        var paths = NewPaths();
        var provider = NewProvider(paths);

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(url, first), out var a));
        var store = RepositoryStore(paths, url);
        var afterFirst = StoreObjects(store);
        Assert.IsTrue(afterFirst >= TreeFiles + 2, $"the first fetch brings the whole tree ({afterFirst} objects)");
        Assert.AreEqual(afterFirst, LoggedTransfer(url, first), "the provider logs what it transferred");

        // The next commit adds one file: a commit, the root tree and one blob are new; everything else is shared.
        File.WriteAllText(Path.Combine(remote, "New.cs"), "namespace App; public class New { }");
        var second = Commit(remote, "second");
        _log.Clear();
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(url, second), out var b));

        var transferred = LoggedTransfer(url, second);
        Assert.IsTrue(transferred is > 0 and <= 4, $"only the new objects cross the wire, got {transferred}");
        Assert.AreEqual(second, GitHead(b.CheckoutDir), "the checkout is at the new commit");
        Assert.IsTrue(File.Exists(Path.Combine(b.CheckoutDir, "New.cs")));
        Assert.AreEqual(a.CheckoutDir, b.CheckoutDir);

        // The published checkout is self-contained (never borrows the store's objects) and still shallow.
        Assert.IsFalse(File.Exists(Path.Combine(b.CheckoutDir, ".git", "objects", "info", "alternates")));
        Assert.AreEqual("true", GitOut(b.CheckoutDir, "rev-parse", "--is-shallow-repository").Trim());
        Assert.AreEqual(url, GitOut(b.CheckoutDir, "remote", "get-url", "origin").Trim(), "origin is still the clean url");

        // The store keeps one ref, on the commit it fetched last, and no fetch record.
        Assert.AreEqual($"{second} {CloningCheckoutProvider.StoreHeadRef}",
            GitOut(store, "for-each-ref", "--format=%(objectname) %(refname)").Trim());
        Assert.IsFalse(File.Exists(Path.Combine(store, "FETCH_HEAD")));
        Assert.IsFalse(Directory.EnumerateDirectories(paths.GitObjectStoreRoot, ".discarded-*").Any());
    }

    [TestMethod]
    public void WithoutTheStore_EveryCommitIsFetchedWhole()
    {
        var (remote, url) = NewRemote("app");
        var first = CommitTree(remote, TreeFiles);
        var paths = NewPaths();
        var provider = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, log: _log.Add)
        {
            UseObjectStore = false
        };

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(url, first), out var resolution));

        Assert.AreEqual(first, GitHead(resolution.CheckoutDir));
        Assert.IsFalse(Directory.Exists(paths.GitObjectStoreRoot) && Directory.EnumerateFileSystemEntries(paths.GitObjectStoreRoot).Any(),
            "SEXTANT_SERVICE_GIT_OBJECT_STORE=false keeps no store");
    }

    [TestMethod]
    public void Stores_AreIsolatedPerRepository()
    {
        var (remoteA, urlA) = NewRemote("alpha");
        var commitA = CommitTree(remoteA, 5);
        var (remoteB, urlB) = NewRemote("bravo");
        var commitB = CommitTree(remoteB, 5, salt: "b");
        var paths = NewPaths();
        var provider = NewProvider(paths);

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(urlA, commitA), out _));
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(urlB, commitB), out _));

        var storeA = RepositoryStore(paths, urlA);
        var storeB = RepositoryStore(paths, urlB);
        Assert.AreNotEqual(storeA, storeB);
        Assert.IsTrue(HasObject(storeA, commitA) && !HasObject(storeA, commitB), "A's store holds only A's objects");
        Assert.IsTrue(HasObject(storeB, commitB) && !HasObject(storeB, commitA), "B's store holds only B's objects");

        // A request for B's commit against A's repository is not satisfied from B's store: A's remote lacks it.
        Assert.IsFalse(provider.TryResolve(ServiceTestFixtures.Request(urlA, commitB), out _));
        Assert.IsFalse(HasObject(storeA, commitB));
    }

    [TestMethod]
    public void ACorruptStore_IsDiscarded_AndTheCommitStillProvisions()
    {
        var (remote, url) = NewRemote("app");
        var first = CommitTree(remote, 5);
        var paths = NewPaths();
        var provider = NewProvider(paths);
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(url, first), out _));

        // Break the store: every object is gone, but the repository shape (HEAD, objects/) remains.
        var store = RepositoryStore(paths, url);
        foreach (var pack in Directory.EnumerateFiles(Path.Combine(store, "objects"), "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(pack, FileAttributes.Normal);
            File.WriteAllText(pack, "garbage");
        }
        File.WriteAllText(Path.Combine(remote, "Next.cs"), "namespace App; class Next { }");
        var second = Commit(remote, "second");

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(url, second), out var resolution),
            string.Join(Environment.NewLine, _log));
        Assert.AreEqual(second, GitHead(resolution.CheckoutDir));
        Assert.IsFalse(Directory.EnumerateDirectories(paths.GitObjectStoreRoot, ".discarded-*").Any());
    }

    [TestMethod]
    public void AStoreHoldingTheToken_IsDiscardedFailClosed_AndNoStoreKeepsIt()
    {
        var (remote, url) = NewRemote("app");
        var first = CommitTree(remote, 5);
        var paths = NewPaths();
        var provider = NewProvider(paths, Token);
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(url, first), out _));
        var store = RepositoryStore(paths, url);
        Assert.IsFalse(AnyFileContains(paths.GitObjectStoreRoot, Token), "the token never reaches a store");

        // Plant the token in the store's metadata (a file the store keeps between jobs).
        File.AppendAllText(Path.Combine(store, "description"), $"\n{Token}\n");
        File.WriteAllText(Path.Combine(remote, "Next.cs"), "namespace App; class Next { }");
        var second = Commit(remote, "second");

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(url, second), out var resolution),
            string.Join(Environment.NewLine, _log));
        Assert.AreEqual(second, GitHead(resolution.CheckoutDir), "the commit still provisions, fetched directly");
        Assert.IsTrue(_log.Any(l => l.Contains("Object store discarded", StringComparison.Ordinal)), string.Join("\n", _log));
        Assert.IsFalse(AnyFileContains(paths.GitObjectStoreRoot, Token), "the planted credential is gone with the store");
        Assert.IsFalse(AnyFileContains(paths.CheckoutRoot, Token));
        Assert.IsFalse(_log.Any(l => l.Contains(Token, StringComparison.Ordinal)), "never logged");
    }

    [TestMethod]
    public void ConfigOrAlternatesPlantedInAStoreBetweenJobs_AreNeverReadByTheNextFetch()
    {
        // The store outlives the job, so anything written into it between jobs (repository code runs in the same
        // account, #76) must not steer the next, possibly token-bearing, fetch: no proxy or ssh command from its
        // config, and no objects borrowed from another repository's store through alternates.
        var (remoteA, urlA) = NewRemote("alpha");
        var commitA = CommitTree(remoteA, 5);
        var (remoteB, urlB) = NewRemote("bravo");
        var commitB = CommitTree(remoteB, 5, salt: "b");
        var paths = NewPaths();
        var provider = NewProvider(paths, Token);
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(urlA, commitA), out _));
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(urlB, commitB), out _));
        var storeA = RepositoryStore(paths, urlA);
        var storeB = RepositoryStore(paths, urlB);

        File.AppendAllText(Path.Combine(storeA, "config"),
            "\n[http]\n\tproxy = http://127.0.0.1:9\n\tsslVerify = false\n[core]\n\tsshCommand = false\n");
        File.WriteAllText(Path.Combine(storeA, "objects", "info", "alternates"), Path.Combine(storeB, "objects") + "\n");
        Assert.IsTrue(HasObject(storeA, commitB), "precondition: the planted alternates make B's objects visible in A");

        File.WriteAllText(Path.Combine(remoteA, "Next.cs"), "namespace App; class Next { }");
        var next = Commit(remoteA, "next");
        _log.Clear();
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(urlA, next), out var resolution),
            string.Join(Environment.NewLine, _log));

        Assert.AreEqual(next, GitHead(resolution.CheckoutDir));
        Assert.IsTrue(LoggedTransfer(urlA, next) is > 0 and <= 4, "fetched through the (reset) store, incrementally");
        var config = File.ReadAllText(Path.Combine(storeA, "config"));
        Assert.IsFalse(config.Contains("proxy", StringComparison.Ordinal) || config.Contains("sshCommand", StringComparison.Ordinal), config);
        Assert.IsFalse(File.Exists(Path.Combine(storeA, "objects", "info", "alternates")));
        Assert.IsFalse(HasObject(storeA, commitB), "A's store no longer reaches B's objects");
    }

    [TestMethod]
    public void ASubmoduleServerThatRefusesFetchBySha_IsFetchedByBranch_AndTheStoreKeepsOnlyItsHeadRef()
    {
        // Protocol v0 enforces uploadpack.allow*SHA1InWant (v2 serves any object), so the pinned, non-tip commit
        // cannot be fetched by id and the store falls back to fetching every branch into a temporary namespace.
        using var env = new EnvironmentScope(
            ("GIT_CONFIG_COUNT", "1"), ("GIT_CONFIG_KEY_0", "protocol.version"), ("GIT_CONFIG_VALUE_0", "0"));
        var (lib, libUrl) = NewRemote("lib");
        Git(lib, "config", "uploadpack.allowAnySHA1InWant", "false");
        Git(lib, "config", "uploadpack.allowReachableSHA1InWant", "false");
        var pinned = CommitTree(lib, 5, salt: "lib");
        Git(lib, "checkout", "--quiet", "-b", "other");
        File.WriteAllText(Path.Combine(lib, "Other.cs"), "namespace Lib; class Other { }");
        Commit(lib, "other branch");
        Git(lib, "checkout", "--quiet", "main");
        File.WriteAllText(Path.Combine(lib, "Tip.cs"), "namespace Lib; class Tip { }");
        Commit(lib, "tip");
        var (app, appUrl) = NewRemote("app");
        Write(app, ".gitmodules", $"[submodule \"lib\"]\n\tpath = libs/lib\n\turl = {libUrl}\n");
        Write(app, "App.slnx", "<Solution />");
        Git(app, "add", "-A");
        Git(app, "update-index", "--add", "--cacheinfo", $"160000,{pinned},libs/lib");
        Git(app, "commit", "--quiet", "-m", "with submodule");
        var paths = NewPaths();

        Assert.IsTrue(NewProvider(paths).TryResolve(ServiceTestFixtures.Request(appUrl, GitHead(app)), out var resolution),
            string.Join(Environment.NewLine, _log));

        Assert.AreEqual(pinned, GitHead(Path.Combine(resolution.CheckoutDir, "libs", "lib")));
        var store = Directory.GetDirectories(
            Path.Combine(Path.GetDirectoryName(RepositoryStore(paths, appUrl))!, "submodules")).Single();
        Assert.AreEqual($"{pinned} {CloningCheckoutProvider.StoreHeadRef}",
            GitOut(store, "for-each-ref", "--format=%(objectname) %(refname)").Trim(),
            "the branches fetched for the fallback are not kept");
    }

    [TestMethod]
    public void AnUnchangedSubmodule_TransfersNothingForTheNextSuperprojectCommit()
    {
        var (lib, libUrl) = NewRemote("lib");
        var libCommit = CommitTree(lib, 50, salt: "lib");
        var (app, appUrl) = NewRemote("app");
        Write(app, ".gitmodules", $"[submodule \"lib\"]\n\tpath = libs/lib\n\turl = {libUrl}\n");
        Write(app, "App.slnx", "<Solution />");
        Git(app, "add", "-A");
        Git(app, "update-index", "--add", "--cacheinfo", $"160000,{libCommit},libs/lib");
        Git(app, "commit", "--quiet", "-m", "with submodule"); // not Commit(): `add -A` would drop the bare gitlink
        var first = GitHead(app);
        var paths = NewPaths();
        var provider = NewProvider(paths);

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, first), out var a));
        Assert.IsTrue(a.SubmoduleProvisioning.Single().IsPopulated,
            a.SubmoduleProvisioning.Single().Reason + Environment.NewLine + string.Join(Environment.NewLine, _log));
        Assert.IsTrue(LoggedTransfer(libUrl, libCommit) >= 50, "the first fetch brings the submodule's tree");

        File.WriteAllText(Path.Combine(app, "Next.cs"), "namespace App; class Next { }");
        Git(app, "add", "Next.cs");
        Git(app, "commit", "--quiet", "-m", "next");
        var second = GitHead(app);
        _log.Clear();
        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(appUrl, second), out var b));

        Assert.AreEqual(libCommit, GitHead(Path.Combine(b.CheckoutDir, "libs", "lib")), "the submodule is populated again");
        Assert.AreEqual(0, LoggedTransfer(libUrl, libCommit), "its store already held the pinned commit");
        var subStores = Directory.GetDirectories(
            Path.Combine(Path.GetDirectoryName(RepositoryStore(paths, appUrl))!, "submodules"));
        Assert.AreEqual(1, subStores.Length, "the submodule's store lives under its superproject's");
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private CloningCheckoutProvider NewProvider(ServicePaths paths, string? token = null) =>
        new(new PersistentVolumeCheckoutProvider(paths), paths, token: token, log: _log.Add)
        {
            AllowFileTransportForTesting = true
        };

    private ServicePaths NewPaths() => new(ServiceVolumes.Rooted(Temp("sextant_store_data")));

    private static string RepositoryStore(ServicePaths paths, string url) =>
        Path.Combine(paths.GitObjectStoreRoot, ServicePaths.RepoDirectoryName(url), "repository.git");

    /// <summary>The object count the provider logged for its store fetch of <paramref name="commit"/> from <paramref name="url"/>.</summary>
    private long LoggedTransfer(string url, string commit)
    {
        var pattern = new Regex(
            $"^Object store for '{Regex.Escape(url)}': fetched (\\d+) new object\\(s\\) \\(\\d+ KiB\\) for {commit[..8]}\\.$");
        var match = _log.Select(l => pattern.Match(l)).LastOrDefault(m => m.Success);
        Assert.IsNotNull(match, "no transfer line for the fetch: " + string.Join(Environment.NewLine, _log));
        return long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long StoreObjects(string store)
    {
        long total = 0;
        foreach (var line in GitOut(store, "count-objects", "-v").Split('\n'))
        {
            if (line.StartsWith("count:", StringComparison.Ordinal) || line.StartsWith("in-pack:", StringComparison.Ordinal))
                total += long.Parse(line[(line.IndexOf(':') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return total;
    }

    private static bool HasObject(string repo, string sha) => TryGit(repo, "cat-file", "-e", sha + "^{commit}");

    private static bool AnyFileContains(string root, string needle)
    {
        if (!Directory.Exists(root))
            return false;
        var bytes = Encoding.UTF8.GetBytes(needle);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Any(f => File.ReadAllBytes(f).AsSpan().IndexOf(bytes) >= 0);
    }

    private (string dir, string url) NewRemote(string name)
    {
        var dir = Temp($"sextant_store_{name}");
        Git(dir, "init", "--quiet", "--initial-branch", "main");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Sextant Test");
        Git(dir, "config", "commit.gpgsign", "false");
        Git(dir, "config", "uploadpack.allowReachableSHA1InWant", "true");
        Git(dir, "config", "uploadpack.allowAnySHA1InWant", "true");
        return (dir, new Uri(dir).AbsoluteUri);
    }

    private static string CommitTree(string dir, int files, string salt = "")
    {
        Write(dir, "App.slnx", "<Solution />");
        for (var i = 0; i < files; i++)
            Write(dir, Path.Combine("src", $"File{i}.cs"), $"namespace App; public class File{i}{salt} {{ /* {Guid.NewGuid()} */ }}");
        return Commit(dir, "tree");
    }

    private static string Commit(string dir, string message)
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
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    private static string GitHead(string dir) => GitOut(dir, "rev-parse", "HEAD").Trim();

    private static void Git(string dir, params string[] args) => GitOut(dir, args);

    private static bool TryGit(string dir, params string[] args) => RunGit(dir, args).ExitCode == 0;

    private static string GitOut(string dir, params string[] args)
    {
        var (code, stdout, stderr) = RunGit(dir, args);
        Assert.AreEqual(0, code, $"git {string.Join(' ', args)} failed in {dir}: {stderr}");
        return stdout;
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

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string dir, string[] args)
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
        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr);
    }
}
