using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// SVC-6/7 on the WORKER path: the orchestrator's publish-time branch advance honors the service's generic
/// <see cref="SnapshotContext.ExpectedHeadCommit"/> CAS and <see cref="SnapshotContext.SuppressBranchUpdate"/>,
/// inside the publish transaction, both for a freshly built snapshot and for a re-selected one. A local run
/// leaves both null and keeps today's unconditional advance byte-for-byte. Drives the real
/// <see cref="IndexOrchestrator"/> over an in-memory Roslyn solution, so no git checkout or MSBuild is needed.
/// </summary>
[TestClass]
public class OrchestratorBranchGuardTests
{
    private const string Repo = "https://github.com/acme/widgets";
    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_orchguard_{Guid.NewGuid():N}");
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

    [TestMethod]
    public async Task Cas_Match_Advances()
    {
        await Index("commit-A");
        await Index("commit-B", expected: "commit-A");

        Assert.AreEqual("commit-B", HeadCommit("main"));
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf("commit-A"));
    }

    [TestMethod]
    public async Task Cas_StaleBefore_PublishesButAttachesOnly()
    {
        await Index("commit-A");
        await Index("commit-B", expected: "commit-X");

        Assert.AreEqual("commit-A", HeadCommit("main"), "a stale before never moves the head");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-A"));
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-B"), "the snapshot is still published");
    }

    [TestMethod]
    public async Task Cas_BranchCreate_AdvancesOnlyWithoutAPointer()
    {
        await Index("commit-A", branch: "feature", expected: "");
        Assert.AreEqual("commit-A", HeadCommit("feature"), "a create with no pointer advances");

        await Index("commit-B", branch: "feature", expected: "0000000000000000000000000000000000000000");
        Assert.AreEqual("commit-A", HeadCommit("feature"), "a create against an existing pointer attaches only");
    }

    [TestMethod]
    public async Task Cas_Mismatch_OnAnUnknownBranch_CreatesNoRow()
    {
        await Index("commit-A");
        await Index("commit-B", branch: "feature", expected: "commit-A");

        Assert.IsFalse(BranchExists("feature"), "a failed CAS never creates (or re-creates) a branch row");
    }

    [TestMethod]
    public async Task None_PublishesWithoutCreatingABranchRow()
    {
        await Index("commit-A", branch: "pr-head", suppress: true);

        Assert.IsFalse(BranchExists("pr-head"));
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-A"));
    }

    [TestMethod]
    public async Task None_NeverMovesAnExistingPointer()
    {
        await Index("commit-A");
        await Index("commit-B", suppress: true);

        Assert.AreEqual("commit-A", HeadCommit("main"));
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-B"));
    }

    [TestMethod]
    public async Task Reselect_CasFailure_RestoresCompleteWithoutRepointing()
    {
        await Index("commit-A");
        await Index("commit-B");
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf("commit-A"), "precondition");

        await Index("commit-A", expected: "commit-X");

        Assert.AreEqual("commit-B", HeadCommit("main"), "the re-select is allowed to re-point only when the CAS passes");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-A"), "the snapshot is still restored and attachable");
    }

    [TestMethod]
    public async Task Reselect_CasPass_Repoints()
    {
        await Index("commit-A");
        await Index("commit-B");

        await Index("commit-A", expected: "commit-B");

        Assert.AreEqual("commit-A", HeadCommit("main"));
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf("commit-B"));
    }

    [TestMethod]
    public async Task LocalPath_NullGuards_KeepsTheUnconditionalAdvance()
    {
        // The local CLI/daemon path leaves both SVC-6/7 fields null: every index advances the ONE default
        // branch unconditionally (including back to an older commit), exactly as before.
        await Index("commit-A");
        await Index("commit-B");
        await Index("commit-A");

        CollectionAssert.AreEqual(
            new[] { ("main", "commit-A", true, (long?)null) },
            BranchRows().ToArray(),
            "one default branch, pointed at the latest index, with no head sequence");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-A"));
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf("commit-B"));
    }

    private async Task Index(string commit, string branch = "main", string? expected = null, bool suppress = false)
    {
        var ctx = new SnapshotContext
        {
            RepositoryRemoteUrl = Repo,
            CommitSha = commit,
            BranchName = branch,
            ExpectedHeadCommit = expected,
            SuppressBranchUpdate = suppress ? true : null
        };
        await new IndexOrchestrator(_db, useDocumentExtractor: true).IndexSolutionAsync(BuildSolution(), snapshotContext: ctx);
    }

    private SnapshotStore Store() => new(_db.GetConnection());

    private long RepoId() => Store().GetRepositoryId(Repo)!.Value;

    private bool BranchExists(string branch) => Store().GetBranch(RepoId(), branch) is not null;

    private string? HeadCommit(string branch)
    {
        var store = Store();
        var row = store.GetBranch(RepoId(), branch);
        return row?.SnapshotId is long snap ? store.GetCommitSha(store.GetById(snap)!.CommitId) : null;
    }

    private string StatusOf(string commit)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = """
            SELECT s.status FROM snapshots s JOIN commits c ON c.id = s.commit_id
            WHERE c.commit_sha = @sha AND s.is_overlay = 0;
            """;
        cmd.Parameters.AddWithValue("@sha", commit);
        return (string)cmd.ExecuteScalar()!;
    }

    private List<(string Name, string? Commit, bool IsDefault, long? HeadSequence)> BranchRows()
    {
        var names = new List<string>();
        using (var cmd = _db.GetConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM branches ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));
        }
        return names.Select(name =>
        {
            var branch = Store().GetBranch(RepoId(), name)!;
            return (branch.Name, HeadCommit(name), branch.IsDefault, branch.HeadSequence);
        }).ToList();
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
        return workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "App", "App", LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(projectId), "Widget.cs", SourceText.From(source), filePath: sourcePath);
    }
}
