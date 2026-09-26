using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #119 — the orchestrator records the worker-computed coverage INSIDE the publish transaction, and
/// never backfills or rewrites it when an already-published snapshot is re-selected (immutability).
/// Drives the real <see cref="IndexOrchestrator"/> over an in-memory Roslyn solution with an explicit
/// <see cref="SnapshotContext"/>, so no git checkout or MSBuild is needed.
/// </summary>
[TestClass]
public class OrchestratorCoverageTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_orchcov_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "index.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly SnapshotCoverage PartialCoverage = new()
    {
        Verdict = SnapshotCoverageVerdict.Partial,
        Reasons = ["1 of 2 discovered solution(s) were not selected"],
        SolutionsDiscovered = 2,
        SolutionsNotSelected = 1
    };

    private static SnapshotContext Context(string commit, SnapshotCoverage? coverage) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/orchcov",
        CommitSha = commit,
        BranchName = "main",
        Coverage = coverage
    };

    [TestMethod]
    public async Task Publish_RecordsCoverage_AndReuseNeverRewritesIt()
    {
        var solution = BuildSolution();
        var orchestrator = new IndexOrchestrator(_db, useDocumentExtractor: true);

        await orchestrator.IndexSolutionAsync(solution, snapshotContext: Context("c1", PartialCoverage));

        var conn = _db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var selected = snapshots.GetSelectedSnapshotId();
        Assert.IsNotNull(selected, "the full index published and selected a snapshot");
        var recorded = new SnapshotCoverageStore(conn).Get(selected.Value);
        Assert.IsNotNull(recorded, "coverage is recorded with the publish");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, recorded.Verdict);
        Assert.AreEqual(1, recorded.SolutionsNotSelected);

        // Same identity again, now claiming complete coverage: the immutable snapshot is re-selected and its
        // recorded verdict stays authoritative (no backfill, no rewrite).
        await orchestrator.IndexSolutionAsync(BuildSolution(),
            snapshotContext: Context("c1", new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }));

        Assert.AreEqual(selected, snapshots.GetSelectedSnapshotId(), "the same immutable snapshot is re-selected");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, new SnapshotCoverageStore(conn).Get(selected.Value)!.Verdict);
    }

    [TestMethod]
    public async Task Publish_WithoutComputedCoverage_RecordsNothing()
    {
        await new IndexOrchestrator(_db, useDocumentExtractor: true)
            .IndexSolutionAsync(BuildSolution(), snapshotContext: Context("c2", coverage: null));

        var conn = _db.GetConnection();
        var selected = new SnapshotStore(conn).GetSelectedSnapshotId();
        Assert.IsNotNull(selected);
        Assert.IsNull(new SnapshotCoverageStore(conn).Get(selected.Value),
            "a local (CLI/daemon) index computes no coverage, so none is recorded");
    }

    private Solution BuildSolution()
    {
        var projectDir = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "App.csproj");
        var sourcePath = Path.Combine(projectDir, "Widget.cs");
        const string source = "namespace App { public class Widget { public int Size() => 1; } }";
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(sourcePath, source);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "App", "App", LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(projectId), "Widget.cs", SourceText.From(source), filePath: sourcePath);
        return solution;
    }
}
