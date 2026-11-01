using System.Text.Json;
using Sextant.Core;

namespace Sextant.Core.Tests;

/// <summary>Issue #351: the one classifier deciding that a project version is an exclusion, not a coverage gap.</summary>
[TestClass]
public class ProjectExclusionTests
{
    private static readonly WorkerPlatform Linux = new(IsWindows: false, IsMacOS: false);
    private static readonly WorkerPlatform Mac = new(IsWindows: false, IsMacOS: true);
    private static readonly WorkerPlatform Windows = new(IsWindows: true, IsMacOS: false);

    [TestMethod]
    [DataRow("net10.0-ios", "ios")]
    [DataRow("net10.0-ios17.0", "ios")]
    [DataRow("net10.0-maccatalyst", "maccatalyst")]
    [DataRow("net10.0-macos", "macos")]
    [DataRow("net10.0-tvos", "tvos")]
    [DataRow("net10.0-watchos", "watchos")]
    [DataRow("net10.0-android", "android")]
    [DataRow("net10.0-android34.0", "android")]
    [DataRow("net10.0-tizen", "tizen")]
    [DataRow("net10.0-windows", "windows")]
    [DataRow("net10.0-windows10.0.19041.0", "windows")]
    public void PlatformSuffixes_AreUnavailableOnALinuxWorker(string tfm, string platform) =>
        Assert.AreEqual(platform, ProjectExclusionClassifier.UnavailablePlatform(tfm, Linux));

    [TestMethod]
    [DataRow("net10.0")]
    [DataRow("netstandard2.0")]
    [DataRow("net45")]
    [DataRow("net8.0-browser1.0")]
    [DataRow("")]
    [DataRow(null)]
    public void PlainFrameworks_AreNeverPlatformExclusions(string? tfm)
    {
        foreach (var worker in new[] { Linux, Mac, Windows })
            Assert.IsNull(ProjectExclusionClassifier.UnavailablePlatform(tfm, worker));
    }

    [TestMethod]
    public void TheWorkersOwnPlatformIsAvailable()
    {
        Assert.IsNull(ProjectExclusionClassifier.UnavailablePlatform("net10.0-ios", Mac));
        Assert.IsNull(ProjectExclusionClassifier.UnavailablePlatform("net10.0-windows10.0.19041.0", Windows));
        Assert.AreEqual("windows", ProjectExclusionClassifier.UnavailablePlatform("net10.0-windows", Mac));
        Assert.AreEqual("ios", ProjectExclusionClassifier.UnavailablePlatform("net10.0-ios", Windows));
        Assert.AreEqual("android", ProjectExclusionClassifier.UnavailablePlatform("net10.0-android", Mac));
    }

    private const string WorkloadMessage =
        "Platform version is not present for one or more target frameworks, even though they have specified a platform: net10.0-ios, net10.0-maccatalyst";
    private const string NoHostMessage = "There is no application host available for the specified RuntimeIdentifier 'ios-arm64'.";

