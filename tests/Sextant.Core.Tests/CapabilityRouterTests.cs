using Sextant.Core.Platform;

namespace Sextant.Core.Tests;

/// <summary>
/// Phase 15: the pure routing DECISION (<see cref="CapabilityRouter"/>). These tests prove the acceptance
/// criteria at the decision level, independently of any worker execution:
/// <list type="number">
///   <item>ordinary/portable graphs (and a <c>-windows</c> TFM Linux evaluated fine) stay on Linux;</item>
///   <item>a project Linux cannot evaluate routes to a Windows worker;</item>
///   <item>an Apple-platform project routes to a macOS worker;</item>
///   <item>no compatible worker → the job cannot run (fail closed).</item>
/// </list>
/// The DEMONSTRATED Linux outcome — never the TFM string — drives the decision.
/// </summary>
[TestClass]
public class CapabilityRouterTests
{
    private static readonly WorkerCapability Linux =
        WorkerCapability.Create(PlatformOperatingSystem.Linux, "x64", targetPlatforms: ["linux"]);
    private static readonly WorkerCapability Windows =
        WorkerCapability.Create(PlatformOperatingSystem.Windows, "x64", targetPlatforms: ["windows"]);
    private static readonly WorkerCapability Mac =
        WorkerCapability.Create(PlatformOperatingSystem.MacOS, "arm64", targetPlatforms: ["ios", "maccatalyst", "macos"], installedWorkloads: ["ios"]);

    private static ProjectCapabilityRequirement Project(string id, string? platform = null) => new()
    {
        ProjectId = id,
        Requirement = CapabilityRequirement.ForTargetPlatform(platform)
    };

    private static Dictionary<string, LinuxEvaluationOutcome> Outcomes(
        params (string id, LinuxEvaluationOutcome outcome)[] entries) =>
        entries.ToDictionary(e => e.id, e => e.outcome, StringComparer.Ordinal);

    [TestMethod]
    public void Criterion1_PortableGraph_StaysOnLinux()
    {
        var reqs = new[] { Project("a"), Project("b") };
        var result = CapabilityRouter.RouteJob(reqs, Outcomes(), Linux, [Windows, Mac], PlatformRoutingPolicy.Default);

        Assert.IsTrue(result.CanRun);
        Assert.IsTrue(result.ExecutionIsDefault, "a portable graph must not route to a native worker");
        Assert.AreEqual(Linux.Fingerprint, result.ExecutionWorker!.Fingerprint);
    }

