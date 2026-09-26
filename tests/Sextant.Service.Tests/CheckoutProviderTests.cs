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
    public void TryResolve_MalformedSextantJson_ResolvesConfigErrorWithoutSilentDefaultRoot()
    {
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));

        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://github.com/org/app.git" };
        var checkout = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
        Directory.CreateDirectory(checkout);
        // A perfectly loadable default-root solution IS present — a lenient config read would silently pick
        // it and report complete. The malformed config must instead surface a config error (issue #109).
        File.WriteAllText(Path.Combine(checkout, "App.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(checkout, "sextant.json"), "{ this is not valid json ]");

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsTrue(provider.TryResolve(request, out var resolution),
            "a checkout with a malformed sextant.json still resolves — but as a config error, not a default-root pick");
        Assert.IsFalse(resolution.HasSelectedSolutions,
            "a malformed config must NOT silently fall back to a default-root solution");
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
        // A discoverable default-root solution exists, but the operator explicitly scoped `solutions` to an
        // entry that does not exist. We must report the config error WITH the reason, never silently index
        // the default root as if it were the configured coverage.
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
