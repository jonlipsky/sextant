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

    // ==== #48: a dirty submodule working tree must be reflected in the parent's pin, not silently
    //          treated as the clean recorded commit. The `git submodule status` prefix column is the
    //          single source of that dirty signal, so its parse is pinned here git-free. ==============

    [TestMethod]
    public void ParseStatusLine_CleanPin_IsNotDirty()
    {
        // A leading SPACE means the submodule is checked out exactly at the recorded pin (clean).
        var parsed = SubmoduleDiscovery.ParseStatusLine(" 1111111111111111111111111111111111111111 libs/MixAndMatch (v1.0)");
        Assert.IsTrue(parsed.Matched);
        Assert.AreEqual("1111111111111111111111111111111111111111", parsed.CommitSha);
        Assert.AreEqual("libs/MixAndMatch", parsed.Path);
        Assert.IsFalse(parsed.IsDirty, "a space-prefixed line is the clean pinned commit");
    }

    [DataTestMethod]
    [DataRow('+', "a different commit / dirty worktree is checked out")]
    [DataRow('-', "the submodule is uninitialized")]
    [DataRow('U', "the submodule has merge conflicts")]
    public void ParseStatusLine_NonSpacePrefix_IsDirty(char prefix, string reason)
    {
        // Every non-space prefix means the parent's working tree does NOT match the clean pinned commit,
        // so the pin must carry a dirty marker (issue #48) rather than be conflated with the clean commit.
        var parsed = SubmoduleDiscovery.ParseStatusLine($"{prefix}2222222222222222222222222222222222222222 libs/MixAndMatch (heads/main)");
        Assert.IsTrue(parsed.Matched);
        Assert.AreEqual("2222222222222222222222222222222222222222", parsed.CommitSha);
        Assert.AreEqual("libs/MixAndMatch", parsed.Path);
        Assert.IsTrue(parsed.IsDirty, $"a '{prefix}'-prefixed line is dirty: {reason}");
    }

    [TestMethod]
    public void ParseStatusLine_TrimmedCleanPin_IsNotDirty()
    {
        // A clean submodule's leading-space prefix is stripped by the upstream git-output trim, so a
        // single-submodule status line reaches the parser with NO prefix at all. It must still parse and
        // classify clean — regressing this silently drops every clean submodule from discovery (the pin
        // never becomes a provider snapshot / dependency edge), which is exactly the dedup-path bug the
        // Phase-12 submodule integration test surfaced.
        var parsed = SubmoduleDiscovery.ParseStatusLine("1111111111111111111111111111111111111111 libs/MixAndMatch (v1.0)");
        Assert.IsTrue(parsed.Matched, "a trimmed (prefix-less) clean line must still match");
        Assert.AreEqual("1111111111111111111111111111111111111111", parsed.CommitSha);
        Assert.AreEqual("libs/MixAndMatch", parsed.Path);
        Assert.IsFalse(parsed.IsDirty, "an absent prefix is the trimmed clean pin, not dirty");
    }

    [TestMethod]
    public void ParseStatusLine_Unrecognized_DoesNotMatch()
    {
        // A blank or non-conforming line is not a submodule entry and is skipped by discovery.
        Assert.IsFalse(SubmoduleDiscovery.ParseStatusLine("").Matched);
        Assert.IsFalse(SubmoduleDiscovery.ParseStatusLine("not a submodule status line").Matched);
    }
}
