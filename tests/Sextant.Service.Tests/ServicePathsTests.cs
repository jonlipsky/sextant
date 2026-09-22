namespace Sextant.Service.Tests;

/// <summary>
/// Acceptance criterion 3: worker scratch cleanup CANNOT delete a published snapshot. The persistent
/// checkout/artifact/cache volumes are on paths SEPARATE from the ephemeral worker-scratch root, scratch
/// directories are allocated per job under the scratch root, and <see cref="ServicePaths.ReleaseScratch"/>
/// refuses any path outside the scratch root — so a botched cleanup can never reach durable data.
/// </summary>
[TestClass]
public class ServicePathsTests
{
    private string _root = null!;
    private ServicePaths _paths = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _root = ServiceTestFixtures.NewDataRoot();
        _paths = new ServicePaths(ServiceVolumes.Rooted(_root));
    }

    [TestCleanup]
    public void TestCleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [TestMethod]
    public void ScratchRoot_IsSeparateFromPersistentVolumes()
    {
        Assert.IsFalse(_paths.IsPersistent(_paths.ScratchRoot), "scratch is not a persistent volume");
        Assert.IsTrue(_paths.IsPersistent(_paths.ArtifactRoot), "artifacts are persistent");
        Assert.IsTrue(_paths.IsPersistent(_paths.CheckoutRoot), "checkouts are persistent");
        Assert.IsTrue(_paths.IsPersistent(_paths.CacheRoot), "cache is persistent");
    }

    [TestMethod]
    public void AllocateScratch_CreatesDirectory_UnderScratchRoot()
    {
        var dir = _paths.AllocateScratch("job-1");
        Assert.IsTrue(Directory.Exists(dir));
        Assert.IsTrue(dir.StartsWith(_paths.ScratchRoot, StringComparison.Ordinal), "scratch is allocated under the scratch root");
        Assert.IsFalse(_paths.IsPersistent(dir), "an allocated scratch dir is not persistent");
    }

    [TestMethod]
    public void ReleaseScratch_DeletesAnAllocatedScratchDir()
    {
        var dir = _paths.AllocateScratch("job-2");
        File.WriteAllText(Path.Combine(dir, "artifact.tmp"), "scratch");
        _paths.ReleaseScratch(dir);
        Assert.IsFalse(Directory.Exists(dir), "scratch cleanup removes the per-job directory");
    }

    [TestMethod]
    public void ReleaseScratch_RefusesToDeleteAPersistentVolume()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => _paths.ReleaseScratch(_paths.ArtifactRoot),
            "scratch cleanup must never delete a persistent (published-snapshot-bearing) volume (criterion 3)");
        Assert.IsTrue(Directory.Exists(_paths.ArtifactRoot), "the artifact volume is untouched");
    }

    [TestMethod]
    public void ReleaseScratch_RefusesAPathThatEscapesTheScratchRoot()
    {
        var escape = Path.Combine(_paths.ScratchRoot, "..", "artifacts");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => _paths.ReleaseScratch(escape),
            "a '..' escape out of the scratch root is refused");
    }

    [TestMethod]
    public void Construction_Fails_WhenScratchIsNestedInAPersistentVolume()
    {
        var bad = new ServiceVolumes
        {
            CheckoutRoot = Path.Combine(_root, "checkouts"),
            ArtifactRoot = Path.Combine(_root, "artifacts"),
            CacheRoot = Path.Combine(_root, "cache"),
            ScratchRoot = Path.Combine(_root, "artifacts", "scratch") // nested inside a persistent volume
        };
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new ServicePaths(bad),
            "nesting scratch inside a persistent volume is rejected so cleanup can never reach published data");
    }
}
