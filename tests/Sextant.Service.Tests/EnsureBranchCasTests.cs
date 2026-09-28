using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-6/7 request contract: <c>expected_head_commit</c>, <c>forced</c> and <c>branch_update</c> ride the
/// snake_case wire, reach the orchestrator through the worker's generic <see cref="SnapshotContext"/> fields,
/// never change the snapshot identity, and a malformed combination is refused before any job exists.
/// </summary>
[TestClass]
public class EnsureBranchGuardContractTests
{
    [TestMethod]
    public void Worker_MapsBranchGuards_ToSnapshotContext()
    {
        var request = ServiceTestFixtures.Request(branch: "feature") with
        {
            ExpectedHeadCommit = "commit-before",
            BranchUpdate = "NONE",
            Forced = true
        };

        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);

        Assert.AreEqual("commit-before", context.ExpectedHeadCommit);
        Assert.IsTrue(context.SuppressBranchUpdate, "branch_update is matched case-insensitively");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("advance")]
    public void Worker_WithoutGuards_LeavesTheContextFieldsNull(string? branchUpdate)
    {
        var request = ServiceTestFixtures.Request(branch: "main") with { BranchUpdate = branchUpdate };

        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);

        Assert.IsNull(context.ExpectedHeadCommit);
        Assert.IsNull(context.SuppressBranchUpdate, "an unguarded ensure advances exactly as before");
    }

    [TestMethod]
    public void BranchGuards_DoNotChangeTheSnapshotIdentity()
    {
        var plain = ServiceTestFixtures.Request();
        var guarded = plain with { ExpectedHeadCommit = "commit-before", Forced = true, BranchUpdate = "none" };

        Assert.AreEqual(plain.ToIdentity().Hash, guarded.ToIdentity().Hash);
    }

    [TestMethod]
    public void BranchGuards_RoundTripOverTheSnakeCaseWire()
    {
        var request = ServiceTestFixtures.Request() with
        {
            ExpectedHeadCommit = "commit-before",
            Forced = true,
            BranchUpdate = "none"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(request, ServiceJson.Options);
        StringAssert.Contains(json, "\"expected_head_commit\":\"commit-before\"");
        StringAssert.Contains(json, "\"forced\":true");
        StringAssert.Contains(json, "\"branch_update\":\"none\"");
        Assert.IsFalse(json.Contains("suppresses", StringComparison.Ordinal), "the derived flag never reaches the wire");

        var round = System.Text.Json.JsonSerializer.Deserialize<EnsureSnapshotRequest>(json, ServiceJson.Options)!;
        Assert.AreEqual("commit-before", round.ExpectedHeadCommit);
        Assert.IsTrue(round.Forced);
        Assert.IsTrue(round.SuppressesBranchUpdate);

        var result = new EnsureSnapshotResult
        {
            JobId = 1, IdentityHash = "h", Status = SnapshotJobStatus.Complete, Attached = true, BranchAdvanced = false
        };
        StringAssert.Contains(System.Text.Json.JsonSerializer.Serialize(result, ServiceJson.Options), "\"branch_advanced\":false");
    }

    [TestMethod]
    [DataRow("commit-before", 5L, null, BranchGuardReason.ConflictingBranchGuards)]
    [DataRow("commit-before", 5L, "advance", BranchGuardReason.ConflictingBranchGuards)]
    [DataRow("", 5L, null, BranchGuardReason.ConflictingBranchGuards)]
    [DataRow("commit-before", 5L, "none", null)]
    [DataRow("commit-before", 5L, "NONE", null)]
    [DataRow(null, null, "sometimes", BranchGuardReason.InvalidBranchUpdate)]
    [DataRow(null, null, "", BranchGuardReason.InvalidBranchUpdate)]
    [DataRow("commit-before", null, "Advance", null)]
    [DataRow(null, 5L, "none", null)]
    [DataRow(null, null, null, null)]
    public void BranchGuardProblem_ClassifiesTheRequest(string? expected, long? sequence, string? update, string? problem)
    {
        var request = ServiceTestFixtures.Request() with
        {
            ExpectedHeadCommit = expected,
            BranchHeadSequence = sequence,
            BranchUpdate = update
        };

        Assert.AreEqual(problem, request.BranchGuardProblem());
    }
}

