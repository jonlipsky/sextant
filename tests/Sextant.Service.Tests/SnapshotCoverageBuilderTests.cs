using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #119 — the pure coverage verdict over (selection, load, file-system inventory). A snapshot that
/// does not cover the whole checkout must carry a PARTIAL verdict with a reason and per-gap diagnostics;
/// an operator's explicit `solutions` scope is honored as deliberate.
/// </summary>
[TestClass]
public class SnapshotCoverageBuilderTests
{
    private static readonly string CheckoutDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sextant_cov_checkout"));

    private static string At(string relative) => Path.Combine(CheckoutDir, relative);

    private static CheckoutResolution Resolution(
        SolutionSelectionSource source = SolutionSelectionSource.DefaultRoot,
        IReadOnlyList<string>? notSelected = null) => new()
        {
            CheckoutDir = CheckoutDir,
            SelectedSolutions = [At("App.slnx")],
            Source = source,
            SkippedSolutions = [],
            DiscoveredButNotSelected = notSelected ?? []
        };

    private static MultiSolutionLoadResult Load(
        IReadOnlyList<string> declared, Solution? solution = null) =>
        new(solution ?? new AdhocWorkspace().CurrentSolution, [],
            [new SolutionCoverage(At("App.slnx"), declared.Count, declared.Count, [])])
        {
            DeclaredProjects = declared
        };

    [TestMethod]
    public void UnreferencedProjectFile_UnderDefaultSelection_IsPartial()
    {
        var load = Load([At("src/App/App.csproj")]);
        var inventory = new SnapshotCoverageBuilder.Inventory(
            [At("src/App/App.csproj"), At("tools/Orphan/Orphan.csproj")], []);

        var result = SnapshotCoverageBuilder.Build(CheckoutDir, Resolution(), load, inventory);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict);
        Assert.AreEqual(1, result.Coverage.ProjectFilesUnreferenced);
        Assert.AreEqual(2, result.Coverage.ProjectFilesOnDisk);
        var diag = result.Diagnostics.Single(d => d.Code == "project_file_unreferenced");
        Assert.AreEqual(JobDiagnosticSeverity.Warning, diag.Severity);
        Assert.AreEqual("tools/Orphan/Orphan.csproj", diag.ProjectPath, "diagnostics are checkout-relative");
    }

    [TestMethod]
    public void UnreferencedProjectFile_UnderConfiguredSelection_IsInfoAndStaysComplete()
    {
        var load = Load([At("src/App/App.csproj")]);
        var inventory = new SnapshotCoverageBuilder.Inventory(
            [At("src/App/App.csproj"), At("tools/Orphan/Orphan.csproj")], []);

        var result = SnapshotCoverageBuilder.Build(
            CheckoutDir, Resolution(SolutionSelectionSource.Configured), load, inventory);

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, result.Coverage.Verdict,
            "an explicit `solutions` list is a deliberate scope — files outside it are not a coverage gap");
        Assert.AreEqual(1, result.Coverage.ProjectFilesUnreferenced, "still counted for visibility");
        Assert.AreEqual(JobDiagnosticSeverity.Info,
            result.Diagnostics.Single(d => d.Code == "project_file_unreferenced").Severity);
        Assert.AreEqual("configured", result.Coverage.SelectionSource);
    }

    [TestMethod]
    public void ProjectPulledInByTheLoad_IsNotUnreferenced_AndMultiTargetCountsOnce()
    {
        // Lib is reached through a ProjectReference (in the loaded workspace, not declared by App.slnx) and is
        // multi-targeted, so the workspace holds two Roslyn projects for one file.
        var workspace = new AdhocWorkspace();
        var app = At("src/App/App.csproj");
        var lib = At("src/Lib/Lib.csproj");
        AddProject(workspace, "App", app);
        AddProject(workspace, "Lib(net8.0)", lib);
        AddProject(workspace, "Lib(net10.0)", lib);
        var load = Load([app], workspace.CurrentSolution);
        var inventory = new SnapshotCoverageBuilder.Inventory([app, lib], []);

        var result = SnapshotCoverageBuilder.Build(CheckoutDir, Resolution(), load, inventory);

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, result.Coverage.Verdict);
        Assert.AreEqual(0, result.Coverage.ProjectFilesUnreferenced);
        Assert.AreEqual(1, result.Coverage.ProjectsDeclared);
        Assert.AreEqual(2, result.Coverage.ProjectsLoaded, "distinct loaded project FILES, not per-TFM projects");
    }

    [TestMethod]
    public void ScanErrors_MakeCoveragePartial_AndAreRedactedToTheCheckout()
    {
        var load = Load([At("src/App/App.csproj")]);
        var inventory = new SnapshotCoverageBuilder.Inventory(
            [At("src/App/App.csproj")], [],
            [$"{At("secret")}: cannot list files (UnauthorizedAccessException)"]);

        var result = SnapshotCoverageBuilder.Build(CheckoutDir, Resolution(), load, inventory);

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage.Verdict,
            "a scan that could not inspect part of the tree cannot prove coverage is complete");
        Assert.AreEqual(1, result.Coverage.ScanErrors);
        var diag = result.Diagnostics.Single(d => d.Code == "coverage_scan_incomplete");
        Assert.AreEqual("./secret: cannot list files (UnauthorizedAccessException)", diag.Message);
        Assert.IsFalse(diag.Message.Contains(CheckoutDir, StringComparison.OrdinalIgnoreCase),
            "the worker's volume layout never reaches the job ledger");
    }

    [TestMethod]
    public void PerItemDiagnostics_AreCappedWithASummaryRow()
    {
        var notSelected = Enumerable.Range(0, SnapshotCoverageBuilder.MaxItemDiagnosticsPerKind + 25)
            .Select(i => At($"heads/H{i}.slnx"))
            .ToList();
        var load = Load([At("src/App/App.csproj")]);

        var result = SnapshotCoverageBuilder.Build(
            CheckoutDir, Resolution(notSelected: notSelected), load, new SnapshotCoverageBuilder.Inventory([], []));

        var rows = result.Diagnostics.Where(d => d.Code == "solution_not_selected").ToList();
        Assert.AreEqual(SnapshotCoverageBuilder.MaxItemDiagnosticsPerKind + 1, rows.Count);
        StringAssert.Contains(rows[^1].Message, "…and 25 more");
        Assert.AreEqual(notSelected.Count, result.Coverage.SolutionsNotSelected, "the count is never capped");
        Assert.AreEqual(notSelected.Count + 1, result.Coverage.SolutionsDiscovered);
    }

    [TestMethod]
    public void FullyCoveredCheckout_IsCompleteWithNoReasons()
    {
        var load = Load([At("src/App/App.csproj")]);
        var inventory = new SnapshotCoverageBuilder.Inventory(
            [At("src/App/App.csproj")], [new DeclaredSubmodule("libs/shared", Populated: true)]);

        var result = SnapshotCoverageBuilder.Build(CheckoutDir, Resolution(), load, inventory);

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, result.Coverage.Verdict);
        Assert.AreEqual(0, result.Coverage.Reasons.Count);
        Assert.AreEqual(1, result.Coverage.SubmodulesDeclared);
        Assert.AreEqual(0, result.Coverage.SubmodulesUnpopulated);
        Assert.AreEqual("default_root", result.Coverage.SelectionSource);
    }

    private static void AddProject(AdhocWorkspace workspace, string name, string filePath) =>
        workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, name, name, LanguageNames.CSharp, filePath: filePath));
}
