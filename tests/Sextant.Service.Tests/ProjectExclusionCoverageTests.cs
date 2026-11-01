using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service.Restore;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #351: project versions a Linux worker structurally cannot build are exclusions, not coverage gaps, in the
/// load verdict, the package-restore verdict, and the provider verdict.
/// </summary>
[TestClass]
public class ProjectExclusionCoverageTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant_excl_cov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private const string Workload =
        "Msbuild failed when processing the file with message: Platform version is not present for one or more target frameworks, even though they have specified a platform: net10.0-ios, net10.0-maccatalyst";
    private const string NoHost = "There is no application host available for the specified RuntimeIdentifier 'android-arm64'.";
    private const string WindowsTool = "WindowsAppSDKSelfContained requires a supported Windows architecture.";
    private const string Unrelated = "Msbuild failed when processing the file with message: something unrelated";

    private string Sdk(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return path;
    }

    private CheckoutResolution Resolution() => new()
    {
        CheckoutDir = _root,
        SelectedSolutions = [Path.Combine(_root, "App.slnx")],
        Source = SolutionSelectionSource.DefaultUnion
    };

    private static MultiSolutionLoadResult Load(
        string solution, IReadOnlyList<string> declared,
        IReadOnlyList<SkippedProject>? skipped = null, IReadOnlyList<DegradedProject>? degraded = null,
        IReadOnlyList<PlatformPackBinding>? packs = null) =>
        new(new AdhocWorkspace().CurrentSolution, skipped ?? [],
            [new SolutionCoverage(solution, declared.Count, declared.Count, [])])
        {
            DeclaredProjects = declared,
            DegradedProjects = degraded ?? [],
            PlatformPackBindings = packs ?? []
        };

    private SnapshotCoverageBuilder.Result Build(MultiSolutionLoadResult load, params string[] onDisk) =>
        SnapshotCoverageBuilder.Build(_root, Resolution(), load,
            new SnapshotCoverageBuilder.Inventory(onDisk, []));

    [TestMethod]
    public void PlatformVersionsWithEvidence_AreExclusions_AndTheSnapshotIsComplete()
    {
        var ios = Sdk("src/Ios/Ios.csproj");
        var droid = Sdk("src/Droid/Droid.csproj");
        var win = Sdk("src/Win/Win.csproj");
        var mac = Sdk("src/Mac/Mac.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [ios, droid, win, mac],
            degraded:
            [
                new DegradedProject(ios, "net10.0-ios", Unrelated),
                new DegradedProject(droid, "net10.0-android", NoHost),
                new DegradedProject(win, "net10.0-windows10.0.19041.0", WindowsTool),
                new DegradedProject(mac, "net10.0-macos", Workload)
            ],
            packs: [new PlatformPackBinding(ios, "net10.0-ios", "Microsoft.iOS.Ref", "17.0", "packs", 10)]);

        var result = Build(load, ios, droid, win, mac);

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, result.Coverage.Verdict, string.Join(" | ", result.Coverage.Reasons));
        Assert.AreEqual(0, result.Coverage.Reasons.Count);
        Assert.AreEqual(4, result.Coverage.ProjectVersionsExcluded);
        Assert.AreEqual(0, result.Coverage.ProjectsDegraded, "a complete snapshot reports no degraded project");
        Assert.AreEqual(0, result.Coverage.ProjectsSkipped);
        Assert.IsTrue(result.Coverage.Exclusions!.All(e => e.Reason == ProjectExclusionReason.PlatformUnavailable));
        CollectionAssert.AreEqual(
            new[] { "android; no_application_host", "ios; platform_pack_binding", "macos; workload_missing", "windows; windows_tooling" },
            result.Coverage.Exclusions!.Select(e => e.Detail).OrderBy(d => d, StringComparer.Ordinal).ToArray());
        StringAssert.Contains(result.Coverage.Notes!.Single(), "4 project version(s) were excluded");
        Assert.AreEqual(4, result.Diagnostics.Count(d => d.Code == "project_excluded"));
        Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "project_evaluation_degraded"));
        Assert.AreEqual(JobDiagnosticSeverity.Info, result.Diagnostics.Single(d => d.Code == "platform_reference_pack").Severity);
    }

    [TestMethod]
    public void APlatformVersionWithNoEvidence_IsAGap()
    {
        var win = Sdk("src/Win/Win.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [win],
            degraded: [new DegradedProject(win, "net10.0-windows", Unrelated)]);

        var result = Build(load, win);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict);
        Assert.AreEqual(1, result.Coverage.ProjectsDegraded);
        Assert.IsNull(result.Coverage.Exclusions);
    }

    [TestMethod]
    public void APlainNetVersion_WithThePlatformMessage_IsAGap()
    {
        var lib = Sdk("src/Lib/Lib.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [lib],
            degraded: [new DegradedProject(lib, "net10.0", Workload)]);

        var result = Build(load, lib);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict);
        Assert.AreEqual(1, result.Coverage.ProjectsDegraded);
        Assert.IsNull(result.Coverage.Exclusions);
    }

    [TestMethod]
    public void AnSdkPinFailure_OnAWindowsVersion_IsAGap_WithItsSdkReason()
    {
        var win = Sdk("src/Win/Win.csproj");
        var pin = "Error while calling hostfxr function hostfxr_resolve_sdk2. Error code: -2147450725 Detailed error: " +
                  $"A compatible .NET SDK was not found.\n\nRequested SDK version: 10.0.300\nglobal.json file: {_root}/global.json\n\nInstalled SDKs:\n";
        var load = Load(Path.Combine(_root, "App.slnx"), [win],
            skipped: [new SkippedProject(win, pin) { TargetFramework = "net10.0-windows" }]);

        var result = Build(load, win);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict);
        Assert.AreEqual(1, result.Coverage.ProjectsSkipped);
        Assert.IsNull(result.Coverage.Exclusions);
        StringAssert.Contains(string.Join(" | ", result.Coverage.Reasons), "SDK");
    }

    [TestMethod]
    public void AGenuineDegradedVersion_StillMakesItPartial_AndOnlyItIsNamed()
    {
        var ios = Sdk("src/Ios/Ios.csproj");
        var lib = Sdk("src/Lib/Lib.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [ios, lib],
            degraded:
            [
                new DegradedProject(ios, "net10.0-ios", Workload),
                new DegradedProject(lib, "net10.0", "failed evaluation")
            ]);

        var result = Build(load, ios, lib);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict);
        var reasons = string.Join(" | ", result.Coverage.Reasons);
        StringAssert.Contains(reasons, "Lib");
        Assert.IsFalse(reasons.Contains("Ios", StringComparison.Ordinal), reasons);
        Assert.AreEqual(1, result.Coverage.ProjectVersionsExcluded);
        Assert.AreEqual(1, result.Coverage.ProjectsDegraded, "only the gap is counted");
    }

    [TestMethod]
    public void AStaleSolutionEntry_WithNoFileInTheCommit_IsProjectFileMissing_AndComplete()
    {
        var gone = Path.Combine(_root, "src", "Gone", "Gone.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [gone],
            skipped: [new SkippedProject(gone, "FileNotFoundException: Project file not found")]);

        var result = Build(load);

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, result.Coverage.Verdict, string.Join(" | ", result.Coverage.Reasons));
        var exclusion = result.Coverage.Exclusions!.Single();
        Assert.AreEqual(ProjectExclusionReason.ProjectFileMissing, exclusion.Reason);
        Assert.AreEqual("src/Gone/Gone.csproj", exclusion.Project);
        Assert.AreEqual(0, result.Coverage.ProjectsSkipped);
        Assert.AreEqual(0, result.Coverage.ProjectsPartiallyLoaded);
    }

    [TestMethod]
    public void AMissingFile_DifferingOnlyByCase_IsAGap_AndOneOutsideTheCheckout_IsProjectFileMissing()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Inconclusive("case-insensitive file system");
        Sdk("src/Cased/cased.csproj");
        var declared = Path.Combine(_root, "src", "Cased", "Cased.csproj");
        var outside = Path.Combine(Path.GetTempPath(), "sextant_sibling_" + Guid.NewGuid().ToString("N"), "X.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [declared, outside],
            skipped:
            [
                new SkippedProject(declared, "FileNotFoundException: Project file not found"),
                new SkippedProject(outside, "FileNotFoundException: Project file not found")
            ]);

        var result = Build(load);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict);
        Assert.AreEqual(1, result.Coverage.ProjectsSkipped);
        Assert.AreEqual(ProjectExclusionReason.ProjectFileMissing, result.Coverage.Exclusions!.Single().Reason);
    }

    [TestMethod]
    public void ANonSdkProject_ExcludedOnlyOnMissingImportEvidence()
    {
        var path = Path.Combine(_root, "src", "Legacy", "Legacy.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" +
            "<Import Project=\"$(MSBuildExtensionsPath)/Xamarin/iOS/Xamarin.iOS.CSharp.targets\" /></Project>");

        var unrelated = Build(Load(Path.Combine(_root, "App.slnx"), [path],
            skipped: [new SkippedProject(path, "boom")]), path);
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, unrelated.Coverage.Verdict);

        var real = Build(Load(Path.Combine(_root, "App.slnx"), [path],
            degraded: [new DegradedProject(path, null, $"Msbuild failed when processing the file '{path}' with message: Project does not contain 'Compile' target.")]), path);
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, real.Coverage.Verdict, string.Join(" | ", real.Coverage.Reasons));
        Assert.AreEqual(ProjectExclusionReason.LegacyProjectFormat, real.Coverage.Exclusions!.Single().Reason);
    }

    [TestMethod]
    public void ASkippedProjectThatExists_IsStillAGap()
    {
        var lib = Sdk("src/Lib/Lib.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [lib], skipped: [new SkippedProject(lib, "boom")]);

        var result = Build(load, lib);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict);
        Assert.IsNull(result.Coverage.Exclusions);
    }

    private static PackageRestoreOutcome Restore(
        bool timedOut = false, int failedSolutions = 1, int attributed = 1, params (string Project, string[] Codes)[] failures) => new()
        {
            SolutionsAttempted = failedSolutions,
            SolutionsSucceeded = 0,
            SolutionsFailedAttributed = attributed,
            TimedOut = timedOut,
            Projects = failures.Select(f => new PackageRestoreProjectIssue { Project = f.Project, Codes = f.Codes }).ToList()
        };

    private static readonly IReadOnlyList<ProjectExclusion> Excluded =
    [
        new("src/Ios/Ios.csproj", "net10.0-ios", ProjectExclusionReason.PlatformUnavailable, "ios; workload_missing"),
        new("src/Gone/Gone.csproj", null, ProjectExclusionReason.ProjectFileMissing)
    ];

    private ProjectVersionIndex Versions(params (string Project, string? Tfm)[] versions)
    {
        var skipped = versions.Select(v => new SkippedProject(Path.Combine(_root, v.Project), "x") { TargetFramework = v.Tfm }).ToList();
        return new ProjectVersionIndex(_root, Load(Path.Combine(_root, "App.slnx"), [], skipped: skipped));
    }

    private SnapshotCoverage WithRestore(PackageRestoreOutcome restore, ProjectVersionIndex? versions = null) =>
        SnapshotCoverageBuilder.WithRestoreOutcome(
            new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }, restore, Excluded,
            versions ?? Versions(("src/Ios/Ios.csproj", "net10.0-ios"), ("src/Gone/Gone.csproj", null)));

    [TestMethod]
    public void RestoreFailuresExplainedByExclusions_AddNoReason()
    {
        var coverage = WithRestore(Restore(false, 1, 1,
            ("src/Ios/Ios.csproj", ["NU1012"]), ("src/Gone/Gone.csproj", ["MSB3202"])));

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, string.Join(" | ", coverage.Reasons));
        Assert.AreEqual(0, coverage.Reasons.Count);
        Assert.IsTrue(coverage.Notes!.Count > 0, "the restore notes are kept");
    }

    [TestMethod]
    public void ARestoreFailureWithAnyOtherCode_StaysAGap()
    {
        var coverage = WithRestore(Restore(false, 1, 1,
            ("src/Ios/Ios.csproj", ["NU1012"]), ("src/Ios/Ios.csproj", ["NU1004"])));
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);

        var wrongProject = WithRestore(Restore(false, 1, 1, ("src/Lib/Lib.csproj", ["NU1012"])));
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, wrongProject.Verdict, "NU1012 on a project that is not excluded");

        var wrongCode = WithRestore(Restore(false, 1, 1, ("src/Gone/Gone.csproj", ["NU1012"])));
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, wrongCode.Verdict, "MSB3202 explains only a missing file");
    }

    [TestMethod]
    public void NETSDK1084_ExplainsAPlatformExcludedProject()
    {
        var coverage = WithRestore(Restore(false, 1, 1, ("src/Ios/Ios.csproj", ["NETSDK1084"])));

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict);
    }

    [TestMethod]
    public void RestoreFailure_OfAProjectWithAnIncludedVersion_StaysAGap_UntilEveryVersionIsExcluded()
    {
        var failure = Restore(false, 1, 1, ("src/Multi/Multi.csproj", ["NU1012"]));
        var onlyIos = new List<ProjectExclusion>
        {
            new("src/Multi/Multi.csproj", "net10.0-ios", ProjectExclusionReason.PlatformUnavailable, "ios; workload_missing")
        };
        var versions = Versions(("src/Multi/Multi.csproj", "net10.0"), ("src/Multi/Multi.csproj", "net10.0-ios"));

        Assert.IsTrue(SnapshotCoverageBuilder.RestoreGapRemains(failure, onlyIos, versions), "net10.0 loses its packages too");

        var all = new List<ProjectExclusion>(onlyIos)
        {
            new("src/Multi/Multi.csproj", "net10.0", ProjectExclusionReason.PlatformUnavailable, "ios; workload_missing")
        };
        Assert.IsFalse(SnapshotCoverageBuilder.RestoreGapRemains(failure, all, versions));
        Assert.IsTrue(SnapshotCoverageBuilder.RestoreGapRemains(failure, all, null), "no version set cannot prove it");
    }

    [TestMethod]
    public void ASolutionThatFailedWithNoParsedError_KeepsTheGap_EvenWhenAnotherSolutionIsExplained()
    {
        var restore = Restore(false, 2, 1, ("src/Ios/Ios.csproj", ["NU1012"]));

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, WithRestore(restore).Verdict);
        Assert.IsTrue(SnapshotCoverageBuilder.HasUnattributedRestoreGap(restore));
        Assert.IsFalse(SnapshotCoverageBuilder.HasUnattributedRestoreGap(Restore(false, 2, 2, ("src/Ios/Ios.csproj", ["NU1012"]))));
    }

    [TestMethod]
    public void ATimedOutRestore_StaysAGap_EvenWhenEveryAttributedFailureIsExcluded()
    {
        var coverage = WithRestore(Restore(true, 1, 1, ("src/Ios/Ios.csproj", ["NU1012"])));

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
    }

    [TestMethod]
    public void MSB4025_IsExplainedByAMissingOrLegacyExclusion_AndMSB4019OnlyByALegacyOne()
    {
        var excluded = new List<ProjectExclusion>
        {
            new("src/Gone/Gone.csproj", null, ProjectExclusionReason.ProjectFileMissing),
            new("src/Old/Old.csproj", null, ProjectExclusionReason.LegacyProjectFormat),
            new("src/Ios/Ios.csproj", "net10.0-ios", ProjectExclusionReason.PlatformUnavailable, "ios; workload_missing")
        };
        var versions = Versions(("src/Gone/Gone.csproj", null), ("src/Old/Old.csproj", null), ("src/Ios/Ios.csproj", "net10.0-ios"));
        bool Gap(string project, string code) =>
            SnapshotCoverageBuilder.RestoreGapRemains(Restore(false, 1, 1, (project, [code])), excluded, versions);

        Assert.IsFalse(Gap("src/Gone/Gone.csproj", "MSB4025"));
        Assert.IsFalse(Gap("src/Old/Old.csproj", "MSB4025"));
        Assert.IsFalse(Gap("src/Old/Old.csproj", "MSB4019"));
        Assert.IsTrue(Gap("src/Gone/Gone.csproj", "MSB4019"), "a missing import is explained only by a legacy project");
        Assert.IsTrue(Gap("src/Ios/Ios.csproj", "MSB4025"), "MSB4025 does not explain a platform exclusion");
        Assert.IsTrue(Gap("src/Old/Old.csproj", "NU1605"), "NU1605 is never explained");
        Assert.IsTrue(Gap("src/Lib/Lib.csproj", "MSB4025"), "a project that is not excluded");
    }

    [TestMethod]
    public void MSB4025_OnAMissingProject_LeavesTheSnapshotComplete_ThroughTheRestoreVerdict()
    {
        var coverage = SnapshotCoverageBuilder.WithRestoreOutcome(
            new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete },
            Restore(false, 1, 1, ("src/Gone/Gone.csproj", ["MSB4025"])), Excluded,
            Versions(("src/Gone/Gone.csproj", null)));

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, string.Join(" | ", coverage.Reasons));
    }

    [TestMethod]
    public void RepairedProject_KeepsItsLeftOutPlatformVariantsExcluded_AndNothingElse()
    {
        var skia = Sdk("src/Skia/Skia.csproj");
        var other = Sdk("src/Other/Other.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [skia, other],
            degraded:
            [
                new DegradedProject(skia, "net10.0-ios17.0", Unrelated),
                new DegradedProject(skia, "net10.0-maccatalyst17.0", Unrelated),
                new DegradedProject(skia, "net10.0-macos14.0", Unrelated),
                new DegradedProject(skia, "net10.0", Unrelated),
                new DegradedProject(other, "net10.0-ios17.0", Unrelated)
            ]);
        PlatformRepair[] repairs =
            [new("src/Skia/Skia.csproj", "net10.0", ["net10.0-ios", "net10.0-maccatalyst", "net10.0-macos"])];

        var result = SnapshotCoverageBuilder.Build(_root, Resolution(), load,
            new SnapshotCoverageBuilder.Inventory([skia, other], []), platformRepairs: repairs);

        var excluded = result.Coverage.Exclusions!;
        Assert.AreEqual(3, excluded.Count, string.Join(" | ", excluded.Select(e => e.Project + " " + e.TargetFramework)));
        Assert.IsTrue(excluded.All(e => e.Reason == ProjectExclusionReason.PlatformUnavailable && e.Project == "src/Skia/Skia.csproj"));
        Assert.IsTrue(excluded.All(e => e.Detail!.Contains(PlatformEvidence.RestoreRepaired, StringComparison.Ordinal)));
        Assert.AreEqual(2, result.Coverage.ProjectsDegraded, "the restored net10.0 and the unrepaired project stay gaps");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict);
    }

    [TestMethod]
    public void RepairedProject_WithAnOldStyleTargetFramework_ExcludesItsVersion_AndDoesNotThrow()
    {
        var old = Sdk("src/Old/Old.csproj");
        var load = Load(Path.Combine(_root, "App.slnx"), [old],
            degraded: [new DegradedProject(old, "xamarinios10", Unrelated), new DegradedProject(old, "net10.0", Unrelated)]);
        PlatformRepair[] repairs = [new("src/Old/Old.csproj", "net10.0", ["XamarinIOS10"])];

        var result = SnapshotCoverageBuilder.Build(_root, Resolution(), load,
            new SnapshotCoverageBuilder.Inventory([old], []), platformRepairs: repairs);
        Assert.AreEqual("xamarinios10", result.Coverage.Exclusions!.Single().TargetFramework);

        var solution = Path.Combine(_root, "libs", "mix", "Mix.slnx");
        var provider = Sdk("libs/mix/src/Old/Old.csproj");
        var resolution = new CheckoutResolution
        {
            CheckoutDir = _root,
            SelectedSolutions = [Path.Combine(_root, "App.slnx"), solution],
            Source = SolutionSelectionSource.DefaultUnion
        };
        var providerLoad = Load(solution, [provider],
            degraded: [new DegradedProject(provider, "xamarinios10", Unrelated)]);
        var coverage = SnapshotCoverageBuilder.BuildProviders(_root, resolution, providerLoad,
            new(new[] { provider }, [new DeclaredSubmodule("libs/mix", Populated: true)]) { SubmoduleSolutionFiles = [solution] },
            platformRepairs: [new("libs/mix/src/Old/Old.csproj", "net10.0", ["XamarinIOS10"])])["libs/mix"];
        Assert.AreEqual("xamarinios10", coverage.Exclusions!.Single().TargetFramework);
    }

    [TestMethod]
    public void RepairedProject_WithAVersionKnownOnlyToTheIndex_IsExcludedToo()
    {
        var tests = Sdk("tests/Edit.Tests/Edit.Tests.csproj");
        var skipped = new[]
        {
            new SkippedProject(tests, "x") { TargetFramework = "net10.0-ios17.0" },
            new SkippedProject(tests, "x") { TargetFramework = "net10.0" }
        };
        var load = Load(Path.Combine(_root, "App.slnx"), [tests], skipped: skipped);
        PlatformRepair[] repairs = [new("tests/Edit.Tests/Edit.Tests.csproj", "net10.0", ["net10.0-ios"])];

        var result = SnapshotCoverageBuilder.Build(_root, Resolution(), load,
            new SnapshotCoverageBuilder.Inventory([tests], []), platformRepairs: repairs);

        Assert.AreEqual("net10.0-ios17.0", result.Coverage.Exclusions!.Single().TargetFramework);
        Assert.AreEqual(1, result.Coverage.ProjectsSkipped, "only the restored net10.0 stays");
    }

    [TestMethod]
    public void RepairedProject_LoadedVariantsOfTheLeftOutTargets_AreExcluded_WithEvidenceNamingTheRepair()
    {
        var skia = Sdk("src/Skia/Skia.csproj");
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var tfm in new[] { "net10.0", "net10.0-ios17.0", "net10.0-macos14.0" })
            solution = solution.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default, $"Skia ({tfm})", "Skia", LanguageNames.CSharp, filePath: skia));
        var load = new MultiSolutionLoadResult(solution, [],
            [new SolutionCoverage(Path.Combine(_root, "App.slnx"), 1, 1, [])]) { DeclaredProjects = [skia] };
        PlatformRepair[] repairs = [new("src/Skia/Skia.csproj", "net10.0", ["net10.0-ios", "net10.0-macos"])];

        var result = SnapshotCoverageBuilder.Build(_root, Resolution(), load,
            new SnapshotCoverageBuilder.Inventory([skia], []), platformRepairs: repairs);

        CollectionAssert.AreEquivalent(new[] { "net10.0-ios17.0", "net10.0-macos14.0" },
            result.Coverage.Exclusions!.Select(e => e.TargetFramework).ToArray());
        Assert.IsTrue(result.Coverage.Exclusions!.All(e =>
            e.Reason == ProjectExclusionReason.PlatformUnavailable
            && e.Detail!.Contains(PlatformEvidence.RestoreRepaired, StringComparison.Ordinal)));
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, result.Coverage.Verdict, string.Join(" | ", result.Coverage.Reasons));
    }

    [TestMethod]
    public void Provider_PlatformDegradedAndMissingVersions_AreExclusions_AndGenuineGapsStayPartial()
    {
        var ios = Sdk("libs/mix/src/Ios/Ios.csproj");
        var lib = Sdk("libs/mix/src/Lib/Lib.csproj");
        var gone = Path.Combine(_root, "libs", "mix", "src", "Gone", "Gone.csproj");
        var solution = Path.Combine(_root, "libs", "mix", "Mix.slnx");
        var resolution = new CheckoutResolution
        {
            CheckoutDir = _root,
            SelectedSolutions = [Path.Combine(_root, "App.slnx"), solution],
            Source = SolutionSelectionSource.DefaultUnion
        };
        SnapshotCoverageBuilder.Inventory InventoryOf(params string[] files) =>
            new(files, [new DeclaredSubmodule("libs/mix", Populated: true)]) { SubmoduleSolutionFiles = [solution] };

        var clean = Load(solution, [ios, gone],
            skipped: [new SkippedProject(gone, "FileNotFoundException: Project file not found")],
            degraded: [new DegradedProject(ios, "net10.0-ios", Workload)]);
        var coverage = SnapshotCoverageBuilder.BuildProviders(_root, resolution, clean, InventoryOf(ios))["libs/mix"];
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, string.Join(" | ", coverage.Reasons));
        Assert.AreEqual(2, coverage.ProjectVersionsExcluded);
        Assert.AreEqual(0, coverage.ProjectsDegraded);
        Assert.AreEqual(0, coverage.ProjectsSkipped);
        CollectionAssert.AreEquivalent(
            new[] { "src/Gone/Gone.csproj", "src/Ios/Ios.csproj" },
            coverage.Exclusions!.Select(e => e.Project).ToArray(), "provider-relative paths");

        var genuine = Load(solution, [ios, lib],
            degraded:
            [
                new DegradedProject(ios, "net10.0-ios", Workload),
                new DegradedProject(lib, "net10.0", "failed evaluation")
            ]);
        var partial = SnapshotCoverageBuilder.BuildProviders(_root, resolution, genuine, InventoryOf(ios, lib))["libs/mix"];
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, partial.Verdict);
        Assert.IsFalse(string.Join(" | ", partial.Reasons).Contains("Ios", StringComparison.Ordinal));
    }
}