/// <summary>
/// SVC-6/7 on the service REUSE paths (no worker run): an ensure of an already-published commit makes the
/// branch decision itself, so it must apply the same precedence as the orchestrator. Driven end-to-end
/// through <see cref="SnapshotService.EnsureSnapshotAsync"/> with a worker that must never run.
/// </summary>
[TestClass]
public class EnsureBranchCasReuseTests
{
    private const string Repo = "https://github.com/acme/widgets";
    private const string ZeroSha = "0000000000000000000000000000000000000000";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;
    private FakeSnapshotWorker _worker = null!;
    private readonly Dictionary<string, long> _snap = [];

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        foreach (var commit in new[] { "commit-A", "commit-B", "commit-C" })
            _snap[commit] = ServiceTestFixtures.PublishComplete(
                _db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = commit }, recordCommit: true);
        _worker = new FakeSnapshotWorker(_db, FakeSnapshotWorker.Throws("worker must not run on the reuse path"));
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), _worker, _db);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task Match_Advances()
    {
        await Ensure("commit-A");

        var result = await Ensure("commit-B", expected: "commit-A");

        Assert.IsTrue(result.BranchAdvanced);
        Assert.AreEqual(_snap["commit-B"], HeadOf("main"));
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf("commit-A"));
        Assert.AreEqual(0, _worker.Calls);
    }

    [TestMethod]
    public async Task StaleBefore_AttachesOnly()
    {
        await Ensure("commit-A");

        var result = await Ensure("commit-B", expected: "commit-X");

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, "the snapshot is still attached");
        Assert.AreEqual(_snap["commit-B"], result.SnapshotId);
        Assert.IsFalse(result.BranchAdvanced);
        Assert.AreEqual(_snap["commit-A"], HeadOf("main"));
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-A"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(ZeroSha)]
    public async Task BranchCreate_AdvancesWithoutAPointer_AndAttachesWithOne(string expected)
    {
        var created = await Ensure("commit-A", branch: "feature", expected: expected);
        Assert.IsTrue(created.BranchAdvanced, "a create with no pointer advances");
        Assert.AreEqual(_snap["commit-A"], HeadOf("feature"));

        var again = await Ensure("commit-B", branch: "feature", expected: expected);
        Assert.IsFalse(again.BranchAdvanced, "a create against an existing pointer attaches only");
        Assert.AreEqual(_snap["commit-A"], HeadOf("feature"));
    }

    [TestMethod]
    public async Task Mismatch_OnAnUnknownBranch_CreatesNoRow()
    {
        var result = await Ensure("commit-B", branch: "feature", expected: "commit-A");

        Assert.IsFalse(result.BranchAdvanced);
        Assert.IsNull(BranchRow("feature"), "a failed CAS never creates (or re-creates) a branch row");
    }

    [TestMethod]
    public async Task Forced_WithAMatch_Advances_AndIsAudited()
    {
        await Ensure("commit-A");

        var result = await Ensure("commit-B", expected: "commit-A", forced: true);

        Assert.IsTrue(result.BranchAdvanced);
        Assert.AreEqual(_snap["commit-B"], HeadOf("main"));
        var audit = _service.RecentAudit(action: AuditAction.Ensure);
        StringAssert.EndsWith(audit[0].Detail, ";forced", "forced is informational and audited");
        Assert.IsFalse(audit[1].Detail!.Contains("forced", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Forced_DoesNotBypassTheCas()
    {
        await Ensure("commit-A");

        var result = await Ensure("commit-B", expected: "commit-X", forced: true);

        Assert.IsFalse(result.BranchAdvanced);
        Assert.AreEqual(_snap["commit-A"], HeadOf("main"));
    }

    [TestMethod]
    public async Task TwoPushesOutOfOrder_ConvergeOnTheNewerOne()
    {
        // Head A. Push 1 is A→B, push 2 is B→C, delivered 2 then 1. Push 2 attaches only (the head is not
        // yet B) and says so, push 1 advances, and the caller's retry of push 2 (a cheap reuse) advances to C.
        // A late duplicate of push 1 can never move the head back.
        await Ensure("commit-A");

        var push2 = await Ensure("commit-C", expected: "commit-B");
        Assert.IsFalse(push2.BranchAdvanced);
        Assert.AreEqual(_snap["commit-A"], HeadOf("main"));

        var push1 = await Ensure("commit-B", expected: "commit-A");
        Assert.IsTrue(push1.BranchAdvanced);

        var retry = await Ensure("commit-C", expected: "commit-B");
        Assert.IsTrue(retry.BranchAdvanced);
        Assert.AreEqual(_snap["commit-C"], HeadOf("main"));

        var lateDuplicate = await Ensure("commit-B", expected: "commit-A");
        Assert.IsFalse(lateDuplicate.BranchAdvanced);
        Assert.AreEqual(_snap["commit-C"], HeadOf("main"), "the newer head wins");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-C"));
        Assert.AreEqual(0, _worker.Calls);
    }

    [TestMethod]
    public async Task UnusableHead_IsReplacedWhateverTheBefore()
    {
        await Ensure("commit-A");
        using (var cmd = _db.GetConnection().CreateCommand())
        {
            cmd.CommandText = "UPDATE snapshots SET status = 'failed' WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", _snap["commit-A"]);
            cmd.ExecuteNonQuery();
        }

        var result = await Ensure("commit-B", expected: "commit-unrelated");

        Assert.IsTrue(result.BranchAdvanced);
        Assert.AreEqual(_snap["commit-B"], HeadOf("main"));
    }

    [TestMethod]
    public async Task None_NeverCreatesABranchRow()
    {
        var result = await Ensure("commit-A", branch: "pr-head", update: "none");

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(_snap["commit-A"], result.SnapshotId);
        Assert.IsFalse(result.BranchAdvanced);
        Assert.IsNull(BranchRow("pr-head"));
        Assert.AreEqual(0L, BranchCount());
    }

    [TestMethod]
    public async Task None_NeverMovesAnExistingPointer_AndIgnoresEveryOtherGuard()
    {
        await Ensure("commit-A");

        var bySequence = await Ensure("commit-B", update: "none", sequence: 99);
        var byCas = await Ensure("commit-B", update: "none", expected: "commit-A");
        // Precedence row 0 comes before row 1: under none, both guards together are ignored, not a conflict.
        var byBoth = await Ensure("commit-C", update: "none", expected: "commit-A", sequence: 99);

        Assert.IsFalse(bySequence.BranchAdvanced);
        Assert.IsFalse(byCas.BranchAdvanced);
        Assert.AreEqual(SnapshotJobStatus.Complete, byBoth.Status);
        Assert.IsFalse(byBoth.BranchAdvanced);
        Assert.AreEqual(_snap["commit-A"], HeadOf("main"));
        Assert.IsNull(BranchRow("main")!.HeadSequence, "none never writes a head sequence");
    }

    [TestMethod]
    public async Task BothGuards_AreRefusedBeforeAnyJobExists()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _service.EnsureSnapshotAsync(Request("commit-A") with { ExpectedHeadCommit = "", BranchHeadSequence = 1 }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _service.EnsureSnapshotAsync(Request("commit-A") with { BranchUpdate = "sometimes" }));

        Assert.AreEqual(0L, JobCount());
    }

    [TestMethod]
    public async Task Superseded_CasPass_ReselectsAndRepoints()
    {
        await Ensure("commit-A");
        await Ensure("commit-B", expected: "commit-A");
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf("commit-A"), "precondition");

        var result = await Ensure("commit-A", expected: "commit-B");

        Assert.IsTrue(result.BranchAdvanced);
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(_snap["commit-A"], HeadOf("main"));
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf("commit-B"));
        Assert.AreEqual(0, _worker.Calls);
    }

    [TestMethod]
    [DataRow("commit-X", null)]
    [DataRow(null, "none")]
    public async Task Superseded_DeclinedGuard_RestoresCompleteWithoutRepointing(string? expected, string? update)
    {
        await Ensure("commit-A");
        await Ensure("commit-B", expected: "commit-A");

        var result = await Ensure("commit-A", expected: expected, update: update);

        Assert.IsFalse(result.BranchAdvanced);
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(_snap["commit-A"], result.SnapshotId);
        Assert.AreEqual(_snap["commit-B"], HeadOf("main"), "the re-select re-points only when the CAS passes");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-A"), "the snapshot is restored and attachable");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf("commit-B"));
        Assert.AreEqual(0, _worker.Calls);
    }

    [TestMethod]
    public async Task Unguarded_AlreadyPointed_ReportsNoAdvance()
    {
        var first = await Ensure("commit-A");
        var second = await Ensure("commit-A");

        Assert.IsTrue(first.BranchAdvanced);
        Assert.IsFalse(second.BranchAdvanced, "the pointer already targeted the snapshot");
    }

    private Task<EnsureSnapshotResult> Ensure(
        string commit, string? branch = null, string? expected = null, bool? forced = null, string? update = null,
        long? sequence = null) =>
        _service.EnsureSnapshotAsync(Request(commit, branch) with
        {
            ExpectedHeadCommit = expected,
            Forced = forced,
            BranchUpdate = update,
            BranchHeadSequence = sequence
        });

    private static EnsureSnapshotRequest Request(string commit, string? branch = null) =>
        new() { RepositoryRemoteUrl = Repo, CommitSha = commit, BranchName = branch };

    private SnapshotStore Store() => new(_db.GetConnection());

    private BranchRow? BranchRow(string branch) =>
        Store().GetRepositoryId(Repo) is long repo ? Store().GetBranch(repo, branch) : null;

    private long? HeadOf(string branch) => BranchRow(branch)?.SnapshotId;

    private string StatusOf(string commit) => Store().GetById(_snap[commit])!.Status;

    private long BranchCount() => Scalar("SELECT COUNT(*) FROM branches;");

    private long JobCount() => Scalar("SELECT COUNT(*) FROM snapshot_jobs;");

    private long Scalar(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}

