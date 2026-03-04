using System.Text.RegularExpressions;

namespace Sextant.Core.Tests;

[TestClass]
public class CanonicalIdGeneratorTests
{
    [TestMethod]
    public void SameInputs_ProduceSameId()
    {
        var id1 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/MyProject/MyProject.csproj");
        var id2 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/MyProject/MyProject.csproj");
        Assert.AreEqual(id1, id2);
    }

    [TestMethod]
    public void DifferentInputs_ProduceDifferentIds()
    {
        var id1 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/ProjectA/ProjectA.csproj");
        var id2 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/ProjectB/ProjectB.csproj");
        Assert.AreNotEqual(id1, id2);
    }

    [TestMethod]
    public void Id_Is16HexChars()
    {
        var id = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/MyProject/MyProject.csproj");
        Assert.AreEqual(16, id.Length);
        Assert.IsTrue(Regex.IsMatch(id, "^[0-9a-f]{16}$"));
    }

    [TestMethod]
    public void DifferentRepos_SamePath_ProduceDifferentIds()
    {
        var id1 = CanonicalIdGenerator.Generate("https://github.com/org/repoA", "src/Shared/Shared.csproj");
        var id2 = CanonicalIdGenerator.Generate("https://github.com/org/repoB", "src/Shared/Shared.csproj");
        Assert.AreNotEqual(id1, id2);
    }
}
