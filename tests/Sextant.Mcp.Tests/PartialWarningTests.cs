using Sextant.Core;

namespace Sextant.Mcp.Tests;

/// <summary>
/// The lean <c>meta.snapshot.warning</c> of a partial snapshot COUNTS what is missing, from the coverage the worker
/// recorded, so an agent can judge whether its question is affected instead of distrusting every answer. It names no
/// project, path or tool and stays a one-line caution (at most <see cref="RemoteResponsePresenter.MaxPartialWarningChars"/>
/// characters) on every result, however large the checkout.
/// </summary>
[TestClass]
public class PartialWarningTests
{
    private static SnapshotCoverage Partial(Func<SnapshotCoverage, SnapshotCoverage> shape) =>
        shape(new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            Reasons = ["Submodule 'libs/shared' is declared in .gitmodules but is not populated (src/Api/Api.csproj)."]
        });

    [TestMethod]
    public void CountsProjectsThatDidNotLoad_OutOfTheDeclaredProjects()
    {
        var coverage = Partial(c => c with { ProjectsDeclared = 12, ProjectsLoaded = 10, ProjectsSkipped = 2 });

        Assert.AreEqual("Partial index: 2 of 12 projects did not load, so results may be incomplete.",
            RemoteResponsePresenter.PartialWarningFor(coverage));
    }

    [TestMethod]
    public void CountsProjectsThatDidNotCompile_AndBoth()
    {
        var degraded = Partial(c => c with
        {
            ProjectsDeclared = 9, ProjectsLoaded = 9, Binding = new BindingHealth { ProjectsDegraded = 1 }
        });
        var both = Partial(c => c with
        {
            ProjectsDeclared = 9, ProjectsLoaded = 8, ProjectsSkipped = 1, Binding = new BindingHealth { ProjectsDegraded = 2 }
        });

        Assert.AreEqual("Partial index: 1 of 9 projects did not compile, so results may be incomplete.",
            RemoteResponsePresenter.PartialWarningFor(degraded));
        Assert.AreEqual("Partial index: 3 of 9 projects did not load or compile, so results may be incomplete.",
            RemoteResponsePresenter.PartialWarningFor(both));
    }

    [TestMethod]
    public void OmitsTheTotal_WhenTheRecordHasNone()
    {
        var one = Partial(c => c with { Binding = new BindingHealth { ProjectsDegraded = 1 } });
        var two = Partial(c => c with { Binding = new BindingHealth { ProjectsDegraded = 2 } });

        Assert.AreEqual("Partial index: 1 project did not compile, so results may be incomplete.",
            RemoteResponsePresenter.PartialWarningFor(one));
        Assert.AreEqual("Partial index: 2 projects did not compile, so results may be incomplete.",
            RemoteResponsePresenter.PartialWarningFor(two));
    }

    [TestMethod]
    public void CountsSubmodulesSolutionsAndScanErrors()
    {
        var submodulesAndScans = Partial(c => c with { SubmodulesDeclared = 3, SubmodulesUnpopulated = 1, ScanErrors = 2 });
        var solution = Partial(c => c with { SolutionsSkipped = 1 });

        Assert.AreEqual(
            "Partial index: 1 of 3 submodules were not checked out, 2 parts of the checkout could not be scanned, " +
            "so results may be incomplete.",
            RemoteResponsePresenter.PartialWarningFor(submodulesAndScans));
        Assert.AreEqual("Partial index: 1 configured solution could not be used, so results may be incomplete.",
            RemoteResponsePresenter.PartialWarningFor(solution));
    }

    [TestMethod]
    public void FoldsTheGapsThatDoNotFit_IntoOtherGaps()
    {
        var coverage = Partial(c => c with
        {
            SubmodulesDeclared = 3, SubmodulesUnpopulated = 1, SolutionsSkipped = 1, ScanErrors = 2
        });

        Assert.AreEqual(
            "Partial index: 1 of 3 submodules were not checked out, 1 configured solution could not be used, " +
            "and other gaps, so results may be incomplete.",
            RemoteResponsePresenter.PartialWarningFor(coverage));
    }

    [TestMethod]
    public void NeverQuotesAReason_OrNamesAProjectOrTool()
    {
        var coverage = Partial(c => c with
        {
            ProjectsDeclared = 4, ProjectsLoaded = 3, ProjectsSkipped = 1,
            Binding = new BindingHealth
            {
                ProjectsDegraded = 1,
                Projects = [new ProjectBindingHealth { Project = "src/Api/Api.csproj", Degraded = true }]
            }
        });

        var warning = RemoteResponsePresenter.PartialWarningFor(coverage);

        foreach (var leaked in new[] { "Api.csproj", "libs/shared", "gitmodules", "get_index_status", "Call " })
            Assert.IsFalse(warning.Contains(leaked, StringComparison.Ordinal), $"'{leaked}' in: {warning}");
    }

    [TestMethod]
    public void StaysWithinTheCap_AtTheLargestCounts_FoldingWhatDoesNotFit()
    {
        var coverage = Partial(c => c with
        {
            ProjectsDeclared = int.MaxValue, ProjectsLoaded = 0, ProjectsSkipped = int.MaxValue,
            Binding = new BindingHealth { ProjectsDegraded = int.MaxValue },
            SubmodulesDeclared = int.MaxValue, SubmodulesUnpopulated = int.MaxValue,
            SolutionsSkipped = int.MaxValue, ScanErrors = int.MaxValue
        });

        var warning = RemoteResponsePresenter.PartialWarningFor(coverage);

        Assert.IsTrue(warning.Length <= RemoteResponsePresenter.MaxPartialWarningChars, $"{warning.Length}: {warning}");
        StringAssert.StartsWith(warning, RemoteResponsePresenter.PartialWarningPrefix + "4294967294 projects did not load or compile");
        StringAssert.Contains(warning, ", and other gaps");
        StringAssert.EndsWith(warning, RemoteResponsePresenter.PartialWarningSuffix);
    }

    [TestMethod]
    public void EveryCombinationOfGaps_StaysWithinTheCap()
    {
        int[] counts = [0, 1, 7, 12_345, int.MaxValue];
        foreach (var projects in counts)
        foreach (var degraded in counts)
        foreach (var submodules in counts)
        foreach (var solutions in counts)
        foreach (var scans in counts)
        {
            var coverage = Partial(c => c with
            {
                ProjectsDeclared = projects, ProjectsSkipped = projects,
                Binding = new BindingHealth { ProjectsDegraded = degraded },
                SubmodulesDeclared = submodules, SubmodulesUnpopulated = submodules,
                SolutionsSkipped = solutions, ScanErrors = scans
            });
            var warning = RemoteResponsePresenter.PartialWarningFor(coverage);
            Assert.IsTrue(warning.Length <= RemoteResponsePresenter.MaxPartialWarningChars, $"{warning.Length}: {warning}");
            StringAssert.StartsWith(warning, RemoteResponsePresenter.PartialWarningPrefix);
        }
    }

    [TestMethod]
    public void FallsBackToTheGenericWarning_WhenNoGapIsCounted()
    {
        Assert.AreEqual(RemoteResponsePresenter.PartialWarning, RemoteResponsePresenter.PartialWarningFor(null));
        Assert.AreEqual(RemoteResponsePresenter.PartialWarning, RemoteResponsePresenter.PartialWarningFor(Partial(c => c)));
        Assert.IsTrue(RemoteResponsePresenter.PartialWarning.Length <= RemoteResponsePresenter.MaxPartialWarningChars);
    }
}