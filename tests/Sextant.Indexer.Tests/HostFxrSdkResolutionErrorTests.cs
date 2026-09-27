using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #113: classification of the hostfxr "compatible .NET SDK was not found" failure that a
/// <c>global.json</c> pin with an uninstalled band (e.g. <c>rollForward: disable</c>) raises from Roslyn's
/// BuildHost. The fixtures are the real message shapes observed from the BuildHost, the in-process
/// MSBuildLocator query, and the production (Linux) service log.
/// </summary>
[TestClass]
public sealed class HostFxrSdkResolutionErrorTests
{
    // Verbatim shape of the BuildHost's RemoteInvocationException message (Windows, SDK 10.0.300 installed).
    private const string BuildHostMessage =
        "An exception of type System.InvalidOperationException was thrown: Error while calling hostfxr function " +
        "hostfxr_resolve_sdk2. Error code: -2147450725 Detailed error: A compatible .NET SDK was not found.\r\n" +
        "\r\n" +
        "Requested SDK version: 10.0.999\r\n" +
        "global.json file: C:\\work\\my repo\\global.json\r\n" +
        "\r\n" +
        "Installed SDKs:\r\n" +
        "\r\n" +
        "Install the [10.0.999] .NET SDK or update [C:\\work\\my repo\\global.json] to match an installed SDK.\r\n" +
        "\r\n" +
        "Learn about SDK resolution:\r\n" +
        "https://aka.ms/dotnet/sdk-not-found";

    // The in-process MSBuildLocator copy lists the installed SDKs after the "Learn about" text (Linux paths).
    private const string LocatorMessage =
        "Error while calling hostfxr function hostfxr_resolve_sdk2. Error code: -2147450725 Detailed error: " +
        "A compatible .NET SDK was not found.\n\nRequested SDK version: 10.0.300\n" +
        "global.json file: /data/service/checkouts/recordstack/global.json\n\nInstalled SDKs:\n\n" +
        "Install the [10.0.300] .NET SDK or update [/data/service/checkouts/recordstack/global.json] to match an " +
        "installed SDK.\n\nLearn about SDK resolution:\nhttps://aka.ms/dotnet/sdk-not-found\n" +
        "10.0.401 [/usr/share/dotnet/sdk]\n9.0.305 [/usr/share/dotnet/sdk]\n10.0.401 [/usr/share/dotnet/sdk]\n";

