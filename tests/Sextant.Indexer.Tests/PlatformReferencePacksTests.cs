namespace Sextant.Indexer.Tests;

/// <summary>Platform reference pack resolution, lookup order and allowlist (issue #295).</summary>
[TestClass]
[DoNotParallelize] // reads or sets NUGET_PACKAGES
public sealed class PlatformReferencePacksTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sextant-platform-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [DataTestMethod]
    [DataRow("net10.0-ios", "ios", 10, null)]
    [DataRow("net10.0-maccatalyst", "maccatalyst", 10, null)]
    [DataRow("net9.0-tvos", "tvos", 9, null)]
    [DataRow("net10.0-ios26.0", "ios", 10, "26.0")]
    public void ParseTarget_ReadsPlatformAndVersion(string tfm, string platform, int major, string? version)
    {
        var target = PlatformReferencePacks.ParseTarget(tfm);
        Assert.IsNotNull(target);
        Assert.AreEqual(platform, target.Platform.Tfm);
        Assert.AreEqual(major, target.DotnetMajor);
        Assert.AreEqual(version is null ? null : Version.Parse(version), target.PlatformVersion);
    }

    [DataTestMethod]
    [DataRow("net10.0")]
    [DataRow("net10.0-android")]
    [DataRow("net10.0-windows10.0.19041")]
    [DataRow("netstandard2.0")]
    [DataRow(null)]
    public void ParseTarget_OtherFrameworksAreNotPlatformTargets(string? tfm) =>
        Assert.IsNull(PlatformReferencePacks.ParseTarget(tfm));

    [DataTestMethod]
    [DataRow("Microsoft.iOS.Ref.net10.0_27.0", true)]
    [DataRow("Microsoft.MacCatalyst.Ref.net10.0_27.0", true)]
    [DataRow("Microsoft.tvOS.Ref.net10.0_27.0", true)]
    [DataRow("Microsoft.Android.Ref.net10.0", false)]
    [DataRow("Newtonsoft.Json", false)]
    [DataRow("Microsoft.iOS.Ref.net10.0_27.0/../x", false)]
    [DataRow("", false)]
    public void IsAllowedPackId_AllowsOnlyTheAppleReferencePacks(string id, bool allowed) =>
        Assert.AreEqual(allowed, PlatformReferencePacks.IsAllowedPackId(id));

    [TestMethod]
    public void IsWorkloadFailure_OnlyForWorkloadMessages()
    {
        Assert.IsTrue(PlatformReferencePacks.IsWorkloadFailure("NETSDK1147: workload ios is not installed"));
        Assert.IsTrue(PlatformReferencePacks.IsWorkloadFailure("Platform version is not present for one or more target frameworks"));
        Assert.IsFalse(PlatformReferencePacks.IsWorkloadFailure("NU1101: Unable to find package Foo"));
        Assert.IsFalse(PlatformReferencePacks.IsWorkloadFailure(null));
    }

    [DataTestMethod]
    [DataRow("net10.0-ios", "microsoft.net.sdk.ios", "Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722")]
    [DataRow("net10.0-maccatalyst", "microsoft.net.sdk.maccatalyst", "Microsoft.MacCatalyst.Ref.net10.0_27.0", "27.0.10722")]
    [DataRow("net10.0-tvos", "microsoft.net.sdk.tvos", "Microsoft.tvOS.Ref.net10.0_27.0", "27.0.10722")]
    [DataRow("net10.0-ios26.0", "microsoft.net.sdk.ios", "Microsoft.iOS.Ref.net10.0_26.0", "26.0.11017")]
    public void ResolvePack_ReadsTheManifestOfTheSdkBand(string tfm, string manifestId, string packId, string version)
    {
        WriteManifest(manifestId, ("Microsoft.iOS.Ref.net10.0_26.0", "26.0.11017"), (packId, version));

        var pack = PlatformReferencePacks.ResolvePack(_dir, PlatformReferencePacks.ParseTarget(tfm)!);

        Assert.IsNotNull(pack);
        Assert.AreEqual(packId, pack.Id);
        Assert.AreEqual(version, pack.Version);
    }

    [TestMethod]
    public void ResolvePack_NoManifest_ReturnsNull() =>
        Assert.IsNull(PlatformReferencePacks.ResolvePack(_dir, PlatformReferencePacks.ParseTarget("net10.0-ios")!));

    [TestMethod]
    public async Task Locate_PrefersThePacksDirectory_ThenThePackageFolder_ThenDownloads()
    {
        var pack = new PlatformPackId("Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722");
        var folder = Path.Combine(_dir, "pkgs");
        var downloader = new FakeDownloader(folder, pack);
        var options = new PlatformPackOptions
        {
            PackageFolders = [folder], ScratchDirectory = Path.Combine(_dir, "scratch"), Downloader = downloader,
        };

        var first = await PlatformReferencePacks.LocateAsync(pack, 10, options, _dir, null, CancellationToken.None);
        Assert.AreEqual(PlatformReferencePacks.SourceDownload, first!.Value.Source);
        Assert.AreEqual(1, downloader.Calls);

        var second = await PlatformReferencePacks.LocateAsync(pack, 10, options, _dir, null, CancellationToken.None);
        Assert.AreEqual(PlatformReferencePacks.SourcePackageFolder, second!.Value.Source);
        Assert.AreEqual(1, downloader.Calls);

        var installed = Path.Combine(_dir, "packs", pack.Id, pack.Version, "ref", "net10.0");
        Directory.CreateDirectory(installed);
        File.WriteAllBytes(Path.Combine(installed, "Microsoft.iOS.dll"), []);
        var third = await PlatformReferencePacks.LocateAsync(pack, 10, options, _dir, null, CancellationToken.None);
        Assert.AreEqual(PlatformReferencePacks.SourcePacksDirectory, third!.Value.Source);
        Assert.AreEqual(1, downloader.Calls);
    }

    [TestMethod]
    public async Task Locate_IgnoresAPackFolderWithoutTheCompletionMarker_AndDownloadsAgain()
    {
        var pack = new PlatformPackId("Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722");
        var folder = Path.Combine(_dir, "pkgs");
        var partial = Path.Combine(folder, pack.Id.ToLowerInvariant(), pack.Version.ToLowerInvariant(), "ref", "net10.0");
        Directory.CreateDirectory(partial);
        File.WriteAllBytes(Path.Combine(partial, "Microsoft.iOS.dll"), []);
        var downloader = new FakeDownloader(folder, pack);
        var options = new PlatformPackOptions
        {
            PackageFolders = [folder], ScratchDirectory = Path.Combine(_dir, "scratch"), Downloader = downloader,
        };

        var result = await PlatformReferencePacks.LocateAsync(pack, 10, options, _dir, null, CancellationToken.None);

        Assert.AreEqual(PlatformReferencePacks.SourceDownload, result!.Value.Source);
        Assert.AreEqual(1, downloader.Calls);
    }

    [TestMethod]
    public async Task Locate_PackageFolderPackIsAcceptedOnlyWithTheMarker_AndThePacksDirectoryWinsWithoutOne()
    {
        var pack = new PlatformPackId("Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722");
        var folder = Path.Combine(_dir, "pkgs");
        var packDir = Path.Combine(folder, pack.Id.ToLowerInvariant(), pack.Version.ToLowerInvariant());
        Directory.CreateDirectory(Path.Combine(packDir, "ref", "net10.0"));
        File.WriteAllBytes(Path.Combine(packDir, "ref", "net10.0", "Microsoft.iOS.dll"), []);
        var downloader = new FakeDownloader(Path.Combine(_dir, "other"), pack);
        var options = new PlatformPackOptions { PackageFolders = [folder], Downloader = downloader };

        Assert.IsNull(await PlatformReferencePacks.LocateAsync(pack, 10, options, _dir, null, CancellationToken.None));

        File.WriteAllText(Path.Combine(packDir, ".nupkg.metadata"), "{}");
        var accepted = await PlatformReferencePacks.LocateAsync(pack, 10, options, _dir, null, CancellationToken.None);
        Assert.AreEqual(PlatformReferencePacks.SourcePackageFolder, accepted!.Value.Source);

        File.Delete(Path.Combine(packDir, ".nupkg.metadata"));
        var installed = Path.Combine(_dir, "packs", pack.Id, pack.Version, "ref", "net10.0");
        Directory.CreateDirectory(installed);
        File.WriteAllBytes(Path.Combine(installed, "Microsoft.iOS.dll"), []);
        var sdk = await PlatformReferencePacks.LocateAsync(pack, 10, options, _dir, null, CancellationToken.None);
        Assert.AreEqual(PlatformReferencePacks.SourcePacksDirectory, sdk!.Value.Source);
        Assert.AreEqual(0, downloader.Calls);
    }

    [TestMethod]
    public async Task Locate_RefusesAPackOutsideTheAllowlist_WithoutDownloading()
    {
        var downloader = new FakeDownloader(Path.Combine(_dir, "pkgs"), new PlatformPackId("Evil.Pack", "1.0.0"));
        var options = new PlatformPackOptions
        {
            PackageFolders = [Path.Combine(_dir, "pkgs")], ScratchDirectory = _dir, Downloader = downloader,
        };

        var result = await PlatformReferencePacks.LocateAsync(
            new PlatformPackId("Evil.Pack", "1.0.0"), 10, options, _dir, null, CancellationToken.None);

        Assert.IsNull(result);
        Assert.AreEqual(0, downloader.Calls);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PlatformReferencePacks.GenerateDownloadProject(new PlatformPackId("Evil.Pack", "1.0.0"), _dir));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PlatformReferencePacks.GenerateDownloadProject(new PlatformPackId("Microsoft.iOS.Ref.net10.0_27.0", "1.0\"/>"), _dir));
    }

    [TestMethod]
    public void GeneratedNuGetConfig_ClearsSources_AndUsesOnlyNuGetOrg()
    {
        var config = PlatformReferencePacks.GenerateNuGetConfig();

        StringAssert.Contains(config, "<clear />");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(config, "<add ").Count);
        StringAssert.Contains(config, "https://api.nuget.org/v3/index.json");
        StringAssert.DoesNotMatch(config, new System.Text.RegularExpressions.Regex("(?i)credential|password|token"));
    }

    [TestMethod]
    public void GeneratedDownloadProject_PinsTheExactVersion()
    {
        var project = PlatformReferencePacks.GenerateDownloadProject(new PlatformPackId("Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722"), _dir);

        StringAssert.Contains(project, "<PackageDownload Include=\"Microsoft.iOS.Ref.net10.0_27.0\" Version=\"[27.0.10722]\" />");
    }

    [TestMethod]
    public async Task Apply_LeavesOtherPlatformsAndOtherFailuresUntouched()
    {
        using var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var project = workspace.AddProject("P", Microsoft.CodeAnalysis.LanguageNames.CSharp);
        var downloader = new FakeDownloader(Path.Combine(_dir, "pkgs"), new PlatformPackId("x", "1"));
        var options = new PlatformPackOptions { DotnetRoot = _dir, ScratchDirectory = _dir, Downloader = downloader };
        WriteManifest("microsoft.net.sdk.ios", ("Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722"));

        var degraded = new[]
        {
            new DegradedProject("a.csproj", "net10.0-android", "NETSDK1147: workload android is not installed") { ProjectId = project.Id },
            new DegradedProject("b.csproj", "net10.0-ios", "NU1101: Unable to find package") { ProjectId = project.Id },
            new DegradedProject("c.csproj", "net10.0", "NETSDK1147: workload") { ProjectId = project.Id },
        };

        var (solution, bindings) = await PlatformReferencePacks.ApplyAsync(workspace.CurrentSolution, degraded, options, null, CancellationToken.None);

        Assert.AreEqual(0, bindings.Count);
        Assert.AreEqual(0, downloader.Calls);
        Assert.AreEqual(0, solution.GetProject(project.Id)!.MetadataReferences.Count);
    }

    [TestMethod]
    public void IsWorkloadFailure_IgnoresMissingAssetsAndUnrelatedWorkloadMentions()
    {
        Assert.IsFalse(PlatformReferencePacks.IsWorkloadFailure("NETSDK1004: Assets file '/r/obj/project.assets.json' not found. Run a NuGet package restore"));
        Assert.IsFalse(PlatformReferencePacks.IsWorkloadFailure("The workload of the build agent is high"));
        Assert.IsTrue(PlatformReferencePacks.IsWorkloadFailure("The target platform isn't supported on this platform"));
    }

    [TestMethod]
    public async Task Apply_AddsThePackReferences_SkipsDuplicatesByName_AndAddsTheBaseOnlyWhenMissing()
    {
        WriteManifest("microsoft.net.sdk.ios", ("Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722"));
        var pack = Path.Combine(_dir, "packs", "Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722", "ref", "net10.0");
        Directory.CreateDirectory(pack);
        File.WriteAllBytes(Path.Combine(pack, "Microsoft.iOS.dll"), []);
        File.WriteAllBytes(Path.Combine(pack, "Dup.dll"), []);
        var baseRef = Path.Combine(_dir, "packs", "Microsoft.NETCore.App.Ref", "10.0.1", "ref", "net10.0");
        Directory.CreateDirectory(baseRef);
        File.WriteAllBytes(Path.Combine(baseRef, "System.Runtime.dll"), []);
        var existingDir = Path.Combine(_dir, "existing");
        Directory.CreateDirectory(existingDir);
        var existingDup = Path.Combine(existingDir, "Dup.dll");
        File.WriteAllBytes(existingDup, []);

        using var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var project = workspace.AddProject("P", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .AddMetadataReference(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(existingDup));
        var withRef = project.Solution;
        var downloader = new FakeDownloader(Path.Combine(_dir, "pkgs"), new PlatformPackId("x", "1"));
        var options = new PlatformPackOptions { DotnetRoot = _dir, ScratchDirectory = _dir, Downloader = downloader };
        var degraded = new[]
        {
            new DegradedProject("/r/App.csproj", "net10.0-ios", "NETSDK1147: workload ios is not installed") { ProjectId = project.Id }
        };

        var (solution, bindings) = await PlatformReferencePacks.ApplyAsync(withRef, degraded, options, null, CancellationToken.None);

        var names = solution.GetProject(project.Id)!.MetadataReferences
            .OfType<Microsoft.CodeAnalysis.PortableExecutableReference>().Select(r => Path.GetFileName(r.FilePath)).ToList();
        CollectionAssert.AreEquivalent(new[] { "Dup.dll", "Microsoft.iOS.dll", "System.Runtime.dll" }, names);
        Assert.AreEqual(1, bindings.Count);
        Assert.AreEqual("packs", bindings[0].Source);
        Assert.AreEqual(2, bindings[0].ReferenceCount);
        Assert.AreEqual(0, downloader.Calls);

        // Now System.Runtime is already referenced: only the pack's own assemblies are added.
        var again = workspace.AddProject("Q", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .AddMetadataReference(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(Path.Combine(baseRef, "System.Runtime.dll")));
        var (solution2, bindings2) = await PlatformReferencePacks.ApplyAsync(again.Solution,
            [new DegradedProject("/r/Q.csproj", "net10.0-ios", "NETSDK1147") { ProjectId = again.Id }], options, null, CancellationToken.None);
        Assert.AreEqual(2, bindings2.Single().ReferenceCount);
        Assert.AreEqual(3, solution2.GetProject(again.Id)!.MetadataReferences.Count);
    }

    [TestMethod]
    public async Task Apply_DownloadIsBoundedByTheSharedDeadline_AndLaterPacksAreSkipped()
    {
        WriteManifest("microsoft.net.sdk.ios", ("Microsoft.iOS.Ref.net10.0_27.0", "27.0.10722"));
        WriteManifest("microsoft.net.sdk.tvos", ("Microsoft.tvOS.Ref.net10.0_27.0", "27.0.10722"));
        var clock = new AdvancingClock();
        var downloader = new FakeDownloader(Path.Combine(_dir, "pkgs"), new PlatformPackId("x", "1")) { OnDownload = clock.Advance };
        var options = new PlatformPackOptions
        {
            DotnetRoot = _dir,
            ScratchDirectory = _dir,
            Downloader = downloader,
            DownloadTimeout = TimeSpan.FromMinutes(3),
            Deadline = new Sextant.Core.IndexDeadline(clock, clock.GetUtcNow() + TimeSpan.FromSeconds(40))
        };
        using var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var a = workspace.AddProject("A", Microsoft.CodeAnalysis.LanguageNames.CSharp);
        var b = workspace.AddProject("B", Microsoft.CodeAnalysis.LanguageNames.CSharp);
        var diagnostics = new List<string>();

        var (_, bindings) = await PlatformReferencePacks.ApplyAsync(workspace.CurrentSolution,
            [
                new DegradedProject("/r/A.csproj", "net10.0-ios", "NETSDK1147") { ProjectId = a.Id },
                new DegradedProject("/r/B.csproj", "net10.0-tvos", "NETSDK1147") { ProjectId = b.Id }
            ], options, diagnostics.Add, CancellationToken.None);

        Assert.AreEqual(1, downloader.Calls, "the second pack is skipped once the deadline passed");
        Assert.AreEqual(TimeSpan.FromSeconds(40), downloader.Timeouts.Single(), "the pack gets min(remaining, 3 min)");
        Assert.AreEqual(1, bindings.Count);
        Assert.IsTrue(diagnostics.Any(d => d.Contains("time budget is spent", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Locate_RejectsAnUnsafeVersionBeforeAnyLookup()
    {
        var downloader = new FakeDownloader(Path.Combine(_dir, "pkgs"), new PlatformPackId("x", "1"));
        var options = new PlatformPackOptions { ScratchDirectory = _dir, PackageFolders = [Path.Combine(_dir, "pkgs")], Downloader = downloader };

        var result = await PlatformReferencePacks.LocateAsync(
            new PlatformPackId("Microsoft.iOS.Ref.net10.0_27.0", "../../x"), 10, options, _dir, null, CancellationToken.None);

        Assert.IsNull(result);
        Assert.AreEqual(0, downloader.Calls);
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now += TimeSpan.FromMinutes(1);
    }

    private void WriteManifest(string manifestId, params (string Pack, string Version)[] packs)
    {
        var dir = Path.Combine(_dir, "sdk-manifests", "10.0.100", manifestId, "27.0.10722");
        Directory.CreateDirectory(dir);
        var entries = string.Join("\n", packs.DistinctBy(p => p.Pack).Select(p => $"    \"{p.Pack}\": {{ \"kind\": \"framework\", \"version\": \"{p.Version}\", }},"));
        File.WriteAllText(Path.Combine(dir, "WorkloadManifest.json"),
            "{\n  \"version\": \"27.0.10722\",\n  \"packs\": {\n" + entries + "\n  },\n}\n");
    }

    private sealed class FakeDownloader(string folder, PlatformPackId expected) : IPlatformPackDownloader
    {
        public int Calls { get; private set; }
        public List<TimeSpan> Timeouts { get; } = [];

        public Action? OnDownload { get; init; }

        public Task<bool> DownloadAsync(PlatformPackId pack, string packageFolder, string scratchDirectory, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            Timeouts.Add(timeout);
            OnDownload?.Invoke();
            var refDir = Path.Combine(packageFolder, pack.Id.ToLowerInvariant(), pack.Version.ToLowerInvariant(), "ref", "net10.0");
            Directory.CreateDirectory(refDir);
            File.WriteAllBytes(Path.Combine(refDir, "Microsoft.iOS.dll"), []);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(refDir))!, ".nupkg.metadata"), "{}");
            return Task.FromResult(true);
        }
    }
}
