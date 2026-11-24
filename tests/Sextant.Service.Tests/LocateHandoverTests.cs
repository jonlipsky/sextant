using System.Collections.Concurrent;
using System.Text.Json;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

[TestClass]
public sealed class LocateHandoverTests
{
    private const string Repo = "https://github.com/acme/locate-handover";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;
    private FakeSnapshotWorker _worker = null!;
    private readonly ConcurrentQueue<string> _built = new();
    private readonly ManualResetEventSlim _release = new(true);
    private readonly TaskCompletionSource _holdStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestInitialize]
    public void Initialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _worker = new FakeSnapshotWorker(_db, Produce);
        _service = Start();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _release.Set();
        _service?.Dispose();
        _release.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    [DataRow("main")]
    [DataRow("feature")]
    public async Task LinkedPushesCoalesceOnEveryBranch(string branch)
    {
        await Seed(branch);
        _release.Reset();
        var hold = _service.EnsureSnapshotAsync(Request("hold", "other"));
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var b = _service.EnsureSnapshotAsync(Push("B", branch, "A") with { Forced = true });
        var c = _service.EnsureSnapshotAsync(Push("C", branch, "B"));
        var d = _service.EnsureSnapshotAsync(Push("D", branch, "C"));
        _release.Set();

        await Task.WhenAll(hold, b, c, d).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(SnapshotJobStatus.Coalesced, (await b).Status);
        Assert.AreEqual(SnapshotJobStatus.Coalesced, (await c).Status);
        Assert.IsTrue((await d).BranchAdvanced);
        Assert.AreEqual("D", Head(branch));
        CollectionAssert.DoesNotContain(_built.ToArray(), "B");
        CollectionAssert.DoesNotContain(_built.ToArray(), "C");
    }

    [TestMethod]
    [DataRow("main")]
    [DataRow("feature")]
    public async Task RestartReplaysPersistedMultiHopHandover(string branch)
    {
        await Seed(branch);
        _service.Dispose();
        var jobs = new SnapshotJobStore(_db.GetConnection());
        var key = RemoteUrlIdentity.Normalize(Repo);
        jobs.RecordHandover(key, branch, "B", "A", "C");
        jobs.RecordHandover(key, branch, "C", "B", "D");
        var request = Push("D", branch, "C");
        using (var cmd = _db.GetConnection().CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO queued_ensures
                    (seq, identity_hash, request_json, restricts_implicit_default, hashed_actor, admitted_at)
                VALUES (1, @hash, @json, 0, 'actor', @at);
                """;
            cmd.Parameters.AddWithValue("@hash", request.ToIdentity().Hash);
            cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(request, ServiceJson.Options));
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        _service = Start();
        await WaitUntil(() => Head(branch) == "D");
        Assert.AreEqual("D", Head(branch));
        CollectionAssert.Contains(_built.ToArray(), "D");
    }

    [TestMethod]
    public async Task RestrictedReplayWithoutDefaultResolvesMultiHopHandover()
    {
        await Seed("feature");
        _service.Dispose();
        var conn = _db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        snapshots.DeleteBranch(snapshots.GetBranchId(repo, "main")!.Value);
        Assert.IsNull(snapshots.GetDefaultBranchId(repo));
        var jobs = new SnapshotJobStore(conn);
        var key = RemoteUrlIdentity.Normalize(Repo);
        jobs.RecordHandover(key, "feature", "B", "A", "C");
        jobs.RecordHandover(key, "feature", "C", "B", "D");
        var request = Push("D", "feature", "C");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO queued_ensures
                    (seq, identity_hash, request_json, restricts_implicit_default, hashed_actor, admitted_at)
                VALUES (1, @hash, @json, 1, 'actor', @at);
                """;
            cmd.Parameters.AddWithValue("@hash", request.ToIdentity().Hash);
            cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(request, ServiceJson.Options));
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        _service = Start();
        await WaitUntil(() => Head("feature") == "D");
        Assert.AreEqual("D", Head("feature"));
    }

    private SnapshotService Start() => SnapshotService.Start(
        ServiceTestFixtures.NewOptions(_dbPath) with { Maintenance = new() { Interval = TimeSpan.Zero } },
        _worker, _db);

    private async Task Seed(string branch)
    {
        if (branch != "main")
            await _service.EnsureSnapshotAsync(Push("main-seed", "main", ""));
        var seed = await _service.EnsureSnapshotAsync(Push("A", branch, ""));
        Assert.IsTrue(seed.BranchAdvanced);
        Assert.AreEqual("A", Head(branch));
    }

    private SnapshotWorkResult Produce(FakeSnapshotWorker self, EnsureSnapshotRequest request)
    {
        if (request.CommitSha == "hold")
        {
            _holdStarted.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(30));
        }
        _built.Enqueue(request.CommitSha);
        var snapshot = ServiceTestFixtures.PublishComplete(self.Database, request, recordCommit: true);
        if (request.BranchName is { } branch && request.ExpectedHeadCommit is { } expected)
        {
            var store = new SnapshotStore(self.Database.GetConnection());
            var repo = store.GetRepositoryId(Repo)!.Value;
            if (store.BranchHeadMatches(store.GetBranchId(repo, branch), expected, repo, branch))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var id = store.EnsureBranch(repo, branch, branch == "main", now);
                store.AdvanceBranchPointerIfHeadMatches(id, snapshot, expected, now);
            }
        }
        return SnapshotWorkResult.Complete(snapshot);
    }

    private static EnsureSnapshotRequest Request(string commit, string branch) =>
        new() { RepositoryRemoteUrl = Repo, CommitSha = commit, BranchName = branch };

    private static EnsureSnapshotRequest Push(string commit, string branch, string expected) =>
        Request(commit, branch) with { ExpectedHeadCommit = expected };

    private string? Head(string branch)
    {
        using var conn = _db.OpenReadConnection();
        var store = new SnapshotStore(conn);
        return store.GetRepositoryId(Repo) is long repo && store.GetBranchId(repo, branch) is long id
            && store.GetBranchSnapshotId(id) is long snapshot
            ? store.GetCommitSha(store.GetById(snapshot)!.CommitId)
            : null;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for the replayed branch head.");
            await Task.Delay(20);
        }
    }
}
