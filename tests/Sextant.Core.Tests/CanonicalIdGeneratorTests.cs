using System.Text.RegularExpressions;

namespace Sextant.Core.Tests;

[TestClass]
public class CanonicalIdGeneratorTests
{
    [TestMethod]
    public void SameInputs_ProduceSameId()
    {
        var id1 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/MyProject/MyProject.csproj", "net10.0");
        var id2 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/MyProject/MyProject.csproj", "net10.0");
        Assert.AreEqual(id1, id2);
    }

    [TestMethod]
    public void DifferentInputs_ProduceDifferentIds()
    {
        var id1 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/ProjectA/ProjectA.csproj", "net10.0");
        var id2 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/ProjectB/ProjectB.csproj", "net10.0");
        Assert.AreNotEqual(id1, id2);
    }

    [TestMethod]
    public void Id_Is16HexChars()
    {
        var id = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/MyProject/MyProject.csproj", "net10.0");
        Assert.AreEqual(16, id.Length);
        Assert.IsTrue(Regex.IsMatch(id, "^[0-9a-f]{16}$"));
    }

    [TestMethod]
    public void DifferentRepos_SamePath_ProduceDifferentIds()
    {
        var id1 = CanonicalIdGenerator.Generate("https://github.com/org/repoA", "src/Shared/Shared.csproj", "net10.0");
        var id2 = CanonicalIdGenerator.Generate("https://github.com/org/repoB", "src/Shared/Shared.csproj", "net10.0");
        Assert.AreNotEqual(id1, id2);
    }

    [TestMethod]
    public void DifferentTargetFrameworks_SameProject_ProduceDifferentIds()
    {
        // Each evaluated target framework of a multi-targeted project is a distinct logical project.
        var id1 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/Shared/Shared.csproj", "net10.0");
        var id2 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/Shared/Shared.csproj", "netstandard2.0");
        Assert.AreNotEqual(id1, id2, "Different target frameworks must produce different logical project ids.");
    }

    [TestMethod]
    public void EmptyTargetFramework_IsDeterministic()
    {
        // A project with no determinable framework must still hash deterministically (never null-keyed).
        var id1 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/Legacy/Legacy.csproj", "");
        var id2 = CanonicalIdGenerator.Generate("https://github.com/org/repo", "src/Legacy/Legacy.csproj", "");
        Assert.AreEqual(id1, id2);
        Assert.AreEqual(16, id1.Length);
    }
}
