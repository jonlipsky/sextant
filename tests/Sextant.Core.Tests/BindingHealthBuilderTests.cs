using Sextant.Core;

namespace Sextant.Core.Tests;

/// <summary>
/// The binding-health threshold, merge and wording that turn per-project unbound counts into the coverage verdict.
/// </summary>
[TestClass]
public class BindingHealthBuilderTests
{
    private static readonly SnapshotCoverage Complete = new() { Verdict = SnapshotCoverageVerdict.Complete };

    private static ProjectBindingCounts Counts(string project, long examined, long unbound, string? tfm = null,
        long invocations = 0, long candidates = 0) =>
        new(project, tfm, examined, unbound, invocations, candidates);

    [TestMethod]
    [DataRow(10_000L, 24L, false, DisplayName = "below the absolute floor")]
    [DataRow(10_000L, 25L, false, DisplayName = "below the ratio")]
    [DataRow(10_000L, 50L, true, DisplayName = "at both")]
    [DataRow(100L, 25L, true, DisplayName = "small project, many unbound")]
    [DataRow(0L, 30L, true, DisplayName = "nothing examined")]
    public void IsDegraded_NeedsBothTheFloorAndTheRatio(long examined, long unbound, bool degraded) =>
        Assert.AreEqual(degraded, BindingHealthBuilder.IsDegraded(examined, unbound));

    [TestMethod]
    public void Build_SumsTotals_ListsOnlySymptomaticProjects_WorstFirst()
    {
        var health = BindingHealthBuilder.Build(
        [
            Counts("src/Clean/Clean.csproj", 1000, 0),
            Counts("src/Few/Few.csproj", 1000, 3, invocations: 1, candidates: 2),
            Counts("src/Bad/Bad.csproj", 2000, 400, invocations: 90, candidates: 180),
        ]);

        Assert.AreEqual(4000L, health.NamesExamined);
        Assert.AreEqual(403L, health.UnboundNames);
        Assert.AreEqual(91L, health.UnboundInvocations);
        Assert.AreEqual(182L, health.CandidateOccurrences);
        Assert.AreEqual(1, health.ProjectsDegraded);
        CollectionAssert.AreEqual(new[] { "src/Bad/Bad.csproj", "src/Few/Few.csproj" },
            health.Projects.Select(p => p.Project).ToArray(), "worst first; clean projects are not listed");
        Assert.IsTrue(health.Projects[0].Degraded);
        Assert.IsFalse(health.Projects[1].Degraded);
    }

    [TestMethod]
    public void Build_MergesVersionsOfOneProjectAndFramework_AndTreatsAnEmptyFrameworkAsNone()
    {
        var health = BindingHealthBuilder.Build(
        [
            Counts("src/A/A.csproj", 100, 10, tfm: ""),
            Counts("src/A/A.csproj", 100, 20, tfm: null),
            Counts("src/A/A.csproj", 100, 5, tfm: "net9.0"),
        ]);

        Assert.AreEqual(2, health.Projects.Count);
        var single = health.Projects.Single(p => p.TargetFramework is null);
        Assert.AreEqual(30L, single.UnboundNames);
        Assert.IsTrue(single.Degraded, "30 of 200 crosses the threshold once merged");
        Assert.AreEqual(5L, health.Projects.Single(p => p.TargetFramework == "net9.0").UnboundNames);
    }

    [TestMethod]
    public void Build_CapsTheListedProjects_ButCountsEveryDegradedOne()
    {
        var projects = Enumerable.Range(0, BindingHealth.MaxProjects + 10)
            .Select(i => Counts($"src/P{i:D2}/P{i:D2}.csproj", 1000, 100 + i));

        var health = BindingHealthBuilder.Build(projects);

        Assert.AreEqual(BindingHealth.MaxProjects, health.Projects.Count);
        Assert.AreEqual(BindingHealth.MaxProjects + 10, health.ProjectsDegraded);
        Assert.AreEqual("src/P34/P34.csproj", health.Projects[0].Project, "the worst project leads");
    }

    [TestMethod]
    public void Build_ListsALoadIssueForAProjectThatWasNotIndexed()
    {
        var health = BindingHealthBuilder.Build(
            [Counts("src/A/A.csproj", 100, 0)],
            new Dictionary<string, string>
            {
                ["src/A/A.csproj"] = "restore: a package source was unreachable",
                ["src/B/B.csproj"] = "restore: package(s) not found: X"
            });

        Assert.AreEqual("restore: a package source was unreachable",
            health.Projects.Single(p => p.Project == "src/A/A.csproj").LoadIssue,
            "a clean project with a load issue is still listed");
        Assert.AreEqual("restore: package(s) not found: X",
            health.Projects.Single(p => p.Project == "src/B/B.csproj").LoadIssue);
        Assert.AreEqual(0, health.ProjectsDegraded, "a load issue alone does not degrade the verdict");
    }

    [TestMethod]
    public void Apply_WithNoDegradedProject_AttachesHealthAndKeepsTheVerdict()
    {
        var health = BindingHealthBuilder.Build([Counts("src/A/A.csproj", 1000, 3)]);

        var result = BindingHealthBuilder.Apply(Complete, health);

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, result.Verdict);
        Assert.AreEqual(0, result.Reasons.Count);
        Assert.AreSame(health, result.Binding);
    }

    [TestMethod]
    public void Apply_WithDegradedProjects_MakesItPartial_AndNamesTheWorstFive()
    {
        var projects = Enumerable.Range(0, 7)
            .Select(i => Counts($"src/P{i}/P{i}.csproj", 1000, 100 - i, tfm: i == 0 ? "net10.0" : null));
        var existing = Complete with { Verdict = SnapshotCoverageVerdict.Partial, Reasons = ["Submodule 'x' is missing."] };

        var result = BindingHealthBuilder.Apply(existing, BindingHealthBuilder.Build(projects));

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Verdict);
        Assert.AreEqual("Submodule 'x' is missing.", result.Reasons[0], "existing reasons are kept first");
        Assert.AreEqual(
            "Code in 7 project(s) did not fully compile on the indexer, so references and calls inside them may be " +
            "missing (src/P0/P0.csproj (net10.0): 100 unbound name(s); src/P1/P1.csproj: 99 unbound name(s); " +
            "src/P2/P2.csproj: 98 unbound name(s); src/P3/P3.csproj: 97 unbound name(s); " +
            "src/P4/P4.csproj: 96 unbound name(s); +2 more). Calls that failed to bind are kept as candidate matches.",
            result.Reasons[1]);
    }
}