    private static ProjectExclusion? ClassifyVersion(string tfm, string? reason, bool packBound = false)
    {
        var dir = NewDir();
        try
        {
            var file = Write(dir, "App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            return ProjectExclusionClassifier.Classify("App/App.csproj", tfm, file, reason, packBound, worker: Linux);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void PlatformTfm_WithoutEvidence_IsAGap()
    {
        foreach (var tfm in new[] { "net10.0-windows", "net10.0-ios", "net10.0-android" })
        {
            Assert.IsNull(ClassifyVersion(tfm, null), tfm);
            Assert.IsNull(ClassifyVersion(tfm, "Msbuild failed when processing the file with message: something unrelated"), tfm);
        }
    }

    [TestMethod]
    [DataRow("net10.0-ios", "NETSDK1147: To build this project, the following workloads must be installed", "ios; workload_missing")]
    [DataRow("net10.0-ios", "NETSDK1139: The target platform identifier ios was not recognized.", "ios; workload_missing")]
    [DataRow("net10.0-ios", "NETSDK1178: The project depends on the following workload packs", "ios; workload_missing")]
    [DataRow("net10.0-maccatalyst", "Target framework net10.0-maccatalyst isn't supported on this platform", "maccatalyst; workload_missing")]
    [DataRow("net10.0-ios", NoHostMessage, "ios; no_application_host")]
    [DataRow("net10.0-android", NoHostMessage, "android; no_application_host")]
    [DataRow("net10.0-windows10.0.19041.0", "WindowsAppSDKSelfContained requires a supported Windows architecture.", "windows; windows_tooling")]
    [DataRow("net10.0-windows10.0.19041.0", "XamlCompiler output file was not found", "windows; windows_tooling")]
    [DataRow("net10.0-windows10.0.19041.0", "The command \"dotnet XamlCompiler.exe in.json out.json\" exited with code 126.", "windows; windows_tooling")]
    [DataRow("net10.0-windows10.0.19041.0", "The command \"C:\\sdk\\mt.exe -manifest app.manifest\" exited with code 9009.", "windows; windows_tooling")]
    public void ProductionMessages_AreExcluded_WithTheEvidenceKind(string tfm, string reason, string detail)
    {
        var exclusion = ClassifyVersion(tfm, reason);

        Assert.AreEqual(ProjectExclusionReason.PlatformUnavailable, exclusion!.Reason);
        Assert.AreEqual(detail, exclusion.Detail);
    }

    [TestMethod]
    public void MacosVersion_BlockedByASiblingsPlatformFailure_IsExcluded() =>
        Assert.AreEqual(ProjectExclusionReason.PlatformUnavailable, ClassifyVersion("net10.0-macos", WorkloadMessage)!.Reason);

    [TestMethod]
    public void PlainNet_WithThePlatformMessage_IsAGap()
    {
        Assert.IsNull(ClassifyVersion("net10.0", WorkloadMessage));
        Assert.IsNull(ClassifyVersion("net10.0", NoHostMessage));
        Assert.IsNull(ClassifyVersion("", WorkloadMessage));
    }

    [TestMethod]
    public void WindowsOnlyEvidence_DoesNotExcludeAnAppleVersion_AndAnAppleOnlyHostMessageNeedsAnApplePlatform()
    {
        Assert.IsNull(ClassifyVersion("net10.0-ios", "XamlCompiler output file was not found"));
        Assert.IsNull(ClassifyVersion("net10.0-windows", NoHostMessage));
    }

    [TestMethod]
    public void SdkPinFailure_OnAWindowsVersion_IsAGap() =>
        Assert.IsNull(ClassifyVersion("net10.0-windows",
            "A compatible .NET SDK was not found. Requested SDK version: 10.0.300 global.json file: /r/global.json"));

    [TestMethod]
    public void APlatformPackBinding_IsEvidence() =>
        Assert.AreEqual("ios; platform_pack_binding", ClassifyVersion("net10.0-ios", null, packBound: true)!.Detail);

    [TestMethod]
    public void Classify_MissingFile_IsExcluded_UnlessInsideAnUnpopulatedSubmodule_OrOutsideTheCheckout()
    {
        var dir = NewDir();
        try
        {
            var missing = Path.Combine(dir, "src", "Gone", "Gone.csproj");
            var inSubmodule = Path.Combine(dir, "libs", "mix", "Mix.csproj");

            var exclusion = ProjectExclusionClassifier.Classify("src/Gone/Gone.csproj", null, missing, null, checkoutRoot: dir, worker: Linux);
            Assert.AreEqual(ProjectExclusionReason.ProjectFileMissing, exclusion!.Reason);

            Assert.IsNull(ProjectExclusionClassifier.Classify(
                "libs/mix/Mix.csproj", null, inSubmodule, null, false, [Path.Combine(dir, "libs", "mix")], dir, Linux),
                "an unpopulated submodule keeps its own gap reason");
            Assert.IsNotNull(ProjectExclusionClassifier.Classify(
                "libs/mix/Mix.csproj", null, inSubmodule, null, false, [Path.Combine(dir, "libs", "other")], dir, Linux));

            var outside = Path.Combine(Path.GetTempPath(), "sextant_outside_" + Guid.NewGuid().ToString("N"), "X.csproj");
            Assert.AreEqual(ProjectExclusionReason.ProjectFileMissing,
                ProjectExclusionClassifier.Classify("X.csproj", null, outside, null, checkoutRoot: dir, worker: Linux)!.Reason,
                "a declared project outside the checkout that does not exist is a stale entry");

            var sibling = Path.Combine(Path.GetTempPath(), "sextant_sibling_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sibling);
            try
            {
                var existing = Path.Combine(sibling, "Y.csproj");
                File.WriteAllText(existing, "<Project ToolsVersion=\"15.0\"><Import Project=\"x/Xamarin.iOS.targets\" /></Project>");
                Assert.IsNull(ProjectExclusionClassifier.Classify("Y.csproj", null, existing,
                    "Project does not contain 'Compile' target.", checkoutRoot: dir, worker: Linux),
                    "an existing sibling repository's project is never classified");
            }
            finally { Directory.Delete(sibling, true); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Classify_XamarinTargetFramework_NeedsTheDesktopMsbuildFailure()
    {
        const string failure = "The project 'RecordStack.Xamarin.iOS' targets Xamarin.iOS: you need to use desktop msbuild to build it.";
        foreach (var tfm in new[] { "xamarin.ios", "Xamarin.Mac", "xamarinios10", "xamarin.tvos" })
        {
            Assert.AreEqual("xamarin", ProjectExclusionClassifier.PlatformOf(tfm), tfm);
            var exclusion = ProjectExclusionClassifier.Classify("App.csproj", tfm, "/nowhere/App.csproj", failure, worker: Linux);
            Assert.AreEqual(ProjectExclusionReason.PlatformUnavailable, exclusion!.Reason, tfm);
            Assert.AreEqual($"xamarin; {PlatformEvidence.DesktopMsbuildRequired}", exclusion.Detail, tfm);
            Assert.AreEqual("xamarin", ProjectExclusionClassifier.UnavailablePlatform(tfm, new WorkerPlatform(true, true)));
        }

        Assert.IsNull(ProjectExclusionClassifier.EvidenceFor("xamarin", "error CS0246: type not found"),
            "the framework alone is never evidence");
        Assert.IsNull(ProjectExclusionClassifier.EvidenceFor("xamarin", null));
        Assert.IsNull(ProjectExclusionClassifier.EvidenceFor("ios", failure), "the message is Xamarin evidence only");
        Assert.IsNull(ProjectExclusionClassifier.PlatformOf("net10.0"));
    }

    [TestMethod]
    public void Classify_CaseOnlyMismatch_IsNotMissing()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Inconclusive("case-insensitive file system");
        var dir = NewDir();
        try
        {
            Write(dir, "App/app.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var declared = Path.Combine(dir, "App", "App.csproj");

            Assert.IsNull(ProjectExclusionClassifier.Classify("App/App.csproj", null, declared, null, checkoutRoot: dir, worker: Linux));
            Assert.IsNull(ProjectExclusionClassifier.Classify(
                "Other/App/App.csproj", null, Path.Combine(dir, "APP", "App.csproj"), null, checkoutRoot: dir, worker: Linux),
                "a directory differing only by case");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Classify_LegacyFormat_NeedsAParsedNonSdkProject_AndMissingImportEvidence()
    {
        var dir = NewDir();
        try
        {
            const string ns = "xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"";
            const string real = "Msbuild failed when processing the file '/x/Legacy.csproj' with message: Project does not contain 'Compile' target.";
            const string import = "<Import Project=\"$(MSBuildExtensionsPath)/Xamarin/iOS/Xamarin.iOS.CSharp.targets\" />";
            var legacy = Write(dir, "Legacy.csproj", $"<Project ToolsVersion=\"15.0\" {ns}><PropertyGroup />{import}</Project>");
            var noImport = Write(dir, "Plain.csproj", $"<Project ToolsVersion=\"15.0\" {ns}><PropertyGroup /></Project>");
            var sdkAttr = Write(dir, "A.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup />{import}</Project>");
            var sdkChild = Write(dir, "B.csproj", "<Project><Sdk Name=\"Microsoft.NET.Sdk\" /></Project>");
            var sdkImport = Write(dir, "C.csproj",
                "<Project><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" /></Project>");
            var malformed = Write(dir, "D.csproj", "<Project><PropertyGroup>");
            var dtd = Write(dir, "E.csproj", "<!DOCTYPE Project [<!ENTITY x \"y\">]><Project />");

            Assert.AreEqual(ProjectExclusionReason.LegacyProjectFormat,
                ProjectExclusionClassifier.Classify("Legacy.csproj", null, legacy, real, worker: Linux)!.Reason);
            Assert.IsNull(ProjectExclusionClassifier.Classify("Legacy.csproj", null, legacy, "error CS0246: type not found", worker: Linux),
                "an unrelated failure of a non-SDK project is a gap");
            Assert.IsNull(ProjectExclusionClassifier.Classify("Legacy.csproj", null, legacy, null, worker: Linux));
            Assert.IsNull(ProjectExclusionClassifier.Classify("Plain.csproj", null, noImport, real, worker: Linux));
            foreach (var file in new[] { sdkAttr, sdkChild, sdkImport, malformed, dtd })
                Assert.IsNull(ProjectExclusionClassifier.Classify(Path.GetFileName(file), null, file, real, worker: Linux), file);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Classify_LegacyFormat_IgnoresAProjectFileOverOneMebibyte()
    {
        var dir = NewDir();
        try
        {
            var big = Write(dir, "Big.csproj", "<Project><Import Project=\"$(X)/a.targets\" /><!--" + new string('x', 1024 * 1024 + 10) + "--></Project>");
            Assert.IsNull(ProjectExclusionClassifier.Classify("Big.csproj", null, big,
                "The imported project \"/a/b.targets\" was not found.", worker: Linux));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void VersionKeys_MatchExactly_AndANullFrameworkCoversNoOtherVersion()
    {
        var keys = ProjectExclusionClassifier.VersionKeys([
            new ProjectExclusion("a/A.csproj", "net10.0-ios", ProjectExclusionReason.PlatformUnavailable),
            new ProjectExclusion("b/B.csproj", null, ProjectExclusionReason.ProjectFileMissing)]);

        Assert.IsTrue(ProjectExclusionClassifier.IsVersionExcluded(keys, "a/A.csproj", "net10.0-ios"));
        Assert.IsFalse(ProjectExclusionClassifier.IsVersionExcluded(keys, "a/A.csproj", "net10.0"));
        Assert.IsFalse(ProjectExclusionClassifier.IsVersionExcluded(keys, "a/A.csproj", null));
        Assert.IsTrue(ProjectExclusionClassifier.IsVersionExcluded(keys, "b/B.csproj", ""));
        Assert.IsFalse(ProjectExclusionClassifier.IsVersionExcluded(keys, "b/B.csproj", "net10.0-ios"));
    }

    [TestMethod]
    public void Record_CapsTheList_KeepsTheCount_AndAddsOneNote_WithoutChangingTheVerdict()
    {
        var many = Enumerable.Range(0, SnapshotCoverage.MaxExclusions + 5)
            .Select(i => new ProjectExclusion($"src/P{i:D3}/P{i:D3}.csproj", "net10.0-ios", ProjectExclusionReason.PlatformUnavailable, "ios; workload_missing"));
        var recorded = ProjectExclusionClassifier.Record(new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }, many);

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, recorded.Verdict);
        Assert.AreEqual(SnapshotCoverage.MaxExclusions, recorded.Exclusions!.Count);
        Assert.AreEqual(SnapshotCoverage.MaxExclusions + 5, recorded.AllExclusions!.Count);
        Assert.AreEqual(SnapshotCoverage.MaxExclusions + 5, recorded.ProjectVersionsExcluded);
        Assert.AreEqual(0, recorded.Reasons.Count);
        StringAssert.Contains(recorded.Notes!.Single(), "platform_unavailable (ios 55)");
    }

    [TestMethod]
    public void CoverageWithNoExclusions_SerializesWithoutTheNewKeys()
    {
        var json = JsonSerializer.Serialize(new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        Assert.IsFalse(json.Contains("exclusions", StringComparison.Ordinal), json);
        Assert.IsFalse(json.Contains("project_versions_excluded", StringComparison.Ordinal), json);
    }

    [TestMethod]
    public void CoverageWithExclusions_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var coverage = ProjectExclusionClassifier.Record(
            new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete },
            [new ProjectExclusion("a/A.csproj", "net10.0-ios", ProjectExclusionReason.PlatformUnavailable, "ios; workload_missing")]);
        var json = JsonSerializer.Serialize(coverage, options);

        StringAssert.Contains(json, "\"project_versions_excluded\":1");
        var back = JsonSerializer.Deserialize<SnapshotCoverage>(json, options)!;
        Assert.AreEqual("ios; workload_missing", back.Exclusions!.Single().Detail);
        Assert.IsNull(back.AllExclusions);
        Assert.AreEqual(1, back.ProjectVersionsExcluded);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sextant_excl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string relative, string text)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static ProjectExclusion? ClassifyFile(string content, string? tfm, string? reason)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sextant_excl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "App.csproj");
            File.WriteAllText(file, content);
            return ProjectExclusionClassifier.Classify("App/App.csproj", tfm, file, reason, checkoutRoot: dir, worker: Linux);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string XamarinFile =
        "<Project Sdk=\"MSBuild.Sdk.Extras/3.0.44\"><PropertyGroup><TargetFrameworks>xamarin.ios10;netstandard2.1</TargetFrameworks></PropertyGroup></Project>";

    [TestMethod]
    [DataRow("")]
    [DataRow("netstandard2.1")]
    public void XamarinPlatformVersionMissing_WithADeclaredPlatformThisWorkerLacks_IsPlatformUnavailable(string tfm)
    {
        var exclusion = ClassifyFile(XamarinFile, tfm,
            "Msbuild failed when processing the file with message: Platform version is not present for one or more target frameworks, even though they have specified a platform: xamarin.ios10");
        Assert.IsNotNull(exclusion);
        Assert.AreEqual(ProjectExclusionReason.PlatformUnavailable, exclusion.Reason);
    }

    [TestMethod]
    public void PlatformVersionMissing_WithNoPlatformDeclaredInTheFile_IsNotClassified() =>
        Assert.IsNull(ClassifyFile(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", "net10.0",
            "Platform version is not present for one or more target frameworks, even though they have specified a platform"));

    [TestMethod]
    public void DesktopMsBuildMessage_OnAnEmptyTfmWithAXamarinFile_IsPlatformUnavailable()
    {
        var exclusion = ClassifyFile(XamarinFile, "",
            "Msbuild failed when processing the file with message: If you are building projects that require targets from Visual Studio, you need to use desktop msbuild.");
        Assert.IsNotNull(exclusion);
        Assert.AreEqual(ProjectExclusionReason.PlatformUnavailable, exclusion.Reason);
    }

    [TestMethod]
    public void WpfPresentationCore_OnAWindowsTarget_IsWindowsToolingEvidence()
    {
        var exclusion = ClassifyFile(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>",
            "net10.0-windows",
            "Msbuild failed when processing the file with message: Project file must include the .NET Framework assembly 'PresentationCore, PresentationFramework' in the reference list.");
        Assert.IsNotNull(exclusion);
        Assert.AreEqual(ProjectExclusionReason.PlatformUnavailable, exclusion.Reason);
        StringAssert.Contains(exclusion.Detail!, "windows");
    }

    private const string RefAsmMessage =
        "Msbuild failed when processing the file with message: The reference assemblies for .NETFramework,Version=v4.7.2 were not found. To resolve this, install the Developer Pack (SDK/Targeting Pack) for this framework version.";
    private const string LegacyNet472 =
        "<Project ToolsVersion=\"15.0\"><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion></PropertyGroup></Project>";

    [TestMethod]
    public void NetFrameworkReferenceAssembliesMissing_OnALegacyNet4xProject_HasItsOwnEvidence()
    {
        var exclusion = ClassifyFile(LegacyNet472, "", RefAsmMessage);
        Assert.IsNotNull(exclusion);
        Assert.AreEqual(ProjectExclusionReason.PlatformUnavailable, exclusion.Reason);
        StringAssert.Contains(exclusion.Detail!, PlatformEvidence.ReferenceAssembliesMissing);
        Assert.IsNotNull(ClassifyFile(LegacyNet472, "net472", RefAsmMessage));

        Assert.IsNull(ClassifyFile(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", "",
            "The reference assemblies for .NETFramework,Version=v4.7.2 were not found."), "a net10.0 project declares no .NET Framework target");
    }

    [TestMethod]
    public void NetFrameworkReferenceAssembliesMissing_OnAnSdkStyleNet48_IsAGap() =>
        Assert.IsNull(ClassifyFile(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>", "", RefAsmMessage));

    [TestMethod]
    public void NetFrameworkReferenceAssembliesMissing_OnAModernSiblingOfALegacyNet48_IsAGap() =>
        Assert.IsNull(ClassifyFile(LegacyNet472, "net10.0", RefAsmMessage));

    [TestMethod]
    [DataRow("<Project Sdk=\"X/1\"><PropertyGroup Condition=\"'$(A)'=='b'\"><TargetFrameworks>xamarin.ios10;netstandard2.1</TargetFrameworks></PropertyGroup></Project>")]
    [DataRow("<Project Sdk=\"X/1\"><PropertyGroup><TargetFrameworks Condition=\"'$(A)'=='b'\">xamarin.ios10;netstandard2.1</TargetFrameworks></PropertyGroup></Project>")]
    [DataRow("<Project Sdk=\"X/1\"><Choose><When Condition=\"'$(A)'=='b'\"><PropertyGroup><TargetFrameworks>xamarin.ios10;netstandard2.1</TargetFrameworks></PropertyGroup></When></Choose></Project>")]
    [DataRow("<Project Sdk=\"X/1\"><PropertyGroup><TargetFrameworks>$(T);net10.0-windows</TargetFrameworks></PropertyGroup></Project>")]
    public void UncertainDeclarations_GiveNoEvidence(string file) =>
        Assert.IsNull(ClassifyFile(file, "netstandard2.1",
            "Platform version is not present for one or more target frameworks, even though they have specified a platform"));

    [TestMethod]
    public void GenericWorkloadFailure_OnAPlainSiblingOfAWindowsProject_IsAGap() =>
        Assert.IsNull(ClassifyFile(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks></PropertyGroup></Project>", "net10.0",
            "NETSDK1147: To build this project, the following workloads must be installed: wasm-tools"));

    [TestMethod]
    [DataRow("Project '../X.csproj' targets 'net10.0'. It cannot be referenced by a project that targets 'net7.0-windows10.0.19041.0'.")]
    [DataRow("System.Text.Encodings.Web 10.0.9 doesn't support net7.0-windows and has not been tested with it. Consider upgrading")]
    [DataRow("NU1201: Project X is not compatible with net7.0-windows (.NETCoreApp,Version=v7.0)")]
    [DataRow("Unknown build error, 'Could not find a part of the path'")]
    public void RepositoryDefects_AreNeverClassified(string reason) =>
        Assert.IsNull(ClassifyFile(XamarinFile, "net10.0-windows", reason));
}
