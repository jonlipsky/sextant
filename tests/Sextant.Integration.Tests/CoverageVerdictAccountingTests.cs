using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Mcp;
using Sextant.Service;
using Sextant.Store;

namespace Sextant.Integration.Tests;

[TestClass]
public sealed class CoverageVerdictAccountingTests
{
    private const string Remote = "https://github.com/org/coverage-accounting";
    private string _root = null!;
    private IndexDatabase _db = null!;
    private AdhocWorkspace _workspace = null!;

    [TestInitialize]
    public void Init()
    {
        _ = IntegrationFixture.Instance;
        _root = Path.Combine(Path.GetTempPath(), $"sextant_coverage_accounting_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new IndexDatabase(Path.Combine(_root, "catalog.db"));
        _db.RunMigrations();
        _workspace = new AdhocWorkspace();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _workspace.Dispose();
        _db.Dispose();
        Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_root);
    }

    [TestMethod]
    public async Task FailedTfm_DoesNotHideBehindHealthySibling_PublishedSymbolsRemainPartial()
    {
        var path = Write("Lib.csproj",
            "<Project><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks></PropertyGroup></Project>");
        var solution = AddProject(_workspace.CurrentSolution, path, "Lib(net10.0)", withDocument: true);
        solution = AddProject(solution, path, "Lib(net9.0)", withDocument: false);
        var failed = solution.Projects.Single(p => p.Name == "Lib(net9.0)");
        var load = Reconcile(solution, path,
            [new SolutionLoader.LoadFailure(failed.Id, null, "NETSDK1100: failed design-time build")]);

        Assert.AreEqual(1, load.SkippedProjects.Count);
        Assert.AreEqual("net9.0", load.SkippedProjects.Single().TargetFramework);
        Assert.AreEqual(0, load.DegradedProjects.Count, "the typed failure must not degrade the healthy sibling");
        Assert.AreEqual(1, load.Solutions.Single().LoadedProjectCount, "a file with a usable TFM still loaded");

        var coverage = await PublishAndRead(load, expectedPartial: true);
        Assert.AreEqual(1, coverage.ProjectsDeclared);
        Assert.AreEqual(1, coverage.ProjectsSkipped, "project files, not TFM versions, are the denominator");
        Assert.AreEqual(1, coverage.ProjectsPartiallyLoaded);
        StringAssert.Contains(RemoteResponsePresenter.PartialWarningFor(coverage), "1 of 1 projects");
        Assert.AreEqual("net9.0", coverage.EvaluationGaps!.Single().TargetFramework);
        Assert.IsFalse(coverage.EvaluationGaps.Single().HasDocuments);
        StringAssert.Contains(string.Join(" ", coverage.Reasons), "net9.0");
    }

    [TestMethod]
    public async Task MissingDeclaredTfm_WithoutAStub_IsStillAPublishedCoverageGap()
    {
        var path = Write("Lib.csproj",
            "<Project><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks></PropertyGroup></Project>");
        var solution = AddProject(_workspace.CurrentSolution, path, "Lib(net10.0)", withDocument: true);
        var load = Reconcile(solution, path, []);

        var coverage = await PublishAndRead(load, expectedPartial: true);
        Assert.AreEqual("net9.0", coverage.EvaluationGaps!.Single().TargetFramework);
        Assert.AreEqual(1, coverage.ProjectsSkipped);
    }

    [TestMethod]
    public async Task FailedEvaluation_WithSurvivingDocuments_StaysPartialAfterPublicationAndReuse()
    {
        var path = Write("Lib.csproj", "<Project />");
        var solution = AddProject(_workspace.CurrentSolution, path, "Lib", withDocument: true);
        var load = Reconcile(solution, path,
            [new SolutionLoader.LoadFailure(solution.ProjectIds.Single(), null,
                $"MSB4019: missing import at {_root}; https://user:secret@example.invalid/feed")]);

        Assert.AreEqual(0, load.SkippedProjects.Count);
        Assert.AreEqual(1, load.DegradedProjects.Count);
        var coverage = await PublishAndRead(load, expectedPartial: true);
        Assert.AreEqual(1, coverage.ProjectsDegraded);
        var gap = coverage.EvaluationGaps!.Single();
        Assert.IsTrue(gap.HasDocuments);
        Assert.IsNull(gap.TargetFramework, "failed evaluation cannot fabricate an evaluated TFM");
        Assert.AreEqual("MSB4019", gap.Reason, "only safe diagnostic codes travel into durable coverage");
        StringAssert.Contains(RemoteResponsePresenter.PartialWarningFor(coverage), "failed evaluation");
    }

