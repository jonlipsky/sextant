using System.IO.Compression;
using Sextant.Service.Restore;
using Sextant.Service.Sandbox;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #272: the per-repository NuGet package folders kept between jobs. Covers the folder mapping, the size bound
/// and its least-recently-used eviction, and — with real restores through the evaluation sandbox — that a later
/// restore of the same repository is served from its warm folder while a package one repository's feed served is
/// never visible to another repository's restore (two local feeds publish the same id and version with different
/// contents).
/// </summary>
[TestClass]
public class PackageCacheTests
{
    private const string PackageId = "Sextant.Fixture.Poisoned";
    private const string RepoA = "https://github.com/acme/alpha";
    private const string RepoB = "https://github.com/other/bravo";

    private string _root = null!;
    private ServicePaths _paths = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = ServiceTestFixtures.NewDataRoot();
        _paths = new ServicePaths(ServiceVolumes.Rooted(_root));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    [TestMethod]
    public void Acquire_GivesEachRepositoryItsOwnFolder_AndEquivalentSpellingsTheSameOne()
    {
        var cache = new PackageCache(_paths, PackageCache.DefaultMaxBytes);

        var a = cache.Acquire(RepoA)!;
        var b = cache.Acquire(RepoB)!;
        // Same basename, different owner: still a different repository (and folder).
        var sameName = cache.Acquire("https://github.com/other/alpha")!;

        Assert.AreNotEqual(a, b);
        Assert.AreNotEqual(a, sameName);
        Assert.AreEqual(a, cache.Acquire("https://github.com/acme/alpha.git/"), "spellings of one remote share a folder");
        foreach (var dir in new[] { a, b, sameName })
        {
            Assert.IsTrue(Directory.Exists(dir));
            Assert.IsTrue(_paths.IsPackageCacheDirectory(dir), dir);
            Assert.IsTrue(_paths.IsPersistent(dir) && !_paths.IsScratch(dir), "the folder outlives the job's scratch");
        }
    }

    [TestMethod]
    public void Disabled_AcquiresNothing_SoTheJobRestoresIntoScratch()
    {
        var cache = new PackageCache(_paths, 0);

        Assert.IsFalse(cache.Enabled);
        Assert.IsNull(cache.Acquire(RepoA));
        cache.Release(null);
        Assert.IsFalse(Directory.Exists(_paths.PackageCacheRoot));
    }

    [TestMethod]
    public void Acquire_ThatCannotPrepareTheFolder_ReturnsNull_SoTheJobFallsBackToScratch()
    {
        var log = new List<string>();
        var cache = new PackageCache(_paths, PackageCache.DefaultMaxBytes, log.Add);
        // A file where the repository's folder belongs: the folder cannot be created.
        File.WriteAllText(Path.Combine(_paths.PackageCacheRoot, ServicePaths.RepoDirectoryName(RepoA)), "in the way");

        Assert.IsNull(cache.Acquire(RepoA), "an unusable cache never fails the job");
        Assert.IsTrue(log.Any(l => l.Contains("restoring into job scratch", StringComparison.Ordinal)), string.Join("\n", log));
        Assert.IsNotNull(cache.Acquire(RepoB), "other repositories are unaffected");
    }

    [TestMethod]
    public void Release_EvictsLeastRecentlyUsedFoldersWhole_UntilTheCacheFitsItsBound()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var cache = new PackageCache(_paths, maxBytes: 250_000, clock: clock);

        var oldest = UseFolder(cache, clock, "https://github.com/acme/one", 100_000);
        var middle = UseFolder(cache, clock, "https://github.com/acme/two", 100_000);
        Assert.IsTrue(Directory.Exists(oldest) && Directory.Exists(middle), "200 KB fits a 250 KB bound");

        // A third job pushes the total to 300 KB: the least recently used folder goes, whole.
        var newest = UseFolder(cache, clock, "https://github.com/acme/three", 100_000);

