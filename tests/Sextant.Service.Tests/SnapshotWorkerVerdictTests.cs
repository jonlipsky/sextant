using Microsoft.CodeAnalysis;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Hermetic tests for the worker's terminal-verdict decision (issue #109). The composed guarantee — a
/// coverage gap is NEVER reported as Complete — is what these lock down WITHOUT a real MSBuild toolchain,
/// a checkout, or a database: <see cref="LocalIndexerSnapshotWorker.BuildResult"/> and
/// <see cref="LocalIndexerSnapshotWorker.BuildConfigErrorDiagnostics"/> are driven directly over synthetic
/// load/resolution inputs (the end-to-end <c>MultiSolutionIndexingIntegrationTests</c> exercise the same
/// mapping over a real fixture, but they are env-gated; these run on every <c>dotnet test</c>).
/// </summary>
[TestClass]
public class SnapshotWorkerVerdictTests
{
    private const string CheckoutDir = "/checkout/repo";

    // An empty Roslyn solution is enough: BuildResult reads only the per-solution coverage + skipped sets,
    // never the Solution graph itself (that was consumed during indexing, before the verdict is built).
    private static Solution EmptySolution() => new AdhocWorkspace().CurrentSolution;

    private static CheckoutResolution Resolution(
        SolutionSelectionSource source = SolutionSelectionSource.DefaultRoot,
        IReadOnlyList<SkippedSolution>? skippedSolutions = null,
        IReadOnlyList<string>? discoveredButNotSelected = null,
        string? configurationError = null) =>
        new()
        {
            CheckoutDir = CheckoutDir,
            SelectedSolutions = configurationError is null ? new[] { $"{CheckoutDir}/App.slnx" } : [],
            Source = source,
            SkippedSolutions = skippedSolutions ?? [],
            DiscoveredButNotSelected = discoveredButNotSelected ?? [],
            ConfigurationError = configurationError
        };

    private static MultiSolutionLoadResult Load(
        IReadOnlyList<SolutionCoverage> solutions, IReadOnlyList<SkippedProject>? skippedProjects = null) =>
        new(EmptySolution(), skippedProjects ?? [], solutions);

    [TestMethod]
    public void BuildResult_FullyCoveredSelection_IsComplete()
    {
        var load = Load([new SolutionCoverage($"{CheckoutDir}/App.slnx", DeclaredProjectCount: 2, LoadedProjectCount: 2, [])]);

        var result = LocalIndexerSnapshotWorker.BuildResult(42, CheckoutDir, Resolution(), load);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(42, result.SnapshotId);
        Assert.IsTrue(result.Projects.Any(p => p.Code == "solution_indexed" && p.Severity == JobDiagnosticSeverity.Info),
            "a fully-covered solution should record a solution_indexed info diagnostic");
    }

    [TestMethod]
    public void BuildResult_SkippedProject_IsPartialNotComplete()
    {
        var skipped = new[] { new SkippedProject($"{CheckoutDir}/src/Ios/Ios.csproj", "iOS workload not available on this worker") };
        var load = Load(
            [new SolutionCoverage($"{CheckoutDir}/App.slnx", DeclaredProjectCount: 2, LoadedProjectCount: 1, skipped)],
            skipped);

        var result = LocalIndexerSnapshotWorker.BuildResult(7, CheckoutDir, Resolution(), load);

        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status,
            "a declared project skipped-with-reason must be reported Partial, never Complete");
        Assert.AreEqual(7, result.SnapshotId, "Partial still carries the published (complete) snapshot id");
        Assert.IsTrue(result.Projects.Any(p => p.Code == "project_skipped" && p.Severity == JobDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void BuildResult_SkippedConfiguredSolution_IsPartial()
    {
        var resolution = Resolution(
            source: SolutionSelectionSource.Configured,
            skippedSolutions: [new SkippedSolution("Missing.slnx", "configured solution not found in checkout")]);
        var load = Load([new SolutionCoverage($"{CheckoutDir}/App.slnx", DeclaredProjectCount: 1, LoadedProjectCount: 1, [])]);

        var result = LocalIndexerSnapshotWorker.BuildResult(9, CheckoutDir, resolution, load);

        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status,
            "a configured solution that could not be selected is a coverage gap ⇒ Partial");
        Assert.IsTrue(result.Projects.Any(p => p.Code == "solution_skipped" && p.Severity == JobDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void BuildResult_SelectedSolutionDeclaredZeroProjects_IsPartial()
    {
        // A selected solution that statically enumerated to zero recognized projects (unreadable / empty /
        // unrecognized format on this worker) must force Partial — a silent 0/0 must never read as success.
        var load = Load([new SolutionCoverage($"{CheckoutDir}/Broken.slnx", DeclaredProjectCount: 0, LoadedProjectCount: 0, [])]);

        var result = LocalIndexerSnapshotWorker.BuildResult(3, CheckoutDir, Resolution(), load);

        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status);
        Assert.IsTrue(result.Projects.Any(p => p.Code == "solution_no_projects" && p.Severity == JobDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void BuildResult_DefaultRootLeavesOtherSolutions_IsCompleteWithTransparencyInfo()
    {
        // A deterministic default-root pick that leaves other DISCOVERED solutions unindexed is the specified
        // behavior, not a gap: it is Complete for what it covered, with an info diagnostic naming the rest.
        var resolution = Resolution(
            source: SolutionSelectionSource.DefaultRoot,
            discoveredButNotSelected: [$"{CheckoutDir}/heads/Ios.slnx", $"{CheckoutDir}/heads/Android.slnx"]);
        var load = Load([new SolutionCoverage($"{CheckoutDir}/App.slnx", DeclaredProjectCount: 3, LoadedProjectCount: 3, [])]);

        var result = LocalIndexerSnapshotWorker.BuildResult(11, CheckoutDir, resolution, load);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status,
            "leaving other discovered solutions unselected is the deterministic default, not partial coverage");
        Assert.IsTrue(result.Projects.Any(p => p.Code == "solutions_not_selected" && p.Severity == JobDiagnosticSeverity.Info));
    }

    [TestMethod]
    public void BuildConfigErrorDiagnostics_CarriesErrorAndPerSkippedSolutionWarning()
    {
        var resolution = Resolution(
            source: SolutionSelectionSource.Configured,
            skippedSolutions: [new SkippedSolution("Typo.slnx", "configured solution not found in checkout")],
            configurationError: "sextant.json is not valid JSON");

        var diagnostics = LocalIndexerSnapshotWorker.BuildConfigErrorDiagnostics(CheckoutDir, resolution);

        var error = diagnostics.Single(d => d.Severity == JobDiagnosticSeverity.Error);
        Assert.AreEqual("solution_config_invalid", error.Code);
        Assert.AreEqual("sextant.json is not valid JSON", error.Message);
        Assert.IsTrue(diagnostics.Any(d => d.Code == "solution_skipped" && d.Severity == JobDiagnosticSeverity.Warning),
            "each unusable configured solution is recorded so the failed job explains WHY coverage could not be honored");
    }
}
