using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

[TestClass]
public class SubmoduleDiscoveryTests
{
    /// <summary>
    /// A repo with no <c>.gitmodules</c> has no submodules, so discovery returns empty. This is the
    /// common case on every incremental/overlay reindex; the fast path short-circuits before shelling
    /// out to the (on git-for-windows, multi-second) <c>git submodule status --recursive</c> helper.
    /// The contract asserted here — absent <c>.gitmodules</c> ⇒ empty — is what makes that skip sound.
    /// </summary>
    [TestMethod]
    public async Task DiscoverAsync_NoGitmodules_ReturnsEmpty()
    {
        using var repo = TestGitRepo.TryCreate(new Dictionary<string, string>
        {
            ["README.md"] = "no submodules here",
        });
        if (repo == null)
        {
            Assert.Inconclusive("git is not available on PATH.");
            return;
        }

        Assert.IsFalse(File.Exists(Path.Combine(repo.Root, ".gitmodules")),
            "the fixture must not define any submodules");

        var submodules = await SubmoduleDiscovery.DiscoverAsync(repo.Root);

        Assert.AreEqual(0, submodules.Count);
    }

    /// <summary>
    /// The fast path is a pure filesystem check, so it holds even for a directory that is not a git
    /// working tree at all (no <c>.git</c>, no <c>.gitmodules</c>) — discovery returns empty without
    /// depending on a successful git invocation.
    /// </summary>
    [TestMethod]
    public async Task DiscoverAsync_NonRepoDirectory_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant_nosub_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var submodules = await SubmoduleDiscovery.DiscoverAsync(dir);
            Assert.AreEqual(0, submodules.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