    [TestMethod]
    public void Criterion1_WindowsTfm_ThatLinuxEvaluated_StaysOnLinux()
    {
        // A net8.0-windows library Linux evaluated fine must NOT be routed — routing keys off the
        // demonstrated outcome, not the TFM name.
        var reqs = new[] { Project("winlib", "windows") };
        var outcomes = Outcomes(("winlib", LinuxEvaluationOutcome.Success));
        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows], PlatformRoutingPolicy.Default);

        Assert.IsTrue(result.CanRun);
        Assert.IsTrue(result.ExecutionIsDefault, "a -windows TFM Linux evaluated fine must stay on Linux (no needless routing)");
    }

    [TestMethod]
    public void Criterion2_WindowsProject_LinuxInsufficient_RoutesToWindows()
    {
        var reqs = new[] { Project("app", "windows") };
        var outcomes = Outcomes(("app", LinuxEvaluationOutcome.Insufficient(
            CapabilityRequirement.ForTargetPlatform("windows", RequirementSource.DemonstratedFailure, "missing windows targeting pack"),
            "missing windows targeting pack")));

        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows, Mac], PlatformRoutingPolicy.Default);

        Assert.IsTrue(result.CanRun);
        Assert.IsFalse(result.ExecutionIsDefault);
        Assert.AreEqual(Windows.Fingerprint, result.ExecutionWorker!.Fingerprint);
        Assert.AreEqual(PlatformOperatingSystem.Windows, result.ExecutionWorker!.OperatingSystem);
    }

    [TestMethod]
    public void Criterion3_AppleProject_LinuxInsufficient_RoutesToMac()
    {
        var reqs = new[] { Project("ios-app", "ios") };
        var outcomes = Outcomes(("ios-app", LinuxEvaluationOutcome.Insufficient(
            CapabilityRequirement.ForTargetPlatform("ios", RequirementSource.DemonstratedFailure, "missing ios workload", workloads: ["ios"]),
            "missing ios workload")));

        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows, Mac], PlatformRoutingPolicy.Default);

        Assert.IsTrue(result.CanRun);
        Assert.AreEqual(Mac.Fingerprint, result.ExecutionWorker!.Fingerprint);
        Assert.AreEqual(PlatformOperatingSystem.MacOS, result.ExecutionWorker!.OperatingSystem);
    }

    [TestMethod]
    public void Criterion4_NoCompatibleWorker_FailsClosed()
    {
        // Apple project but only a Windows worker is available → the job cannot run, with a per-project
        // unsupported decision carrying a reason (fail closed, no silent success).
        var reqs = new[] { Project("ios-app", "ios") };
        var outcomes = Outcomes(("ios-app", LinuxEvaluationOutcome.Insufficient(
            CapabilityRequirement.ForTargetPlatform("ios", RequirementSource.DemonstratedFailure, "missing ios workload", workloads: ["ios"]),
            "missing ios workload")));

        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows], PlatformRoutingPolicy.Default);

        Assert.IsFalse(result.CanRun, "no macOS worker exists → the job must fail closed");
        Assert.IsNull(result.ExecutionWorker);
        Assert.IsTrue(result.Plan.AnyUnsupported);
        var unsupported = result.Plan.Unsupported.Single();
        Assert.AreEqual("ios-app", unsupported.ProjectId);
        Assert.IsNotNull(unsupported.Reason);
    }

    [TestMethod]
    public void LinuxOnlyPolicy_DoesNotEscalate_FailsClosed()
    {
        var reqs = new[] { Project("app", "windows") };
        var outcomes = Outcomes(("app", LinuxEvaluationOutcome.Insufficient(
            CapabilityRequirement.ForTargetPlatform("windows", RequirementSource.DemonstratedFailure, "missing windows pack"),
            "missing windows pack")));

        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows], PlatformRoutingPolicy.LinuxOnly);

        Assert.IsFalse(result.CanRun, "linux_only policy must never escalate, even when a Windows worker exists");
        Assert.IsTrue(result.Plan.Unsupported.Single().Reason!.Contains("policy", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MixedGraph_WindowsPlusApple_NoSingleWorker_FailsClosed()
    {
        // Job-granular routing: one project needs Windows, another needs Apple; no single worker covers
        // both, so the job fails closed (per-project mixed production is a later phase).
        var reqs = new[] { Project("win", "windows"), Project("ios", "ios") };
        var outcomes = Outcomes(
            ("win", LinuxEvaluationOutcome.Insufficient(
                CapabilityRequirement.ForTargetPlatform("windows", RequirementSource.DemonstratedFailure, "win"), "win")),
            ("ios", LinuxEvaluationOutcome.Insufficient(
                CapabilityRequirement.ForTargetPlatform("ios", RequirementSource.DemonstratedFailure, "ios", workloads: ["ios"]), "ios")));

        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows, Mac], PlatformRoutingPolicy.Default);

        Assert.IsFalse(result.CanRun, "no single worker satisfies both Windows and Apple requirements");
    }

    [TestMethod]
    public void Policy_RestrictedNativeOs_WithholdsMac()
    {
        // A policy that allows Windows escalation but withholds macOS.
        var policy = new PlatformRoutingPolicy
        {
            Mode = PlatformRoutingMode.Auto,
            AllowedNativeOperatingSystems = [PlatformOperatingSystem.Windows]
        };
        var reqs = new[] { Project("ios", "ios") };
        var outcomes = Outcomes(("ios", LinuxEvaluationOutcome.Insufficient(
            CapabilityRequirement.ForTargetPlatform("ios", RequirementSource.DemonstratedFailure, "ios", workloads: ["ios"]), "ios")));

        var result = CapabilityRouter.RouteJob(reqs, outcomes, Linux, [Windows, Mac], policy);
        Assert.IsFalse(result.CanRun, "macOS escalation is withheld by policy → fail closed");
    }
}
