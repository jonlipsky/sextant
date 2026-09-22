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
        Assert.IsFalse(provider.TryResolve(request, out var dir, out var sln),
            "a traversal repository url must never resolve a checkout outside the persistent checkout volume");
        Assert.AreEqual(string.Empty, dir);
        Assert.AreEqual(string.Empty, sln);
    }

    [TestMethod]
    public void TryResolve_NormalRepositoryUrl_ResolvesWithinVolume()
    {
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        var volumes = ServiceVolumes.Rooted(_dataRoot);
        var paths = new ServicePaths(volumes);

        // A well-formed checkout under the volume resolves normally (the guard is not over-broad).
        var checkout = Path.Combine(paths.CheckoutRoot, "app");
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, "App.slnx"), string.Empty);

        var request = ServiceTestFixtures.Request() with { RepositoryRemoteUrl = "https://github.com/org/app.git" };

        var provider = new PersistentVolumeCheckoutProvider(paths);
        Assert.IsTrue(provider.TryResolve(request, out var dir, out var sln),
            "a normal repository url resolves its checkout within the volume");
        Assert.IsTrue(dir.StartsWith(paths.CheckoutRoot, StringComparison.Ordinal));
        Assert.IsTrue(sln.EndsWith("App.slnx", StringComparison.Ordinal));
    }
}
