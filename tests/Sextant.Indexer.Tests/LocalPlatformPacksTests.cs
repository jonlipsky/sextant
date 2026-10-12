using Microsoft.CodeAnalysis;

namespace Sextant.Indexer.Tests;

/// <summary>The opt-in platform reference pack binding of the local CLI and daemon (issue #309).</summary>
[TestClass]
[DoNotParallelize] // reads or sets NUGET_PACKAGES
public sealed class LocalPlatformPacksTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sextant-local-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [TestMethod]
    public async Task WhenOn_BindsTheDegradedProject_AndRemovesItsScratch()
    {
        var (result, downloader, options) = Arrange();
        var lines = new List<string>();

        var applied = await LocalPlatformPacks.ApplyIfEnabledAsync(result, true, lines.Add, CancellationToken.None, options);

        Assert.AreEqual(1, applied.PlatformPackBindings.Count);
        Assert.AreEqual(PlatformReferencePacks.SourceDownload, applied.PlatformPackBindings[0].Source);
        Assert.AreEqual(1, downloader.Calls);
        Assert.IsTrue(applied.Solution.Projects.Single().MetadataReferences.Count > 0);
        StringAssert.Contains(LocalPlatformPacks.BindingSummary(applied.PlatformPackBindings), "compiled against the platform reference pack without the workload (a.csproj");
        Assert.IsTrue(lines.Any(l => l.Contains("Compiled 'a.csproj'")));
    }

    [TestMethod]
    public async Task WhenOff_IsANoOp()
    {
        var (result, downloader, options) = Arrange();
        var lines = new List<string>();

        var applied = await LocalPlatformPacks.ApplyIfEnabledAsync(result, false, lines.Add, CancellationToken.None, options);

        Assert.AreSame(result, applied);
        Assert.AreEqual(0, downloader.Calls);
        Assert.AreEqual(0, lines.Count);
        Assert.AreEqual(0, applied.Solution.Projects.Single().MetadataReferences.Count);
    }

    [TestMethod]
    public async Task WhenOn_ButNothingDegraded_IsANoOp()
    {
        var (result, downloader, options) = Arrange();
        var clean = result with { DegradedProjects = [] };

        var applied = await LocalPlatformPacks.ApplyIfEnabledAsync(clean, true, null, CancellationToken.None, options);

        Assert.AreSame(clean, applied);
        Assert.AreEqual(0, downloader.Calls);
    }

    [TestMethod]
    public async Task WithoutSuppliedScratch_ACreatedTempScratchIsRemoved()
    {
        var (result, downloader, options) = Arrange();
        var noScratch = options with { ScratchDirectory = null };
        string? seen = null;
        var spy = new FakeDownloader { OnScratch = s => seen = s };
        noScratch = noScratch with { Downloader = spy };

        var applied = await LocalPlatformPacks.ApplyIfEnabledAsync(result, true, null, CancellationToken.None, noScratch);

        Assert.AreEqual(1, applied.PlatformPackBindings.Count);
        Assert.IsNotNull(seen);
        Assert.IsTrue(Path.IsPathRooted(seen));
        Assert.IsFalse(Directory.Exists(seen));
        Assert.AreEqual(0, downloader.Calls);
    }

    [TestMethod]
    public async Task RestoreAppleProjects_SkipsProjectsThatAreNotAppleTargetsOrAlreadyRestored()
    {
        var plain = Path.Combine(_dir, "Plain");
        var ios = Path.Combine(_dir, "Ios");
        Directory.CreateDirectory(plain);
        Directory.CreateDirectory(Path.Combine(ios, "obj"));
        File.WriteAllText(Path.Combine(plain, "Plain.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(ios, "Ios.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-ios</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(ios, "obj", "project.assets.json"), "{}");
        var solution = Path.Combine(_dir, "App.slnx");
        File.WriteAllText(solution, "<Solution><Project Path=\"Plain/Plain.csproj\" /><Project Path=\"Ios/Ios.csproj\" /></Solution>");

        var restored = await LocalPlatformPacks.RestoreAppleProjectsAsync(solution, null, CancellationToken.None);

        Assert.AreEqual(0, restored);
    }

    private string WriteProject(string name, string body)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".csproj");
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">{body}</Project>");
        File.WriteAllText(Path.Combine(_dir, name + ".slnx"), $"<Solution><Project Path=\"{name}/{name}.csproj\" /></Solution>");
        return Path.Combine(_dir, name + ".slnx");
    }

    private string FakeDotnet(string script)
    {
        var root = Path.Combine(_dir, "fake-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "dotnet");
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    [TestMethod]
    [DataRow("<PropertyGroup><TargetFrameworks Condition=\"'$(OS)' == 'Windows_NT'\">net10.0-ios;net10.0-windows</TargetFrameworks></PropertyGroup>")]
    [DataRow("<PropertyGroup><TargetFrameworks>$(Base);net10.0-maccatalyst</TargetFrameworks></PropertyGroup>")]
    [DataRow("<PropertyGroup><TargetFramework>$(Tfm)</TargetFramework><UseMaui>true</UseMaui></PropertyGroup>")]
    public async Task Restore_TriesAProjectWhoseStaticTargetsAreEmptyButLooksApple(string body)
    {
        LocalPlatformPacks.ResetRestoreMemo();
        var solution = WriteProject("Maui", body);
        var root = FakeDotnet($"echo \"$@\" > '{_dir}/args.txt'\nexit 0");

        var restored = await LocalPlatformPacks.RestoreAppleProjectsAsync(solution, null, CancellationToken.None, dotnetRoot: root);

        Assert.AreEqual(1, restored);
        StringAssert.Contains(File.ReadAllText(Path.Combine(_dir, "args.txt")), "restore");
    }

    [TestMethod]
    public async Task Restore_LeavesANonCandidateWithConditionalTargetsAlone()
    {
        LocalPlatformPacks.ResetRestoreMemo();
        var solution = WriteProject("Plain", "<PropertyGroup><TargetFramework Condition=\"'$(X)' == ''\">net10.0</TargetFramework></PropertyGroup>");
        var root = FakeDotnet($"echo ran > '{_dir}/ran.txt'\nexit 0");

        var restored = await LocalPlatformPacks.RestoreAppleProjectsAsync(solution, null, CancellationToken.None, dotnetRoot: root);

        Assert.AreEqual(0, restored);
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "ran.txt")));
    }

    [TestMethod]
    public async Task Restore_IsNotRetriedUntilTheProjectFileChanges_RegardlessOfOutcome()
    {
        LocalPlatformPacks.ResetRestoreMemo();
        var solution = WriteProject("Ios", "<PropertyGroup><TargetFramework>net10.0-ios</TargetFramework></PropertyGroup>");
        var log = Path.Combine(_dir, "count.txt");
        var root = FakeDotnet($"echo x >> '{log}'\nexit 1");
        var lines = new List<string>();

        Assert.AreEqual(0, await LocalPlatformPacks.RestoreAppleProjectsAsync(solution, lines.Add, CancellationToken.None, dotnetRoot: root));
        Assert.AreEqual(0, await LocalPlatformPacks.RestoreAppleProjectsAsync(solution, lines.Add, CancellationToken.None, dotnetRoot: root));
        Assert.AreEqual(1, File.ReadAllLines(log).Length);
        Assert.AreEqual(1, lines.Count(l => l.Contains("did not complete")));

        File.SetLastWriteTimeUtc(Path.Combine(_dir, "Ios", "Ios.csproj"), DateTime.UtcNow.AddMinutes(5));
        await LocalPlatformPacks.RestoreAppleProjectsAsync(solution, null, CancellationToken.None, dotnetRoot: root);
        Assert.AreEqual(2, File.ReadAllLines(log).Length);
    }

    [TestMethod]
    public async Task Restore_ATimedOutRestoreIsKilled_AndReportedAsIncomplete()
    {
        LocalPlatformPacks.ResetRestoreMemo();
        var solution = WriteProject("Ios", "<PropertyGroup><TargetFramework>net10.0-ios</TargetFramework></PropertyGroup>");
        var root = FakeDotnet("sleep 60");
        var lines = new List<string>();

        var restored = await LocalPlatformPacks.RestoreAppleProjectsAsync(
            solution, lines.Add, CancellationToken.None, timeout: TimeSpan.FromMilliseconds(500), dotnetRoot: root);

        Assert.AreEqual(0, restored);
        Assert.IsTrue(lines.Any(l => l.Contains("did not complete")));
    }

    [TestMethod]
    public async Task Restore_RequestedCancellationPropagates()
    {
        LocalPlatformPacks.ResetRestoreMemo();
        var solution = WriteProject("Ios", "<PropertyGroup><TargetFramework>net10.0-ios</TargetFramework></PropertyGroup>");
        var root = FakeDotnet("sleep 60");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LocalPlatformPacks.RestoreAppleProjectsAsync(solution, null, cts.Token, dotnetRoot: root));
    }

    [TestMethod]
    public async Task Restore_StopsAtTheOverallDeadline_WithOneDiagnostic()
    {
        LocalPlatformPacks.ResetRestoreMemo();
        foreach (var n in new[] { "A", "B", "C" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, n));
            File.WriteAllText(Path.Combine(_dir, n, n + ".csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-ios</TargetFramework></PropertyGroup></Project>");
        }
        var solution = Path.Combine(_dir, "Many.slnx");
        File.WriteAllText(solution, "<Solution><Project Path=\"A/A.csproj\" /><Project Path=\"B/B.csproj\" /><Project Path=\"C/C.csproj\" /></Solution>");
        var log = Path.Combine(_dir, "many.txt");
        var root = FakeDotnet($"echo x >> '{log}'\nsleep 1\nexit 0");
        var lines = new List<string>();

        await LocalPlatformPacks.RestoreAppleProjectsAsync(
            solution, lines.Add, CancellationToken.None, totalTimeout: TimeSpan.FromMilliseconds(1500), dotnetRoot: root);

        Assert.IsTrue(File.ReadAllLines(log).Length < 3);
        Assert.AreEqual(1, lines.Count(l => l.Contains("time budget is spent")));
    }

    [TestMethod]
    public async Task LoadAsync_WhenOff_DoesNotRestoreAnAppleProject()
    {
        if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
            Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
        var solution = WriteProject("Ios", "<PropertyGroup><TargetFramework>net10.0-ios</TargetFramework></PropertyGroup>");

        await LocalPlatformPacks.LoadAsync(solution, enabled: false);

        // The design-time load may write generated files into obj/; only a restore writes the assets file.
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "Ios", "obj", "project.assets.json")));
    }

    [TestMethod]
    public void ForLocal_UsesTheGlobalPackagesFolder()
    {
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            var custom = Path.Combine(_dir, "custom-packages");
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", custom);
            var options = LocalPlatformPacks.ForLocal(_dir);
            CollectionAssert.AreEqual(new[] { custom }, options.PackageFolders.ToArray());
            Assert.AreEqual(_dir, options.ScratchDirectory);

            Environment.SetEnvironmentVariable("NUGET_PACKAGES", null);
            Assert.AreEqual(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
                LocalPlatformPacks.GlobalPackagesFolder());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
        }
    }

    private (SolutionLoadResult Result, FakeDownloader Downloader, PlatformPackOptions Options) Arrange()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var manifestDir = Path.Combine(_dir, "sdk-manifests", "10.0.100", "microsoft.net.sdk.ios", "27.0.10722");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, "WorkloadManifest.json"),
            "{\n  \"version\": \"27.0.10722\",\n  \"packs\": {\n" +
            "    \"Microsoft.iOS.Ref.net10.0_27.0\": { \"kind\": \"framework\", \"version\": \"27.0.10722\", },\n  },\n}\n");

        var downloader = new FakeDownloader();
        var options = new PlatformPackOptions
        {
            DotnetRoot = _dir,
            PackageFolders = [Path.Combine(_dir, "pkgs")],
            ScratchDirectory = Path.Combine(_dir, "scratch"),
            Downloader = downloader
        };
        var result = new SolutionLoadResult(workspace.CurrentSolution, [])
        {
            DegradedProjects =
            [
                new DegradedProject("a.csproj", "net10.0-ios", "NETSDK1147: workload ios is not installed") { ProjectId = project.Id }
            ]
        };
        return (result, downloader, options);
    }

    private sealed class FakeDownloader : IPlatformPackDownloader
    {
        public int Calls { get; private set; }
        public Action<string>? OnScratch { get; init; }

        public Task<bool> DownloadAsync(PlatformPackId pack, string packageFolder, string scratchDirectory, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            OnScratch?.Invoke(scratchDirectory);
            var refDir = Path.Combine(packageFolder, pack.Id.ToLowerInvariant(), pack.Version.ToLowerInvariant(), "ref", "net10.0");
            Directory.CreateDirectory(refDir);
            File.WriteAllBytes(Path.Combine(refDir, "Microsoft.iOS.dll"), []);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(refDir))!, ".nupkg.metadata"), "{}");
            return Task.FromResult(true);
        }
    }
}
