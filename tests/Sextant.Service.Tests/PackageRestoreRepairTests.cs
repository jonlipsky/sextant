using System.Text.Json;
using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #352: a project whose restore fails on a stale committed lock file (NU1004), or only on platform targets this
/// worker cannot build, is restored again for what it can restore. These restore real projects offline.
/// </summary>
[TestClass]
public class PackageRestoreRepairTests
{
    private static readonly string Tfm = $"net{Environment.Version.Major}.0";

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        // An empty local source: nothing downloads, and a package reference fails with NU1101.
        Directory.CreateDirectory(Path.Combine(root, "empty-feed"));
        File.WriteAllText(Path.Combine(root, "nuget.config"),
            "<configuration><packageSources><clear /><add key=\"empty\" value=\"empty-feed\" /></packageSources></configuration>");
        return root;
    }

    private static string AddProject(string root, string name, string body, string? lockFile = null)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        var project = Path.Combine(dir, name + ".csproj");
        File.WriteAllText(project, $"<Project Sdk=\"Microsoft.NET.Sdk\">{body}</Project>");
        if (lockFile is not null)
            File.WriteAllText(Path.Combine(dir, "packages.lock.json"), lockFile);
        return project;
    }

    private static string WriteSolution(string root, params string[] projects)
    {
        var solution = Path.Combine(root, "App.slnx");
        File.WriteAllText(solution,
            "<Solution>" + string.Concat(projects.Select(p => $"<Project Path=\"{Path.GetRelativePath(root, p).Replace('\\', '/')}\" />")) + "</Solution>");
        return solution;
    }

    // A lock file that disagrees with a project that has no packages, so a locked restore fails with NU1004.
    private static readonly string StaleLock =
        $$"""
        {
          "version": 1,
          "dependencies": {
            "{{Tfm}}": {
              "Ghost.Package": { "type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0", "contentHash": "AAAA" }
            }
          }
        }
        """;

    private static string LockedProps(string frameworks = "") =>
        $"<PropertyGroup><TargetFramework>{Tfm}</TargetFramework><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>" +
        $"<RestoreLockedMode>true</RestoreLockedMode>{frameworks}</PropertyGroup>";

    [TestMethod]
    public async Task AStaleLockFile_IsRestoredUnlocked_WithoutRewritingTheCheckoutLockFile()
    {
        var root = NewRoot();
        var logs = new List<string>();
        try
        {
            var stale = AddProject(root, "Stale", LockedProps(), StaleLock);
            var good = AddProject(root, "Good", $"<PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup>");
            var lockPath = Path.Combine(root, "Stale", "packages.lock.json");
            var before = File.ReadAllBytes(lockPath);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(3), log: logs.Add, staticGraphRestore: false)
                .RunAsync(root, [WriteSolution(root, stale, good)], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Stale/Stale.csproj" }, outcome.LockFileRetriedProjects.ToArray(), string.Join("|", logs) + string.Join(";", outcome.Projects.Select(p => p.Project + ":" + string.Join(",", p.Codes))));
            Assert.AreEqual(0, outcome.Projects.Count, "the NU1004 failure is resolved: " + string.Join(", ", outcome.Projects.SelectMany(p => p.Codes)));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Stale", "obj", "project.assets.json")));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(lockPath), "the committed lock file is never rewritten");
            Assert.IsTrue(outcome.Notes().Any(n => n.Contains("NU1004", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task OnlyNU1004_TriggersTheUnlockedRetry()
    {
        var root = NewRoot();
        var logs = new List<string>();
        try
        {
            // NU1101: the package exists on no source. A lock file does not matter.
            var missing = AddProject(root, "Missing",
                $"<PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup>" +
                "<ItemGroup><PackageReference Include=\"No.Such.Package\" Version=\"1.0.0\" /></ItemGroup>");
            var good = AddProject(root, "Good", $"<PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup>");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(3), log: logs.Add, staticGraphRestore: false)
                .RunAsync(root, [WriteSolution(root, missing, good)], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.AreEqual(0, outcome.LockFileRetriedProjects.Count);
            Assert.AreEqual(0, outcome.PlatformRetriedProjects.Count);
            CollectionAssert.Contains(outcome.Projects.Single().Codes.ToArray(), "NU1101");
            Assert.IsFalse(outcome.Notes().Any(n => n.Contains("NU1004", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task AMultiTargetProjectWithAnUnavailablePlatform_IsRestoredForItsBuildableTargets()
    {
        var root = NewRoot();
        var logs = new List<string>();
        try
        {
            var multi = AddProject(root, "Multi",
                $"<PropertyGroup><TargetFrameworks>{Tfm}-ios;{Tfm}</TargetFrameworks></PropertyGroup>");
            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(3), log: logs.Add, staticGraphRestore: false)
                .RunAsync(root, [WriteSolution(root, multi)], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            if (outcome.PlatformRetriedProjects.Count == 0 && outcome.Projects.Count == 0)
                Assert.Inconclusive("this host has the iOS workload: the platform target restores on its own");
            Assert.AreEqual($"Multi/Multi.csproj ({Tfm})", outcome.PlatformRetriedProjects.SingleOrDefault(), string.Join("|", logs) + string.Join(";", outcome.Projects.Select(p => p.Project + ":" + string.Join(",", p.Codes))));
            Assert.AreEqual(0, outcome.Projects.Count, string.Join(", ", outcome.Projects.SelectMany(p => p.Codes)));
            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Multi", "obj", "project.assets.json")));
            CollectionAssert.AreEqual(new[] { Tfm }, assets.RootElement.GetProperty("targets").EnumerateObject().Select(t => t.Name).ToArray());
            Assert.IsTrue(outcome.Notes().Any(n => n.Contains("buildable targets", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ThePlatformRepair_NeverRewritesTheAssetsOfAMultiTargetProjectReference()
    {
        var root = NewRoot();
        try
        {
            var lib = AddProject(root, "Lib", $"<PropertyGroup><TargetFrameworks>netstandard2.1;{Tfm}</TargetFrameworks></PropertyGroup>");
            var multi = AddProject(root, "Multi",
                $"<PropertyGroup><TargetFrameworks>{Tfm}-ios;{Tfm}</TargetFrameworks></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup>");
            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(3), log: _ => { }, staticGraphRestore: false)
                .RunAsync(root, [WriteSolution(root, multi, lib)], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            if (outcome.PlatformRetriedProjects.Count == 0)
                Assert.Inconclusive("this host has the iOS workload: no repair ran");
            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Lib", "obj", "project.assets.json")));
            CollectionAssert.AreEquivalent(
                new[] { "netstandard2.1", Tfm },
                assets.RootElement.GetProperty("targets").EnumerateObject().Select(t => t.Name).ToArray(),
                "the repair's TargetFrameworks must not reach a referenced project: " + string.Join(",", assets.RootElement.GetProperty("targets").EnumerateObject().Select(t => t.Name)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ARepairedProject_IsReported_AndNeverCountedAsCleanlyRestored()
    {
        var root = NewRoot();
        try
        {
            var multi = AddProject(root, "Multi", $"<PropertyGroup><TargetFrameworks>{Tfm}-ios;{Tfm}</TargetFrameworks></PropertyGroup>");
            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(3), log: _ => { }, staticGraphRestore: false)
                .RunAsync(root, [WriteSolution(root, multi)], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            if (outcome.PlatformRetriedProjects.Count == 0)
                Assert.Inconclusive("this host has the iOS workload: no repair ran");
            CollectionAssert.AreEqual(new[] { "Multi/Multi.csproj" }, outcome.RepairedProjects.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TheProbe_IsBoundedByTheTimeLeft()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("needs a FIFO to block the evaluation");
        var root = NewRoot();
        try
        {
            var fifo = Path.Combine(root, "block");
            using (var mk = System.Diagnostics.Process.Start("mkfifo", fifo)!) { await mk.WaitForExitAsync(); }
            var slow = AddProject(root, "Slow",
                $"<PropertyGroup><TargetFrameworks>{Tfm}-ios;{Tfm}</TargetFrameworks><X>$([System.IO.File]::ReadAllText('{fifo}'))</X></PropertyGroup>");
            var runner = new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(3), log: _ => { }, staticGraphRestore: false);
            var env = new Dictionary<string, string>();

            var none = await runner.BuildableFrameworksAsync(slow, [], env, TimeSpan.Zero, CancellationToken.None);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var cut = await runner.BuildableFrameworksAsync(slow, [], env, TimeSpan.FromSeconds(3), CancellationToken.None);
            watch.Stop();

            Assert.IsTrue(none.OutOfTime);
            Assert.IsNull(cut.Frameworks);
            Assert.IsTrue(cut.OutOfTime, "the time limit, not the 60 s probe bound, stopped it");
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString());
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
