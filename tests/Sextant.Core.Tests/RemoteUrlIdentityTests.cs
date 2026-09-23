namespace Sextant.Core.Tests;

/// <summary>
/// Issue #92 — the canonical remote-url identity fold shared by the checkout directory and the catalog
/// repository row. Folds identity-neutral SPELLING differences (trailing whitespace, trailing slash,
/// trailing <c>.git</c>, and case) to a single key while keeping genuinely-distinct repositories apart.
/// </summary>
[TestClass]
public class RemoteUrlIdentityTests
{
    // The three real spellings from the bug report plus a trailing-slash variant. They differ only by a
    // trailing .git, a trailing slash, and case — all identity-neutral — so all four fold to one key.
    [DataTestMethod]
    [DataRow("https://github.com/elevenworks/MixAndMatch.git")]
    [DataRow("https://github.com/elevenworks/MixAndMatch")]
    [DataRow("https://github.com/elevenworks/mixandmatch")]
    [DataRow("https://github.com/elevenworks/MixAndMatch/")]
    [DataRow("https://github.com/elevenworks/MixAndMatch.git/")]
    [DataRow("  https://github.com/elevenworks/MixAndMatch.git  ")]
    public void EquivalentSpellings_FoldToOneCanonicalKey(string spelling)
    {
        Assert.AreEqual("https://github.com/elevenworks/mixandmatch", RemoteUrlIdentity.Normalize(spelling));
    }

    [TestMethod]
    public void DifferentRepositories_StayDistinct()
    {
        var a = RemoteUrlIdentity.Normalize("https://github.com/elevenworks/MixAndMatch.git");
        var differentRepo = RemoteUrlIdentity.Normalize("https://github.com/elevenworks/OtherRepo.git");
        var differentOwner = RemoteUrlIdentity.Normalize("https://github.com/someoneelse/MixAndMatch.git");
        var differentHost = RemoteUrlIdentity.Normalize("https://gitlab.com/elevenworks/MixAndMatch.git");

        Assert.AreNotEqual(a, differentRepo, "a different repo name is a different repository");
        Assert.AreNotEqual(a, differentOwner, "a different owner is a different repository (cross-tenant isolation)");
        Assert.AreNotEqual(a, differentHost, "a different host is a different repository");
    }

    [TestMethod]
    public void Normalize_IsIdempotent()
    {
        var once = RemoteUrlIdentity.Normalize("https://github.com/Org/Repo.git/");
        var twice = RemoteUrlIdentity.Normalize(once);
        Assert.AreEqual(once, twice);
        Assert.AreEqual("https://github.com/org/repo", once);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void NullOrBlank_MapsToEmptyString(string? input)
    {
        Assert.AreEqual(string.Empty, RemoteUrlIdentity.Normalize(input));
    }

    [TestMethod]
    public void OnlyTrailingGitIsStripped_NotAnEmbeddedGit()
    {
        // ".git" is stripped only as a trailing suffix; a repo literally named "git" is preserved.
        Assert.AreEqual("https://github.com/org/git", RemoteUrlIdentity.Normalize("https://github.com/org/git"));
        Assert.AreEqual("https://github.com/org/mygit", RemoteUrlIdentity.Normalize("https://github.com/org/mygit"));
    }

    [TestMethod]
    public void KnownCaseInsensitiveHost_FoldsPathCase()
    {
        // Owner/repo on github/gitlab/bitbucket/azure is case-insensitive, so path case is folded — this is
        // the exact #92 evidence (".../MixAndMatch" and ".../mixandmatch" are the same GitHub repository).
        Assert.AreEqual(
            RemoteUrlIdentity.Normalize("https://github.com/elevenworks/mixandmatch"),
            RemoteUrlIdentity.Normalize("https://github.com/elevenworks/MixAndMatch"));
        Assert.AreEqual(
            RemoteUrlIdentity.Normalize("https://gitlab.com/group/project"),
            RemoteUrlIdentity.Normalize("https://gitlab.com/Group/Project"));
    }

    [TestMethod]
    public void UnknownHost_FoldsHostCaseButPreservesPathCase()
    {
        // A self-hosted git server may be path-case-SENSITIVE, so the path case is preserved: two repos
        // that differ only by path case must stay DISTINCT (no cross-repo/tenant merge). The host name is
        // still folded (DNS is case-insensitive), and trailing .git / slash are still stripped.
        var upper = RemoteUrlIdentity.Normalize("https://Git.Internal.Corp/team/Service.git");
        var lower = RemoteUrlIdentity.Normalize("https://git.internal.corp/team/Service/");
        Assert.AreEqual(lower, upper, "host case and trailing .git/slash are identity-neutral even on an unknown host");
        Assert.AreEqual("https://git.internal.corp/team/Service", upper, "the path case is preserved on a possibly case-sensitive host");

        var pathCaseVariant = RemoteUrlIdentity.Normalize("https://git.internal.corp/team/service");
        Assert.AreNotEqual(upper, pathCaseVariant, "two path-case-distinct repos on a case-sensitive host stay distinct");
    }

    [TestMethod]
    public void ScpLikeRemote_FoldsKnownHostPath()
    {
        // scp-like git remotes (git@host:owner/repo) have no scheme; the host is still recognized so a known
        // case-insensitive host folds the whole key while the ".git" suffix is stripped.
        Assert.AreEqual(
            RemoteUrlIdentity.Normalize("git@github.com:Elevenworks/MixAndMatch.git"),
            RemoteUrlIdentity.Normalize("git@github.com:elevenworks/mixandmatch"));
    }
}
