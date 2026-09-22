using Sextant.Core.Platform;

namespace Sextant.Core.Tests;

/// <summary>
/// Phase 15: the pure worker-capability model — normalization, fingerprint stability, and the
/// <see cref="WorkerCapability.Satisfies"/> matching that underpins routing (acceptance criteria 1–5).
/// </summary>
[TestClass]
public class WorkerCapabilityTests
{
    private static WorkerCapability Windows(params string[] platforms) =>
        WorkerCapability.Create(PlatformOperatingSystem.Windows, "x64", targetPlatforms: platforms.Length == 0 ? ["windows"] : platforms);

    [TestMethod]
    public void PortableRequirement_IsSatisfiedByAnyWorker()
    {
        var linux = WorkerCapability.Create(PlatformOperatingSystem.Linux, targetPlatforms: ["linux"]);
        Assert.IsTrue(linux.Satisfies(CapabilityRequirement.Portable));
    }

    [TestMethod]
    public void WindowsRequirement_NotSatisfiedByLinux_ButSatisfiedByWindows()
    {
        var linux = WorkerCapability.Create(PlatformOperatingSystem.Linux, targetPlatforms: ["linux"]);
        var windows = Windows();
        var requirement = CapabilityRequirement.ForTargetPlatform("windows");

        Assert.IsFalse(linux.Satisfies(requirement), "a Linux worker must not claim to evaluate a Windows target");
        Assert.IsTrue(windows.Satisfies(requirement), "a Windows worker evaluates a Windows target");
    }

    [TestMethod]
    public void ApplePlatform_FoldsToMacOsOwner()
    {
        var requirement = CapabilityRequirement.ForTargetPlatform("ios");
        Assert.AreEqual(PlatformOperatingSystem.MacOS, requirement.RequiredOperatingSystem);

        var mac = WorkerCapability.Create(PlatformOperatingSystem.MacOS, targetPlatforms: ["ios", "maccatalyst"]);
        var linux = WorkerCapability.Create(PlatformOperatingSystem.Linux, targetPlatforms: ["linux"]);
        Assert.IsTrue(mac.Satisfies(requirement));
        Assert.IsFalse(linux.Satisfies(requirement));
    }

    [TestMethod]
    public void MissingWorkload_BlocksSatisfaction()
    {
        var macNoWorkload = WorkerCapability.Create(PlatformOperatingSystem.MacOS, targetPlatforms: ["ios"]);
        var macWithWorkload = WorkerCapability.Create(
            PlatformOperatingSystem.MacOS, targetPlatforms: ["ios"], installedWorkloads: ["ios"]);
        var requirement = CapabilityRequirement.ForTargetPlatform("ios", workloads: ["ios"]);

        Assert.IsFalse(macNoWorkload.Satisfies(requirement), "missing the required workload must block");
        Assert.IsTrue(macWithWorkload.Satisfies(requirement));
    }

    [TestMethod]
    public void Fingerprint_IsStableAcrossEquivalentCapabilities_AndDiffersOnChange()
    {
        var a = WorkerCapability.Create(PlatformOperatingSystem.Windows, "X64", targetPlatforms: ["Windows"]);
        var b = WorkerCapability.Create(PlatformOperatingSystem.Windows, "x64", targetPlatforms: ["windows"]);
        Assert.AreEqual(a.Fingerprint, b.Fingerprint, "normalization must make equivalent capabilities fingerprint-equal");

        var c = WorkerCapability.Create(PlatformOperatingSystem.Linux, "x64", targetPlatforms: ["linux"]);
        Assert.AreNotEqual(a.Fingerprint, c.Fingerprint, "a materially different worker must fingerprint differently");
    }

    [TestMethod]
    public void LocalDefault_AdvertisesOwnPlatform_AndIsDeterministic()
    {
        Assert.AreEqual(WorkerCapability.LocalDefault.Fingerprint, WorkerCapability.LocalDefault.Fingerprint);
        Assert.AreNotEqual(PlatformOperatingSystem.Unknown, WorkerCapability.LocalDefault.OperatingSystem,
            "the test host OS should classify to a known platform");
    }

    [TestMethod]
    public void DirectConstruction_WithNonCanonicalValues_NormalizesToCanonicalFingerprint()
    {
        // #66: the Create factory normalizes, but a capability can also be built directly — an object
        // initializer, a `with` expression, or JSON deserialization (which uses the init setters). Every
        // one of those paths must canonicalize too, or an attacker-supplied capability could dodge the
        // snapshot compatibility gate by fingerprinting differently from a logically-equal one.
        var direct = new WorkerCapability
        {
            OperatingSystem = PlatformOperatingSystem.Windows,
            Architecture = "  X64 ",
            SdkFeatureBands = ["8.0.400", "8.0.100", "8.0.400"],
            TargetPlatforms = ["Windows", "windows"],
            InstalledWorkloads = [" IOS ", "android"]
        };
        var canonical = WorkerCapability.Create(
            PlatformOperatingSystem.Windows, "x64",
            sdkFeatureBands: ["8.0.100", "8.0.400"],
            targetPlatforms: ["windows"],
            installedWorkloads: ["android", "ios"]);

        Assert.AreEqual(canonical.Fingerprint, direct.Fingerprint,
            "a directly-constructed non-canonical capability must fingerprint identically to its canonical form");
        Assert.AreEqual("x64", direct.Architecture);
        CollectionAssert.AreEqual(new[] { "8.0.100", "8.0.400" }, direct.SdkFeatureBands.ToArray());
        CollectionAssert.AreEqual(new[] { "windows" }, direct.TargetPlatforms.ToArray());
        CollectionAssert.AreEqual(new[] { "android", "ios" }, direct.InstalledWorkloads.ToArray());
    }

    [TestMethod]
    public void JsonRoundTrip_NormalizesNonCanonicalCapability()
    {
        const string json =
            "{\"operatingSystem\":3,\"architecture\":\"ARM64\",\"sdkFeatureBands\":[\"9.0.200\",\"9.0.100\",\"9.0.100\"]," +
            "\"targetPlatforms\":[\"IOS\",\"ios\"],\"installedWorkloads\":[],\"referencePacks\":[],\"customToolLabels\":[]}";
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var capability = System.Text.Json.JsonSerializer.Deserialize<WorkerCapability>(json, options)!;

        var canonical = WorkerCapability.Create(
            PlatformOperatingSystem.MacOS, "arm64",
            sdkFeatureBands: ["9.0.100", "9.0.200"], targetPlatforms: ["ios"]);
        Assert.AreEqual(canonical.Fingerprint, capability.Fingerprint,
            "a deserialized non-canonical capability must fingerprint identically to its canonical form");
    }

    [TestMethod]
    public void CanReuse_BlocksIncompatibleCapability()
    {
        // Criterion 5 (routing-side gate): a snapshot produced without the Apple workloads must not be
        // reused to satisfy an Apple-target requirement.
        var linux = WorkerCapability.Create(PlatformOperatingSystem.Linux, targetPlatforms: ["linux"]);
        var appleRequirement = CapabilityRequirement.ForTargetPlatform("ios", workloads: ["ios"]);
        Assert.IsFalse(CapabilityRouter.CanReuse(linux, appleRequirement));

        var mac = WorkerCapability.Create(
            PlatformOperatingSystem.MacOS, targetPlatforms: ["ios"], installedWorkloads: ["ios"]);
        Assert.IsTrue(CapabilityRouter.CanReuse(mac, appleRequirement));
    }
}