    [TestMethod]
    public async Task AmbiguousFailure_IsNotAssignedToASibling_AndCannotPublishHealthyCoverage()
    {
        var a = Write("one/Lib.csproj", "<Project />");
        var b = Write("two/Lib.csproj", "<Project />");
        var solution = AddProject(_workspace.CurrentSolution, a, "One", withDocument: true);
        solution = AddProject(solution, b, "Two", withDocument: true);
        var reconciled = SolutionLoader.ReconcileLoads([a, b], solution,
            [new SolutionLoader.LoadFailure(null, null, "Lib.csproj failed evaluation")], [], null);
        var load = FromReconciliation(solution, [a, b], reconciled);

        var coverage = await PublishAndRead(load, expectedPartial: true);
        Assert.AreEqual(0, coverage.ProjectsSkipped);
        Assert.AreEqual(0, coverage.ProjectsDegraded);
        Assert.AreEqual(1, coverage.UnattributedLoadFailures);
    }

    [TestMethod]
    public async Task UnresolvedTypedFailure_NamingASurvivor_IsUnattributedAndPartial()
    {
        var path = Write("Lib.csproj", "<Project />");
        var solution = AddProject(_workspace.CurrentSolution, path, "Lib", withDocument: true);
        var load = Reconcile(solution, path,
            [new SolutionLoader.LoadFailure(ProjectId.CreateNewId(), null, $"failed reference to {path}")]);
        var coverage = await PublishAndRead(load, expectedPartial: true);
        Assert.AreEqual(1, coverage.UnattributedLoadFailures);
        Assert.AreEqual(0, coverage.ProjectsDegraded, "an unresolved typed owner is never guessed from its message");
    }

    [TestMethod]
    [DataRow(".sln")]
    [DataRow(".slnx")]
    public async Task ReadableEmptySolution_InARealUnion_IsCompleteAtThePublishedBoundary(string extension)
    {
        var load = await LoadUnion(extension, broken: false);
        Assert.IsTrue(load.Solutions.Single(s => s.DeclaredProjectCount == 0).IsReadable);
        var coverage = await PublishAndRead(load, expectedPartial: false);
        Assert.AreEqual(0, coverage.SolutionsUnreadable);
    }

    [TestMethod]
    [DataRow(".sln")]
    [DataRow(".slnx")]
    public async Task TruncatedSolution_DiscardsPartialDeclarations_AndPublishesPartialUnion(string extension)
    {
        var load = await LoadUnion(extension, broken: true);
        var unreadable = load.Solutions.Single(s => !s.IsReadable);
        Assert.AreEqual(0, unreadable.DeclaredProjectCount, "a partial parse is never a declaration authority");
        var coverage = await PublishAndRead(load, expectedPartial: true);
        Assert.AreEqual(1, coverage.SolutionsUnreadable);
    }

    [TestMethod]
    public async Task RealMsBuildFailure_WithDocuments_IsPartialAtThePublishedBoundary()
    {
        Write("Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="Widget.cs" /></ItemGroup>
            </Project>
            """);
        Write("Widget.cs", "namespace Accounting; public class Widget { }");
        var sln = Write("App.slnx", "<Solution><Project Path=\"Lib.csproj\" /></Solution>");
        var diagnostics = new List<string>();
        var load = await MultiSolutionLoader.LoadAsync([sln], diagnostics.Add);

        Assert.IsTrue(load.Solution.Projects.Any(p => p.Documents.Any()), "MSBuild retained the source despite duplicate Compile items");
        Assert.IsTrue(load.DegradedProjects.Count > 0,
            "surviving source is not proof of successful evaluation: " + string.Join("\n", diagnostics));
        var coverage = await PublishAndRead(load, expectedPartial: true);
        Assert.AreEqual(1, coverage.ProjectsDegraded);
    }

    private async Task<MultiSolutionLoadResult> LoadUnion(string extension, bool broken)
    {
        Write("Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup>
            </Project>
            """);
        Write("Widget.cs", "namespace Accounting; public class Widget { }");
        var app = Write("App.slnx", "<Solution><Project Path=\"Lib.csproj\" /></Solution>");
        var empty = Write("Empty" + extension, extension == ".slnx"
            ? broken ? "<Solution><Project Path=\"Missing.csproj\" />" : "<Solution />"
            : broken
                ? "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Missing\", \"Missing.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n"
                : "Microsoft Visual Studio Solution File, Format Version 12.00\nGlobal\nEndGlobal\n");
        var restore = Sextant.TestSupport.BoundedProcess.DotnetRestore(
            Path.Combine(_root, "Lib.csproj"), TimeSpan.FromMinutes(1));
        Assert.IsTrue(restore.Succeeded, restore.Describe());
        return await MultiSolutionLoader.LoadAsync([app, empty]);
    }