/// <summary>
/// SVC-6/7 convergence: the WORKER path (a fresh identity, whose branch decision is the real orchestrator's
/// <c>AdvanceBranchToSnapshot</c> inside the publish transaction) and the pre-built REUSE path (the same
/// identities already published, so the service decides) must leave the SAME branch state and report the
/// same <c>branch_advanced</c> for the same guarded request sequence.
/// </summary>
[TestClass]
public class EnsureBranchCasConvergenceTests
{
    private const string Repo = "https://github.com/acme/widgets";
    private readonly List<string> _cleanup = [];

    [TestCleanup]
    public void TestCleanup()
    {
        foreach (var path in _cleanup)
            try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    // (commit, branch, expected_head_commit, branch_update)
    private static readonly (string Commit, string? Branch, string? Expected, string? Update)[] Pushes =
    [
        ("commit-A", null, "", null),              // main created
        ("commit-B", null, "commit-A", null),      // match → advance
        ("commit-C", null, "commit-X", null),      // stale before → attach only
        ("commit-D", "feature", "", null),         // feature created
        ("commit-E", "feature", "commit-A", null), // mismatch → attach only
        ("commit-F", "pr-head", null, "none"),     // none → no row
        ("commit-A", null, "commit-B", null),      // superseded A, CAS pass → re-point
        ("commit-B", "feature", "commit-X", null)  // superseded B, CAS fail → restored, not pointed
    ];

    [TestMethod]
    public async Task WorkerPath_AndPrebuiltReusePath_Converge()
    {
        var (workerAdvanced, workerState, workerCalls) = await Run(prebuild: false);
        var (reuseAdvanced, reuseState, reuseCalls) = await Run(prebuild: true);

        Assert.AreEqual(Pushes.Select(p => p.Commit).Distinct().Count(), workerCalls,
            "each new identity ran the orchestrator once; the superseded re-selects did not");
        Assert.AreEqual(0, reuseCalls, "every guarded ensure over pre-built identities is a reuse");
        CollectionAssert.AreEqual(
            new bool?[] { true, true, false, true, false, false, true, false }, workerAdvanced,
            string.Join(",", workerAdvanced));
        CollectionAssert.AreEqual(workerAdvanced, reuseAdvanced, "both paths report the same decisions");
        CollectionAssert.AreEqual(workerState, reuseState,
            $"worker: {string.Join(" | ", workerState)}\nreuse: {string.Join(" | ", reuseState)}");
        CollectionAssert.AreEqual(
            new[]
            {
                "branch main -> commit-A default=True seq=",
                "branch feature -> commit-D default=False seq=",
                "snapshot commit-A complete",
                "snapshot commit-B complete",
                "snapshot commit-C complete",
                "snapshot commit-D complete",
                "snapshot commit-E complete",
                "snapshot commit-F complete"
            },
            workerState, string.Join(" | ", workerState));
    }

    private async Task<(bool?[] Advanced, List<string> State, int Calls)> Run(bool prebuild)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant_cas_conv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _cleanup.Add(root);
        var dbPath = Path.Combine(root, "catalog.db");
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath, dataRoot: Path.Combine(root, "data")) with
        {
            // The orchestrator stamps the default profile's configuration hash into the identity.
            DefaultConfigHash = IndexProfileDescriptor.Full.ConfigurationHash
        };
        var worker = new FakeSnapshotWorker(db, (self, request) => IndexWithOrchestrator(self.Database, root, request));
        try
        {
            using var service = SnapshotService.Start(options, worker, db);
            if (prebuild)
            {
                foreach (var commit in Pushes.Select(p => p.Commit).Distinct())
                    await service.EnsureSnapshotAsync(Request(commit, null, null, BranchUpdateMode.None));
            }
            var callsBefore = worker.Calls;

            var advanced = new List<bool?>();
            foreach (var (commit, branch, expected, update) in Pushes)
            {
                var result = await service.EnsureSnapshotAsync(Request(commit, branch, expected, update));
                Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, $"{commit}: {result.Reason}");
                advanced.Add(result.BranchAdvanced);
            }
            return ([.. advanced], State(db), worker.Calls - callsBefore);
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    private static EnsureSnapshotRequest Request(string commit, string? branch, string? expected, string? update) => new()
    {
        RepositoryRemoteUrl = Repo,
        CommitSha = commit,
        BranchName = branch,
        ExpectedHeadCommit = expected,
        BranchUpdate = update
    };

