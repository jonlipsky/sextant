using Sextant.Indexer;

namespace Sextant.Service.Tests;

/// <summary>
/// The persistent-volume checkout resolver must never let a crafted repository URL escape the checkout
/// volume (defense in depth: URL sanitization collapses "." / ".." to a safe name, and a containment
/// guard rejects anything that still resolves outside the volume). A checkout resolved outside the volume
/// could expose — or, via later scratch/volume cleanup, delete — arbitrary host paths.
/// </summary>
[TestClass]
public class CheckoutProviderTests
{
    private string _dataRoot = null!;

    [TestCleanup]
    public void TestCleanup()
    {
        try { if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [TestMethod]
    public void TryResolve_TraversalRepositoryUrl_DoesNotEscapeCheckoutVolume()
    {
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var volumes = ServiceVolumes.Rooted(_dataRoot);
        var paths = new ServicePaths(volumes); // creates the checkout volume

        // Plant a solution OUTSIDE the checkout volume (a sibling under the data root). A naive
        // last-segment sanitizer on a URL ending in "/.." would resolve the checkout dir to the parent of
        // the checkout volume and discover this file.
        var outside = Path.Combine(_dataRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Escaped.sln"), string.Empty);

        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://host/repo/.." };

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsFalse(provider.TryResolve(request, out var resolution),
            "a traversal repository url must never resolve a checkout outside the persistent checkout volume");
        Assert.IsNull(resolution);
    }

    [TestMethod]
    public void TryResolve_NormalRepositoryUrl_ResolvesWithinVolume()
    {
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var volumes = ServiceVolumes.Rooted(_dataRoot);
        var paths = new ServicePaths(volumes);

        // A well-formed checkout under the volume resolves normally (the guard is not over-broad).
        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://github.com/org/app.git" };
        var checkout = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, "App.slnx"), string.Empty);

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsTrue(provider.TryResolve(request, out var resolution),
            "a normal repository url resolves its checkout within the volume");
        Assert.IsTrue(resolution.CheckoutDir.StartsWith(paths.CheckoutRoot, StringComparison.Ordinal));
        Assert.IsTrue(resolution.PrimarySolution.EndsWith("App.slnx", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryResolve_MalformedSextantJson_ResolvesConfigErrorWithoutSilentDefaultSelection()
    {
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));

        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://github.com/org/app.git" };
        var checkout = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
        Directory.CreateDirectory(checkout);
        // A perfectly loadable solution IS present for the no-config default — a lenient config read would silently pick
        // it and report complete. The malformed config must instead surface a config error (issue #109).
        File.WriteAllText(Path.Combine(checkout, "App.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(checkout, "sextant.json"), "{ this is not valid json ]");

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsTrue(provider.TryResolve(request, out var resolution),
            "a checkout with a malformed sextant.json still resolves — but as a config error, not the default selection");
        Assert.IsFalse(resolution.HasSelectedSolutions,
            "a malformed config must NOT silently fall back to the default selection");
        Assert.IsNotNull(resolution.ConfigurationError, "the config error is surfaced with a reason");
        StringAssert.Contains(resolution.ConfigurationError!, "json",
            "the reason names the malformed JSON");
    }

    [TestMethod]
    public void TryResolve_AllConfiguredSolutionsInvalid_ResolvesConfigErrorWithReasons()
    {
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));

        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://github.com/org/app.git" };
        var checkout = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
        Directory.CreateDirectory(checkout);
        // A discoverable solution exists for the no-config default, but the operator explicitly scoped `solutions` to an
        // entry that does not exist. We must report the config error WITH the reason, never silently index
        // the default selection as if it were the configured coverage.
        File.WriteAllText(Path.Combine(checkout, "App.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(checkout, "sextant.json"),
            "{ \"solutions\": [ \"does/not/Exist.slnx\" ] }");

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsTrue(provider.TryResolve(request, out var resolution),
            "an explicit-but-unusable config resolves as a config error rather than degrading to unsupported");
        Assert.IsFalse(resolution.HasSelectedSolutions, "no solution is selected when every configured entry is invalid");
        Assert.IsNotNull(resolution.ConfigurationError, "the config error is surfaced");
        Assert.AreEqual(1, resolution.SkippedSolutions.Count,
            "the invalid configured entry is recorded skipped-with-reason, not silently dropped");
        Assert.AreEqual("does/not/Exist.slnx", resolution.SkippedSolutions[0].RequestedPath);
    }

    [TestMethod]
    public void TryResolve_NoConfig_MultipleSolutions_SelectsTheUnion_NothingLeftUnselected()
    {
        // #124: with no sextant.json every discovered solution is selected (the default union), in the
        // deterministic order, so DiscoveredButNotSelected is empty and PrimarySolution is the top-ranked one.
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));

        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://github.com/org/app.git" };
        var checkout = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
        Directory.CreateDirectory(Path.Combine(checkout, "Build.Mac"));
        Directory.CreateDirectory(Path.Combine(checkout, "Build.Linux"));
        File.WriteAllText(Path.Combine(checkout, "Build.Mac", "App-ios.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(checkout, "Build.Linux", "App-server.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(checkout, "Build.Linux", "Tools.sln"), string.Empty);

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsTrue(provider.TryResolve(request, out var resolution));

        Assert.AreEqual(SolutionSelectionSource.DefaultUnion, resolution.Source);
        CollectionAssert.AreEqual(
            new[] { "Build.Linux/App-server.slnx", "Build.Linux/Tools.sln", "Build.Mac/App-ios.slnx" },
            resolution.SelectedSolutions.Select(s => Path.GetRelativePath(checkout, s).Replace('\\', '/')).ToArray(),
            "every solution is selected: Linux-marker first, neutral next, the platform head last");
        Assert.AreEqual(0, resolution.DiscoveredButNotSelected.Count);
        Assert.IsTrue(resolution.PrimarySolution.EndsWith("App-server.slnx", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryResolve_ExplicitConfig_StaysAuthoritative_UnderTheUnionDefault()
    {
        // The default union applies ONLY without config: a `solutions` list still selects exactly that set.
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));

        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://github.com/org/app.git" };
        var checkout = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, "App.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(checkout, "Other.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(checkout, "sextant.json"), "{ \"solutions\": [ \"Other.slnx\" ] }");

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsTrue(provider.TryResolve(request, out var resolution));

        Assert.AreEqual(SolutionSelectionSource.Configured, resolution.Source);
        Assert.AreEqual(1, resolution.SelectedSolutions.Count);
        Assert.IsTrue(resolution.PrimarySolution.EndsWith("Other.slnx", StringComparison.Ordinal));
        Assert.AreEqual(0, resolution.DiscoveredButNotSelected.Count, "an explicit config records no discovery");
    }

    [TestMethod]
    public void TryResolve_NoConfigAndNoSolution_ReturnsFalse()
    {
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));

        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://github.com/org/app.git" };
        var checkout = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
        Directory.CreateDirectory(checkout); // a checkout with no solution and no config

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsFalse(provider.TryResolve(request, out var resolution),
            "a checkout with neither a config nor any discoverable solution is genuinely unsupported");
        Assert.IsNull(resolution);
    }
}