    private MultiSolutionLoadResult Reconcile(
        Solution solution, string path, IReadOnlyList<SolutionLoader.LoadFailure> failures) =>
        FromReconciliation(solution, [path], SolutionLoader.ReconcileLoads([path], solution, failures, [], null));

    private MultiSolutionLoadResult FromReconciliation(
        Solution solution, IReadOnlyList<string> declared, SolutionLoader.LoadReconciliation reconciled) =>
        new(solution, reconciled.SkippedProjects,
            MultiSolutionLoader.BuildCoverage([(Path.Combine(_root, "App.slnx"), declared)], reconciled.SkippedProjects, loadedSolution: solution))
        {
            DeclaredProjects = declared,
            Membership = [new SolutionMembership(Path.Combine(_root, "App.slnx"), declared)],
            DegradedProjects = reconciled.DegradedProjects,
            UnattributedFailureCount = reconciled.UnattributedFailureCount
        };

    private async Task<SnapshotCoverage> PublishAndRead(MultiSolutionLoadResult load, bool expectedPartial)
    {
        var resolution = new CheckoutResolution
        {
            CheckoutDir = _root,
            SelectedSolutions = load.Solutions.Select(s => s.SolutionPath).ToList(),
            Source = SolutionSelectionSource.DefaultUnion
        };
        var computed = SnapshotCoverageBuilder.Build(_root, resolution, load, SnapshotCoverageBuilder.Inventory.Scan(_root));
        var context = new SnapshotContext
        {
            RepositoryRemoteUrl = Remote,
            CommitSha = new string('a', 40),
            BranchName = "main",
            Coverage = computed.Coverage
        };
        var orchestrator = new IndexOrchestrator(_db, useDocumentExtractor: true);
        await orchestrator.IndexSolutionAsync(load.Solution, snapshotContext: context, solutionMembership: load.Membership);
        var snapshots = new SnapshotStore(_db.GetConnection());
        var id = snapshots.GetSelectedSnapshotId()!.Value;
        var snapshot = snapshots.GetById(id)!;
        Assert.AreEqual(SnapshotStatus.Complete, snapshot.Status, "partial means servable, not failed publication");
        var page = await new LocalBaseSnapshotSource(_db.GetConnection()).FetchSymbolsAsync(
            new SnapshotPageRequest { IdentityHash = snapshot.IdentityHash }, CancellationToken.None);
        Assert.IsTrue(page.Published);
        Assert.AreEqual(!expectedPartial, page.Complete, "query coverage must agree with the durable verdict");
        Assert.IsTrue(page.Symbols.Any(s => s.DisplayName == "Widget"), "surviving symbols remain queryable");
        var recorded = page.Coverage!;
        Assert.AreEqual(expectedPartial, recorded.IsPartial);
        var result = LocalIndexerSnapshotWorker.BuildResult(id, _root, resolution, load, computed with { Coverage = recorded });
        Assert.AreEqual(expectedPartial ? SnapshotJobStatus.Partial : SnapshotJobStatus.Complete, result.Status);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(recorded).Contains("secret", StringComparison.Ordinal));
        if (recorded.ProjectsDegraded > 0)
            Assert.IsTrue(result.Projects.Any(p => p.Code == "project_evaluation_degraded"));
        if (recorded.SolutionsUnreadable > 0)
            Assert.IsTrue(result.Projects.Any(p => p.Code == "solution_no_projects"));
        else
            Assert.IsFalse(result.Projects.Any(p => p.Code == "solution_no_projects"));

        // An already-published identity never rewrites its truthful coverage on a later healthy claim.
        await orchestrator.IndexSolutionAsync(load.Solution, snapshotContext: context with
        {
            Coverage = new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }
        });
        Assert.AreEqual(expectedPartial, new SnapshotCoverageStore(_db.GetConnection()).Get(id)!.IsPartial);
        return recorded;
    }

    private Solution AddProject(Solution solution, string path, string name, bool withDocument)
    {
        var id = ProjectId.CreateNewId();
        solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
            filePath: path, metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        if (!withDocument) return solution;
        const string source = "namespace Accounting; public class Widget { }";
        var sourcePath = Write(Path.GetRelativePath(_root, Path.Combine(Path.GetDirectoryName(path)!, "Widget.cs")), source);
        return solution.AddDocument(DocumentId.CreateNewId(id), "Widget.cs", SourceText.From(source), filePath: sourcePath);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
