using System.Text.Json;
using Sextant.Core;

namespace Sextant.Core.Tests;

/// <summary>
/// Issue #245: the coverage gap a time-budgeted evaluation records when it runs out of time, and how it turns the
/// verdict partial with the reason first.
/// </summary>
[TestClass]
public class TimeBudgetCoverageBuilderTests
{
    private static readonly SnapshotCoverage Complete = new()
    {
        Verdict = SnapshotCoverageVerdict.Complete,
        SolutionsSelected = 4
    };

    [TestMethod]
    public void Apply_SomethingLeftOut_MakesThePartialReasonFirst_AndKeepsTheGap()
    {
        var earlier = Complete with { Verdict = SnapshotCoverageVerdict.Partial, Reasons = ["an earlier reason"] };
        var gap = TimeBudgetCoverageBuilder.Build(
            TimeSpan.FromMinutes(30), projectsNotLoaded: 2, projectsNotIndexed: 1, projectsNotFullyExtracted: 3,
            ["a/One.slnx", "Two.sln"]);

        var applied = TimeBudgetCoverageBuilder.Apply(earlier, gap);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, applied.Verdict);
        Assert.AreEqual(2, applied.Reasons.Count);
        Assert.AreEqual(
            "The indexing time budget (30 min) ran out before the whole checkout was indexed: 2 of 4 selected " +
            "solution(s) are unfinished (a/One.slnx, Two.sln). 2 project(s) were not loaded, 1 have no symbols, " +
            "and 3 are missing relationships, references, calls or comments.",
            applied.Reasons[0]);
        Assert.AreEqual("an earlier reason", applied.Reasons[1], "the earlier reasons are kept after it");
        Assert.AreSame(gap, applied.TimeBudget);
    }

    [TestMethod]
    public void Apply_CompleteCoverage_BecomesPartial()
    {
        var gap = TimeBudgetCoverageBuilder.Build(TimeSpan.FromSeconds(90), 0, 0, 1, []);

        var applied = TimeBudgetCoverageBuilder.Apply(Complete, gap);

        Assert.IsTrue(applied.IsPartial);
        Assert.AreEqual(
            "The indexing time budget (90 s) ran out before the whole checkout was indexed. 0 project(s) were not " +
            "loaded, 0 have no symbols, and 1 are missing relationships, references, calls or comments.",
            applied.Reasons.Single());
    }

    [TestMethod]
    public void Apply_NothingLeftOut_IsUnchanged()
    {
        var gap = TimeBudgetCoverageBuilder.Build(TimeSpan.FromMinutes(30), 0, 0, 0, ["listed.slnx"]);

        Assert.IsFalse(gap.Exhausted);
        Assert.AreSame(Complete, TimeBudgetCoverageBuilder.Apply(Complete, gap),
            "a budget that did not run out leaves the coverage (and its JSON) untouched");
    }

    [TestMethod]
    [DataRow(1, 0, 0)]
    [DataRow(0, 1, 0)]
    [DataRow(0, 0, 1)]
    [DataRow(int.MaxValue, int.MaxValue, 0)]
    public void Exhausted_AnyProjectLeftOut(int notLoaded, int notIndexed, int notExtracted) =>
        Assert.IsTrue(TimeBudgetCoverageBuilder.Build(TimeSpan.FromMinutes(1), notLoaded, notIndexed, notExtracted, []).Exhausted);

    [TestMethod]
    public void Build_CapsTheListedSolutions_ButKeepsTheirCount()
    {
        var solutions = Enumerable.Range(0, TimeBudgetCoverage.MaxSolutions + 7).Select(i => $"s{i:000}.slnx").ToList();

        var gap = TimeBudgetCoverageBuilder.Build(TimeSpan.FromMinutes(30), 1, 0, 0, solutions);

        Assert.AreEqual(TimeBudgetCoverage.MaxSolutions + 7, gap.SolutionsUnfinished);
        Assert.AreEqual(TimeBudgetCoverage.MaxSolutions, gap.UnfinishedSolutions.Count);
        CollectionAssert.AreEqual(solutions.Take(TimeBudgetCoverage.MaxSolutions).ToArray(), gap.UnfinishedSolutions.ToArray(),
            "the list keeps selection order");
        Assert.AreEqual(1800L, gap.BudgetSeconds);
    }

    [TestMethod]
    public void Describe_NamesTheFirstFive_AndCountsTheRest()
    {
        var gap = TimeBudgetCoverageBuilder.Build(
            TimeSpan.FromMinutes(30), 1, 0, 0, ["a", "b", "c", "d", "e", "f", "g"]);

        var reason = TimeBudgetCoverageBuilder.Describe(gap, solutionsSelected: 61);

        StringAssert.Contains(reason, "7 of 61 selected solution(s) are unfinished (a, b, c, d, e; +2 more)");
    }

    [TestMethod]
    public void Describe_OmitsTheTotal_WhenItIsNotKnown()
    {
        var gap = TimeBudgetCoverageBuilder.Build(TimeSpan.FromMinutes(30), 1, 0, 0, ["only.slnx"]);

        StringAssert.Contains(TimeBudgetCoverageBuilder.Describe(gap, solutionsSelected: 0),
            ": 1 selected solution(s) are unfinished (only.slnx)");
    }

    [TestMethod]
    [DataRow(1800L, "30 min")]
    [DataRow(60L, "1 min")]
    [DataRow(90L, "90 s")]
    [DataRow(0L, "0 s")]
    public void FormatBudget_WholeMinutesOrSeconds(long seconds, string expected) =>
        Assert.AreEqual(expected, TimeBudgetCoverageBuilder.FormatBudget(seconds));

    [TestMethod]
    public void Coverage_Json_IsSnakeCase_AndOmitsAnAbsentGap()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var applied = TimeBudgetCoverageBuilder.Apply(
            Complete, TimeBudgetCoverageBuilder.Build(TimeSpan.FromMinutes(30), 2, 1, 3, ["x/A.slnx"]));

        var json = JsonSerializer.Serialize(applied, options);
        StringAssert.Contains(json,
            "\"time_budget\":{\"budget_seconds\":1800,\"projects_not_loaded\":2,\"projects_not_indexed\":1," +
            "\"projects_not_fully_extracted\":3,\"solutions_unfinished\":1,\"unfinished_solutions\":[\"x/A.slnx\"]}");
        Assert.IsFalse(json.Contains("exhausted"), "the computed flag is not stored");
        var read = JsonSerializer.Deserialize<SnapshotCoverage>(json, options)!.TimeBudget!;
        Assert.AreEqual(3, read.ProjectsNotFullyExtracted);
        Assert.AreEqual("x/A.slnx", read.UnfinishedSolutions.Single());
        Assert.IsTrue(read.Exhausted);

        Assert.IsFalse(JsonSerializer.Serialize(Complete, options).Contains("time_budget"),
            "a row without a gap keeps the JSON it had before the field existed");
    }
}
