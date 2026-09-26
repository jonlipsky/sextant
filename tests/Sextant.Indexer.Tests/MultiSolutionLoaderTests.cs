using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Proves the pure aggregation helpers of <see cref="MultiSolutionLoader"/> (issue #109) independently of
/// MSBuild: the UNION of projects across solutions is de-duplicated by project identity while preserving a
/// deterministic first-appearance order, and per-solution coverage attributes each skipped project back to
/// every solution that declared it so PARTIAL coverage is reported honestly rather than as complete.
/// </summary>
[TestClass]
public sealed class MultiSolutionLoaderTests
{
    private static string P(string name) => Path.GetFullPath(Path.Combine("repo", name));

    [TestMethod]
    public void ComputeUnion_DeduplicatesSharedProjects_PreservingFirstAppearanceOrder()
    {
        // Two heads that share Core.csproj; Core appears once, at its first-appearance position, and the
        // combined order is deterministic (solution order, then declaration order).
        var perSolution = new List<(string Solution, IReadOnlyList<string> Declared)>
        {
            ("A.slnx", new[] { P("Core.csproj"), P("A.csproj") }),
            ("B.slnx", new[] { P("Core.csproj"), P("B.csproj") })
        };

        var union = MultiSolutionLoader.ComputeUnion(perSolution);

        CollectionAssert.AreEqual(
            new[] { P("Core.csproj"), P("A.csproj"), P("B.csproj") },
            union,
            "the union de-duplicates the shared project and preserves first-appearance order");
    }

    [TestMethod]
    public void ComputeUnion_NormalizesPathsBeforeDeduplicating()
    {
        // The same project referenced with a non-normalized path (a "./" segment) must be treated as ONE
        // project — project identity is the normalized full path, not the literal string.
        var messy = Path.Combine("repo", ".", "Core.csproj");
        var perSolution = new List<(string Solution, IReadOnlyList<string> Declared)>
        {
            ("A.slnx", new[] { P("Core.csproj") }),
            ("B.slnx", new[] { messy })
        };

        var union = MultiSolutionLoader.ComputeUnion(perSolution);

        Assert.AreEqual(1, union.Count, "a differently-spelled path to the same project must de-duplicate");
    }

    [TestMethod]
    public void BuildCoverage_AttributesSkippedProjectToEveryDeclaringSolution()
    {
        // Core is shared and fails to load → both A and B report it skipped and reduced coverage; each
        // head's OWN project loads.
        var perSolution = new List<(string Solution, IReadOnlyList<string> Declared)>
        {
            ("A.slnx", new[] { P("Core.csproj"), P("A.csproj") }),
            ("B.slnx", new[] { P("Core.csproj"), P("B.csproj") })
        };
        var skipped = new[] { new SkippedProject(P("Core.csproj"), "platform head will not load on Linux") };

        var coverage = MultiSolutionLoader.BuildCoverage(perSolution, skipped);

        Assert.AreEqual(2, coverage.Count);

        var a = coverage.Single(c => c.SolutionPath == "A.slnx");
        Assert.AreEqual(2, a.DeclaredProjectCount);
        Assert.AreEqual(1, a.LoadedProjectCount, "A loaded only its own project");
        Assert.AreEqual(1, a.SkippedProjects.Count);
        Assert.AreEqual(P("Core.csproj"), Path.GetFullPath(a.SkippedProjects[0].ProjectPath));

        var b = coverage.Single(c => c.SolutionPath == "B.slnx");
        Assert.AreEqual(1, b.LoadedProjectCount, "B loaded only its own project");
        Assert.AreEqual(1, b.SkippedProjects.Count, "the shared skipped project is attributed to B as well");
    }

    [TestMethod]
    public void BuildCoverage_NoSkips_ReportsFullyLoaded()
    {
        var perSolution = new List<(string Solution, IReadOnlyList<string> Declared)>
        {
            ("A.slnx", new[] { P("Core.csproj"), P("A.csproj") })
        };

        var coverage = MultiSolutionLoader.BuildCoverage(perSolution, []);

        Assert.AreEqual(1, coverage.Count);
        Assert.AreEqual(2, coverage[0].DeclaredProjectCount);
        Assert.AreEqual(2, coverage[0].LoadedProjectCount);
        Assert.AreEqual(0, coverage[0].SkippedProjects.Count);
    }

    [TestMethod]
    public void BuildCoverage_CountsDistinctDeclaredProjects()
    {
        // A solution that declares the same project twice counts it once (declared distinct by identity).
        var perSolution = new List<(string Solution, IReadOnlyList<string> Declared)>
        {
            ("A.slnx", new[] { P("Core.csproj"), P("Core.csproj") })
        };

        var coverage = MultiSolutionLoader.BuildCoverage(perSolution, []);

        Assert.AreEqual(1, coverage[0].DeclaredProjectCount, "a duplicate declaration counts once");
        Assert.AreEqual(1, coverage[0].LoadedProjectCount);
    }
}
