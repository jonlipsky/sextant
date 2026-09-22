using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #84 wiring on the SERVICE side: the optional monotonic <c>BranchHeadSequence</c> on the ensure
/// request must (a) reach the orchestrator via the worker's <see cref="SnapshotContext"/> so the branch
/// advance can be gated, (b) round-trip over the snake_case wire as <c>branch_head_sequence</c>, and (c)
/// NEVER change the snapshot identity — the same committed state re-ensured under a different sequence is
/// the SAME immutable snapshot. A null sequence flows through unchanged so a non-sequence ensure keeps the
/// unconditional-advance behavior byte-for-byte (criterion 2).
/// </summary>
[TestClass]
public class EnsureBranchHeadSequenceTests
{
    [TestMethod]
    public void Worker_MapsBranchHeadSequence_FromRequestToSnapshotContext()
    {
        var request = ServiceTestFixtures.Request(branch: "main") with { BranchHeadSequence = 42 };

        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);

        Assert.AreEqual(42L, context.BranchHeadSequence,
            "the worker carries the request's head sequence into the orchestrator so the advance is gated (#84)");
        Assert.AreEqual("main", context.BranchName);
    }

    [TestMethod]
    public void Worker_NullBranchHeadSequence_FlowsThroughAsNull()
    {
        var request = ServiceTestFixtures.Request(branch: "main");

        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);

        Assert.IsNull(context.BranchHeadSequence,
            "a non-sequence ensure produces a null-sequence context → unconditional advance, byte-identical (criterion 2)");
    }

    [TestMethod]
    public void BranchHeadSequence_DoesNotChangeSnapshotIdentity()
    {
        var baseRequest = ServiceTestFixtures.Request();
        var withSeq = baseRequest with { BranchHeadSequence = 7 };
        var withOtherSeq = baseRequest with { BranchHeadSequence = 99 };

        Assert.AreEqual(baseRequest.ToIdentity().Hash, withSeq.ToIdentity().Hash,
            "the head sequence is an ordering hint, not part of the content-addressed snapshot identity");
        Assert.AreEqual(withSeq.ToIdentity().Hash, withOtherSeq.ToIdentity().Hash,
            "two ensures for the same commit under different sequences resolve to the ONE immutable snapshot");
    }

    [TestMethod]
    public void BranchHeadSequence_RoundTripsOverSnakeCaseWire()
    {
        var request = ServiceTestFixtures.Request(branch: "main") with { BranchHeadSequence = 123 };

        var json = System.Text.Json.JsonSerializer.Serialize(request, ServiceJson.Options);
        StringAssert.Contains(json, "\"branch_head_sequence\":123",
            "the wire field is snake_case branch_head_sequence (the contract ProcessStack sends)");

        var round = System.Text.Json.JsonSerializer.Deserialize<EnsureSnapshotRequest>(json, ServiceJson.Options)!;
        Assert.AreEqual(123L, round.BranchHeadSequence);
    }

    [TestMethod]
    public void BranchHeadSequence_OmittedFromWire_BindsAsNull()
    {
        const string json = """
            {"repository_remote_url":"https://github.com/org/app","commit_sha":"commit-aaaa"}
            """;

        var request = System.Text.Json.JsonSerializer.Deserialize<EnsureSnapshotRequest>(json, ServiceJson.Options)!;

        Assert.IsNull(request.BranchHeadSequence,
            "a legacy client that omits the field binds to null → today's unconditional-advance behavior (criterion 2)");
    }
}

/// <summary>
/// Issue #84 on the service REUSE/terminal-attach paths (no worker run): an ensure for an already-published
/// commit attaches without invoking the orchestrator, so the branch-pointer decision is made by the service
/// itself. It must apply the SAME forward-only gate — a higher-sequence re-ensure of an already-published
/// commit advances the pointer (e.g. a reset/force-push A→B→A), a lower/equal one never regresses it, and a
/// null-sequence request preserves today's forward-only attach-if-unset behavior (criterion 2). Driven
/// end-to-end through <see cref="SnapshotService.EnsureSnapshotAsync"/> with a worker that must never run.
/// </summary>
[TestClass]
public class EnsureBranchHeadSequenceReuseTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;
    private const string Repo = "https://github.com/org/app";

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task Reuse_HigherThenLowerSequence_AdvancesThenNeverRegresses()
    {
        var (snapA, snapB) = StartWithTwoPublishedCommits();

        await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: 20));
        Assert.AreEqual(snapB, Head(), "a higher (first) sequence advances the default branch to B on the reuse path");

        // An out-of-order older-commit ensure carries a LOWER sequence — it must NOT regress the pointer.
        await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 5));
        Assert.AreEqual(snapB, Head(), "a lower-sequence reuse ensure never regresses the branch head (criterion 1/3)");
    }

    [TestMethod]
    public async Task Reuse_AscendingSequencesAdvanceThroughCommits()
    {
        // Two already-published commits ensured on the reuse path in ascending-sequence order: each higher
        // sequence advances the branch head forward to the newly-ensured commit (the forward half of the
        // gate, exercised entirely through the no-worker reuse path).
        var (snapA, snapB) = StartWithTwoPublishedCommits();

        await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 10));
        Assert.AreEqual(snapA, Head(), "the first sequence advances the default branch head to A");

        await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: 20));
        Assert.AreEqual(snapB, Head(),
            "a strictly higher sequence advances the head forward to B on the reuse path (criterion 1)");
    }

    [TestMethod]
    public async Task Reuse_EqualSequence_DoesNotAdvance()
    {
        var (snapA, snapB) = StartWithTwoPublishedCommits();
        await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: 7));

        // A delayed duplicate advance carrying the SAME sequence (defense-in-depth) must not move the head.
        await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 7));
        Assert.AreEqual(snapB, Head(), "an equal sequence does not advance the pointer (forward-only, strict >)");
    }

    [TestMethod]
    public async Task Reuse_NullSequence_PreservesAttachIfUnset()
    {
        var (snapA, snapB) = StartWithTwoPublishedCommits();

        // No sequence + a named branch → today's forward-only attach-if-unset: the first attach sets the
        // pointer, a later null-sequence attach for an OLDER commit never supersedes it (criterion 2).
        await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: null, branch: "release"));
        Assert.AreEqual(snapB, HeadOf("release"));
        await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: null, branch: "release"));
        Assert.AreEqual(snapB, HeadOf("release"), "null-sequence reuse keeps the pre-#84 attach-if-unset behavior");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private (long snapA, long snapB) StartWithTwoPublishedCommits()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var snapA = ServiceTestFixtures.PublishComplete(_db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = "commit-A" });
        var snapB = ServiceTestFixtures.PublishComplete(_db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = "commit-B" });
        // A worker that MUST NOT run — every ensure below hits the already-published reuse path.
        var worker = new FakeSnapshotWorker(_db, FakeSnapshotWorker.Throws("worker must not run on the reuse path"));
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        return (snapA, snapB);
    }

    private static EnsureSnapshotRequest Ensure(string commit, long? seq, string? branch = null) => new()
    {
        RepositoryRemoteUrl = Repo,
        CommitSha = commit,
        BranchName = branch,
        BranchHeadSequence = seq
    };

    private long? Head() => _service.ResolveBranch(Repo, null)?.Id;
    private long? HeadOf(string branch) => _service.ResolveBranch(Repo, branch)?.Id;
}
