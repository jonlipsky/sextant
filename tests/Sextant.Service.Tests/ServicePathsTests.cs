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
    public void RepoDirectoryName_SameBasenameDifferentOwner_DoesNotCollide()
    {
        // Cross-tenant isolation (issue #7): two DIFFERENT repositories that happen to share a basename
        // must map to DIFFERENT checkout directories, or one tenant's checkout could satisfy another
        // tenant's request. The basename is preserved for readability, but a URL-hash suffix disambiguates.
        var a = ServicePaths.RepoDirectoryName("https://github.com/org-a/common.git");
        var b = ServicePaths.RepoDirectoryName("https://github.com/org-b/common.git");
        Assert.AreNotEqual(a, b, "same-basename repos from different owners must not share a checkout directory");
        Assert.IsTrue(a.StartsWith("common-", StringComparison.Ordinal), "the human-readable basename is preserved");
        Assert.IsTrue(b.StartsWith("common-", StringComparison.Ordinal), "the human-readable basename is preserved");
    }

    [TestMethod]
    public void RepoDirectoryName_EquivalentSpellingsOfSameRepo_MapToSameDirectory()
    {
        // The same repository must map to a STABLE directory across identity-neutral spelling differences
        // (a trailing slash or a .git suffix), so a checkout is found regardless of how the URL was written.
        var bare = ServicePaths.RepoDirectoryName("https://github.com/org/app");
        var dotGit = ServicePaths.RepoDirectoryName("https://github.com/org/app.git");
        var trailingSlash = ServicePaths.RepoDirectoryName("https://github.com/org/app/");
        Assert.AreEqual(bare, dotGit, ".git is not identity-bearing");
        Assert.AreEqual(bare, trailingSlash, "a trailing slash is not identity-bearing");
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
