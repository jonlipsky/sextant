using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Unit tests for the pure invalidation-closure logic (Phase 4, acceptance criterion 6). These need
/// no Roslyn workspace: they prove that expanding a changed project to its undirected connected
/// component pulls in exactly the projects that could own a cross-project edge, and no more.
/// </summary>
[TestClass]
public class ProjectClosureTests
{
    private static Dictionary<string, IReadOnlyCollection<string>> Adjacency(
        params (string consumer, string dependency)[] edges) =>
        ProjectClosure.BuildUndirectedAdjacency(edges, StringComparer.Ordinal);

    [TestMethod]
    public void Expand_ChangedDependency_PullsInConsumers()
    {
        // App -> Lib. Changing Lib must invalidate App (a consumer may hold references into Lib).
        var adjacency = Adjacency(("App", "Lib"));
        var closure = ProjectClosure.Expand(["Lib"], adjacency, StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(new[] { "Lib", "App" }, closure.ToList());
    }

    [TestMethod]
    public void Expand_ChangedConsumer_PullsInDependency()
    {
        // App -> Lib. Changing App must invalidate Lib too (App's edges point into Lib).
        var adjacency = Adjacency(("App", "Lib"));
        var closure = ProjectClosure.Expand(["App"], adjacency, StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(new[] { "App", "Lib" }, closure.ToList());
    }

    [TestMethod]
    public void Expand_IndependentProject_IsNotInClosure()
    {
        // App -> Lib; Unrelated is disconnected. Changing Lib must NOT reprocess Unrelated.
        var adjacency = Adjacency(("App", "Lib"));
        var closure = ProjectClosure.Expand(["Lib"], adjacency, StringComparer.Ordinal);

        Assert.IsFalse(closure.Contains("Unrelated"), "a disconnected project must never enter the closure.");
    }

    [TestMethod]
    public void Expand_TransitiveChain_ReachesEveryConnectedNode()
    {
        // A -> B -> C. Changing C reaches B and A through the undirected chain.
        var adjacency = Adjacency(("A", "B"), ("B", "C"));
        var closure = ProjectClosure.Expand(["C"], adjacency, StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(new[] { "A", "B", "C" }, closure.ToList());
    }

    [TestMethod]
    public void Expand_SeparateComponents_StayIsolated()
    {
        // Two disjoint components. A change in one never reaches the other.
        var adjacency = Adjacency(("A", "B"), ("C", "D"));
        var closure = ProjectClosure.Expand(["A"], adjacency, StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(new[] { "A", "B" }, closure.ToList());
    }

    [TestMethod]
    public void Expand_MultipleSeeds_UnionsTheirComponents()
    {
        var adjacency = Adjacency(("A", "B"), ("C", "D"));
        var closure = ProjectClosure.Expand(["A", "C"], adjacency, StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(new[] { "A", "B", "C", "D" }, closure.ToList());
    }

    [TestMethod]
    public void Expand_SeedWithNoEdges_ReturnsJustTheSeed()
    {
        // An isolated project (e.g. an independent multi-target project) with no references is its
        // own closure — an edit to it reprocesses only itself.
        var adjacency = Adjacency(("A", "B"));
        var closure = ProjectClosure.Expand(["Solo"], adjacency, StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(new[] { "Solo" }, closure.ToList());
    }

    [TestMethod]
    public void BuildUndirectedAdjacency_IgnoresSelfEdges()
    {
        var adjacency = ProjectClosure.BuildUndirectedAdjacency(
            new[] { ("A", "A") }, StringComparer.Ordinal);

        Assert.IsFalse(adjacency.TryGetValue("A", out var neighbors) && neighbors.Count > 0,
            "a self-edge must not create an adjacency entry.");
    }

    [TestMethod]
    public void BuildUndirectedAdjacency_MaterialisesBothDirections()
    {
        var adjacency = ProjectClosure.BuildUndirectedAdjacency(
            new[] { ("App", "Lib") }, StringComparer.Ordinal);

        CollectionAssert.Contains(adjacency["Lib"].ToList(), "App");
        CollectionAssert.Contains(adjacency["App"].ToList(), "Lib");
    }

    [TestMethod]
    public void Expand_DiamondGraph_ReachesAllFourViaEitherPath()
    {
        // Top -> Left, Top -> Right, Left -> Bottom, Right -> Bottom. A change to Bottom reaches all.
        var adjacency = Adjacency(
            ("Top", "Left"), ("Top", "Right"), ("Left", "Bottom"), ("Right", "Bottom"));
        var closure = ProjectClosure.Expand(["Bottom"], adjacency, StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(new[] { "Top", "Left", "Right", "Bottom" }, closure.ToList());
    }
}
