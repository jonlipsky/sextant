using Sextant.Core;

namespace Sextant.Core.Tests;

[TestClass]
public class GitRootResolutionTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant-git-root-{Guid.NewGuid():N}");
        foreach (var repo in new[] { "p", "p/external/lib", "q" })
            Directory.CreateDirectory(Path.Combine(_root, repo, ".git"));
    }

    [TestCleanup]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private string File(string relative) => Path.Combine(_root, relative);

    [TestMethod]
    public void TheFirstPathInASubmodule_ResolvesToTheSuperproject()
        => Assert.AreEqual(File("p"), GitRemoteResolver.ResolveOutermostGitRoot([File("p/external/lib/X.csproj"), File("p/src/App.csproj")]));

    [TestMethod]
    public void AnUnrelatedRoot_DoesNotBringBackTheSubmodule()
        => Assert.AreEqual(File("p"), GitRemoteResolver.ResolveOutermostGitRoot(
            [File("p/external/lib/X.csproj"), File("p/src/App.csproj"), File("q/Y.csproj")]));

    [TestMethod]
    public void UnrelatedRootsOnly_KeepTheFirst()
        => Assert.AreEqual(File("q"), GitRemoteResolver.ResolveOutermostGitRoot([File("q/Y.csproj"), File("p/src/App.csproj")]));

    [TestMethod]
    public void ASiblingWithTheSamePrefix_IsNotAnEnclosingRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "p-other", ".git"));
        Assert.AreEqual(File("p-other"), GitRemoteResolver.ResolveOutermostGitRoot([File("p-other/Z.csproj"), File("p/src/App.csproj")]));
    }

    [TestMethod]
    public void NoGitRoot_IsNull()
        => Assert.IsNull(GitRemoteResolver.ResolveOutermostGitRoot([Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}", "X.csproj")]));
}
