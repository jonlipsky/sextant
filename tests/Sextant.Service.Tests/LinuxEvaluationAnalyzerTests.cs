using Sextant.Core.Platform;
using Sextant.Service.Placement;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 15 — the PURE Linux-outcome derivation (<see cref="LinuxEvaluationAnalyzer"/>). This is the rule
/// that makes routing key off DEMONSTRATED evaluation, never the TFM string: a project that loaded cleanly
/// is Linux-SUCCEEDED even with a <c>-windows</c> TFM, while a project that failed to load or surfaced a
/// missing-capability diagnostic is Linux-INSUFFICIENT and carries the demonstrated requirement. No
/// MSBuild/Roslyn is involved, so the rule is exhaustively unit-testable on any OS.
/// </summary>
[TestClass]
public class LinuxEvaluationAnalyzerTests
{
    [TestMethod]
    public void WindowsTfm_ThatLoadedCleanly_IsLinuxSuccess_NotRouted()
    {
        var outcome = LinuxEvaluationAnalyzer.Analyze(new ProjectEvaluationInfo
        {
            ProjectId = "src/Lib/Lib.csproj",
            TargetPlatform = "windows", // a -windows TFM the Linux worker evaluated fine
            Loaded = true
        });

        Assert.IsTrue(outcome.Succeeded,
            "a project that loaded cleanly is Linux-evaluated even with a -windows TFM (no route on TFM name)");
        Assert.IsNull(outcome.DemonstratedRequirement);
    }

    [TestMethod]
    public void ProjectThatFailedToLoad_IsInsufficient_AttributedToItsPlatform()
    {
        var outcome = LinuxEvaluationAnalyzer.Analyze(new ProjectEvaluationInfo
        {
            ProjectId = "src/App/App.csproj",
            TargetPlatform = "windows",
            Loaded = false
        });

        Assert.IsFalse(outcome.Succeeded, "a project that never loaded is Linux-insufficient");
        Assert.AreEqual(PlatformOperatingSystem.Windows, outcome.DemonstratedRequirement!.RequiredOperatingSystem);
        Assert.AreEqual(RequirementSource.DemonstratedFailure, outcome.DemonstratedRequirement.Source);
    }

    [TestMethod]
    public void LoadedWithMissingCapabilityDiagnostic_IsInsufficient()
    {
        var outcome = LinuxEvaluationAnalyzer.Analyze(new ProjectEvaluationInfo
        {
            ProjectId = "src/Mobile/Mobile.csproj",
            TargetPlatform = "ios",
            Loaded = true,
            MissingCapabilityDiagnostics = ["workload 'ios' is not installed"]
        });

        Assert.IsFalse(outcome.Succeeded, "a missing-capability diagnostic demonstrates insufficiency");
        Assert.AreEqual(PlatformOperatingSystem.MacOS, outcome.DemonstratedRequirement!.RequiredOperatingSystem,
            "an Apple target folds to macOS");
        StringAssert.Contains(outcome.Reason, "ios");
    }

    [TestMethod]
    public void PortableProject_IsLinuxSuccess()
    {
        var outcome = LinuxEvaluationAnalyzer.Analyze(new ProjectEvaluationInfo
        {
            ProjectId = "src/Core/Core.csproj",
            TargetPlatform = null,
            Loaded = true
        });

        Assert.IsTrue(outcome.Succeeded, "an ordinary portable project evaluates on Linux");
    }

    [TestMethod]
    public void ToProbeResult_MapsEachProjectOutcomeAndRequirement()
    {
        var probe = LinuxEvaluationAnalyzer.ToProbeResult([
            new ProjectEvaluationInfo { ProjectId = "a", Loaded = true },
            new ProjectEvaluationInfo { ProjectId = "b", TargetPlatform = "ios", Loaded = false }
        ]);

        Assert.AreEqual(2, probe.Requirements.Count);
        Assert.IsTrue(probe.LinuxOutcomes["a"].Succeeded);
        Assert.IsFalse(probe.LinuxOutcomes["b"].Succeeded);
        Assert.IsTrue(probe.CheckoutAvailable);
    }

    [TestMethod]
    public void DefaultProbe_ReportsEverythingLinuxCapable()
    {
        var probe = new AssumeLinuxCapableProbe();
        var result = probe.ProbeAsync(ServiceTestFixtures.Request(), scratchDir: "unused", CancellationToken.None).Result;

        Assert.AreEqual(0, result.Requirements.Count, "the default probe never fabricates a routing requirement");
        Assert.IsTrue(result.CheckoutAvailable);
    }
}