    // The real worker's orchestrator step over an in-memory solution: the SAME SnapshotContext mapping as
    // LocalIndexerSnapshotWorker, so the branch decision is the orchestrator's own.
    internal static SnapshotWorkResult IndexWithOrchestrator(IndexDatabase db, string root, EnsureSnapshotRequest request)
    {
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);
        new IndexOrchestrator(db, useDocumentExtractor: true)
            .IndexSolutionAsync(BuildSolution(root), snapshotContext: context).GetAwaiter().GetResult();
        var identity = request.ToIdentity(IndexProfileDescriptor.Full.ConfigurationHash);
        var published = new SnapshotStore(db.GetConnection()).GetByIdentityHash(identity.Hash);
        return published is { Status: SnapshotStatus.Complete }
            ? SnapshotWorkResult.Complete(published.Id)
            : SnapshotWorkResult.Failed("the orchestrator did not publish the requested identity", []);
    }

    internal static List<string> State(IndexDatabase db)
    {
        var conn = db.GetConnection();
        var state = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT b.name, c.commit_sha, b.is_default, b.head_sequence
                FROM branches b
                LEFT JOIN snapshots s ON s.id = b.snapshot_id
                LEFT JOIN commits c ON c.id = s.commit_id
                ORDER BY b.is_default DESC, b.name DESC;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                state.Add($"branch {reader.GetString(0)} -> {(reader.IsDBNull(1) ? "none" : reader.GetString(1))} " +
                          $"default={reader.GetInt64(2) != 0} seq={(reader.IsDBNull(3) ? "" : reader.GetInt64(3).ToString())}");
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.commit_sha, s.status FROM snapshots s JOIN commits c ON c.id = s.commit_id
                WHERE s.is_overlay = 0 ORDER BY c.commit_sha;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                state.Add($"snapshot {reader.GetString(0)} {reader.GetString(1)}");
        }
        return state;
    }

    internal static Solution BuildSolution(string root)
    {
        var projectDir = Path.Combine(root, "src", "App");
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
