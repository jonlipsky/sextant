using System.Collections.Concurrent;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #273: a branch-advance ensure (expected_head_commit, as a repository push sends) still waiting for its turn is
/// superseded by a newer push of the same branch that expects it as the head. It settles as <c>coalesced</c> without
/// running the worker, and the newer ensure takes over its compare-and-swap, so the pointer advances exactly as serial
/// processing would have left it while the index builds only the newest commit.
/// </summary>
[TestClass]
public class EnsureCoalescingTests
{
    private const string Repo = "https://github.com/acme/widgets";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;
    private FakeSnapshotWorker _worker = null!;
    private readonly ConcurrentQueue<EnsureSnapshotRequest> _produced = new();

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _worker = new FakeSnapshotWorker(_db, (self, request) =>
        {
            _produced.Enqueue(request);
            return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(self.Database, request));
        }) { UseGate = true };
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), _worker, _db);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task QueuedPush_SupersededByTheNextPush_IsCoalesced_AndTheNextCarriesItsCompareAndSwap()
    {
        var hold = Ensure("commit-hold", "other", expected: null);
        var b = Ensure("commit-B", "main", expected: "commit-A");
        var c = Ensure("commit-C", "main", expected: "commit-B");
        _worker.Gate.SetResult();

        var coalesced = await b;
        var newest = await c;
        await hold;

        Assert.AreEqual(SnapshotJobStatus.Coalesced, coalesced.Status);
        Assert.IsNull(coalesced.SnapshotId, "a coalesced ensure publishes nothing");
        StringAssert.Contains(coalesced.Reason, "commit-C");
        Assert.AreEqual(SnapshotJobStatus.Coalesced, _service.GetStatus(coalesced.JobId)!.Job.Status, "durably recorded");
        Assert.AreEqual(SnapshotJobStatus.Complete, newest.Status);
        CollectionAssert.AreEqual(new[] { "commit-hold", "commit-C" }, _produced.Select(r => r.CommitSha).ToArray(),
            "the worker never builds the superseded commit");
        Assert.AreEqual("commit-A", _produced.Last().ExpectedHeadCommit,
            "the newest push expects what the superseded one expected, since that one never moved the head");
    }

    [TestMethod]
    public async Task AChainOfQueuedPushes_CoalescesToTheNewest()
    {
        var hold = Ensure("commit-hold", "other", expected: null);
        var b = Ensure("commit-B", "main", expected: "commit-A");
        var c = Ensure("commit-C", "main", expected: "commit-B");
        var d = Ensure("commit-D", "main", expected: "commit-C");
        _worker.Gate.SetResult();

        await Task.WhenAll(hold, b, c, d);

        Assert.AreEqual(SnapshotJobStatus.Coalesced, (await b).Status);
        Assert.AreEqual(SnapshotJobStatus.Coalesced, (await c).Status);
        Assert.AreEqual(SnapshotJobStatus.Complete, (await d).Status);
        Assert.AreEqual(2, _worker.Calls);
        Assert.AreEqual("commit-A", _produced.Last().ExpectedHeadCommit);
    }

    [TestMethod]
    public async Task PushesOfDifferentBranches_AreNotCoalesced()
    {
        var hold = Ensure("commit-hold", "other", expected: null);
        var b = Ensure("commit-B", "main", expected: "commit-A");
        var c = Ensure("commit-C", "feature", expected: "commit-B");
        _worker.Gate.SetResult();

        await Task.WhenAll(hold, b, c);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await b).Status);
        Assert.AreEqual(3, _worker.Calls);
        Assert.AreEqual("commit-B", _produced.Last().ExpectedHeadCommit, "nothing was carried over");
    }

    [TestMethod]
    public async Task AnUnlinkedPush_DoesNotSupersede()
    {
        // C does not expect B as the head (an out-of-order or unrelated push): both run, in order.
        var hold = Ensure("commit-hold", "other", expected: null);
        var b = Ensure("commit-B", "main", expected: "commit-A");
        var c = Ensure("commit-C", "main", expected: "commit-X");
        _worker.Gate.SetResult();

        await Task.WhenAll(hold, b, c);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await b).Status);
        Assert.AreEqual(3, _worker.Calls);
        Assert.AreEqual("commit-X", _produced.Last().ExpectedHeadCommit);
    }

    [TestMethod]
    public async Task EnsuresWithoutAnAdvanceGuard_AreNeverCoalesced()
    {
        var hold = Ensure("commit-hold", "other", expected: null);
        var none = _service.EnsureSnapshotAsync(Request("commit-B", "main") with
        {
            ExpectedHeadCommit = "commit-A", BranchUpdate = "none"
        });
        var unguarded = Ensure("commit-C", "main", expected: null);
        var linked = Ensure("commit-D", "main", expected: "commit-C");
        _worker.Gate.SetResult();

        await Task.WhenAll(hold, none, unguarded, linked);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await none).Status, "branch_update: none never coalesces");
        Assert.AreEqual(SnapshotJobStatus.Complete, (await unguarded).Status, "an unguarded ensure never coalesces");
        Assert.AreEqual(4, _worker.Calls);
    }

    [TestMethod]
    public async Task AStartedPush_IsNotSuperseded()
    {
        var b = Ensure("commit-B", "main", expected: "commit-A");
        await WaitUntil(() => _worker.Calls == 1);
        var c = Ensure("commit-C", "main", expected: "commit-B");
        _worker.Gate.SetResult();

        await Task.WhenAll(b, c);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await b).Status, "once producing, a push runs to completion");
        Assert.AreEqual(2, _worker.Calls);
        Assert.AreEqual("commit-B", _produced.Last().ExpectedHeadCommit);
    }

    [TestMethod]
    public async Task ACoalescedIdentity_IsProducedByALaterEnsure()
    {
        var hold = Ensure("commit-hold", "other", expected: null);
        var b = Ensure("commit-B", "main", expected: "commit-A");
        var c = Ensure("commit-C", "main", expected: "commit-B");
        _worker.Gate.SetResult();
        await Task.WhenAll(hold, b, c);

        var again = await _service.EnsureSnapshotAsync(Request("commit-B", "main") with { BranchUpdate = "none" });

        Assert.AreEqual(SnapshotJobStatus.Complete, again.Status, "coalesced is settled, not terminal");
        Assert.AreEqual((await b).JobId, again.JobId, "the identity keeps its one durable job");
        Assert.IsNotNull(again.SnapshotId);
        Assert.AreEqual(3, _worker.Calls);
    }

    [TestMethod]
    public async Task ACoalescedEnsure_IsAudited_AndCounted_ButIsNotAnOutcome()
    {
        var hold = Ensure("commit-hold", "other", expected: null);
        var b = Ensure("commit-B", "main", expected: "commit-A");
        var c = Ensure("commit-C", "main", expected: "commit-B");
        _worker.Gate.SetResult();
        await Task.WhenAll(hold, b, c);

        var metrics = _service.CollectMetrics();

        Assert.AreEqual(1, metrics.Jobs.Coalesced);
        Assert.AreEqual(2, metrics.Jobs.Terminal, "a coalesced job is not a terminal outcome");
        Assert.AreEqual(1.0, metrics.Jobs.SuccessRate);
        Assert.AreEqual(1, _service.RecentAudit().Count(e => e.Outcome == AuditOutcome.Coalesced));
    }

    private Task<EnsureSnapshotResult> Ensure(string commit, string branch, string? expected) =>
        _service.EnsureSnapshotAsync(Request(commit, branch) with { ExpectedHeadCommit = expected });

    private static EnsureSnapshotRequest Request(string commit, string branch) =>
        new() { RepositoryRemoteUrl = Repo, CommitSha = commit, BranchName = branch };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("timed out waiting for the condition");
            await Task.Delay(10);
        }
    }
}
