using Sextant.Core;

namespace Sextant.ProcessStack.Activities.Tests;

[TestClass]
public sealed class GitRefsAndRepositoryReferenceTests
{
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";

    [TestMethod]
    [DataRow(Sha1, true)]
    [DataRow("0123456789ABCDEF0123456789ABCDEF01234567", true)]
    [DataRow("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [DataRow("0000000000000000000000000000000000000000", true)]
    [DataRow("0123456789abcdef0123456789abcdef0123456", false)]
    [DataRow("0123456789abcdef0123456789abcdef012345678", false)]
    [DataRow("g123456789abcdef0123456789abcdef01234567", false)]
    [DataRow("", false)]
    public void IsCommitSha_accepts_full_sha1_and_sha256_hex(string value, bool expected)
    {
        Assert.AreEqual(expected, GitRefs.IsCommitSha(value));
    }

    [TestMethod]
    [DataRow("0000000000000000000000000000000000000000", true)]
    [DataRow("0", true)]
    [DataRow("", false)]
    [DataRow(Sha1, false)]
    public void IsZeroSha_is_a_non_empty_run_of_zeros(string value, bool expected)
    {
        Assert.AreEqual(expected, GitRefs.IsZeroSha(value));
    }

    [TestMethod]
    [DataRow("main", true)]
    [DataRow("feature/x", true)]
    [DataRow("release/1.2.3", true)]
    [DataRow("user@work", true)]
    [DataRow("", false)]
    [DataRow("@", false)]
    [DataRow("HEAD", false)]
    [DataRow("-bad", false)]
    [DataRow("/bad", false)]
    [DataRow("bad/", false)]
    [DataRow("bad.", false)]
    [DataRow("a..b", false)]
    [DataRow("a//b", false)]
    [DataRow("a@{b", false)]
    [DataRow("a b", false)]
    [DataRow("a~b", false)]
    [DataRow("a^b", false)]
    [DataRow("a:b", false)]
    [DataRow("a?b", false)]
    [DataRow("a*b", false)]
    [DataRow("a[b", false)]
    [DataRow("a\\b", false)]
    [DataRow("a\u0001b", false)]
    [DataRow("a\u007fb", false)]
    [DataRow(".hidden", false)]
    [DataRow("x/.hidden", false)]
    [DataRow("x.lock", false)]
    [DataRow("x.lock/y", false)]
    public void IsValidBranchName_follows_git_check_ref_format(string name, bool expected)
    {
        Assert.AreEqual(expected, GitRefs.IsValidBranchName(name));
    }

    [TestMethod]
    public void IsValidBranchName_caps_the_length()
    {
        Assert.IsTrue(GitRefs.IsValidBranchName(new string('b', 255)));
        Assert.IsFalse(GitRefs.IsValidBranchName(new string('b', 256)));
    }

    [TestMethod]
    [DataRow("refs/heads/main", "main")]
    [DataRow("main", "main")]
    [DataRow("refs/tags/v1", "refs/tags/v1")]
    public void StripHeadsPrefix_removes_only_refs_heads(string value, string expected)
    {
        Assert.AreEqual(expected, GitRefs.StripHeadsPrefix(value));
    }

    [TestMethod]
    [DataRow("https://github.com/Octo/Repo.git", "https://github.com/octo/repo")]
    [DataRow("https://github.com/octo/repo/", "https://github.com/octo/repo")]
    [DataRow("  https://github.com/octo/repo  ", "https://github.com/octo/repo")]
    [DataRow("git@github.com:Octo/Repo.git", "https://github.com/octo/repo")]
    [DataRow("ssh://git@github.com/octo/repo.git", "https://github.com/octo/repo")]
    [DataRow("", "")]
    public void KeyFor_folds_every_spelling_of_one_repository(string url, string expected)
    {
        Assert.AreEqual(expected, RepositoryReference.KeyFor(url));
    }

    [TestMethod]
    public void KeyFor_falls_back_to_the_core_normalizer_and_drops_credentials()
    {
        var key = RepositoryReference.KeyFor("https://user:secret@github.com/octo/repo.git");

        Assert.AreEqual(RemoteUrlIdentity.Normalize(GitRemoteNormalizer.Normalize("https://user:secret@github.com/octo/repo.git")), key);
        Assert.DoesNotContain("secret", key);
    }

    [TestMethod]
    [DataRow("https://github.com/Octo/Repo", "https://github.com/Octo/Repo")]
    [DataRow("http://github.com/octo/repo", "http://github.com/octo/repo")]
    [DataRow("git@github.com:Octo/Repo.git", "https://github.com/Octo/Repo.git")]
    [DataRow("git@github.com:Octo/Repo", "https://github.com/Octo/Repo.git")]
    [DataRow("octo/repo", "octo/repo")]
    public void ToCloneUrl_keeps_http_urls_verbatim_and_converts_ssh(string url, string expected)
    {
        Assert.AreEqual(expected, RepositoryReference.ToCloneUrl(url));
    }

    [TestMethod]
    [DataRow("octo/repo", "https://github.com/octo/repo")]
    [DataRow("Octo/Repo.git", "https://github.com/octo/repo")]
    [DataRow("GitHub.com/octo/repo", "https://github.com/octo/repo")]
    [DataRow("ghe.example.com/octo/repo", "https://ghe.example.com/octo/repo")]
    [DataRow("https://github.com/octo/repo", "https://github.com/octo/repo")]
    [DataRow("git@github.com:octo/repo.git", "https://github.com/octo/repo")]
    public void Parse_accepts_urls_scp_remotes_and_shorthand(string reference, string expectedKey)
    {
        var verdict = RepositoryReference.Parse(reference, RepositoryReference.DefaultHost);

        Assert.IsTrue(verdict.Ok, reference);
        Assert.AreEqual(expectedKey, verdict.Canonical);
    }

    [TestMethod]
    [DataRow("repo", RepositoryUrlShape.PathNotAllowed)]
    [DataRow("a/b/c", RepositoryUrlShape.PathNotAllowed)]
    [DataRow("a/b/c/d", RepositoryUrlShape.PathNotAllowed)]
    [DataRow("-octo/repo", RepositoryUrlShape.PathNotAllowed)]
    [DataRow("http://github.com/octo/repo", RepositoryUrlShape.SchemeNotAllowed)]
    [DataRow("localhost.localhost/octo/repo", RepositoryUrlShape.HostNotAllowed)]
    public void Parse_refuses_other_shapes(string reference, string reason)
    {
        var verdict = RepositoryReference.Parse(reference, RepositoryReference.DefaultHost);

        Assert.IsFalse(verdict.Ok, reference);
        Assert.AreEqual(reason, verdict.Reason);
    }
}