        Assert.IsFalse(Directory.Exists(oldest), "the least recently used folder is evicted");
        Assert.IsFalse(File.Exists(oldest + ".usage"), "with its usage record");
        Assert.IsTrue(Directory.Exists(middle) && Directory.Exists(newest));
        Assert.IsTrue(cache.TotalBytes() <= cache.MaxBytes, $"{cache.TotalBytes()} > {cache.MaxBytes}");
        Assert.IsFalse(Directory.EnumerateDirectories(_paths.PackageCacheRoot, ".evicting-*").Any());

        // Using the older survivor again makes the newer one the eviction candidate.
        UseFolder(cache, clock, "https://github.com/acme/two", 100_000);
        UseFolder(cache, clock, "https://github.com/acme/four", 100_000);
        Assert.IsTrue(Directory.Exists(middle), "a recently used folder survives");
        Assert.IsFalse(Directory.Exists(newest), "the folder used longest ago is evicted");
        Assert.IsTrue(cache.TotalBytes() <= cache.MaxBytes);
    }

    [TestMethod]
    public void Release_AFolderLargerThanTheBound_EvictsEveryOtherFolderFirst_ThenItself()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var cache = new PackageCache(_paths, maxBytes: 150_000, clock: clock);
        var other = UseFolder(cache, clock, RepoB, 50_000);

        var huge = UseFolder(cache, clock, RepoA, 200_000);

        Assert.IsFalse(Directory.Exists(other), "others go first");
        Assert.IsFalse(Directory.Exists(huge), "a folder that alone exceeds the bound cannot be kept");
        Assert.AreEqual(0, cache.TotalBytes());
    }

    [TestMethod]
    public void Startup_SweepsAnInterruptedEviction()
    {
        var cache = new PackageCache(_paths, PackageCache.DefaultMaxBytes);
        var leftover = Path.Combine(_paths.PackageCacheRoot, ".evicting-0123");
        Directory.CreateDirectory(Path.Combine(leftover, "some.package", "1.0.0"));
        cache.Acquire(RepoA);

        _ = new PackageCache(_paths, PackageCache.DefaultMaxBytes);

        Assert.IsFalse(Directory.Exists(leftover));
        Assert.AreEqual(1, Directory.EnumerateDirectories(_paths.PackageCacheRoot).Count(), "the live folder is kept");
    }

    // ==== real restores through the sandbox ==============================================================

    [TestMethod]
    public async Task Restore_ASecondJobOfTheSameRepository_IsServedFromItsWarmFolder()
    {
        var cache = new PackageCache(_paths, PackageCache.DefaultMaxBytes);
        var feed = CreateFeed("feed-a", "from-a");
        var first = CreateConsumer("first", feed);

        var cold = await RestoreAsync(cache, RepoA, first);
        Assert.AreEqual("from-a", RestoredMarker(first), "the first job restores from the repository's feed");

        // The next commit is a fresh checkout (no obj/), and its feed is now gone: only the warm folder can serve it.
        Directory.Delete(feed, recursive: true);
        var second = CreateConsumer("second", feed);
        var warm = await RestoreAsync(cache, RepoA, second);

        Assert.AreEqual(1, warm.SolutionsSucceeded, "the later job restores without its feed");
        Assert.AreEqual("from-a", RestoredMarker(second));
        Console.WriteLine($"cold restore {cold.Elapsed.TotalSeconds:0.0}s, warm restore {warm.Elapsed.TotalSeconds:0.0}s");
    }

    [TestMethod]
    public async Task Restore_APackageOneRepositorysFeedServed_IsNeverVisibleToAnotherRepository()
    {
        // Two feeds publish the SAME id and version with DIFFERENT contents, as a hostile feed would to shadow a
        // package another repository trusts.
        var cache = new PackageCache(_paths, PackageCache.DefaultMaxBytes);
        var a = CreateConsumer("alpha", CreateFeed("feed-a", "from-a"));
        var b = CreateConsumer("bravo", CreateFeed("feed-b", "from-b"));

        await RestoreAsync(cache, RepoA, a);
        await RestoreAsync(cache, RepoB, b);

        Assert.AreEqual("from-a", RestoredMarker(a));
        Assert.AreEqual("from-b", RestoredMarker(b), "repository B's restore resolves its own feed's package");
        Assert.AreEqual("from-a", CachedMarker(cache.Acquire(RepoA)!));
        Assert.AreEqual("from-b", CachedMarker(cache.Acquire(RepoB)!));

        // The hazard the per-repository folders exist for: had B restored into A's folder, NuGet would have
        // trusted A's package by id and version and never asked B's feed.
        var shared = CreateConsumer("bravo-shared", CreateFeed("feed-b2", "from-b"));
        await RestoreInto(cache.Acquire(RepoA)!, shared);
        Assert.AreEqual("from-a", RestoredMarker(shared), "a shared folder is poisoned (control)");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private string UseFolder(PackageCache cache, ManualClock clock, string repository, int bytes)
    {
        clock.Advance(TimeSpan.FromMinutes(1));
        var dir = cache.Acquire(repository)!;
        var package = Path.Combine(dir, "some.package", "1.0.0");
        Directory.CreateDirectory(package);
        File.WriteAllBytes(Path.Combine(package, "payload.bin"), new byte[bytes]);
        cache.Release(dir);
        return dir;
    }

    private Task<PackageRestoreOutcome> RestoreAsync(PackageCache cache, string repository, (string Checkout, string Solution) consumer)
    {
        var packages = cache.Acquire(repository)!;
        return RestoreInto(packages, consumer, () => cache.Release(packages));
    }

    private async Task<PackageRestoreOutcome> RestoreInto(
        string packages, (string Checkout, string Solution) consumer, Action? after = null)
    {
        var sandbox = new EvaluationSandbox(
            SandboxPolicy.Enforced with { TimeBudget = TimeSpan.FromMinutes(5), MemoryBudgetBytes = 0 }, _paths);
        var scratch = _paths.AllocateScratch("restore");
        try
        {
            var outcome = await sandbox.RunAsync(consumer.Checkout, scratch, packages,
                token => new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(4))
                    .RunAsync(consumer.Checkout, [consumer.Solution], limit: null, token, scratch),
                CancellationToken.None);
            Assert.AreEqual(1, outcome.SolutionsSucceeded, string.Join("; ", outcome.Notes()));
            return outcome;
        }
        finally
        {
            after?.Invoke();
            _paths.ReleaseScratch(scratch);
        }
    }

    // The checkout's own view of the restore: the marker file of the package folder its assets file points at.
    private static string RestoredMarker((string Checkout, string Solution) consumer)
    {
        var assets = File.ReadAllText(Path.Combine(consumer.Checkout, "App", "obj", "project.assets.json"));
        using var json = System.Text.Json.JsonDocument.Parse(assets);
        var folder = json.RootElement.GetProperty("packageFolders").EnumerateObject().Single().Name;
        return CachedMarker(folder);
    }

    private static string CachedMarker(string packagesFolder) =>
        File.ReadAllText(Path.Combine(packagesFolder, PackageId.ToLowerInvariant(), "1.0.0", "content", "marker.txt")).Trim();

    private string CreateFeed(string name, string marker)
    {
        var feed = Path.Combine(_root, "feeds", name);
        Directory.CreateDirectory(feed);
        using var zip = ZipFile.Open(Path.Combine(feed, $"{PackageId}.1.0.0.nupkg"), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry($"{PackageId}.nuspec").Open()))
        {
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{PackageId}</id>
                    <version>1.0.0</version>
                    <authors>sextant</authors>
                    <description>Issue #272 fixture.</description>
                  </metadata>
                </package>
                """);
        }
        using (var writer = new StreamWriter(zip.CreateEntry("content/marker.txt").Open()))
            writer.Write(marker);
        using (var writer = new StreamWriter(zip.CreateEntry("lib/netstandard2.0/_._").Open()))
            writer.Write(string.Empty);
        return feed;
    }

    private (string Checkout, string Solution) CreateConsumer(string name, string feed)
    {
        var checkout = Path.Combine(_root, "checkouts-under-test", name);
        Directory.CreateDirectory(Path.Combine(checkout, "App"));
        File.WriteAllText(Path.Combine(checkout, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(checkout, "App", "App.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net{Environment.Version.Major}.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="{PackageId}" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        var solution = Path.Combine(checkout, "App.slnx");
        File.WriteAllText(solution, """<Solution><Project Path="App/App.csproj" /></Solution>""");
        return (checkout, solution);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