    [TestMethod]
    public void TryParse_BuildHostMessage_ExtractsRequestedVersionAndGlobalJsonPath()
    {
        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(BuildHostMessage, out var error));
        Assert.AreEqual("10.0.999", error.RequestedVersion);
        Assert.AreEqual(@"C:\work\my repo\global.json", error.GlobalJsonPath, "a path with spaces is kept whole");
        Assert.AreEqual(0, error.InstalledSdks.Count, "the BuildHost's copy lists no installed SDKs");
    }

    [TestMethod]
    public void TryParse_LocatorMessage_ExtractsDistinctInstalledSdks()
    {
        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(LocatorMessage, out var error));
        Assert.AreEqual("10.0.300", error.RequestedVersion);
        Assert.AreEqual("/data/service/checkouts/recordstack/global.json", error.GlobalJsonPath);
        CollectionAssert.AreEqual(new[] { "10.0.401", "9.0.305" }, error.InstalledSdks.ToArray(),
            "installed SDKs are de-duplicated and the bracketed [10.0.300] request is not mistaken for one");
    }

    [TestMethod]
    public void TryParse_ProductionLogWithFlattenedLineBreaks_IsRecognized()
    {
        // As recorded in the job's last_error on the production service (issue #113).
        const string production =
            "System.InvalidOperationException: Error while calling hostfxr function hostfxr_resolve_sdk2. " +
            "Error code: -2147450725  A compatible .NET SDK was not found. Requested SDK version: 10.0.300 " +
            "global.json file: /data/service/checkouts/recordstack-abc/global.json Installed SDKs: (10.0.300 not present)";

        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(production, out var error));
        Assert.AreEqual("10.0.300", error.RequestedVersion);
        Assert.AreEqual("/data/service/checkouts/recordstack-abc/global.json", error.GlobalJsonPath);
        Assert.AreEqual(0, error.InstalledSdks.Count);
    }

    [TestMethod]
    public void TryParse_LegacyHostFxrWording_IsRecognized()
    {
        const string legacy =
            "A compatible installed .NET SDK for global.json version [6.0.100] from [/src/app/global.json] was not found.";

        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(legacy, out var error));
        Assert.AreEqual("6.0.100", error.RequestedVersion);
        Assert.AreEqual("/src/app/global.json", error.GlobalJsonPath);
    }

    [TestMethod]
    public void TryParse_FallsBackToTheInstallHintForVersionAndPath()
    {
        const string hintOnly =
            "Error while calling hostfxr function hostfxr_resolve_sdk2. Install the [9.0.999] .NET SDK or update " +
            "[/r/global.json] to match an installed SDK.";

        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(hintOnly, out var error));
        Assert.AreEqual("9.0.999", error.RequestedVersion);
        Assert.AreEqual("/r/global.json", error.GlobalJsonPath);
        Assert.IsTrue(error.IsGlobalJsonPin);
    }

    [TestMethod]
    [DataRow("Error while calling hostfxr function hostfxr_resolve_sdk2. Error code: -2147450725 Detailed error: No .NET SDKs were found.")]
    [DataRow("SDK resolution failed. Error code: -2147450725")]
    [DataRow("hostfxr returned 0x8000809B (SdkResolveFailure)")]
    public void TryParse_NoSdkInstalled_IsAResolutionFailureButNotAPin(string message)
    {
        // A runtime-only image with no SDK (and no global.json) fails the same hostfxr call; callers must not
        // describe it as a global.json pin, so no requested version or path is invented.
        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(message, out var error));
        Assert.IsNull(error.RequestedVersion);
        Assert.IsNull(error.GlobalJsonPath);
        Assert.IsFalse(error.IsGlobalJsonPin);
    }

    [TestMethod]
    public void IsGlobalJsonPin_IsTrueWhenHostFxrNamedTheFileOrVersion()
    {
        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(BuildHostMessage, out var error));
        Assert.IsTrue(error.IsGlobalJsonPin);
        Assert.IsTrue(new HostFxrSdkResolutionError { RequestedVersion = "10.0.300" }.IsGlobalJsonPin);
        Assert.IsTrue(new HostFxrSdkResolutionError { GlobalJsonPath = "/r/global.json" }.IsGlobalJsonPin);
        Assert.IsFalse(new HostFxrSdkResolutionError().IsGlobalJsonPin);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("MSB4236: The SDK 'Microsoft.NET.Sdk' specified could not be found.")]
    [DataRow("Error while calling hostfxr function hostfxr_get_available_sdks. Error code: -1")]
    [DataRow("The project file could not be loaded. Could not find a part of the path.")]
    public void TryParse_UnrelatedFailures_AreNotClassified(string? message)
    {
        Assert.IsFalse(HostFxrSdkResolutionError.TryParse(message, out var error));
        Assert.IsNull(error);
    }

    [TestMethod]
    public void TryClassify_WalksInnerAndAggregateExceptions()
    {
        var wrapped = new InvalidOperationException(
            "Solution 'Fixture.slnx' loaded but all 1 declared project(s) failed to load.",
            new AggregateException(
                new IOException("unrelated"),
                new Exception("wrapper", new InvalidOperationException(BuildHostMessage))));

        Assert.IsTrue(HostFxrSdkResolutionError.TryClassify(wrapped, out var error));
        Assert.AreEqual("10.0.999", error.RequestedVersion);
    }

    [TestMethod]
    public void TryClassify_UnrelatedException_IsNotClassified()
    {
        Assert.IsFalse(HostFxrSdkResolutionError.TryClassify(new InvalidOperationException("boom", new IOException("disk")), out _));
        Assert.IsFalse(HostFxrSdkResolutionError.TryClassify(null, out _));
    }

    [TestMethod]
    public void GlobalJsonLocator_FindsTheNearestFileWalkingUp()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant_gjl_{Guid.NewGuid():N}");
        try
        {
            var nested = Path.Combine(root, "a", "b");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(root, "global.json"), "{}");
            Assert.AreEqual(Path.Combine(root, "global.json"), GlobalJsonLocator.FindNearest(nested));

            File.WriteAllText(Path.Combine(root, "a", "global.json"), "{}");
            Assert.AreEqual(Path.Combine(root, "a", "global.json"), GlobalJsonLocator.FindNearest(nested),
                "the NEAREST global.json wins, exactly as hostfxr's walk-up");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EvaluationDirectories_IncludesSolutionAndDeclaredProjectDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant_gjl_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var sln = Path.Combine(root, "Repo.slnx");
            File.WriteAllText(sln,
                "<Solution><Project Path=\"src/Lib/Lib.csproj\" /><Project Path=\"tools/Tool/Tool.csproj\" /></Solution>");

            var dirs = GlobalJsonLocator.EvaluationDirectories([sln]);

            CollectionAssert.AreEqual(
                new[] { root, Path.Combine(root, "src", "Lib"), Path.Combine(root, "tools", "Tool") },
                dirs.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
