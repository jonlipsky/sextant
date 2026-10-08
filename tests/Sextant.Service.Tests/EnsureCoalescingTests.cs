using System.Collections.Concurrent;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #273: a branch-advance ensure (expected_head_commit, as a repository push sends) still waiting for its turn is
/// superseded by a newer push of the same branch that expects it as the head. It settles as <c>coalesced</c> without
/// running the worker and records a durable handover of its compare-and-swap, which the newer ensure resolves at its own
/// turn. The branch therefore ends where serial processing would leave it while the index builds only the newest commit.
/// The worker here applies the real compare-and-swap advance, as the orchestrator does, so the branch head is observable.
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
    private readonly ManualResetEventSlim _release = new(initialState: true);
    private readonly ConcurrentDictionary<string, int> _transientFailures = new(StringComparer.Ordinal);

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _worker = new FakeSnapshotWorker(_db, Produce);
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), _worker, _db);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _release.Set();
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task QueuedPush_SupersededByTheNextPush_IsCoalesced_AndTheBranchEndsOnTheNewest()
    {
        await SeedMainAt("commit-A");

        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();

        var coalesced = await b;
        var newest = await c;
        await hold;

        Assert.AreEqual(SnapshotJobStatus.Coalesced, coalesced.Status);
        Assert.IsNull(coalesced.SnapshotId, "a coalesced ensure publishes nothing");
        StringAssert.Contains(coalesced.Reason, "commit-C");
        Assert.AreEqual(SnapshotJobStatus.Coalesced, _service.GetStatus(coalesced.JobId)!.Job.Status, "durably recorded");
        Assert.AreEqual(SnapshotJobStatus.Complete, newest.Status);
        Assert.IsTrue(newest.BranchAdvanced);
        CollectionAssert.AreEqual(new[] { "commit-A", "commit-hold", "commit-C" }, Built(),
            "the worker never builds the superseded commit");
        Assert.AreEqual("commit-C", HeadCommit("main"), "main advances from A to C, as building B then C would leave it");
    }

    [TestMethod]
    public async Task AChainOfQueuedPushes_CoalescesToTheNewest()
    {
        await SeedMainAt("commit-A");

        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-B");
        var d = Push("commit-D", expected: "commit-C");
        _release.Set();
        await Task.WhenAll(hold, b, c, d);

        Assert.AreEqual(SnapshotJobStatus.Coalesced, (await b).Status);
        Assert.AreEqual(SnapshotJobStatus.Coalesced, (await c).Status);
        Assert.AreEqual(SnapshotJobStatus.Complete, (await d).Status);
        CollectionAssert.AreEqual(new[] { "commit-A", "commit-hold", "commit-D" }, Built());
        Assert.AreEqual("commit-D", HeadCommit("main"));
    }

    [TestMethod]
    public async Task ASupersededPushWhoseCommitIsAlreadyBuilt_AttachesAndAdvances_AndTheNextPushStillAdvances()
    {
        // A fast-forward merge of a commit already built (here on another branch): its push attaches at no cost and moves
        // the head, so it must not hand anything over.
        await SeedMainAt("commit-A");
        await _service.EnsureSnapshotAsync(Request("commit-B", "feature") with { ExpectedHeadCommit = "" });

        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(hold, b, c);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await b).Status, "an already-built commit attaches");
        Assert.IsTrue((await b).BranchAdvanced);
        Assert.IsTrue((await c).BranchAdvanced, "C's own expectation (B) matches the head B moved to");
        Assert.AreEqual("commit-C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task ASuccessorRetriedAfterATransientFailure_StillResolvesTheHandover()
    {
        await SeedMainAt("commit-A");
        _transientFailures["commit-C"] = 1;

        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(hold, b, c);
        Assert.AreEqual(SnapshotJobStatus.Coalesced, (await b).Status);
        Assert.AreEqual(SnapshotJobStatus.Queued, (await c).Status, "precondition: C was requeued");
        Assert.AreEqual("commit-A", HeadCommit("main"));

        // The client re-sends C exactly as pushed (expecting B), and to a fresh instance: the handover is durable.
        _service.Dispose();
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), _worker, _db);
        var retried = await Push("commit-C", expected: "commit-B");

        Assert.AreEqual(SnapshotJobStatus.Complete, retried.Status);
        Assert.IsTrue(retried.BranchAdvanced);
        Assert.AreEqual("commit-C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task AHandoverStopsApplying_OnceItsCommitIsBuilt()
    {
        await SeedMainAt("commit-A");
        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(hold, b, c);

        // B is built later without moving main, then main is reset back to A. A stale push expecting B must not pass:
        // serially its compare-and-swap against B would fail.
        await _service.EnsureSnapshotAsync(Request("commit-B", "main") with { BranchUpdate = "none" });
        await Push("commit-A", expected: "commit-C");
        Assert.AreEqual("commit-A", HeadCommit("main"), "precondition: main was reset to A");

        var stale = await Push("commit-E", expected: "commit-B");

        Assert.IsFalse(stale.BranchAdvanced);
        Assert.AreEqual("commit-A", HeadCommit("main"));
    }

    [TestMethod]
    public async Task AHandoverIsConsumedByTheNextHeadMove_SoAResetCannotReviveIt()
    {
        await SeedMainAt("commit-A");
        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(hold, b, c);
        Assert.AreEqual("commit-C", HeadCommit("main"));

        // B stays unbuilt. main is reset to A, then C's push is redelivered (still expecting B). Serially its
        // compare-and-swap against B fails, so main must stay at A.
        await Push("commit-A", expected: "commit-C");
        Assert.AreEqual("commit-A", HeadCommit("main"), "precondition: main was reset to A");
        var redelivered = await Push("commit-C", expected: "commit-B");

        Assert.IsFalse(redelivered.BranchAdvanced);
        Assert.AreEqual("commit-A", HeadCommit("main"));
    }

    [TestMethod]
    public async Task OnlyTheLinkedSuccessor_ResolvesAHandover()
    {
        await SeedMainAt("commit-A");
        _transientFailures["commit-C"] = 1;
        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(hold, b, c);
        Assert.AreEqual(SnapshotJobStatus.Queued, (await c).Status, "precondition: C was requeued, so B's handover is unused");

        var other = await Push("commit-E", expected: "commit-B");

        Assert.IsFalse(other.BranchAdvanced, "a push that did not supersede B cannot take over its compare-and-swap");
        Assert.AreEqual("commit-A", HeadCommit("main"));
    }

    [TestMethod]
    public async Task ASupersededPushSharingItsCommitWithAnotherPendingEnsure_IsProduced()
    {
        // The job row is per identity, not per branch: another branch's pending ensure of the same commit must see it built.
        await SeedMainAt("commit-A");
        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var feature = _service.EnsureSnapshotAsync(Request("commit-B", "feature") with { ExpectedHeadCommit = "" });
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(hold, b, feature, c);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await b).Status);
        Assert.AreEqual(SnapshotJobStatus.Complete, (await feature).Status);
        CollectionAssert.AreEqual(new[] { "commit-A", "commit-hold", "commit-B", "commit-C" }, Built());
        Assert.AreEqual("commit-C", HeadCommit("main"));
        Assert.AreEqual("commit-B", HeadCommit("feature"));
    }

    [TestMethod]
    public async Task PushesOfDifferentBranches_AreNotCoalesced()
    {
        var hold = Hold();
        var b = Push("commit-B", expected: "", branch: "main");
        var c = Push("commit-C", expected: "commit-B", branch: "feature");
        _release.Set();
        await Task.WhenAll(hold, b, c);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await b).Status);
        CollectionAssert.AreEqual(new[] { "commit-hold", "commit-B", "commit-C" }, Built());
    }

    [TestMethod]
    public async Task AnUnlinkedPush_DoesNotSupersede()
    {
        // C does not expect B as the head (an out-of-order or unrelated push): both run, in order, and C's own
        // compare-and-swap decides.
        await SeedMainAt("commit-A");
        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-X");
        _release.Set();
        await Task.WhenAll(hold, b, c);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await b).Status);
        Assert.IsFalse((await c).BranchAdvanced);
        Assert.AreEqual("commit-B", HeadCommit("main"));
    }

    [TestMethod]
    public async Task EnsuresWithoutAnAdvanceGuard_AreNeverCoalesced()
    {
        var hold = Hold();
        var none = _service.EnsureSnapshotAsync(Request("commit-B", "main") with
        {
            ExpectedHeadCommit = "commit-A", BranchUpdate = "none"
        });
        var unguarded = _service.EnsureSnapshotAsync(Request("commit-C", "main"));
        var linked = Push("commit-D", expected: "commit-C");
        _release.Set();
        await Task.WhenAll(hold, none, unguarded, linked);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await none).Status, "branch_update: none never coalesces");
        Assert.AreEqual(SnapshotJobStatus.Complete, (await unguarded).Status, "an unguarded ensure never coalesces");
        Assert.AreEqual(4, _worker.Calls);
    }

    [TestMethod]
    public async Task AStartedPush_IsNotSuperseded()
    {
        await SeedMainAt("commit-A");
        _release.Reset();
        var b = Push("commit-B", expected: "commit-A");
        await WaitUntil(() => _worker.Calls == 2);
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(b, c);

        Assert.AreEqual(SnapshotJobStatus.Complete, (await b).Status, "once producing, a push runs to completion");
        CollectionAssert.AreEqual(new[] { "commit-A", "commit-B", "commit-C" }, Built());
        Assert.AreEqual("commit-C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task ACoalescedIdentity_IsProducedByALaterEnsure_UnderTheSameJob()
    {
        await SeedMainAt("commit-A");
        var hold = Hold();
        var b = Push("commit-B", expected: "commit-A");
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(hold, b, c);

        var again = await _service.EnsureSnapshotAsync(Request("commit-B", "main") with { BranchUpdate = "none" });

        Assert.AreEqual(SnapshotJobStatus.Complete, again.Status, "coalesced is settled, not terminal");
        Assert.AreEqual((await b).JobId, again.JobId, "the identity keeps its one durable job");
        Assert.IsNotNull(again.SnapshotId);
        Assert.AreEqual("commit-C", HeadCommit("main"), "branch_update: none moves nothing");
    }

    [TestMethod]
    public async Task ACoalescedEnsure_IsAudited_AndCounted_ButIsNotAnOutcome()
    {
        var hold = Hold();
        var b = Push("commit-B", expected: "");
        var c = Push("commit-C", expected: "commit-B");
        _release.Set();
        await Task.WhenAll(hold, b, c);

        var metrics = _service.CollectMetrics();

        Assert.AreEqual(1, metrics.Jobs.Coalesced);
        Assert.AreEqual(2, metrics.Jobs.Terminal, "a coalesced job is not a terminal outcome");
        Assert.AreEqual(1.0, metrics.Jobs.SuccessRate);
        Assert.AreEqual(1, _service.RecentAudit().Count(e => e.Outcome == AuditOutcome.Coalesced));
    }

    // Publishes the request's snapshot and applies the compare-and-swap branch advance, as the orchestrator does. While
    // _release is reset the worker blocks, holding the single writer so later ensures queue behind it.
    private SnapshotWorkResult Produce(FakeSnapshotWorker self, EnsureSnapshotRequest request)
    {
        _release.Wait(TimeSpan.FromSeconds(30));
        _produced.Enqueue(request);
        if (_transientFailures.TryGetValue(request.CommitSha, out var left) && left > 0)
        {
            _transientFailures[request.CommitSha] = left - 1;
            throw new TransientProvisioningException("fetch timed out");
        }
        var snapshotId = ServiceTestFixtures.PublishComplete(self.Database, request, recordCommit: true);
        if (request.BranchName is { } branch && request.ExpectedHeadCommit is { } expected && !request.SuppressesBranchUpdate)
        {
            var store = new SnapshotStore(self.Database.GetConnection());
            var repoId = store.GetRepositoryId(Repo)!.Value;
            if (store.BranchHeadMatches(store.GetBranchId(repoId, branch), expected))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var branchId = store.EnsureBranch(repoId, branch, isDefault: branch == "main", now);
                store.AdvanceBranchPointerIfHeadMatches(branchId, snapshotId, expected, now);
            }
        }
        return SnapshotWorkResult.Complete(snapshotId);
    }

    private async Task SeedMainAt(string commit)
    {
        var seeded = await Push(commit, expected: "");
        Assert.AreEqual(commit, HeadCommit("main"), "precondition: main is seeded");
        Assert.IsTrue(seeded.BranchAdvanced);
    }

    // Takes the writer with another repository's-worth of work (another branch) and blocks it until _release is set.
    private Task<EnsureSnapshotResult> Hold()
    {
        _release.Reset();
        return _service.EnsureSnapshotAsync(Request("commit-hold", "other"));
    }

    private Task<EnsureSnapshotResult> Push(string commit, string expected, string branch = "main") =>
        _service.EnsureSnapshotAsync(Request(commit, branch) with { ExpectedHeadCommit = expected });

    private static EnsureSnapshotRequest Request(string commit, string branch) =>
        new() { RepositoryRemoteUrl = Repo, CommitSha = commit, BranchName = branch };

    private string[] Built() => _produced.Select(r => r.CommitSha).ToArray();

    private string? HeadCommit(string branch)
    {
        var store = new SnapshotStore(_db.GetConnection());
        return store.GetRepositoryId(Repo) is long repoId
               && store.GetBranchId(repoId, branch) is long branchId
               && store.GetBranchSnapshotId(branchId) is long snapshotId
            ? store.GetCommitSha(store.GetById(snapshotId)!.CommitId)
            : null;
    }

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
