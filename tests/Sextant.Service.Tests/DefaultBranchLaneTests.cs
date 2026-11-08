using System.Collections.Concurrent;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #403: the write gate was one FIFO queue shared by every production, so a push to a default branch waited behind
/// every feature-branch build admitted before it (hours when feature branches queue up). An ensure that claims the default
/// branch now takes an expedited lane, bounded so feature branches still progress.
/// </summary>
[TestClass]
public class DefaultBranchLaneTests
{
    private const string Repo = "https://github.com/acme/widgets";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;
    private readonly ConcurrentQueue<string> _produced = new();
    private readonly ManualResetEventSlim _release = new(initialState: true);

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var worker = new FakeSnapshotWorker(_db, Produce);
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _release.Set();
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task Gate_AnExpeditedTurnGoesBeforeNormalTurns_AndAfterPriorityTurns()
    {
        var gate = new WriteGate();
        await gate.WaitAsync();
        var order = new List<string>();
        var n1 = Take(gate, "n1", order, expedited: false);
        var e1 = Take(gate, "e1", order, expedited: true);
        var e2 = Take(gate, "e2", order, expedited: true);
        var p1 = Take(gate, "p1", order, expedited: false, priority: true);

        gate.Release();
        await Task.WhenAll(n1, e1, e2, p1).WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { "p1", "e1", "e2", "n1" }, order);
    }

    [TestMethod]
    public async Task Gate_ANormalTurnRunsAfterTheStreakLimit_SoFeatureBranchesAreNotStarved()
    {
        var gate = new WriteGate(expeditedStreakLimit: 2);
        await gate.WaitAsync();
        var order = new List<string>();
        var turns = new List<Task> { Take(gate, "n1", order, expedited: false), Take(gate, "n2", order, expedited: false) };
        for (var i = 1; i <= 5; i++)
            turns.Add(Take(gate, $"e{i}", order, expedited: true));

        gate.Release();
        await Task.WhenAll(turns).WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { "e1", "e2", "n1", "e3", "e4", "n2", "e5" }, order,
            "two expedited turns, then one normal turn while normal turns wait");
    }

    [TestMethod]
    public async Task Gate_ACancelledExpeditedTurnLeavesTheQueue()
    {
        var gate = new WriteGate();
        await gate.WaitAsync();
        using var cts = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(cts.Token, expedited: true);
        var order = new List<string>();
        var next = Take(gate, "n1", order, expedited: false);

        cts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);
        Assert.AreEqual(0, gate.ExpeditedWaiters);
        gate.Release();
        await next.WaitAsync(Settle);
        CollectionAssert.AreEqual(new[] { "n1" }, order);
    }

    [TestMethod]
    public async Task ADefaultBranchPush_AdmittedBehindFeatureBranchWork_RunsBeforeIt()
    {
        _release.Reset();
        var hold = _service.EnsureSnapshotAsync(Request("hold", "other"));
        var f1 = _service.EnsureSnapshotAsync(Request("feature-1", "feature-a"));
        var f2 = _service.EnsureSnapshotAsync(Request("feature-2", "feature-b"));
        var main = _service.EnsureSnapshotAsync(Request("main-1", "main") with { IsDefaultBranch = true });
        _release.Set();

        await Task.WhenAll(hold, f1, f2, main).WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { "hold", "main-1", "feature-1", "feature-2" }, _produced.ToArray());
    }

    [TestMethod]
    public async Task ABranchUpdateNoneEnsure_ClaimingTheDefault_DoesNotJumpTheQueue()
    {
        _release.Reset();
        var hold = _service.EnsureSnapshotAsync(Request("hold", "other"));
        var f1 = _service.EnsureSnapshotAsync(Request("feature-1", "feature-a"));
        var none = _service.EnsureSnapshotAsync(Request("historical", "main") with { IsDefaultBranch = true, BranchUpdate = "none" });
        _release.Set();

        await Task.WhenAll(hold, f1, none).WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { "hold", "feature-1", "historical" }, _produced.ToArray());
    }

    private SnapshotWorkResult Produce(FakeSnapshotWorker self, EnsureSnapshotRequest request)
    {
        _release.Wait(TimeSpan.FromSeconds(30));
        _produced.Enqueue(request.CommitSha);
        var snapshotId = ServiceTestFixtures.PublishComplete(self.Database, request, recordCommit: true);
        return SnapshotWorkResult.Complete(snapshotId);
    }

    private static EnsureSnapshotRequest Request(string commit, string branch) =>
        new() { RepositoryRemoteUrl = Repo, CommitSha = commit, BranchName = branch };

    private static Task Take(WriteGate gate, string name, List<string> order, bool expedited, bool priority = false) =>
        gate.WaitAsync(priority: priority, expedited: expedited).ContinueWith(_ =>
        {
            lock (order)
                order.Add(name);
            gate.Release();
        }, TaskScheduler.Default);
}
