using Sextant.Core.Platform;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 15 / acceptance criterion 6 — the platform-fixture matrix. It sweeps the (declared target platform
/// → demonstrated Linux outcome) fixtures across the full worker matrix and asserts the ROUTING DECISION +
/// capability-fingerprint provenance for each: a portable/Linux-evaluable graph stays on Linux, a
/// Windows-only graph routes to a Windows worker, an Apple graph routes to a macOS worker, and a graph with
/// no compatible worker fails closed. Because CI is Linux and real native execution is Phase 14, the native
/// legs are exercised through the substitutable capability model (a simulated Windows/macOS worker), so the
/// decision + fingerprint logic is fully validated on Linux.
///
/// Gated behind <c>SEXTANT_RUN_PLATFORM_MATRIX=1</c> and <c>[TestCategory("Performance")]</c> (mirroring the
/// Phase-6/Phase-10 <c>SEXTANT_RUN_PERF</c> pattern) so a runner lacking a given native toolchain never
/// destabilizes the default suite's pass/fail count (issue #33). The routing DECISION itself is also
/// covered unconditionally by <see cref="CapabilityRoutingWorkerTests"/> and the Core router tests.
/// </summary>
[TestClass]
[TestCategory("Performance")]
public class PlatformFixtureMatrixTests
{
    private static readonly WorkerCapability Linux =
        WorkerCapability.Create(PlatformOperatingSystem.Linux, "x64", targetPlatforms: ["linux"]);
    private static readonly WorkerCapability Windows =
        WorkerCapability.Create(PlatformOperatingSystem.Windows, "x64", targetPlatforms: ["windows"]);
    private static readonly WorkerCapability Mac =
        WorkerCapability.Create(PlatformOperatingSystem.MacOS, "arm64",
            targetPlatforms: ["ios", "maccatalyst", "macos", "tvos"], installedWorkloads: ["ios", "maccatalyst"]);

    [DataTestMethod]
    [DataRow(null, true, "linux", false)]        // portable graph → Linux, no route
    [DataRow("windows", true, "linux", false)]   // -windows TFM Linux evaluated fine → stays on Linux
    [DataRow("windows", false, "windows", true)] // -windows Linux could not evaluate → Windows worker
    [DataRow("ios", false, "macos", true)]       // Apple graph Linux could not evaluate → macOS worker
    [DataRow("maccatalyst", false, "macos", true)]
    [DataRow("tvos", false, "macos", true)]
    public void Matrix_RoutesToExpectedWorker_AndRecordsFingerprint(
        string? targetPlatform, bool linuxEvaluated, string expectedOs, bool expectRouted)
    {
        RequireMatrixEnabled();

        var outcomes = new Dictionary<string, LinuxEvaluationOutcome>(StringComparer.Ordinal)
        {
            ["p"] = linuxEvaluated
                ? LinuxEvaluationOutcome.Success
                : LinuxEvaluationOutcome.Insufficient(
                    CapabilityRequirement.ForTargetPlatform(targetPlatform, RequirementSource.DemonstratedFailure,
                        $"Linux cannot evaluate {targetPlatform}"),
                    $"Linux cannot evaluate {targetPlatform}")
        };
        var reqs = new[]
        {
            new ProjectCapabilityRequirement
            {
                ProjectId = "p",
                Requirement = CapabilityRequirement.ForTargetPlatform(targetPlatform)
            }
        };

        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows, Mac], PlatformRoutingPolicy.Default);

        Assert.IsTrue(result.CanRun, $"the {targetPlatform ?? "portable"} fixture must route to a worker");
        Assert.AreEqual(expectedOs, result.ExecutionWorker!.OperatingSystem.ToName());
        Assert.AreEqual(!expectRouted, result.ExecutionIsDefault,
            "a Linux-evaluable fixture stays on the default worker; a native one is routed off it");

        // The chosen worker's capability fingerprint is what gets stamped into snapshot provenance.
        Assert.IsFalse(string.IsNullOrEmpty(result.ExecutionWorker!.Fingerprint),
            "the routed worker advertises a capability fingerprint for provenance/cache-compat (criterion 5)");
    }

    [TestMethod]
    public void Matrix_AppleFixture_WithNoMacWorker_FailsClosed()
    {
        RequireMatrixEnabled();

        var outcomes = new Dictionary<string, LinuxEvaluationOutcome>(StringComparer.Ordinal)
        {
            ["p"] = LinuxEvaluationOutcome.Insufficient(
                CapabilityRequirement.ForTargetPlatform("ios", RequirementSource.DemonstratedFailure, "no ios"),
                "no ios")
        };
        var reqs = new[]
        {
            new ProjectCapabilityRequirement
            {
                ProjectId = "p",
                Requirement = CapabilityRequirement.ForTargetPlatform("ios")
            }
        };

        // Only Linux + Windows workers exist — no macOS capacity for the Apple fixture.
        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows], PlatformRoutingPolicy.Default);

        Assert.IsFalse(result.CanRun, "an Apple fixture with no macOS worker fails closed (criterion 4)");
        Assert.IsTrue(result.Plan.AnyUnsupported);
    }

    private static void RequireMatrixEnabled()
    {
        if (Environment.GetEnvironmentVariable("SEXTANT_RUN_PLATFORM_MATRIX") != "1")
            Assert.Inconclusive("Platform-fixture matrix skipped (set SEXTANT_RUN_PLATFORM_MATRIX=1 to run).");
    }
}
