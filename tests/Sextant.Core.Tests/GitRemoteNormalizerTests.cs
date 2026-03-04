namespace Sextant.Core.Tests;

[TestClass]
public class GitRemoteNormalizerTests
{
    [DataTestMethod]
    [DataRow("git@github.com:org/repo", "https://github.com/org/repo")]
    [DataRow("git@github.com:org/repo.git", "https://github.com/org/repo")]
    [DataRow("ssh://git@github.com/org/repo.git", "https://github.com/org/repo")]
    public void SshUrls_ConvertedToHttps(string input, string expected)
    {
        Assert.AreEqual(expected, GitRemoteNormalizer.Normalize(input));
    }

    [DataTestMethod]
    [DataRow("https://github.com/org/repo.git", "https://github.com/org/repo")]
    [DataRow("https://github.com/org/repo.GIT", "https://github.com/org/repo")]
    public void GitSuffix_Stripped(string input, string expected)
    {
        Assert.AreEqual(expected, GitRemoteNormalizer.Normalize(input));
    }

    [DataTestMethod]
    [DataRow("https://user:pass@github.com/org/repo", "https://github.com/org/repo")]
    [DataRow("https://token@github.com/org/repo", "https://github.com/org/repo")]
    public void Credentials_Stripped(string input, string expected)
    {
        Assert.AreEqual(expected, GitRemoteNormalizer.Normalize(input));
    }

    [DataTestMethod]
    [DataRow("https://GITHUB.COM/org/MyRepo", "https://github.com/org/MyRepo")]
    [DataRow("https://GitHub.Com/Org/Repo", "https://github.com/Org/Repo")]
    public void HostLowercased_PathCasingPreserved(string input, string expected)
    {
        Assert.AreEqual(expected, GitRemoteNormalizer.Normalize(input));
    }

    [TestMethod]
    public void AlreadyNormalized_ReturnedUnchanged()
    {
        const string url = "https://github.com/org/repo";
        Assert.AreEqual(url, GitRemoteNormalizer.Normalize(url));
    }
}
