using System.Collections.Concurrent;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #403: the write gate was one FIFO queue shared by every production, so a push to a default branch waited behind
/// every feature-branch build admitted before it (hours when feature branches queue up). An ensure that claims the default
/// branch now takes an expedited FIFO lane, including requests that omit the default-branch flag.
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
    public async Task Gate_AllDefaultTurnsRunBeforeFeatureTurns_InFifoOrder()
    {
        var gate = new WriteGate();
        await gate.WaitAsync();
        var order = new List<string>();
        var turns = new List<Task> { Take(gate, "n1", order, expedited: false), Take(gate, "n2", order, expedited: false) };
        for (var i = 1; i <= 5; i++)
            turns.Add(Take(gate, $"e{i}", order, expedited: true));

        gate.Release();
        await Task.WhenAll(turns).WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { "e1", "e2", "e3", "e4", "e5", "n1", "n2" }, order);
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
    public async Task AKnownDefaultWithoutTheFlag_RunsNextAheadOfFiftyFeatures()
    {
        await _service.EnsureSnapshotAsync(Request("seed", "trunk") with { IsDefaultBranch = true });
        var snapshots = new SnapshotStore(_db.GetConnection());
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        var branch = snapshots.EnsureBranch(repo, "trunk", true, 1);
        snapshots.SetBranchPointer(branch, snapshots.ResolveCompleteSnapshotByCommit(repo, "seed")!.Value, 1);
        _release.Reset();
        var hold = _service.EnsureSnapshotAsync(Request("hold", "other"));
        var features = Enumerable.Range(0, 50)
            .Select(i => _service.EnsureSnapshotAsync(Request($"feature-{i}", $"branch-{i}"))).ToArray();
        var main = _service.EnsureSnapshotAsync(Request("main-1", "trunk"));
        var main2 = _service.EnsureSnapshotAsync(Request("main-2", "trunk"));
        _release.Set();

        await Task.WhenAll(features.Append(hold).Append(main).Append(main2)).WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { "seed", "hold", "main-1", "main-2" }
            .Concat(Enumerable.Range(0, 50).Select(i => $"feature-{i}")).ToArray(), _produced.ToArray());
    }

    [TestMethod]
    public async Task AContinuousDefaultStreamLetsAWaitingFeatureStartWithinTheConfiguredBurst()
    {
        await _service.EnsureSnapshotAsync(Request("seed", "trunk") with { IsDefaultBranch = true });
        var snapshots = new SnapshotStore(_db.GetConnection());
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        var branch = snapshots.EnsureBranch(repo, "trunk", true, 1);
        snapshots.SetBranchPointer(branch, snapshots.ResolveCompleteSnapshotByCommit(repo, "seed")!.Value, 1);
        _release.Reset();
        var hold = _service.EnsureSnapshotAsync(Request("hold", "other"));
        var feature = _service.EnsureSnapshotAsync(Request("feature", "feature"));
        var defaults = Enumerable.Range(0, 12)
            .Select(i => _service.EnsureSnapshotAsync(Request($"main-{i}", "trunk"))).ToArray();
        _release.Set();

        await Task.WhenAll(defaults.Append(feature).Append(hold)).WaitAsync(Settle);

        CollectionAssert.AreEqual(
            new[] { "seed", "hold" }
                .Concat(Enumerable.Range(0, WriteGate.DefaultExpeditedStreakLimit).Select(i => $"main-{i}"))
                .Append("feature")
                .Concat(Enumerable.Range(WriteGate.DefaultExpeditedStreakLimit, defaults.Length - WriteGate.DefaultExpeditedStreakLimit)
                    .Select(i => $"main-{i}"))
                .ToArray(),
            _produced.ToArray());
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
