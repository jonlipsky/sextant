using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Service;
using Sextant.Service.Host;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

[TestClass]
public sealed class DefaultBranchRefreshTests
{
    private const string Repo = "https://github.com/acme/refresh";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;
    private FakeSnapshotWorker _worker = null!;
    private RefreshRemote _remote = null!;
    private ManualClock _clock = null!;
    private readonly TaskCompletionSource _holdStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _built = new();
    private readonly ConcurrentDictionary<string, int> _failOnce = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _terminalFailOnce = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _returnedFailOnce = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _unsupportedCommits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _partialCommits = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _progress = new(0);
    private string? _blockedCommit;
    private TaskCompletionSource? _blockedStarted;
    private TaskCompletionSource? _releaseBlocked;
    private Action<EnsureSnapshotRequest>? _onProduce;

    [TestInitialize]
    public void Initialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _clock = new ManualClock(DateTimeOffset.UtcNow);
        _remote = new RefreshRemote();
        _worker = new FakeSnapshotWorker(_db) { AsyncBehavior = ProduceAsync };
        _service = StartService();
    }

    [TestMethod]
    public async Task QueuedSameSnapshotAttachDoesNotRepairToAnOlderRemoteHead()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["archive"] = "A";
        await _service.EnsureSnapshotAsync(Request("A", "archive") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        _remote.Head = "C";
        _remote.AllAncestors = true;
        BlockCommit("B");
        var earlier = _service.EnsureSnapshotAsync(Request("B", "main") with { ExpectedHeadCommit = "A" });
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var later = _service.EnsureSnapshotAsync(Request("A", "main") with { ExpectedHeadCommit = "A" });
        await WaitUntil(() => QueuedEnsureCount() >= 2);
        _releaseBlocked!.TrySetResult();
        await earlier.WaitAsync(TimeSpan.FromSeconds(10));
        var attached = await later.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(attached.AdmissionId);
        Assert.AreEqual(false, attached.BranchAdvanced);
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task SameSnapshotEnsureRecordsNewerSequenceBeforeOlderAdvance()
    {
        _remote.Head = "A";
        await _service.EnsureSnapshotAsync(Request("A", "main") with { BranchHeadSequence = 10 });
        await WaitUntil(() => PendingRefreshCount() == 0);
        var repeat = await _service.EnsureSnapshotAsync(Request("A", "main") with { BranchHeadSequence = 30 });
        Assert.IsNotNull(repeat.AdmissionId);
        await WaitUntil(() => PendingRefreshCount() == 0);
        _remote.Head = "A";
        await _service.EnsureSnapshotAsync(Request("B", "main") with { BranchHeadSequence = 20 });
        Assert.AreEqual("A", HeadCommit("main"));
    }

    [TestMethod]
    public async Task SameSnapshotEnsurePromotesPreindexedDefault()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["trunk"] = "A";
        await _service.EnsureSnapshotAsync(Request("A", "trunk") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        var result = await _service.EnsureSnapshotAsync(Request("A", "trunk") with { IsDefaultBranch = true });
        Assert.IsNotNull(result.AdmissionId);
        using var conn = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        var repository = snapshots.GetRepositoryId(Repo)!.Value;
        Assert.AreEqual("trunk", snapshots.GetBranchById(snapshots.GetDefaultBranchId(repository)!.Value)!.Name);
    }

    [TestMethod]
    public async Task RetryingRefreshOnOneBranchDoesNotBlockAnotherBranch()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["x"] = "X";
        await _service.EnsureSnapshotAsync(Request("X", "x") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        _remote.TimeoutHeadReads = 1;
        var retrying = _service.EnsureSnapshotAsync(Request("hint", "x") with { Refresh = true });
        await WaitUntil(() => PendingRefreshAttempts() == 1);
        var retryingAdmission = ReadPendingRefreshes().Single().AdmissionId;

        _remote.BranchHeads["y"] = "Y";
        var healthy = await _service.EnsureSnapshotAsync(Request("hint", "y") with { Refresh = true });

        Assert.AreEqual(SnapshotJobStatus.Complete, healthy.Status);
        Assert.AreEqual("Y", HeadCommit("y"));
        Assert.AreEqual("retrying", _service.GetRefreshAdmissionStatus(retryingAdmission)!.Status);
        Assert.AreEqual(1, PendingRefreshAttempts());
        Assert.IsFalse(retrying.IsCompleted);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _releaseHold.TrySetResult();
        _releaseBlocked?.TrySetResult();
        _service?.Dispose();
        Assert.AreEqual(0, _service?.WriterTraceViolationsForTesting ?? 0,
            "every statement on the shared writer must belong to the granted logical turn");
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task FiveQueuedDefaultPushes_AreAbsorbedIntoOneRefreshAndIndexRemoteHead()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _remote.Head = "E";
        var admissions = await Task.WhenAll(new[]
        {
            AdmitPush("B", "A"),
            AdmitPush("C", "B"),
            AdmitPush("D", "unrelated", forced: true),
            AdmitPush("E", "D"),
            AdmitPush("F", "E")
        });
        Assert.AreEqual(1, admissions.Count(r => r.Status == SnapshotJobStatus.Queued));
        Assert.AreEqual(4, admissions.Count(r => r.Status == SnapshotJobStatus.Coalesced));
        Assert.IsTrue(admissions.All(r => r.JobId is null));
        // Issue #273 (FIFO): each push lands directly behind the first, still pending refresh and is absorbed into it,
        // so every caller holds that one admission.
        Assert.AreEqual(1, admissions.Select(r => r.AdmissionId).Distinct().Count());
        Assert.AreEqual(1, PendingRefreshCount());
        Assert.AreEqual(1, QueuedEnsureCount(), "only the unrelated writer hold is in queued_ensures");
        Assert.AreEqual("queued", _service.GetRefreshAdmissionStatus(admissions[0].AdmissionId!)!.Status);
        _releaseHold.TrySetResult();

        await hold.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        CollectionAssert.AreEqual(new[] { "A", "hold", "E" }, _built.ToArray());
        Assert.AreEqual("E", HeadCommit("main"));
        var statuses = admissions.Select(r => _service.GetRefreshAdmissionStatus(r.AdmissionId!)!).ToArray();
        Assert.IsTrue(statuses.All(s => s.Status == "completed"));
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount, "all admitted turns release once");
    }

    [TestMethod]
    public async Task UncatalogedRemoteDefaultBootstrapsAndAbsorbsQueuedPushes()
    {
        _remote.DefaultBranch = "main";
        _remote.Head = "C";
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var admissions = await Task.WhenAll(new[]
        {
            AdmitPush("hint-1", "before-1"),
            AdmitPush("hint-2", "before-2"),
            AdmitPush("hint-3", "before-3")
        });
        Assert.AreEqual(1, admissions.Count(r => r.Status == SnapshotJobStatus.Queued));
        Assert.AreEqual(1, admissions.Select(r => r.AdmissionId).Distinct().Count(), "adjacent pending pushes share one refresh");
        Assert.AreEqual(1, PendingRefreshCount());
        Assert.AreEqual(1, QueuedEnsureCount(), "only the unrelated writer hold is in queued_ensures");
        _releaseHold.TrySetResult();

        await hold.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
        CollectionAssert.AreEqual(new[] { "hold", "C" }, _built.ToArray());
        var statuses = admissions.Select(r => _service.GetRefreshAdmissionStatus(r.AdmissionId!)!).ToArray();
        Assert.IsTrue(statuses.All(s => s.Status == "completed"));
        using var conn = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        var repositoryId = snapshots.GetRepositoryId(Repo)!.Value;
        Assert.AreEqual("main", snapshots.GetBranchById(snapshots.GetDefaultBranchId(repositoryId)!.Value)!.Name);
    }

    [TestMethod]
    [DataRow("main")]
    [DataRow("feature")]
    public async Task AutomaticRefreshPreservesSelectedRebuildOfVerifiedHead(string branch)
    {
        await SeedMainAt("A");
        if (branch != "main")
        {
            _remote.BranchHeads[branch] = "A";
            await _service.EnsureSnapshotAsync(Request("A", branch) with { ExpectedHeadCommit = "" });
            await WaitUntil(() => PendingRefreshCount() == 0);
        }
        var rebuilt = await _service.EnsureSnapshotAsync(Request("A", branch) with
        {
            RebuildGeneration = "repair-1", ExpectedHeadCommit = "A"
        });
        await WaitUntil(() => PendingRefreshCount() == 0);
        using var conn = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        Assert.AreEqual(rebuilt.SnapshotId, snapshots.GetBranch(repo, branch)!.SnapshotId);
        Assert.AreEqual(SnapshotStatus.Complete, snapshots.GetById(rebuilt.SnapshotId!.Value)!.Status);
        CollectionAssert.AreEqual(new[] { "A", "A" }, _built.ToArray());
    }

    [TestMethod]
    public async Task ExplicitEnsureRetryDeadlineSurvivesRestart()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        _failOnce["B"] = 1;
        var result = await _service.EnsureSnapshotAsync(
            Request("B", "main") with { ExpectedHeadCommit = "A" });
        Assert.AreEqual(SnapshotJobStatus.Queued, result.Status);
        Assert.IsNotNull(result.AdmissionId);
        var before = _service.GetRefreshAdmissionStatus(result.AdmissionId)!;
        Assert.AreEqual("retrying", before.Status);
        Assert.AreEqual(1, before.Attempts);
        Assert.AreEqual("ensure_queued", before.LastError);

        _service.Dispose();
        _service = StartService();
        var after = _service.GetRefreshAdmissionStatus(result.AdmissionId)!;
        Assert.AreEqual("retrying", after.Status);
        Assert.AreEqual(before.NextAttemptAt, after.NextAttemptAt);
        _clock.Advance(TimeSpan.FromSeconds(29));
        _service.RescanBranchIntentsForTesting();
        Assert.AreEqual(1, _built.Count(commit => commit == "B"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => _built.Count(commit => commit == "B") == 2 && PendingRefreshCount() == 0);
        Assert.AreEqual("B", HeadCommit("main"));
    }

    [TestMethod]
    public async Task ConcurrentIntentRescansDoNotReadmitTheSameEnsure()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        _failOnce["B"] = 1;
        var result = await _service.EnsureSnapshotAsync(
            Request("B", "main") with { ExpectedHeadCommit = "A" });
        BranchIntent intent;
        using (var conn = _db.OpenReadConnection())
            intent = new BranchIntentStore(conn).GetByAdmissionId(result.AdmissionId!)!;
        await WaitUntil(() => !_service.HasLiveQueuedEnsureForTesting(intent.QueuedEnsureId!.Value));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.BeforeEnsureIntentRescheduleForTesting = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Rescan test was not released.");
        };
        try
        {
            var advance = Task.Run(() => _clock.Advance(TimeSpan.FromSeconds(30)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(_service.RescanBranchIntentsForTesting).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, _built.Count(commit => commit == "B"));
            release.Set();
            await advance.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(() => _built.Count(commit => commit == "B") == 2 && PendingRefreshCount() == 0);
            Assert.AreEqual(2, _built.Count(commit => commit == "B"));
        }
        finally
        {
            release.Set();
            _service.BeforeEnsureIntentRescheduleForTesting = null;
        }
    }

    [TestMethod]
    public async Task FailedIntentReschedulePersistsItsRetryDeadline()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        _failOnce["B"] = 1;
        var result = await _service.EnsureSnapshotAsync(
            Request("B", "main") with { ExpectedHeadCommit = "A" });
        BranchIntent intent;
        using (var conn = _db.OpenReadConnection())
            intent = new BranchIntentStore(conn).GetByAdmissionId(result.AdmissionId!)!;
        await WaitUntil(() => !_service.HasLiveQueuedEnsureForTesting(intent.QueuedEnsureId!.Value));
        _service.BeforeEnsureIntentRescheduleForTesting = () => throw new InvalidOperationException("test");
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(result.AdmissionId!)?.Attempts == 2);
        _service.BeforeEnsureIntentRescheduleForTesting = null;

        var status = _service.GetRefreshAdmissionStatus(result.AdmissionId!)!;
        Assert.AreEqual("retrying", status.Status);
        Assert.AreEqual(2, status.Attempts);
        Assert.AreEqual("reschedule_InvalidOperationException", status.LastError);
        Assert.AreEqual(_clock.GetUtcNow().AddSeconds(60).ToUnixTimeMilliseconds(), status.NextAttemptAt);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticRefreshPreservesSelectedAliasIdentity(bool rebuild)
    {
        await SeedMainAt("A");
        var commit = rebuild ? "A" : "B";
        _remote.Head = commit;
        var ensured = await _service.EnsureSnapshotAsync(Request(commit, "main") with
        {
            RepositoryRemoteUrl = Repo + ".git",
            ExpectedHeadCommit = "A",
            RebuildGeneration = rebuild ? "repair-alias" : null
        });
        await WaitUntil(() => PendingRefreshCount() == 0);

        using var conn = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        Assert.AreEqual(ensured.SnapshotId, snapshots.GetBranch(repo, "main")!.SnapshotId);
        CollectionAssert.AreEqual(new[] { "A", commit }, _built.ToArray());
    }

    [TestMethod]
    public async Task HistoricalAliasIdentityIsReusedAcrossBranchesAndRefresh()
    {
        await SeedMainAt("A");
        _service.Dispose();
        var oldRequest = Request("B", "other") with
        {
            RepositoryRemoteUrl = Repo + ".git",
            BranchUpdate = BranchUpdateMode.None
        };
        var snapshot = ServiceTestFixtures.PublishComplete(_db, oldRequest, recordCommit: true);
        var jobs = new SnapshotJobStore(_db.GetConnection());
        var (oldJob, _) = jobs.EnsureJob(oldRequest.ToIdentity().Hash, oldRequest.RepositoryRemoteUrl, "B", "other");
        jobs.MarkResult(oldJob.Id, SnapshotJobStatus.Complete, snapshot);
        var snapshots = new SnapshotStore(_db.GetConnection());
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        var branch = snapshots.EnsureBranch(repo, "other", false, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        snapshots.SetBranchPointer(branch, snapshot, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        _service = StartService();

        var retry = await _service.EnsureSnapshotAsync(oldRequest);
        var alias = await _service.EnsureSnapshotAsync(Request("B", "main") with { BranchUpdate = BranchUpdateMode.None });
        Assert.AreEqual(snapshot, retry.SnapshotId);
        Assert.AreEqual(snapshot, alias.SnapshotId);
        Assert.AreEqual(oldJob.Id, alias.JobId);
        _remote.Head = "B";
        await _service.EnsureSnapshotAsync(Request("hint", "main") with { Refresh = true });
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("B", HeadCommit("main"));
        using (var read = _db.OpenReadConnection())
            Assert.AreEqual(snapshot, new SnapshotStore(read).GetBranch(repo, "main")!.SnapshotId);
        CollectionAssert.AreEqual(new[] { "A" }, _built.ToArray());

        var distinct = await _service.EnsureSnapshotAsync(Request("B", "main") with
        {
            BranchUpdate = BranchUpdateMode.None, ConfigHash = "different-config"
        });
        Assert.AreNotEqual(snapshot, distinct.SnapshotId);
        CollectionAssert.AreEqual(new[] { "A", "B" }, _built.ToArray());
    }

    [TestMethod]
    public async Task HistoricalSnapshotOnlyAliasIsReusedWhenProjectRetainsSpelling()
    {
        await SeedMainAt("A");
        _service.Dispose();
        var oldRequest = Request("B", "other") with
        {
            RepositoryRemoteUrl = Repo + ".git",
            BranchUpdate = BranchUpdateMode.None
        };
        var snapshot = ServiceTestFixtures.PublishComplete(_db, oldRequest, recordCommit: true);
        var snapshots = new SnapshotStore(_db.GetConnection());
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        var branch = snapshots.EnsureBranch(repo, "other", false, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        snapshots.SetBranchPointer(branch, snapshot, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        _service = StartService();

        var alias = await _service.EnsureSnapshotAsync(Request("B", "main") with { BranchUpdate = BranchUpdateMode.None });
        Assert.AreEqual(snapshot, alias.SnapshotId);
        CollectionAssert.AreEqual(new[] { "A" }, _built.ToArray());
        var distinct = await _service.EnsureSnapshotAsync(Request("B", "main") with
        {
            BranchUpdate = BranchUpdateMode.None, ConfigHash = "different-config"
        });
        Assert.AreNotEqual(snapshot, distinct.SnapshotId);
        CollectionAssert.AreEqual(new[] { "A", "B" }, _built.ToArray());
    }

    [TestMethod]
    public async Task AutomaticRefreshPreservesRebuildWhenOriginalIdentityWasNeverPublished()
    {
        _service.Dispose();
        _service = StartService(useRemote: false);
        _remote.Head = "A";
        await _service.EnsureSnapshotAsync(Request("A", "main") with
        {
            ConfigHash = "old-config", ExpectedHeadCommit = ""
        });
        _service.Dispose();

        using (var conn = _db.OpenReadConnection())
            Assert.IsNull(new SnapshotStore(conn).GetByIdentityHash(Request("A", "main").ToIdentity().Hash));

        _service = StartService();
        var rebuilt = await _service.EnsureSnapshotAsync(Request("A", "main") with
        {
            RebuildGeneration = "repair-missing-original", ExpectedHeadCommit = "A"
        });
        await WaitUntil(() => PendingRefreshCount() == 0);

        using var read = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(read);
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        Assert.AreEqual(rebuilt.SnapshotId, snapshots.GetBranch(repo, "main")!.SnapshotId);
        CollectionAssert.AreEqual(new[] { "A", "A" }, _built.ToArray());
    }

    [TestMethod]
    public async Task AutomaticRefreshPreservesRebuildWhenOriginalSnapshotWasReclaimed()
    {
        _service.Dispose();
        _service = StartService(useRemote: false);
        _remote.Head = "A";
        var original = await _service.EnsureSnapshotAsync(Request("A", "main") with { ExpectedHeadCommit = "" });
        var rebuilt = await _service.EnsureSnapshotAsync(Request("A", "main") with
        {
            RebuildGeneration = "repair-reclaimed-original", ExpectedHeadCommit = "A"
        });
        _service.Dispose();

        using (var command = _db.GetConnection().CreateCommand())
        {
            command.CommandText = """
                DELETE FROM projects WHERE snapshot_id = @id;
                DELETE FROM snapshots WHERE id = @id;
                """;
            command.Parameters.AddWithValue("@id", original.SnapshotId!.Value);
            command.ExecuteNonQuery();
        }

        _service = StartService();
        var result = await _service.EnsureSnapshotAsync(Request("ignored", "main") with { Refresh = true });
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(rebuilt.SnapshotId, result.SnapshotId);
        using var conn = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        var repo = snapshots.GetRepositoryId(Repo)!.Value;
        Assert.IsNull(snapshots.GetById(original.SnapshotId!.Value));
        Assert.AreEqual(rebuilt.SnapshotId, snapshots.GetBranch(repo, "main")!.SnapshotId);
        CollectionAssert.AreEqual(new[] { "A", "A" }, _built.ToArray());
    }

    [TestMethod]
    public async Task UncatalogedVerifiedBranchWithDifferentDefaultBecomesFeature()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["feature"] = "C";
        var result = await _service.EnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual("C", HeadCommit("feature"));
        Assert.AreEqual("main", ServedDefault());
    }

    [TestMethod]
    public async Task UncatalogedHeadConfirmationHasInjectedDeadlineEvenWhenResolverIgnoresCancellation()
    {
        _remote.BranchHeads["feature"] = "B";
        var options = ServiceTestFixtures.NewOptions(_dbPath);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ServiceApp.RegisterServices(builder, options, _service);
        await using var app = builder.Build();
        ServiceApp.MapEndpoints(app, options);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ControlToken}");
        var (started, release) = _remote.BlockNextHeadRead();
        try
        {
            var admission = client.PostAsJsonAsync("/control/ensure?wait=false",
                Request("hint", "feature") with { Refresh = true }, ServiceJson.Options);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _clock.Advance(TimeSpan.FromSeconds(30));
            using var response = await admission.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("refresh_lookup_busy", body.RootElement.GetProperty("reason").GetString());
            Assert.AreEqual(0, PendingRefreshCount());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task UncatalogedRefreshReturnsRetryableResponseWhenLookupSlotsAreFull()
    {
        var options = ServiceTestFixtures.NewOptions(_dbPath);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ServiceApp.RegisterServices(builder, options, _service);
        await using var app = builder.Build();
        ServiceApp.MapEndpoints(app, options);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ControlToken}");
        using var gate = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _remote.OnHeadRead = () =>
        {
            if (Interlocked.Increment(ref calls) == RemoteDefaultBranchLookup.MaxConcurrentLookups)
                started.TrySetResult();
            gate.Wait();
        };
        var requests = Enumerable.Range(0, RemoteDefaultBranchLookup.MaxConcurrentLookups)
            .Select(i => client.PostAsJsonAsync("/control/ensure?wait=false",
                Request("hint", $"feature-{i}") with { Refresh = true }, ServiceJson.Options)).ToArray();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var response = await client.PostAsJsonAsync("/control/ensure?wait=false",
                Request("hint", "another") with { Refresh = true }, ServiceJson.Options);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("refresh_lookup_busy", body.RootElement.GetProperty("reason").GetString());
            Assert.AreEqual(0, PendingRefreshCount());
        }
        finally
        {
            gate.Set();
            foreach (var response in await Task.WhenAll(requests))
                response.Dispose();
            _remote.OnHeadRead = null;
        }
        _remote.TimeoutHeadReads = 1;
        using var unavailable = await client.PostAsJsonAsync("/control/ensure?wait=false",
            Request("hint", "unavailable") with { Refresh = true }, ServiceJson.Options);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        using var unavailableBody = System.Text.Json.JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.AreEqual("refresh_lookup_busy", unavailableBody.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task TimedOutStuckLookupsDoNotBlockAHealthyRepositoryRefresh()
    {
        using var gate = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _remote.OnHeadRead = () =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call <= RemoteDefaultBranchLookup.MaxConcurrentLookups)
            {
                if (call == RemoteDefaultBranchLookup.MaxConcurrentLookups)
                    started.TrySetResult();
                gate.Wait();
            }

        };
        var stuckRequests = Enumerable.Range(0, RemoteDefaultBranchLookup.MaxConcurrentLookups)
            .Select(i => _service.BeginEnsureSnapshotAsync(Request("hint", $"stuck-{i}") with
            {
                RepositoryRemoteUrl = $"https://github.com/acme/stuck-{i}",
                Refresh = true
            })).ToArray();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _clock.Advance(TimeSpan.FromSeconds(30));
            foreach (var request in stuckRequests)
                await Assert.ThrowsExactlyAsync<RefreshLookupUnavailableException>(async () => await request);

            _remote.OnHeadRead = null;
            _remote.Head = "D";
            var healthyRepository = "https://github.com/acme/healthy";
            var healthyRequest = Request("hint", "main") with
            {
                RepositoryRemoteUrl = healthyRepository,
                Refresh = true
            };
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _service.BeginEnsureSnapshotAsync(healthyRequest);
                    break;
                }
                catch (RefreshLookupUnavailableException) when (attempt < 9)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20));
                }
            }
            await WaitUntil(() => PendingRefreshCount() == 0);

            using var conn = _db.OpenReadConnection();
            var snapshots = new SnapshotStore(conn);
            var repositoryId = snapshots.GetRepositoryId(healthyRepository)!.Value;
            var branchId = snapshots.GetBranchId(repositoryId, "main")!.Value;
            var snapshotId = snapshots.GetBranchSnapshotId(branchId)!.Value;
            Assert.AreEqual("D", snapshots.GetCommitSha(snapshots.GetById(snapshotId)!.CommitId));
        }
        finally
        {
            gate.Set();
            _remote.OnHeadRead = null;
            foreach (var request in stuckRequests)
                try { await request; } catch (RefreshLookupUnavailableException) { }
        }
    }

    [TestMethod]
    public async Task DefaultPointerWaitsForFinalVerifiedHeadAfterProduction()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        BlockCommit("B");
        await AdmitPush("B", "A");
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var releaseB = _releaseBlocked!;
        _remote.Head = "C";
        BlockCommit("C");
        releaseB.TrySetResult();
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("A", HeadCommit("main"));
        _releaseBlocked!.TrySetResult();
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task DefaultForcePushUsesVerifiedNonAncestorHead()
    {
        await SeedMainAt("A");
        _remote.NonAncestor = true;
        _remote.Head = "B";
        var result = await Push("B", "A");
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual("B", HeadCommit("main"));
        Assert.AreEqual(0, PendingRefreshCount());
    }

    [TestMethod]
    public async Task FinalHeadLookupObservesNewHeadWithoutAnotherAdmission()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        BlockCommit("B");
        await AdmitPush("B", "A");
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _remote.Head = "C";
        var (started, release) = _remote.BlockNextStateRead();
        var releaseB = _releaseBlocked!;
        BlockCommit("C");
        releaseB.TrySetResult();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("A", HeadCommit("main"));
            Assert.AreEqual(1, PendingRefreshCount());
            release.TrySetResult();
            await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("A", HeadCommit("main"), "B must not be selected after C was observed");
            Assert.AreEqual(1, PendingRefreshCount());
            _releaseBlocked!.TrySetResult();
            await WaitUntil(() => PendingRefreshCount() == 0);
            Assert.AreEqual("C", HeadCommit("main"));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task PushAfterFinalObservationHasItsOwnMarker()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        BlockCommit("B");
        var first = await AdmitPush("B", "A");
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.AfterStateRead = () => _remote.Head = "C";
        _releaseBlocked!.TrySetResult();
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(first.AdmissionId!)?.Status == "completed");
        Assert.AreEqual("B", HeadCommit("main"));
        var second = await AdmitPush("C", "B");
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(second.AdmissionId!)?.Status == "completed");
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task HeadIsRecheckedAfterBlockedDefaultLookup()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        BlockCommit("B");
        await AdmitPush("B", "A");
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var (started, release) = _remote.BlockNextStateRead();
        _releaseBlocked!.TrySetResult();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.Head = "C";
        BlockCommit("C");
        release.TrySetResult();
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("A", HeadCommit("main"));
        Assert.AreEqual(1, PendingRefreshCount());
        _releaseBlocked!.TrySetResult();
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task UncatalogedRefreshWithoutVerifiedRemoteDefaultIsRejectedAndAudited()
    {
        _remote.DefaultBranch = "develop";
        _remote.Head = "C";
        _remote.MissingBranches.Add("main");

        var result = await _service.BeginEnsureSnapshotAsync(Request("hint", "main") with
        {
            ExpectedHeadCommit = "before",
            Refresh = true
        });

        Assert.AreEqual("rejected", result.Status);
        Assert.AreEqual(BranchGuardReason.RefreshDefaultUnconfirmed, result.Reason);
        Assert.AreEqual(0, PendingRefreshCount());
        Assert.IsTrue(_service.RecentAudit(action: AuditAction.Ensure).Any(row =>
            row.Outcome == AuditOutcome.Denied && row.Detail?.Contains(
                BranchGuardReason.RefreshDefaultUnconfirmed, StringComparison.Ordinal) == true));
        Assert.AreEqual(0, _built.Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("?wait=true")]
    [DataRow("?wait=false")]
    public async Task HttpRefreshReturnsCommittedAdmissionWithoutWaitingForBlockedWorker(string query)
    {
        await SeedMainAt("A");
        _remote.Head = "C";
        BlockCommit("C");
        var options = ServiceTestFixtures.NewOptions(_dbPath);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ServiceApp.RegisterServices(builder, options, _service);
        await using var app = builder.Build();
        ServiceApp.MapEndpoints(app, options);
        await app.StartAsync();
        using var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync("/control/ensure" + query,
            Request("hint", "main") with { Refresh = true }, ServiceJson.Options)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var admission = (await response.Content.ReadFromJsonAsync<EnsureSnapshotResult>(ServiceJson.Options))!;
        Assert.AreEqual("queued", admission.Status);
        Assert.IsNotNull(admission.AdmissionId);
        Assert.IsNull(admission.SnapshotId);
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("A", HeadCommit("main"));
        _releaseBlocked!.TrySetResult();
        await WaitUntil(() => PendingRefreshCount() == 0);
        var status = await client.GetFromJsonAsync<RefreshAdmissionStatus>(
            "/control/refresh-status/" + admission.AdmissionId, ServiceJson.Options);
        Assert.AreEqual("completed", status!.Status);
        Assert.AreEqual("C", status.CommitSha);
    }

    // Rules 2, 4 and 6: a refresh absorbed into a pending refresh reports `coalesced` with that refresh's id; it has not
    // run yet, so it is a 202 admission like the first, never a settled 200.
    [TestMethod]
    [DataRow("")]
    [DataRow("?wait=true")]
    [DataRow("?wait=false")]
    public async Task HttpAbsorbedRefreshIsAcceptedNotSettled(string query)
    {
        await SeedMainAt("A");
        _remote.Head = "C";
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var options = ServiceTestFixtures.NewOptions(_dbPath);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ServiceApp.RegisterServices(builder, options, _service);
        await using var app = builder.Build();
        ServiceApp.MapEndpoints(app, options);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var first = await client.PostAsJsonAsync("/control/ensure" + query,
            Request("hint-1", "main") with { Refresh = true }, ServiceJson.Options).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);
        var queued = (await first.Content.ReadFromJsonAsync<EnsureSnapshotResult>(ServiceJson.Options))!;
        Assert.AreEqual("queued", queued.Status);
        var second = await client.PostAsJsonAsync("/control/ensure" + query,
            Request("hint-2", "main") with { Refresh = true }, ServiceJson.Options).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(HttpStatusCode.Accepted, second.StatusCode, "an absorbed refresh has not run yet");
        var absorbed = (await second.Content.ReadFromJsonAsync<EnsureSnapshotResult>(ServiceJson.Options))!;
        Assert.AreEqual(SnapshotJobStatus.Coalesced, absorbed.Status);
        Assert.AreEqual(queued.AdmissionId, absorbed.AdmissionId);
        Assert.AreEqual("A", HeadCommit("main"));

        _releaseHold.TrySetResult();
        await hold.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    // Rule 7: a refresh that cannot finish (the remote head stays unknown) retries on its own due times and, past the
    // maximum age, ends `expired` with no effect.
    [TestMethod]
    public async Task UnreachableHeadRefreshExpiresAfterTheMaximumAge_WithNoEffect()
    {
        await SeedMainAt("A");
        _remote.Head = null;
        var refresh = _service.EnsureSnapshotAsync(Request("hint", "main") with { Refresh = true });
        await WaitUntil(() => PendingRefreshAttempts() == 1);
        var admission = ReadPendingRefreshes().Single().AdmissionId;
        Assert.AreEqual("retrying", _service.GetRefreshAdmissionStatus(admission)!.Status);

        _clock.Advance(TimeSpan.FromDays(8));
        await WaitUntil(() => PendingRefreshCount() == 0 || ClockStep(), QueueDiagnostics);
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("expired", result.Status);
        Assert.AreEqual("expired", _service.GetRefreshAdmissionStatus(admission)!.Status);
        Assert.AreEqual("A", HeadCommit("main"), "an expired refresh moves nothing");
    }

    [TestMethod]
    public async Task RefreshWithoutCasIndexesOnlyVerifiedHead()
    {
        await SeedMainAt("A");
        _remote.Head = "C";
        var result = await _service.EnsureSnapshotAsync(Request("hint", "main") with { Refresh = true });
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual("C", HeadCommit("main"));
        CollectionAssert.AreEqual(new[] { "A", "C" }, _built.ToArray());
    }

    [TestMethod]
    public async Task PushAfterClaim_CreatesASecondMarkerAndWriterTurn()
    {
        await SeedMainAt("A");
        BlockCommit("B");
        _remote.Head = "B";
        var first = await AdmitPush("B", "A");
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, PendingRefreshCount(true), "the first marker is claimed before remote-head work");

        _remote.Head = "C";
        var next = await AdmitPush("C", "B");
        Assert.AreEqual(SnapshotJobStatus.Queued, next.Status);
        Assert.AreNotEqual(first.AdmissionId, next.AdmissionId);
        Assert.AreEqual(2, PendingRefreshCount());
        Assert.AreEqual(1, PendingRefreshCount(claimedOnly: true));
        _releaseBlocked!.TrySetResult();

        await WaitUntil(() => PendingRefreshCount() == 0);
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, _built.ToArray());
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task ClaimedSlotHeldDuringRetryReportsRetrying()
    {
        await SeedMainAt("A");
        _remote.Head = null;
        var (started, release) = _remote.BlockNextHeadRead();
        var first = await AdmitPush("ignored", "A");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await AdmitPush("ignored-again", "A");
        Assert.AreNotEqual(first.AdmissionId, second.AdmissionId);
        release.TrySetResult();
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(first.AdmissionId!)?.Attempts == 1);
        var status = _service.GetRefreshAdmissionStatus(first.AdmissionId!)!;
        Assert.AreEqual("retrying", status.Status);
        Assert.AreEqual("remote_head_unknown", status.LastError);
        Assert.IsNotNull(status.NextAttemptAt);
    }

    [TestMethod]
    public async Task UnknownRemoteHead_DoesNotIndexOrMovePointerAndRetriesAfterBackoff()
    {
        await SeedMainAt("A");
        _remote.Head = null;

        var admission = await AdmitPush("B", "A");
        Assert.AreEqual(SnapshotJobStatus.Queued, admission.Status);
        await WaitUntil(() => PendingRefreshAttempts() == 1);
        Assert.AreEqual("A", HeadCommit("main"));
        CollectionAssert.AreEqual(new[] { "A" }, _built.ToArray(), "an unknown head creates no job and indexes nothing");

        _remote.Head = "C";
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
        CollectionAssert.AreEqual(new[] { "A", "C" }, _built.ToArray());
    }

    [TestMethod]
    public async Task AdmissionStatus_TransitionsFromQueuedToCompleted()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.Head = "C";
        var admitted = await AdmitPush("ignored", "A");
        var queued = _service.GetRefreshAdmissionStatus(admitted.AdmissionId!);
        Assert.AreEqual("queued", queued!.Status);
        Assert.AreEqual(0, queued.Attempts);

        _releaseHold.TrySetResult();
        await hold.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(admitted.AdmissionId!)?.Status == "completed");
        var completed = _service.GetRefreshAdmissionStatus(admitted.AdmissionId!)!;
        Assert.IsNotNull(completed.SnapshotId);
        Assert.AreEqual("C", completed.CommitSha);
        Assert.IsNull(completed.NextAttemptAt);
    }

    [TestMethod]
    public async Task AdmissionStatus_ReportsRetryingMetadata()
    {
        await SeedMainAt("A");
        _remote.Head = null;
        var admitted = await AdmitPush("ignored", "A");
        await WaitUntil(() => PendingRefreshAttempts() == 1);

        var retrying = _service.GetRefreshAdmissionStatus(admitted.AdmissionId!);
        Assert.AreEqual("retrying", retrying!.Status);
        Assert.AreEqual(1, retrying.Attempts);
        Assert.IsNotNull(retrying.NextAttemptAt);
        Assert.AreEqual("remote_head_unknown", retrying.LastError);
    }

    [TestMethod]
    public async Task TurnAndFinalRecheckDoNotJoinBlockedPreTurnIntakeObservation()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.Head = "B";
        await _service.BeginEnsureSnapshotAsync(Request("hint", "trunk") with { Refresh = true });
        var (started, release) = _remote.BlockNextHeadRead();
        var intake = _service.BeginEnsureSnapshotAsync(Request("another-hint", "trunk") with { Refresh = true });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.Head = "C";
        _releaseHold.TrySetResult();
        await hold.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => HeadCommit("trunk") == "C");
        Assert.IsFalse(intake.IsCompleted, "both repair observations finish independently of the earlier intake");
        release.TrySetResult();
        await intake.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => PendingRefreshCount() == 0);
        CollectionAssert.AreEqual(new[] { "A", "hold", "C" }, _built.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClaimFailureOrPreClaimException_RetriesOnItsOwnDueTime(bool throws)
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        var calls = 0;
        _service.BeforeRefreshClaim = () =>
        {
            Interlocked.Increment(ref calls);
            if (throws)
                throw new InvalidOperationException("test pre-claim failure");
            using var command = _db.GetConnection().CreateCommand();
            command.CommandText = "UPDATE pending_refreshes SET next_attempt_at = @next;";
            command.Parameters.AddWithValue("@next", _clock.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        };
        await AdmitPush("ignored", "A");
        await WaitUntil(() => Volatile.Read(ref calls) > 0 && _service.RefreshTurnCountForTesting == 0);
        Assert.AreEqual(1, calls, "a failed turn must not spin");
        Assert.AreEqual(1, PendingRefreshCount());
        Assert.AreEqual(0, PendingRefreshAttempts());
        Assert.AreEqual("A", HeadCommit("main"));

        _service.BeforeRefreshClaim = null;
        _clock.Advance(TimeSpan.FromSeconds(60));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("B", HeadCommit("main"));
    }

    // Rule 7: no periodic sweep. A refresh whose turn stopped before its claim waits for its own armed wake: a
    // maintenance tick past its due time does not start it, and a wake whose scan faults re-arms itself and still runs it.
    [TestMethod]
    public async Task DueRefreshIsNotStartedByMaintenance_AndAFaultedWakeReArmsAndRunsIt()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        var claims = 0;
        _service.BeforeRefreshClaim = () =>
        {
            if (Interlocked.Increment(ref claims) == 1)
                throw new InvalidOperationException("test pre-claim failure");
        };
        var wakeFaults = 0;
        _service.BeforeIntentWakeScanForTesting = () =>
        {
            if (Interlocked.Increment(ref wakeFaults) == 1)
                throw new InvalidOperationException("test wake fault");
        };
        await AdmitPush("ignored", "A");
        await WaitUntil(() => Volatile.Read(ref claims) == 1 && _service.RefreshTurnCountForTesting == 0);
        var firstWake = _service.ArmedIntentWakeForTesting;
        Assert.IsNotNull(firstWake, "the stopped turn armed its own wake");

        // The wake fires and its scan faults: it re-arms for a later time instead of leaving the retry to maintenance.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => Volatile.Read(ref wakeFaults) == 1
            && _service.ArmedIntentWakeForTesting is long rearmed && rearmed > firstWake.Value);

        // Past the refresh's due time, a maintenance tick starts nothing.
        await _service.RunMaintenanceTickAsync();
        Assert.AreEqual(0, _service.RefreshTurnCountForTesting, "maintenance does not dispatch the retry");
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, Volatile.Read(ref claims), "no turn claimed it");
        Assert.AreEqual(1, PendingRefreshCount());
        Assert.AreEqual("A", HeadCommit("main"));

        // The re-armed wake runs it.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => PendingRefreshCount() == 0, QueueDiagnostics);
        Assert.AreEqual("B", HeadCommit("main"));
        Assert.AreEqual(2, Volatile.Read(ref claims));
    }

    // Rule 7: one armed wake. A request for an earlier due time replaces the armed wake; a later one is covered by it.
    [TestMethod]
    public void IntentWake_KeepsOneArmedWake_ReplacedByAnEarlierDueTime()
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        _service.ScheduleIntentRetryAtForTesting(now + 60_000);
        Assert.AreEqual(now + 60_000, _service.ArmedIntentWakeForTesting);
        _service.ScheduleIntentRetryAtForTesting(now + 90_000);
        Assert.AreEqual(now + 60_000, _service.ArmedIntentWakeForTesting, "a later due time is covered");
        _service.ScheduleIntentRetryAtForTesting(now + 30_000);
        Assert.AreEqual(now + 30_000, _service.ArmedIntentWakeForTesting, "an earlier due time replaces it");
    }

    [TestMethod]
    public async Task CancelledAdmissionCallerDoesNotStopCommittedRefresh()
    {
        await SeedMainAt("A");
        _remote.Head = "C";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.BeforeRefreshAdmissionWrite = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        using var cancel = new CancellationTokenSource();
        var admission = _service.BeginEnsureSnapshotAsync(
            Request("hint", "main") with { Refresh = true }, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        release.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await admission);
        await WaitUntil(() => HeadCommit("main") == "C");
        CollectionAssert.AreEqual(new[] { "A", "C" }, _built.ToArray());
    }

    [TestMethod]
    public async Task RescanCompletingBeforeAdmissionRegistersWaiterStillSettlesCaller()
    {
        await SeedMainAt("A");
        _remote.Head = "C";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.AfterRefreshAdmissionCommit = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var waiting = _service.EnsureSnapshotAsync(Request("hint", "main") with { Refresh = true });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Another scheduling pass (a wake for any due intent) starts and completes the refresh first.
        _service.RescanBranchIntentsForTesting();
        await WaitUntil(() => HeadCommit("main") == "C" && PendingRefreshCount() == 0);
        release.TrySetResult();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.IsNotNull(result.SnapshotId);
    }

    [TestMethod]
    public async Task FailureBeforeFirstCatalogReadReleasesReservedTurn()
    {
        await SeedMainAt("A");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.BeforeRefreshRead = () =>
        {
            entered.TrySetResult();
            throw new InvalidOperationException("test read failure");
        };
        await AdmitPush("ignored", "A");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await _service.EnsureSnapshotAsync(Request("B", "feature") with { BranchUpdate = BranchUpdateMode.None })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(1, PendingRefreshCount());
    }

    [TestMethod]
    public async Task ShutdownBeforeFirstRefreshReadReleasesReservedTurn()
    {
        await SeedMainAt("A");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.BeforeRefreshRead = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        await AdmitPush("ignored", "A");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _service.StopProduction();
        release.TrySetResult();
        await WaitUntil(() => _service.RefreshTurnCountForTesting == 0);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount);
        Assert.AreEqual(1, PendingRefreshCount());
    }

    [TestMethod]
    public async Task TimedOutHeadLookup_LeavesPointerUntilALaterRefreshVerifiesTheHead()
    {
        await SeedMainAt("A");
        _remote.Head = null;
        _remote.TimeoutHeadReads = 1;
        var admission = await AdmitPush("B", "A");
        await WaitUntil(() => PendingRefreshAttempts() == 1);
        Assert.AreEqual("A", HeadCommit("main"));
        CollectionAssert.AreEqual(new[] { "A" }, _built.ToArray());
        Assert.AreEqual(SnapshotJobStatus.Queued, admission.Status);
        _remote.Head = "C";
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task SlowHeadLookup_LeavesPointerUntilALaterRefreshVerifiesTheHead()
    {
        await SeedMainAt("A");
        _remote.Head = null;
        var (started, release) = _remote.BlockNextHeadRead();

        var fallback = await AdmitPush("B", "A");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("A", HeadCommit("main"), "a refresh waiting on an unverified remote read cannot move the pointer");

        release.TrySetResult();
        await WaitUntil(() => PendingRefreshAttempts() == 1);
        Assert.AreEqual("A", HeadCommit("main"), "a remote read that completes without a head cannot authorize pointer movement");
        Assert.AreEqual(SnapshotJobStatus.Queued, fallback.Status);

        _remote.Head = "C";
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
        Assert.AreEqual("C", _built.Last());
    }

    [TestMethod]
    public async Task SameCommitWithChangedIndexIdentity_IsRebuilt()
    {
        _remote.Head = "A";
        var original = await _service.EnsureSnapshotAsync(Request("A", "main") with
        {
            ConfigHash = "old-config",
            ExpectedHeadCommit = ""
        });
        Assert.AreEqual(SnapshotJobStatus.Complete, original.Status);

        _remote.Head = "A";
        var refresh = await Push("A", "A");

        Assert.AreEqual(SnapshotJobStatus.Complete, refresh.Status);
        CollectionAssert.AreEqual(new[] { "A", "A" }, _built.ToArray());
        Assert.AreEqual("A", HeadCommit("main"));
        Assert.AreNotEqual(original.SnapshotId, refresh.SnapshotId);
        using var conn = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        var repositoryId = snapshots.GetRepositoryId(Repo)!.Value;
        var branchId = snapshots.GetBranchId(repositoryId, "main")!.Value;
        Assert.AreEqual(refresh.SnapshotId, snapshots.GetBranchSnapshotId(branchId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshRepairsNullOrUnusableDefaultPointer(bool unusable)
    {
        await SeedMainAt("A");
        using var mutation = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        mutation.Open();
        var store = new SnapshotStore(mutation);
        var repositoryId = store.GetRepositoryId(Repo)!.Value;
        var branchId = store.GetBranchId(repositoryId, "main")!.Value;
        if (unusable)
            store.MarkStatus(store.GetBranchSnapshotId(branchId)!.Value, SnapshotStatus.Superseded);
        else
            store.SetBranchPointer(branchId, null, _clock.GetUtcNow().ToUnixTimeMilliseconds());

        _remote.Head = "B";
        var result = await Push("unverified", "unrelated");
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(result.SnapshotId, store.GetBranchSnapshotId(branchId));
        Assert.AreEqual("B", HeadCommit("main"));
        Assert.IsFalse(_built.Contains("unverified"));
        Assert.AreEqual(0, PendingRefreshCount());
    }

    [TestMethod]
    [DataRow(SnapshotStatus.Superseded, false)]
    [DataRow(SnapshotStatus.Failed, false)]
    [DataRow(SnapshotStatus.Superseded, true)]
    [DataRow(SnapshotStatus.Failed, true)]
    public async Task UnusableDefaultRepairsWithoutAncestry(string status, bool unavailable)
    {
        await SeedMainAt("A");
        using var mutation = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        mutation.Open();
        var store = new SnapshotStore(mutation);
        var repositoryId = store.GetRepositoryId(Repo)!.Value;
        var branchId = store.GetBranchId(repositoryId, "main")!.Value;
        store.MarkStatus(store.GetBranchSnapshotId(branchId)!.Value, status);
        _remote.NonAncestor = true;
        _remote.AncestryUnavailable = unavailable;
        _remote.Head = "B";

        var result = await Push("hint", "A");

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(result.SnapshotId, store.GetBranchSnapshotId(branchId));
        Assert.AreEqual("B", HeadCommit("main"));
        Assert.AreEqual(0, PendingRefreshCount());
    }

    [TestMethod]
    public async Task RefreshRepairsCompleteCommitlessDefaultPointer()
    {
        await SeedMainAt("A");
        using var mutation = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        mutation.Open();
        var store = new SnapshotStore(mutation);
        var repo = store.GetRepositoryId(Repo)!.Value;
        var branch = store.GetBranchId(repo, "main")!.Value;
        var old = store.GetBranchSnapshotId(branch)!.Value;
        using (var cmd = mutation.CreateCommand())
        {
            cmd.CommandText = "UPDATE snapshots SET commit_id = NULL WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", old);
            cmd.ExecuteNonQuery();
        }
        Assert.AreEqual(SnapshotStatus.Complete, store.GetById(old)!.Status);
        _remote.Head = "B";
        var result = await Push("hint", "old");
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual("B", HeadCommit("main"));
        Assert.AreEqual(result.SnapshotId, store.GetBranchSnapshotId(branch));
        Assert.IsFalse(_built.Contains("hint"));
    }

    [TestMethod]
    public async Task ExplicitEnsureAndRebuildKeepTheirOwnTurnsAndCas()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _remote.Head = "C";
        var push = await AdmitPush("B", "A");
        Assert.AreEqual(SnapshotJobStatus.Queued, push.Status);
        var explicitEnsure = _service.EnsureSnapshotAsync(Request("X", "main") with
        {
            ExpectedHeadCommit = "A",
            RestrictsImplicitDefault = true
        });
        await WaitUntil(() => IsEnsureQueued("X"));
        var rebuild = _service.EnsureSnapshotAsync(Request("Y", "main") with
        {
            ExpectedHeadCommit = "Y",
            RebuildGeneration = "refresh-test"
        });
        await WaitUntil(() => IsEnsureQueued("Y"));
        // Issue #273 (FIFO): the newer push lands directly behind the rebuild's own pending refresh and is absorbed into it.
        var newerPush = await AdmitPush("C", "B");
        Assert.AreEqual(SnapshotJobStatus.Coalesced, newerPush.Status);
        _releaseHold.TrySetResult();

        await Task.WhenAll(hold, explicitEnsure, rebuild).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(newerPush.AdmissionId!)?.Status == "completed");
        // In admission order: the first push's refresh (C), the explicit ensure (X), the rebuild (Y), each with its CAS.
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(push.AdmissionId!)!.Status);
        Assert.AreEqual(SnapshotJobStatus.Complete, explicitEnsure.Result.Status);
        Assert.AreEqual(SnapshotJobStatus.Complete, rebuild.Result.Status);
        CollectionAssert.AreEqual(new[] { "A", "hold", "C", "X", "Y" }, _built.ToArray());
        Assert.AreEqual("C", HeadCommit("main"), "the rebuild's stale CAS cannot move the pointer");
        await WaitUntil(() => PendingRefreshCount() == 0 && _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount);
    }

    [TestMethod]
    public async Task ExplicitApplicationCasQueuedBehindRefresh_KeepsItsCommitAndOwnTurn()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.Head = "C";

        var refresh = await AdmitPush("B", "A");
        Assert.AreEqual(SnapshotJobStatus.Queued, refresh.Status);
        var explicitEnsure = _service.EnsureSnapshotAsync(Request("X", "main") with
        {
            ExpectedHeadCommit = "A",
            RestrictsImplicitDefault = true
        });
        await WaitUntil(() => QueuedEnsureCount() >= 2);
        Assert.AreEqual("queued", _service.GetRefreshAdmissionStatus(refresh.AdmissionId!)!.Status,
            "the later explicit ensure never supersedes the earlier refresh (issue #273, FIFO)");
        _releaseHold.TrySetResult();

        await Task.WhenAll(hold, explicitEnsure).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);

        Assert.AreEqual(SnapshotJobStatus.Complete, explicitEnsure.Result.Status);
        Assert.IsTrue(_built.Contains("X"));
        Assert.AreEqual("C", HeadCommit("main"), "the stale explicit CAS attaches without moving the refreshed pointer");
    }

    [TestMethod]
    public async Task RefreshQueuedBeforeExplicitEnsureIsNotDeferredByThatLaterEnsure()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _remote.Head = "C";
        var first = await AdmitPush("ignored-1", "A");
        var explicitEnsure = _service.EnsureSnapshotAsync(Request("B", "main") with { ExpectedHeadCommit = "A" });
        await WaitUntil(() => QueuedEnsureCount() >= 2 && PendingRefreshCount() == 2);
        Assert.AreEqual("queued", _service.GetRefreshAdmissionStatus(first.AdmissionId!)!.Status);
        var second = await AdmitPush("ignored-2", "B");
        Assert.AreNotEqual(first.AdmissionId, second.AdmissionId,
            "the later explicit ensure has a separate marker so it cannot consume or postpone the first refresh");

        _releaseHold.TrySetResult();
        await Task.WhenAll(hold, explicitEnsure).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);

        // In admission order the first refresh converges to C before the explicit ensure, whose CAS (A) then no longer
        // matches; the later refresh keeps the branch at the remote head.
        Assert.AreEqual(false, explicitEnsure.Result.BranchAdvanced);
        Assert.AreEqual("C", HeadCommit("main"));
        CollectionAssert.AreEqual(new[] { "A", "hold", "C", "B" }, _built.ToArray());
    }

    [TestMethod]
    public async Task BootstrapRefreshWaitsForEarlierUnguardedExplicitEnsure()
    {
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var explicitEnsure = _service.EnsureSnapshotAsync(Request("B", "main"));
        await WaitUntil(() => QueuedEnsureCount() >= 2);
        _remote.Head = "C";
        await AdmitPush("hint", "B");
        _releaseHold.TrySetResult();
        await Task.WhenAll(hold, explicitEnsure).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
        CollectionAssert.AreEqual(new[] { "hold", "B", "C" }, _built.ToArray());
    }

    [TestMethod]
    public async Task RefreshWaitsForEarlierAdvanceHiddenByNonMovingAdmission()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["feature"] = "C";
        await _service.EnsureSnapshotAsync(Request("C", "feature") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var earlier = _service.EnsureSnapshotAsync(Request("B", "feature") with { ExpectedHeadCommit = "C" });
        await WaitUntil(() => QueuedEnsureCount() >= 2 && PendingRefreshCount() == 1);
        var later = _service.EnsureSnapshotAsync(Request("other", "feature") with { BranchUpdate = BranchUpdateMode.None });
        await WaitUntil(() => QueuedEnsureCount() >= 3);
        await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        _releaseHold.TrySetResult();

        await Task.WhenAll(hold, earlier, later).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("feature"));
    }

    [TestMethod]
    public async Task ClaimedRefreshCompletesBeforeLaterEnsures()
    {
        await SeedMainAt("A");
        _remote.Head = "C";
        BlockCommit("C");
        var refresh = await AdmitPush("hint", "A");
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var later = _service.EnsureSnapshotAsync(Request("later", "main") with { ExpectedHeadCommit = "A" });
        await WaitUntil(() => QueuedEnsureCount() >= 1);
        Assert.IsFalse(later.IsCompleted, "a claimed refresh remains ahead of later branch intents");
        _releaseBlocked!.TrySetResult();
        await later.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(refresh.AdmissionId!)!.Status);
        CollectionAssert.AreEqual(new[] { "A", "C", "later" }, _built.ToArray());
    }

    [TestMethod]
    public async Task ClockAdvanceBetweenRetryDeadlineReadAndTimerArmingDoesNotDelayConvergence()
    {
        await SeedMainAt("A");
        _remote.Head = null;
        _service.BeforeRefreshRetryDelayForTesting = () =>
        {
            _service.BeforeRefreshRetryDelayForTesting = null;
            _remote.Head = "C";
            _clock.Advance(TimeSpan.FromSeconds(30));
        };
        var admission = await AdmitPush("hint", "A");
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(admission.AdmissionId!)?.Status == "completed");
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task EarlyRetryWakeDoesNotStopRefreshBeforeDeadline()
    {
        await SeedMainAt("A");
        _remote.Head = null;
        var waitingAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = 0;
        _service.BeforeRefreshRetryDelayForTesting = () =>
        {
            if (Interlocked.Increment(ref waits) == 1)
                _service.WakeRefreshRetriesForTesting();
            else
                waitingAgain.TrySetResult();
        };
        var admission = await AdmitPush("hint", "A");
        await waitingAgain.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("retrying", _service.GetRefreshAdmissionStatus(admission.AdmissionId!)!.Status);
        _remote.Head = "C";
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(admission.AdmissionId!)?.Status == "completed");
        Assert.AreEqual("C", HeadCommit("main"));
    }

    // Rules 3 and 4: an explicit ensure admitted behind a retrying refresh waits for it (admission order), then its
    // expected head no longer matches the pointer the refresh moved, so it moves nothing.
    [TestMethod]
    public async Task RetryingRefreshThenExplicitEnsure_RunInAdmissionOrder_AndTheStaleCasMovesNothing()
    {
        await SeedMainAt("A");
        _remote.Head = null;
        await AdmitPush("ignored-1", "A");
        await WaitUntil(() => PendingRefreshAttempts() == 1);

        var explicitEnsure = _service.EnsureSnapshotAsync(Request("B", "main") with { ExpectedHeadCommit = "A" });
        await WaitUntil(() => IntentsAfter(0, "main").Any(i => i.Kind == "ensure"), QueueDiagnostics);
        Assert.IsFalse(explicitEnsure.IsCompleted, "the later ensure waits behind the earlier refresh");

        _remote.Head = "C";
        await WaitUntil(() => explicitEnsure.IsCompleted || ClockStep(), QueueDiagnostics);
        var result = await explicitEnsure;
        Assert.AreNotEqual(true, result.BranchAdvanced, "its expected head A no longer matches the pointer");
        await WaitUntil(() => PendingRefreshCount() == 0 || ClockStep());
        Assert.AreEqual("C", HeadCommit("main"));
        Assert.IsTrue(_built.Contains("C"));
    }

    [TestMethod]
    public async Task FailedRefreshRetriesWithBackoffAndReleasesItsWriterTurn()
    {
        await SeedMainAt("A");
        _remote.Head = "B";
        _failOnce["B"] = 1;

        var accepted = await AdmitPush("B", "A");
        Assert.AreEqual(SnapshotJobStatus.Queued, accepted.Status);
        await WaitUntil(() => PendingRefreshAttempts() == 1);
        Assert.AreEqual("A", HeadCommit("main"));
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount, "the failed turn released the writer");

        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("B", HeadCommit("main"));
        Assert.AreEqual(2, _built.Count(commit => commit == "B"));
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task NonConvergenceAdmissionFailureStillRunsAndCompleteAttachSkipsWrite(bool locate, bool suppress)
    {
        if (locate)
        {
            _service.Dispose();
            _service = StartService(useRemote: false);
        }
        await SeedMainAt("A");
        var writes = 0;
        _service.BeforeQueuedEnsureWriteForTesting = () =>
        {
            Interlocked.Increment(ref writes);
            throw new InvalidOperationException("injected admission failure");
        };
        var request = Request("B", "main") with
        {
            BranchUpdate = suppress ? BranchUpdateMode.None : BranchUpdateMode.Advance
        };
        var first = await _service.EnsureSnapshotAsync(request);
        Assert.AreEqual(SnapshotJobStatus.Complete, first.Status);
        Assert.AreEqual(1, writes);
        var second = await _service.EnsureSnapshotAsync(request);
        Assert.AreEqual(SnapshotJobStatus.Complete, second.Status);
        Assert.AreEqual(1, writes, "a completed identity does not write an admission");
        Assert.AreEqual(1, _built.Count(commit => commit == "B"));
    }

    [TestMethod]
    public async Task RemoteMovingAdmissionFailureFailsClosed()
    {
        await SeedMainAt("A");
        _service.BeforeQueuedEnsureWriteForTesting =
            () => throw new InvalidOperationException("injected admission failure");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.EnsureSnapshotAsync(Request("B", "main")));
        Assert.AreEqual(0, _built.Count(commit => commit == "B"));
    }

    [TestMethod]
    public async Task LocateFailureRetryCannotRunAfterRetirement()
    {
        _service.Dispose();
        _service = StartService(useRemote: false);
        await SeedMainAt("A");
        await _service.EnsureSnapshotAsync(Request("feature-old", "feature"));
        _terminalFailOnce["B"] = 1;
        var request = Request("B", "feature");
        Assert.AreEqual(SnapshotJobStatus.Failed, (await _service.EnsureSnapshotAsync(request)).Status);
        Assert.AreEqual(SnapshotJobStatus.Failed, (await _service.EnsureSnapshotAsync(request)).Status);
        var retired = await _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" });
        Assert.IsTrue(retired.Retired);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsNull(HeadCommit("feature"));
        Assert.AreEqual(1, _built.Count(commit => commit == "B"));
    }

    [TestMethod]
    public async Task HeldWriter_RetireBeforeExpeditedRefresh_PreservesBranchIntentOrder()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["feature"] = "B";
        await _service.EnsureSnapshotAsync(Request("B", "feature") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        var holder = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var retire = _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" });
        await WaitUntil(() => _service.WriteGateForTesting.NormalWaiters == 1);
        var retireId = LatestRetireIntentId("feature");

        _remote.BranchHeads["feature"] = "C";
        var refresh = await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        await WaitUntil(() => PendingRefreshCount() == 1);
        var refreshIntent = IntentByAdmissionId(refresh.AdmissionId!);
        Assert.IsTrue(retireId < refreshIntent.Id);
        Assert.AreEqual(0, _service.WriteGateForTesting.ExpeditedWaiters,
            "a later expedited refresh cannot take a turn while an earlier retire is the branch head");

        _releaseHold.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue((await retire.WaitAsync(TimeSpan.FromSeconds(20))).Retired);
        await WaitUntil(() => PendingRefreshCount() == 0 && HeadCommit("feature") == "C");
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(refresh.AdmissionId!)!.Status);
    }

    [TestMethod]
    public async Task HeldWriter_ExpeditedRefreshBeforeNormalRetire_RunInAdmissionOrder()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["feature"] = "B";
        await _service.EnsureSnapshotAsync(Request("B", "feature") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        var holder = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _remote.BranchHeads["feature"] = "C";
        var refresh = await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        await WaitUntil(() => _service.WriteGateForTesting.ExpeditedWaiters == 1);
        var retire = _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" });
        // The retirement waits behind the earlier refresh (its branch head) before it takes a writer turn.
        await WaitUntil(() => HasRetireIntent("feature"));
        var retireId = LatestRetireIntentId("feature");
        var refreshIntent = IntentByAdmissionId(refresh.AdmissionId!);
        Assert.IsTrue(refreshIntent.Id < retireId);
        Assert.AreEqual("queued", _service.GetRefreshAdmissionStatus(refresh.AdmissionId!)!.Status,
            "a later retirement never supersedes the earlier refresh (issue #273, FIFO)");

        _releaseHold.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(20));
        // The refresh runs first (C), then the retirement removes the branch.
        Assert.IsTrue((await retire.WaitAsync(TimeSpan.FromSeconds(20))).Retired);
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(refresh.AdmissionId!)!.Status);
        Assert.IsTrue(_built.Contains("C"));
        await WaitUntil(() => HeadCommit("feature") is null);
        Assert.IsNull(HeadCommit("feature"));
    }

    [TestMethod]
    public async Task HeldWriter_RefreshOnEachSideOfRetire_AdmissionsStaySplit()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["feature"] = "B";
        await _service.EnsureSnapshotAsync(Request("B", "feature") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        var holder = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _remote.BranchHeads["feature"] = "C";
        var before = await _service.BeginEnsureSnapshotAsync(Request("before", "feature") with { Refresh = true });
        await WaitUntil(() => _service.WriteGateForTesting.ExpeditedWaiters == 1);
        var retire = _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" });
        // The retirement waits behind the earlier refresh (its branch head) before it takes a writer turn.
        await WaitUntil(() => HasRetireIntent("feature"));
        var retireId = LatestRetireIntentId("feature");
        Assert.AreEqual("queued", _service.GetRefreshAdmissionStatus(before.AdmissionId!)!.Status);

        var after = await _service.BeginEnsureSnapshotAsync(Request("after", "feature") with { Refresh = true });
        Assert.AreNotEqual(before.AdmissionId, after.AdmissionId);
        await WaitUntil(() => PendingRefreshCount() == 2);
        Assert.IsTrue(retireId < IntentByAdmissionId(after.AdmissionId!).Id);
        Assert.AreEqual("queued", _service.GetRefreshAdmissionStatus(after.AdmissionId!)!.Status);

        _releaseHold.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue((await retire.WaitAsync(TimeSpan.FromSeconds(20))).Retired);
        await WaitUntil(() => PendingRefreshCount() == 0 && HeadCommit("feature") == "C");
        // In admission order: the earlier refresh, the retirement, then the later refresh recreates the branch.
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(before.AdmissionId!)!.Status);
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(after.AdmissionId!)!.Status);
    }

    // Issue #273 (review round 28): an idle retirement never discards a retrying push. The push's admitted refresh is
    // branch activity, so the idle rule leaves the branch alone, and the push is indexed once its lookup recovers.
    [TestMethod]
    public async Task IdleRetirementDoesNotDiscardARetryingPush()
    {
        await SeedMainAt("A");
        _service.Dispose();
        _db.Dispose();
        _db = new IndexDatabase(_dbPath);
        CreateIdleFeatureBranch();
        _service = StartService();
        _remote.BranchHeads["feature"] = "B";

        _remote.TimeoutHeadReads = 1;
        var push = _service.EnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        await WaitUntil(() => PendingRefreshAttempts() == 1);
        var admission = ReadPendingRefreshes().Single().AdmissionId;

        var report = await _service.RetireIdleBranchesAsync();
        Assert.AreEqual(1, report.Idle);
        Assert.AreEqual(1, report.Busy, "the retrying push is pending work on the branch");
        Assert.AreEqual(0, report.Attempted);
        Assert.AreEqual("retrying", _service.GetRefreshAdmissionStatus(admission)!.Status);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(SnapshotJobStatus.Complete, (await push.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.AreEqual("B", HeadCommit("feature"), "the push was indexed, not discarded");
    }

    private void CreateIdleFeatureBranch()
    {
        var conn = _db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var repositoryId = snapshots.GetRepositoryId(Repo)!.Value;
        var main = snapshots.GetBranch(repositoryId, "main")!;
        var movedAt = (_clock.GetUtcNow() - TimeSpan.FromDays(8)).ToUnixTimeMilliseconds();
        var feature = snapshots.EnsureBranch(repositoryId, "feature", isDefault: false, movedAt);
        snapshots.SetBranchPointer(feature, main.SnapshotId!.Value, movedAt);
    }

    [TestMethod]
    public async Task FailedPointerVerification_RetainsMarkerUntilThePointerAdvances()
    {
        await SeedMainAt("A");
        BlockCommit("B");
        _remote.Head = "B";
        await AdmitPush("B", "A");
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.Head = null;
        _releaseBlocked!.TrySetResult();

        await WaitUntil(() => PendingRefreshAttempts() == 1);
        Assert.AreEqual("A", HeadCommit("main"));
        Assert.AreEqual(1, PendingRefreshCount());
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount);

        _remote.Head = "B";
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("B", HeadCommit("main"));
        CollectionAssert.AreEqual(new[] { "A", "B" }, _built.ToArray());
    }

    [TestMethod]
    public async Task FeatureBranchLinkedSuccessorsConvergeWithoutHandover()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.BranchHeads["feature"] = "C";

        var predecessor = _service.EnsureSnapshotAsync(Request("B", "feature") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => QueuedEnsureCount() == 2);
        var successor = _service.EnsureSnapshotAsync(Request("C", "feature") with { ExpectedHeadCommit = "B" });
        await WaitUntil(() => QueuedEnsureCount() == 3);
        await WaitUntil(() => _service.WriteGateForTesting.NormalWaiters == 2);
        _releaseHold.TrySetResult();

        await Task.WhenAll(hold, predecessor, successor).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual(SnapshotJobStatus.Complete, predecessor.Result.Status);
        Assert.AreEqual(SnapshotJobStatus.Complete, successor.Result.Status);
        Assert.AreEqual("C", HeadCommit("feature"));
        CollectionAssert.AreEqual(new[] { "A", "hold", "B", "C" }, _built.ToArray());
    }

    [TestMethod]
    public async Task RestartSchedulesUnclaimedAndAbandonedClaimedMarkers()
    {
        await SeedMainAt("A");
        _service.Dispose();
        _remote.Head = "C";

        var conn = _db.GetConnection();
        var repositoryId = new SnapshotStore(conn).GetRepositoryId(Repo)!.Value;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO pending_refreshes
                    (repository_id, branch, admission_id, admitted_at, claimed_by_turn, claimed_at, next_attempt_at)
                VALUES
                    (@repository, 'main', 'dead-claim', @at, 'dead-process', @at, @at),
                    (@repository, 'main', 'pending', @at + 1, NULL, NULL, @at);
                INSERT INTO branch_intents
                    (repository_id, branch, kind, admission_id, state, admitted_at)
                VALUES
                    (@repository, 'main', 'refresh', 'dead-claim', 'claimed', @at),
                    (@repository, 'main', 'refresh', 'pending', 'pending', @at + 1);
                """;
            cmd.Parameters.AddWithValue("@repository", repositoryId);
            cmd.Parameters.AddWithValue("@at", _clock.GetUtcNow().ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        _service = StartService();
        await WaitUntil(() => PendingRefreshCount() == 0 && HeadCommit("main") == "C");

        CollectionAssert.AreEqual(new[] { "A", "C" }, _built.ToArray());
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount);
    }

    [TestMethod]
    public async Task RestartReplaysRetirementBeforeLaterRefresh()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["feature"] = "X";
        await _service.EnsureSnapshotAsync(Request("X", "feature") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0 && _service.WriteGateForTesting.CurrentCount == 1);
        _service.Dispose();

        var conn = _db.GetConnection();
        var repositoryId = new SnapshotStore(conn).GetRepositoryId(Repo)!.Value;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var intents = new BranchIntentStore(conn);
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            request = new RetireBranchRequest { Repository = Repo, Branch = "feature" },
            principal = default(AuditCaller),
            idle = (object?)null
        }, ServiceJson.Options);
        var retire = intents.Admit(repositoryId, "feature", "retire", null, null, payload, now);
        var refresh = intents.Admit(repositoryId, "feature", "refresh", null, "restart-refresh", null, now);
        new PendingRefreshStore(conn).Admit(repositoryId, "feature", "restart-refresh", now);

        _remote.BranchHeads["feature"] = "Y";
        _service = StartService();
        await WaitUntil(() => _service.GetRefreshAdmissionStatus("restart-refresh")?.Status == "completed");
        using var read = _db.OpenReadConnection();
        var restartedIntents = new BranchIntentStore(read);
        Assert.AreEqual("retired", restartedIntents.Get(retire.Id)!.Outcome);
        Assert.AreEqual("settled", restartedIntents.Get(refresh.Id)!.State);
        Assert.AreEqual("Y", HeadCommit("feature"));
    }

    [TestMethod]
    public async Task RestartSchedulesMultipleAbandonedClaimsForOneBranch()
    {
        await SeedMainAt("A");
        _service.Dispose();
        _remote.Head = "C";

        var conn = _db.GetConnection();
        var repositoryId = new SnapshotStore(conn).GetRepositoryId(Repo)!.Value;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO pending_refreshes
                    (repository_id, branch, admission_id, admitted_at, claimed_by_turn, claimed_at, next_attempt_at)
                VALUES
                    (@repository, 'main', 'dead-first', @at, 'dead-process', @at, @at),
                    (@repository, 'main', 'dead-second', @at + 1, 'dead-process', @at, @at);
                INSERT INTO branch_intents
                    (repository_id, branch, kind, admission_id, state, admitted_at)
                VALUES
                    (@repository, 'main', 'refresh', 'dead-first', 'claimed', @at),
                    (@repository, 'main', 'refresh', 'dead-second', 'claimed', @at + 1);
                """;
            cmd.Parameters.AddWithValue("@repository", repositoryId);
            cmd.Parameters.AddWithValue("@at", _clock.GetUtcNow().ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        _service = StartService();
        await WaitUntil(() => PendingRefreshCount() == 0 && HeadCommit("main") == "C");
        CollectionAssert.AreEqual(new[] { "A", "C" }, _built.ToArray());
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount);
    }

    [TestMethod]
    public async Task RefreshAdmissionDoesNotConsumeAnUnrelatedReservedJobId()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var controlWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.ControlWaitStartedForTesting = () => controlWait.TrySetResult();
        var unrelated = _service.BeginEnsureSnapshotAsync(
            Request("unrelated", "feature") with { ExpectedHeadCommit = "" });
        await controlWait.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromSeconds(ServiceOptions.DefaultControlWriteWaitSeconds));
        var reserved = await unrelated.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(reserved.JobId);
        _remote.BranchHeads["feature"] = "unrelated";
        _remote.Head = "C";
        var refresh = await AdmitPush("B", "A");
        Assert.IsNull(refresh.JobId);
        Assert.IsNotNull(_service.GetStatus(reserved.JobId));
        Assert.AreEqual(2, PendingRefreshCount(), "both branch-moving admissions carry convergence markers");

        _releaseHold.TrySetResult();
        await hold.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual(SnapshotJobStatus.Queued, reserved.Status);
        await WaitUntil(() => _service.GetStatus(reserved.JobId)?.Job.Status == SnapshotJobStatus.Complete);
        Assert.AreEqual("C", HeadCommit("main"));
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount);
    }

    [TestMethod]
    public async Task MetricsCountSameIdentityOnTwoDistinctTurns()
    {
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = _service.EnsureSnapshotAsync(Request("same", "feature-a") with { ExpectedHeadCommit = "" });
        var second = _service.EnsureSnapshotAsync(Request("same", "feature-b") with { ExpectedHeadCommit = "" });

        await WaitUntil(() => _service.CollectQueuedEnsureMetrics().Total.Depth == 2);
        _releaseHold.TrySetResult();
        await Task.WhenAll(hold, first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(0, _service.CollectQueuedEnsureMetrics().Total.Depth);
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(1, _service.WriteGateForTesting.CurrentCount);
    }

    [TestMethod]
    [DataRow("main")]
    [DataRow("feature")]
    public async Task FailedExplicitIntermediateConvergesToRemoteHeadWithoutAnotherPush(string branch)
    {
        await SeedMainAt("A");
        if (branch != "main")
        {
            _remote.BranchHeads[branch] = "A";
            await _service.EnsureSnapshotAsync(Request("A", branch) with { ExpectedHeadCommit = "" });
            await WaitUntil(() => PendingRefreshCount() == 0);
        }
        BlockCommit("B");
        _terminalFailOnce["B"] = 1;
        _remote.BranchHeads[branch] = "C";
        if (branch == "main") _remote.Head = "C";
        var intermediate = _service.EnsureSnapshotAsync(Request("B", branch) with { ExpectedHeadCommit = "A" });
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, PendingRefreshCount(), "the marker is durable before the worker can fail");
        var successor = _service.EnsureSnapshotAsync(Request("C", branch) with { ExpectedHeadCommit = "B" });
        _releaseBlocked!.TrySetResult();
        await Task.WhenAll(intermediate, successor).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(SnapshotJobStatus.Failed, intermediate.Result.Status);
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit(branch));
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM branch_advance_handovers;";
        Assert.AreEqual(0L, cmd.ExecuteScalar());
    }

    [TestMethod]
    [DataRow("main", true)]
    [DataRow("feature", true)]
    [DataRow("main", false)]
    [DataRow("feature", false)]
    public async Task ExplicitMismatchOrUnguardedPushAdmitsRefreshBeforeOutcome(string branch, bool cas)
    {
        await SeedMainAt("A");
        _remote.BranchHeads[branch] = "C";
        if (branch == "main") _remote.Head = "C";
        BlockCommit("X");
        var ensure = _service.EnsureSnapshotAsync(Request("X", branch) with
        {
            ExpectedHeadCommit = cas ? "missing-intermediate" : null
        });
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, PendingRefreshCount());
        _releaseBlocked!.TrySetResult();
        await ensure.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit(branch));
    }

    [TestMethod]
    public async Task FeatureBranchRefreshForcePushUsesVerifiedNonAncestorHead()
    {
        await SeedMainAt("A");
        _remote.BranchHeads["feature"] = "X";
        await _service.EnsureSnapshotAsync(Request("X", "feature") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        _remote.BranchHeads["feature"] = "Z";
        var result = await _service.EnsureSnapshotAsync(Request("untrusted", "feature") with
        {
            Refresh = true, Forced = true, ExpectedHeadCommit = "wrong"
        });
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual("Z", HeadCommit("feature"));
        Assert.IsFalse(_built.Contains("untrusted"));
        Assert.AreEqual("A", HeadCommit("main"));
    }

    [TestMethod]
    public async Task ExplicitShutdownMidWorkerKeepsMarkerForRestart()
    {
        await SeedMainAt("A");
        BlockCommit("B");
        var ensure = _service.EnsureSnapshotAsync(Request("B", "main") with { ExpectedHeadCommit = "A" });
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.Head = "C";
        _service.StopProduction();
        _releaseBlocked!.TrySetResult();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await ensure);
        _service.Dispose();
        Assert.AreEqual(1, PendingRefreshCount());
        _service = StartService();
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task CallerCancellationKeepsConvergenceRunning()
    {
        await SeedMainAt("A");
        BlockCommit("B");
        using var caller = new CancellationTokenSource();
        var ensure = _service.EnsureSnapshotAsync(
            Request("B", "main") with { ExpectedHeadCommit = "A" }, caller.Token);
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await ensure);
        Assert.AreEqual(1, PendingRefreshCount());
        _remote.Head = "C";
        _releaseBlocked!.TrySetResult();
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task ExplicitShutdownBeforeTurnKeepsMarkerForRestart()
    {
        await SeedMainAt("A");
        var hold = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ensure = _service.EnsureSnapshotAsync(Request("B", "main") with { ExpectedHeadCommit = "A" });
        await WaitUntil(() => PendingRefreshCount() == 1);
        _remote.Head = "C";
        _service.StopProduction();
        _releaseHold.TrySetResult();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await hold);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await ensure);
        _service.Dispose();
        Assert.AreEqual(1, PendingRefreshCount());
        _service = StartService();
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task PartialExplicitResultConvergesToNewerRemoteHead()
    {
        await SeedMainAt("A");
        BlockCommit("B");
        _partialCommits["B"] = true;
        var ensure = _service.EnsureSnapshotAsync(Request("B", "main") with { ExpectedHeadCommit = "A" });
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, PendingRefreshCount());
        _remote.Head = "C";
        _releaseBlocked!.TrySetResult();
        var result = await ensure.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status);
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    [TestMethod]
    public async Task RemoteHeadChangesDuringRefreshConvergesWithoutAnotherPush()
    {
        await SeedMainAt("A");
        BlockCommit("B");
        _remote.Head = "B";
        var refresh = Push("hint", "A");
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _remote.Head = "C";
        _releaseBlocked!.TrySetResult();
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual("C", HeadCommit("main"));
        Assert.AreEqual(0, PendingRefreshCount());
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, _built.ToArray());
    }

    [TestMethod]
    public async Task AdmissionAndConvergenceMarkerRollbackTogetherOnCrashBoundary()
    {
        await SeedMainAt("A");
        _service.BeforeConvergenceAdmissionForTesting = () => throw new IOException("simulated admission crash");
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await _service.EnsureSnapshotAsync(Request("B", "main") with { ExpectedHeadCommit = "A" }));
        _service.BeforeConvergenceAdmissionForTesting = null;
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        Assert.AreEqual(0, QueuedEnsureCount());
        Assert.AreEqual(0, PendingRefreshCount());
        Assert.IsFalse(_built.Contains("B"), "no outcome can be recorded without durable convergence admission");
        _remote.Head = "C";
        await _service.EnsureSnapshotAsync(Request("C", "main") with { ExpectedHeadCommit = "A" });
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("C", HeadCommit("main"));
    }

    // Issue #273 (B1): a transient failure of the write that returns a failed refresh to its retry schedule is retried
    // by the turn's own finally path, so the intent is pending again (not stranded as claimed) and converges later.
    [TestMethod]
    public async Task Recover_RefreshRetryWriteFailure_FinallyPathReturnsClaimToPending()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        var failures = 1;
        _service.BeforeIntentWriteForTesting = op =>
        {
            if (op == "retry-refresh" && Interlocked.Decrement(ref failures) >= 0)
                throw new Microsoft.Data.Sqlite.SqliteException("injected busy", 5);
        };
        _remote.TimeoutHeadReads = 1;
        var refresh = await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        await WaitUntil(() => PendingRefreshAttempts() == 1);

        var intent = IntentByAdmissionId(refresh.AdmissionId!);
        Assert.AreEqual("pending", intent.State);
        Assert.IsNull(intent.ClaimedByTurn);
        Assert.AreEqual(1, intent.Attempts);
        Assert.IsNotNull(intent.NextAttemptAt);
        Assert.AreEqual(1, PendingRefreshCount());
        Assert.AreEqual(0, PendingRefreshCount(claimedOnly: true));

        _remote.BranchHeads["feature"] = "Y";
        _clock.Advance(TimeSpan.FromSeconds(60));
        await WaitUntil(() => PendingRefreshCount() == 0);
        Assert.AreEqual("Y", HeadCommit("feature"));
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(refresh.AdmissionId!)!.Status);
    }

    // Issue #273 (B1): both the in-turn retry write and the finally path's return fail, so the intent stays claimed by
    // a turn that no longer exists. The rescan reclaims it without a restart (owner gone), and an explicit ensure
    // admitted meanwhile waits behind it and runs afterwards, in admission order.
    [TestMethod]
    public async Task Recover_OwnerGoneClaim_IsReclaimedWithoutRestartAndLaterIntentRunsAfterIt()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        var failures = 2;
        _service.BeforeIntentWriteForTesting = op =>
        {
            if (op == "retry-refresh" && Interlocked.Decrement(ref failures) >= 0)
                throw new Microsoft.Data.Sqlite.SqliteException("injected busy", 5);
        };
        _remote.TimeoutHeadReads = 1;
        var stranded = await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        await WaitUntil(() => Volatile.Read(ref failures) <= 0 && _service.RefreshTurnCountForTesting == 0);

        var intent = IntentByAdmissionId(stranded.AdmissionId!);
        Assert.AreEqual("claimed", intent.State, "neither recovery write landed");
        Assert.IsNotNull(intent.ClaimedByTurn);
        Assert.AreEqual(0, PendingRefreshAttempts());

        _remote.BranchHeads["feature"] = "Y";
        var later = _service.EnsureSnapshotAsync(Request("Y", "feature") with { ExpectedHeadCommit = "X" });
        await WaitUntil(() => IsEnsureQueued("Y"));
        Assert.IsFalse(later.IsCompleted, "the later ensure waits behind the stranded head");
        Assert.AreEqual("X", HeadCommit("feature"));

        _clock.Advance(TimeSpan.FromSeconds(60));
        var result = await later.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        await WaitUntil(() => PendingRefreshCount() == 0 && QueuedEnsureCount() == 0);
        Assert.AreEqual("Y", HeadCommit("feature"));
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(stranded.AdmissionId!)!.Status);
        using var connection = _db.OpenReadConnection();
        var intents = new BranchIntentStore(connection);
        Assert.IsEmpty(intents.ListUnsettled());
        Assert.AreEqual(1, _built.Count(commit => commit == "Y"), "the reclaimed refresh built Y; the ensure attached");
    }

    // Issue #273 (B1): a failed retirement turn records its retry time on the head intent; the rescan runs it again
    // without a restart, and a refresh admitted after it runs only afterwards and recreates the branch.
    [TestMethod]
    public async Task Recover_RetirementTurnWriteFailure_RetriesWithoutRestartBeforeLaterRefresh()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        var failures = 1;
        _service.BeforeIntentWriteForTesting = op =>
        {
            if (op == "retire-branch:retire" && Interlocked.Decrement(ref failures) >= 0)
                throw new Microsoft.Data.Sqlite.SqliteException("injected busy", 5);
        };
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" }));
        var retireId = LatestRetireIntentId("feature");
        var retire = IntentById(retireId);
        Assert.AreEqual("pending", retire.State);
        Assert.AreEqual(1, retire.Attempts);
        Assert.IsNotNull(retire.NextAttemptAt);
        Assert.AreEqual("X", HeadCommit("feature"));

        _remote.BranchHeads["feature"] = "C";
        var later = await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        Assert.AreEqual("queued", _service.GetRefreshAdmissionStatus(later.AdmissionId!)!.Status);
        Assert.AreEqual("X", HeadCommit("feature"), "the later refresh does not overtake the failed retirement");

        _clock.Advance(TimeSpan.FromSeconds(60));
        await WaitUntil(() => PendingRefreshCount() == 0 && HeadCommit("feature") == "C");
        Assert.AreEqual("retired", IntentById(retireId).Outcome);
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(later.AdmissionId!)!.Status);
        Assert.IsTrue(retireId < IntentByAdmissionId(later.AdmissionId!).Id);
    }

    // Rules 6 and 7: a failed retirement turn's retry deadline is durable: after a restart it runs on that due time.
    [TestMethod]
    public async Task Recover_RetireRetryDeadlineSurvivesRestart()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        var failures = 1;
        _service.BeforeIntentWriteForTesting = op =>
        {
            if (op == "retire-branch:retire" && Interlocked.Decrement(ref failures) >= 0)
                throw new Microsoft.Data.Sqlite.SqliteException("injected busy", 5);
        };
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" }));
        var intentId = LatestRetireIntentId("feature");
        var due = IntentById(intentId).NextAttemptAt;
        Assert.IsNotNull(due);
        _service.Dispose();
        _service = StartService();
        _clock.Advance(TimeSpan.FromSeconds(29));
        _service.RescanBranchIntentsForTesting();
        Assert.AreEqual("pending", IntentById(intentId).State);
        Assert.AreEqual("X", HeadCommit("feature"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => IntentById(intentId).State == "settled");
        Assert.IsNull(HeadCommit("feature"));
    }

    // Issue #273 (B2): an explicit CAS ensure that moves a branch onto an already-published (off-branch) snapshot is an
    // admission like any branch move: it writes its ensure intent and following refresh intent, waits behind an
    // earlier retrying intent of the branch, attaches without another worker run and then converges to the remote head.
    [TestMethod]
    public async Task OffBranchPublishedIdentity_CasMoveIsAdmittedWithRefresh_WaitsBehindRetryingIntentAndEndsAtRemoteHead()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("FA");
        var offBranch = await _service.EnsureSnapshotAsync(
            Request("FB", "feature") with { BranchUpdate = BranchUpdateMode.None });
        Assert.AreEqual(SnapshotJobStatus.Complete, offBranch.Status);
        Assert.AreEqual("FA", HeadCommit("feature"), "the published identity is off the branch");

        // An earlier intent of the branch is retrying: a guarded retirement whose turn failed once.
        var failures = 1;
        _service.BeforeIntentWriteForTesting = op =>
        {
            if (op == "retire-branch:retire" && Interlocked.Decrement(ref failures) >= 0)
                throw new Microsoft.Data.Sqlite.SqliteException("injected busy", 5);
        };
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => _service.RetireBranchAsync(
            new RetireBranchRequest { Repository = Repo, Branch = "feature", ExpectedHeadCommit = "not-the-head" }));
        var retireId = LatestRetireIntentId("feature");

        _remote.BranchHeads["feature"] = "FC";
        var move = _service.EnsureSnapshotAsync(Request("FB", "feature") with { ExpectedHeadCommit = "FA" });
        await WaitUntil(() => IntentsFor("feature").Count(i => i.Id > retireId) == 2);
        var admitted = IntentsFor("feature").Where(i => i.Id > retireId).ToList();
        CollectionAssert.AreEqual(new[] { "ensure", "refresh" }, admitted.Select(i => i.Kind).ToArray(),
            "the move writes its ensure intent and the following refresh intent");
        Assert.IsTrue(admitted.All(i => i.State == "pending"));
        Assert.IsFalse(move.IsCompleted, "the move waits behind the earlier retrying intent");
        Assert.AreEqual("FA", HeadCommit("feature"));

        _clock.Advance(TimeSpan.FromSeconds(60));
        var moved = await move.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(SnapshotJobStatus.Complete, moved.Status);
        Assert.IsTrue(moved.BranchAdvanced == true, "the move attached FB and advanced the pointer");
        await WaitUntil(() => PendingRefreshCount() == 0 && QueuedEnsureCount() == 0 && HeadCommit("feature") == "FC");
        Assert.AreEqual(BranchGuardReason.HeadMismatch, IntentById(retireId).Outcome);
        Assert.IsTrue(IntentsFor("feature").All(i => i.State == "settled"));
        Assert.AreEqual(1, _built.Count(commit => commit == "FB"), "the published identity was attached, not rebuilt");
        Assert.AreEqual(1, _built.Count(commit => commit == "FC"));
    }

    private IReadOnlyList<BranchIntent> IntentsFor(string branch)
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM branch_intents WHERE branch = @branch ORDER BY id;";
        cmd.Parameters.AddWithValue("@branch", branch);
        var ids = new List<long>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        var store = new BranchIntentStore(conn);
        return ids.Select(id => store.Get(id)!).ToList();
    }

    // Issue #273 (D2): an identical ensure shares an earlier ensure only while that ensure is its branch's newest intent.
    // A retirement admitted after E puts the retry after it: E builds Y, the retirement deletes the branch, and the
    // retry's own intent recreates it at E's snapshot without another worker run.
    [TestMethod]
    public async Task Coalesce_IdenticalEnsureAfterAdmittedRetire_IsAdmittedAfterItAndRecreatesBranch()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        var baseline = MaxIntentId();
        _remote.BranchHeads["feature"] = "Y";
        BlockCommit("Y");
        var request = Request("Y", "feature");
        var first = _service.EnsureSnapshotAsync(request);
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retire = _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" });
        await WaitUntil(() => IntentsAfter(baseline, "feature").Any(i => i.Kind == "retire"));

        var retry = _service.EnsureSnapshotAsync(request);
        await WaitUntil(() => IntentsAfter(baseline, "feature").Count(i => i.Kind == "ensure") == 2);
        _releaseBlocked!.TrySetResult();

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue((await retire.WaitAsync(TimeSpan.FromSeconds(10))).Retired);
        var retryResult = await retry.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => PendingRefreshCount() == 0 && QueuedEnsureCount() == 0);

        Assert.AreEqual(SnapshotJobStatus.Complete, retryResult.Status);
        Assert.AreNotEqual(firstResult.AdmissionId, retryResult.AdmissionId, "the retry was not shared across the retirement");
        var retireId = LatestRetireIntentId("feature");
        Assert.AreEqual("retired", IntentById(retireId).Outcome);
        Assert.IsTrue(IntentByAdmissionId(retryResult.AdmissionId!).Id > retireId);
        Assert.AreEqual("Y", HeadCommit("feature"), "the retry recreated the retired branch at E's snapshot");
        Assert.AreEqual(1, _built.Count(c => c == "Y"), "the retry attached E's snapshot");
    }

    // Issue #273 (D2, FIFO since round 28): a push refresh admitted while E runs lands directly behind E's own pending
    // following refresh and is absorbed into it, so nothing intervenes after E: an identical retry still shares E. (A
    // retirement, or another ensure, admitted after E does intervene: Coalesce_IdenticalEnsureAfterAdmittedRetire_...)
    [TestMethod]
    public async Task Coalesce_RefreshAbsorbedIntoEsOwnRefresh_DoesNotPreventSharing()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        var baseline = MaxIntentId();
        _remote.BranchHeads["feature"] = "Y";
        BlockCommit("Y");
        var request = Request("Y", "feature");
        var first = _service.EnsureSnapshotAsync(request);
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var refresh = await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        Assert.AreEqual(SnapshotJobStatus.Coalesced, refresh.Status);
        var ownRefresh = IntentsAfter(baseline, "feature").Single(i => i.Kind == "refresh");
        Assert.AreEqual(ownRefresh.AdmissionId, refresh.AdmissionId, "the push shares E's own following refresh");

        var shared = await _service.BeginEnsureSnapshotAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(shared.Attached);
        _releaseBlocked!.TrySetResult();

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => PendingRefreshCount() == 0 && QueuedEnsureCount() == 0);
        Assert.AreEqual(firstResult.AdmissionId, shared.AdmissionId);
        Assert.AreEqual(1, IntentsAfter(baseline, "feature").Count(i => i.Kind == "ensure"));
        Assert.AreEqual("Y", HeadCommit("feature"));
        Assert.AreEqual(1, _built.Count(c => c == "Y"));
    }

    // Issue #273 (D2): with only E's own following refresh after it, an identical ensure still shares E: one worker call
    // and one ensure intent.
    [TestMethod]
    public async Task Coalesce_IdenticalEnsureWithNothingInBetween_SharesOneIntentAndWorkerCall()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        var baseline = MaxIntentId();
        _remote.BranchHeads["feature"] = "Y";
        BlockCommit("Y");
        var request = Request("Y", "feature");
        var first = _service.EnsureSnapshotAsync(request);
        await _blockedStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // wait=false returns once the admission transaction has decided (E is producing), before E is released, so
        // both retries are decided while E is still live.
        var shared = await _service.BeginEnsureSnapshotAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        var sharedAgain = await _service.BeginEnsureSnapshotAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(shared.Attached);
        Assert.IsTrue(sharedAgain.Attached);
        _releaseBlocked!.TrySetResult();

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => PendingRefreshCount() == 0 && QueuedEnsureCount() == 0);
        Assert.AreEqual(SnapshotJobStatus.Complete, firstResult.Status);
        Assert.AreEqual(firstResult.AdmissionId, shared.AdmissionId);
        Assert.AreEqual(firstResult.AdmissionId, sharedAgain.AdmissionId, "a share of a share resolves to E");
        Assert.AreEqual(1, IntentsAfter(baseline, "feature").Count(i => i.Kind == "ensure"));
        Assert.AreEqual(1, _built.Count(c => c == "Y"));
        Assert.AreEqual("Y", HeadCommit("feature"));
    }

    // Issue #273 (D2): startup replays ensures and retirements interleaved in branch_intents id order and applies the
    // same sharing rule, so an identical ensure recorded after a retirement runs its own turn after it.
    [TestMethod]
    public async Task Coalesce_ReplayedIdenticalEnsureAfterRetire_RunsAfterTheRetirement()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        _service.Dispose();

        var conn = _db.GetConnection();
        var repositoryId = new SnapshotStore(conn).GetRepositoryId(Repo)!.Value;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var intents = new BranchIntentStore(conn);
        var refreshes = new PendingRefreshStore(conn);
        var request = Request("Y", "feature");
        var json = System.Text.Json.JsonSerializer.Serialize(request, ServiceJson.Options);
        var hash = request.ToIdentity().Hash;
        long nextSeq;
        using (var max = conn.CreateCommand())
        {
            max.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1000 FROM queued_ensures;";
            nextSeq = Convert.ToInt64(max.ExecuteScalar());
        }
        BranchIntent AdmitEnsure(long seq, string refreshId)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO queued_ensures (seq, identity_hash, request_json, admitted_at) VALUES (@seq, @hash, @json, @at);
                """;
            insert.Parameters.AddWithValue("@seq", seq);
            insert.Parameters.AddWithValue("@hash", hash);
            insert.Parameters.AddWithValue("@json", json);
            insert.Parameters.AddWithValue("@at", now);
            insert.ExecuteNonQuery();
            var ensure = intents.Admit(repositoryId, "feature", "ensure", seq, null, json, now);
            intents.Admit(repositoryId, "feature", "refresh", null, refreshId, null, now);
            refreshes.Admit(repositoryId, "feature", refreshId, now);
            return ensure;
        }
        var first = AdmitEnsure(nextSeq, "replay-refresh-1");
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            request = new RetireBranchRequest { Repository = Repo, Branch = "feature" },
            principal = default(AuditCaller),
            idle = (object?)null
        }, ServiceJson.Options);
        var retire = intents.Admit(repositoryId, "feature", "retire", null, null, payload, now);
        var second = AdmitEnsure(nextSeq + 1, "replay-refresh-2");

        _remote.BranchHeads["feature"] = "Y";
        _service = StartService();
        await WaitUntil(() => QueuedEnsureCount() == 0 && PendingRefreshCount() == 0
            && IntentById(second.Id).State == "settled");

        Assert.AreEqual("retired", IntentById(retire.Id).Outcome);
        Assert.AreEqual(SnapshotJobStatus.Complete, IntentById(first.Id).Outcome);
        Assert.AreEqual(SnapshotJobStatus.Complete, IntentById(second.Id).Outcome);
        Assert.AreEqual("Y", HeadCommit("feature"));
        Assert.AreEqual(1, _built.Count(c => c == "Y"));
        using var read = _db.OpenReadConnection();
        using var audit = read.CreateCommand();
        audit.CommandText = """
            SELECT COUNT(*) FROM audit_log
            WHERE action = 'ensure' AND repository_scope = @repo AND actor IS NULL
              AND id > (SELECT MAX(id) FROM audit_log WHERE action = 'retire');
            """;
        audit.Parameters.AddWithValue("@repo", Repo);
        Assert.AreEqual(1L, (long)audit.ExecuteScalar()!, "the second ensure ran its own turn after the retirement");
    }

    // Issue #273: an unguarded remote ensure of X gets a 503 while its admission is written; the admission commits, X
    // publishes, and its refresh then observes Y and supersedes X. The identical retry reselects the intact X snapshot
    // without running the worker; its own (unchanged) branch decision applies, and its following refresh leaves the
    // branch at the remote head Y.
    [TestMethod]
    public async Task UnguardedRetryAfter503ReselectsSupersededSnapshotWithoutWorker()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("W");
        _remote.BranchHeads["feature"] = "X";
        var request = Request("X", "feature");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        _service.BeforeQueuedEnsureWriteForTesting = () =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var post = _service.BeginEnsureSnapshotAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsExactlyAsync<Sextant.Service.Grants.GrantStoreUnavailableException>(() => post.WaitAsync(TimeSpan.FromSeconds(10)));
        _service.BeforeQueuedEnsureWriteForTesting = null;
        _remote.BranchHeads["feature"] = "Y";
        release.Set();
        await WaitUntil(() => _built.Contains("X") && _built.Contains("Y")
            && PendingRefreshCount() == 0 && QueuedEnsureCount() == 0);
        Assert.AreEqual("Y", HeadCommit("feature"), "the refresh superseded X");
        var calls = _built.Count;

        var retry = await _service.EnsureSnapshotAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => PendingRefreshCount() == 0 && QueuedEnsureCount() == 0);

        Assert.AreEqual(calls, _built.Count, "the retained X snapshot was reselected without the worker");
        Assert.AreEqual(SnapshotJobStatus.Complete, retry.Status);
        Assert.IsNotNull(retry.AdmissionId);
        using (var conn = _db.OpenReadConnection())
        {
            var snapshots = new SnapshotStore(conn);
            Assert.AreEqual("X", snapshots.GetCommitSha(snapshots.GetById(retry.SnapshotId!.Value)!.CommitId),
                "the retry reports the retained X snapshot");
        }
        Assert.AreEqual("Y", HeadCommit("feature"), "its following refresh converged back to the remote head");
    }

    // Issue #273 (D2), review rounds 20-21, FIFO since round 28: with a queued-ensure age above seven days, ensure E (own
    // refresh R1) stays pending, a retirement T (whose CAS will not match) is admitted, then an identical ensure F (own
    // refresh R4). Retention runs before the restart; nothing between E and F is settled while E is unsettled, so it
    // keeps them, and F's admitted intent (the stored decision) takes its own turn after E.
    [TestMethod]
    public async Task Coalesce_ReplayAfterRetentionDoesNotShareAcrossPrunableRefreshes()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        await WaitUntil(() => _service.WriteGateForTesting.CurrentCount == 1);
        _service.Dispose();

        var conn = _db.GetConnection();
        var repositoryId = new SnapshotStore(conn).GetRepositoryId(Repo)!.Value;
        var admittedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var intents = new BranchIntentStore(conn);
        var refreshes = new PendingRefreshStore(conn);
        var request = Request("Y", "feature");
        var json = System.Text.Json.JsonSerializer.Serialize(request, ServiceJson.Options);
        long nextSeq;
        using (var max = conn.CreateCommand())
        {
            max.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1000 FROM queued_ensures;";
            nextSeq = Convert.ToInt64(max.ExecuteScalar());
        }
        BranchIntent Refresh(string id)
        {
            var refresh = intents.Admit(repositoryId, "feature", "refresh", null, id, null, admittedAt);
            refreshes.Admit(repositoryId, "feature", id, admittedAt);
            return refresh;
        }
        BranchIntent Ensure(long seq, string refreshId)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO queued_ensures (seq, identity_hash, request_json, admitted_at) VALUES (@seq, @hash, @json, @at);
                """;
            insert.Parameters.AddWithValue("@seq", seq);
            insert.Parameters.AddWithValue("@hash", request.ToIdentity().Hash);
            insert.Parameters.AddWithValue("@json", json);
            insert.Parameters.AddWithValue("@at", admittedAt);
            insert.ExecuteNonQuery();
            var ensure = intents.Admit(repositoryId, "feature", "ensure", seq, null, json, admittedAt);
            intents.SetFollowingRefresh(ensure.Id, Refresh(refreshId).Id);
            return ensure;
        }
        var e = Ensure(nextSeq, "rv20-r1");
        var t = intents.Admit(repositoryId, "feature", "retire", null, null, System.Text.Json.JsonSerializer.Serialize(new
        {
            request = new RetireBranchRequest { Repository = Repo, Branch = "feature", ExpectedHeadCommit = "Z" },
            principal = default(AuditCaller),
            idle = (object?)null
        }, ServiceJson.Options), admittedAt);
        var f = Ensure(nextSeq + 1, "rv20-r4");

        _clock.Advance(TimeSpan.FromDays(8));
        var pruned = refreshes.PruneCompleted(_clock.GetUtcNow().AddDays(-7).ToUnixTimeMilliseconds());
        Assert.IsTrue(pruned > 0, "older settled intents of other branches are pruned");
        Assert.IsNotNull(intents.Get(t.Id), "nothing between E and F settles while E is unsettled, so retention keeps it");
        Assert.AreEqual(2, intents.ListUnsettled().Count(i => i.Kind == "ensure"));
        long auditBaseline;
        using (var audit = conn.CreateCommand())
        {
            audit.CommandText = "SELECT COALESCE(MAX(id), 0) FROM audit_log;";
            auditBaseline = Convert.ToInt64(audit.ExecuteScalar());
        }

        _remote.BranchHeads["feature"] = "Y";
        _service = StartService(queuedEnsureMaxAge: TimeSpan.FromDays(30));
        await WaitUntil(() => QueuedEnsureCount() == 0 && PendingRefreshCount() == 0
            && IntentById(f.Id).State == "settled");

        Assert.AreEqual(SnapshotJobStatus.Complete, IntentById(e.Id).Outcome);
        Assert.AreEqual(SnapshotJobStatus.Complete, IntentById(f.Id).Outcome);
        Assert.AreEqual("Y", HeadCommit("feature"));
        Assert.AreEqual(1, _built.Count(c => c == "Y"));
        using var read = _db.OpenReadConnection();
        using var ensures = read.CreateCommand();
        ensures.CommandText = """
            SELECT COUNT(*) FROM audit_log
            WHERE action = 'ensure' AND repository_scope = @repo AND actor IS NULL AND id > @baseline;
            """;
        ensures.Parameters.AddWithValue("@repo", Repo);
        ensures.Parameters.AddWithValue("@baseline", auditBaseline);
        Assert.AreEqual(2L, (long)ensures.ExecuteScalar()!, "F ran its own ordered turn instead of sharing E");
    }

    // Issue #273, review rounds 21 and 29: a retirement that keeps failing stays its branch's head while pushes keep
    // arriving; they are absorbed into one pending refresh, so the queue stays bounded across a restart. Once the
    // retirement is older than the queued-ensure max age it is dropped (settled `expired`, without effect) and the
    // push behind it converges the branch.
    [TestMethod]
    public async Task Retention_PushesBehindAFailingRetirementStayBounded_ThenItExpiresAndThePushConverges()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        var baseline = MaxIntentId();
        void FailRetirements(SnapshotService service) => service.BeforeIntentWriteForTesting = op =>
        {
            if (op == "retire-branch:retire")
                throw new Microsoft.Data.Sqlite.SqliteException("injected busy", 5);
        };
        FailRetirements(_service);
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" }));
        var retireId = LatestRetireIntentId("feature");

        async Task Pushes(int count)
        {
            for (var i = 0; i < count; i++)
                await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        }
        int Settled() => IntentsAfter(baseline, "feature").Count(i => i.State == "settled");
        int Refreshes() => IntentsAfter(baseline, "feature").Count(i => i.Kind == "refresh");

        // Issue #273 (FIFO): every push lands directly behind the first, still pending refresh and is absorbed into it,
        // so the queue stays one retirement plus one refresh however many pushes arrive.
        await Pushes(20);
        Assert.AreEqual(1, Refreshes());
        Assert.AreEqual(0, Settled());

        // Restart while the retirement's persisted retry deadline is still in the future, so it does not run before the
        // new instance's failure injection is in place.
        _service.Dispose();
        _service = StartService();
        FailRetirements(_service);
        await Pushes(10);
        Assert.AreEqual(1, Refreshes());

        _clock.Advance(TimeSpan.FromDays(6));
        await WaitUntil(() => IntentById(retireId).Attempts >= 2);
        await Pushes(5);
        Assert.AreEqual(1, Refreshes(), "the branch queue stays bounded behind the failing head");
        Assert.AreEqual(0, Settled());
        Assert.AreNotEqual("settled", IntentById(retireId).State, "the failing head retried and is still unsettled");
        Assert.AreEqual("X", HeadCommit("feature"));

        _remote.BranchHeads["feature"] = "Y";
        _clock.Advance(TimeSpan.FromDays(2));
        await WaitUntil(() => IntentById(retireId).State == "settled" || ClockStep());
        var retire = IntentById(retireId);
        Assert.AreEqual("expired", retire.Outcome, "the retirement is dropped without effect once it is too old");
        Assert.AreEqual("expired", _service.GetRefreshAdmissionStatus(retire.AdmissionId!)!.Status);
        await WaitUntil(() => HeadCommit("feature") == "Y" || ClockStep());
        Assert.AreEqual(0, IntentsAfter(baseline, "feature").Count(i => i.State != "settled"));
    }

    // Rules 4 and 7: a retirement whose persisted payload can no longer be read ends `refused`, without effect, instead
    // of blocking its branch, and the push behind it converges.
    [TestMethod]
    public async Task UnreadableRetirementPayload_IsRefusedAndThePushBehindItConverges()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        _service.BeforeIntentWriteForTesting = op =>
        {
            if (op == "retire-branch:retire")
                throw new Microsoft.Data.Sqlite.SqliteException("injected busy", 5);
        };
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" }));
        var retireId = LatestRetireIntentId("feature");
        _service.Dispose();
        using (var corrupt = _db.GetConnection().CreateCommand())
        {
            corrupt.CommandText = "UPDATE branch_intents SET payload_json = '{not json' WHERE id = @id;";
            corrupt.Parameters.AddWithValue("@id", retireId);
            corrupt.ExecuteNonQuery();
        }
        _remote.BranchHeads["feature"] = "Y";
        _service = StartService();
        var push = await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });

        await WaitUntil(() => IntentById(retireId).State == "settled" || ClockStep());
        Assert.AreEqual("refused", IntentById(retireId).Outcome);
        await WaitUntil(() => HeadCommit("feature") == "Y" || ClockStep());
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(push.AdmissionId!)!.Status);
    }

    // ---- review round 28: strict per-branch FIFO -----------------------------------------------------------------

    // A push between the upstream listing and the delete admission: the delete runs after it (FIFO), refuses because an
    // intent was admitted after the listing began, and the push is indexed.
    [TestMethod]
    public async Task Fifo_PushBetweenUpstreamListingAndDeleteAdmission_DeleteRefusesAndPushIsIndexed()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        _remote.MissingBranches.Add("feature");
        var first = await _service.ReconcileUpstreamBranchesAsync(_clock.GetUtcNow());
        Assert.AreEqual(1, first.AwaitingConfirmation, first.ToString());
        _clock.Advance(TimeSpan.FromHours(1));

        var (started, release) = _remote.BlockNextListing();
        var second = _service.ReconcileUpstreamBranchesAsync(_clock.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromSeconds(1));
        _remote.MissingBranches.Remove("feature");
        _remote.BranchHeads["feature"] = "B";
        var push = await _service.BeginEnsureSnapshotAsync(Request("hint", "feature") with { Refresh = true });
        release.TrySetResult();

        var report = await second.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => DeleteIntent("feature")?.State == "settled"
            && _service.GetRefreshAdmissionStatus(push.AdmissionId!)?.Status == "completed");
        Assert.AreEqual(0, report.Retired, report.ToString());
        // The push ran first and moved the pointer, which (like the admission after the listing) refuses the stale delete.
        Assert.IsTrue(DeleteIntent("feature")!.Outcome is BranchGuardReason.UpstreamActivity or BranchGuardReason.PointerMoved,
            DeleteIntent("feature")!.Outcome);
        Assert.AreEqual("B", HeadCommit("feature"), "the push was indexed, not erased");
    }

    // Rules 3 and 5: refresh R1 defers, retirement T is admitted behind it and held past R1's retry deadline, then push P
    // is admitted. P is never absorbed into R1 across T (T is the tail): it gets its own refresh after T. In admission
    // order R1 runs, T retires the branch, and P recreates it: the retirement never removes the push admitted after it.
    [TestMethod]
    public async Task Fifo_RetireThenPush_ThePushAdmittedAfterTheRetirementRecreatesTheBranch()
    {
        await SeedMainAt("A");
        await CreateFeatureAt("X");
        _remote.TimeoutHeadReads = 1;
        var r1 = await _service.BeginEnsureSnapshotAsync(Request("hint-1", "feature") with { Refresh = true });
        await WaitUntil(() => PendingRefreshAttempts() == 1 && _service.WriteGateForTesting.CurrentCount == 1);

        var holder = HoldWriter();
        await _holdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retire = _service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" });
        await WaitUntil(() => HasRetireIntent("feature"));

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsFalse(IntentByAdmissionId(r1.AdmissionId!).NextAttemptAt > _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            "R1's retry deadline has passed while T is held");
        _remote.BranchHeads["feature"] = "Y";
        var push = await _service.BeginEnsureSnapshotAsync(Request("hint-2", "feature") with { Refresh = true });
        Assert.AreNotEqual(r1.AdmissionId, push.AdmissionId, "a push is never absorbed across a retirement");
        Assert.IsTrue(IntentByAdmissionId(push.AdmissionId!).Id > LatestRetireIntentId("feature"));

        _releaseHold.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue((await retire.WaitAsync(TimeSpan.FromSeconds(20))).Retired);
        await WaitUntil(() => HeadCommit("feature") == "Y" || ClockStep());
        Assert.AreEqual("completed", _service.GetRefreshAdmissionStatus(r1.AdmissionId!)!.Status, "R1 ran first");
        await WaitUntil(() => _service.GetRefreshAdmissionStatus(push.AdmissionId!)?.Status == "completed" || ClockStep());
    }

    // Advances the injected clock to the next future durable retry deadline (or a short nudge when none is pending), but only
    // while no turn is in flight: a fixed large step could fire an in-flight remote read's 30-second timeout and turn a
    // healthy turn into another retry. Reports false, for WaitUntil.
    private bool ClockStep()
    {
        if (_service.WriteGateForTesting.CurrentCount != 1)
            return false;
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (SELECT 1 FROM pending_refreshes WHERE claimed_by_turn IS NOT NULL)
                OR EXISTS (SELECT 1 FROM branch_intents WHERE state = 'claimed'),
                   (SELECT MIN(due) FROM (
                        SELECT next_attempt_at AS due FROM pending_refreshes
                        UNION ALL
                        SELECT next_attempt_at FROM branch_intents WHERE state = 'pending' AND next_attempt_at IS NOT NULL)
                    WHERE due > @now);
            """;
        cmd.Parameters.AddWithValue("@now", _clock.GetUtcNow().ToUnixTimeMilliseconds());
        using var reader = cmd.ExecuteReader();
        reader.Read();
        if (reader.GetInt64(0) == 1)
            return false;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var due = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        _clock.Advance(due is long next ? TimeSpan.FromMilliseconds(next - now + 1) : TimeSpan.FromSeconds(1));
        return false;
    }

    // Dumps every intent and refresh marker (state, attempts, retry deadline, last error) for a timed-out wait.
    private string QueueDiagnostics()
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 'intent ' || id || ' ' || kind || ' ' || branch || ' ' || state || ' outcome=' || COALESCE(outcome, '-')
                   || ' attempts=' || attempts || ' next=' || COALESCE(next_attempt_at, '-')
                   || ' queue_key=' || COALESCE(queue_key, '-') || ' claimed=' || COALESCE(claimed_by_turn, '-')
                   || ' error=' || COALESCE(last_error, '-')
            FROM branch_intents
            UNION ALL
            SELECT 'marker ' || admission_id || ' ' || branch || ' attempts=' || attempts || ' next=' || next_attempt_at
                   || ' claimed=' || COALESCE(claimed_by_turn, '-') || ' error=' || COALESCE(last_error, '-')
            FROM pending_refreshes;
            """;
        using var reader = cmd.ExecuteReader();
        var lines = new List<string> { "now=" + _clock.GetUtcNow().ToUnixTimeMilliseconds() };
        while (reader.Read())
            lines.Add(reader.GetString(0));
        return string.Join(Environment.NewLine, lines);
    }

    private long MaxIntentId()
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM branch_intents;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private List<BranchIntent> IntentsAfter(long id, string branch)
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM branch_intents WHERE id > @id AND branch = @branch ORDER BY id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@branch", branch);
        var ids = new List<long>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        var store = new BranchIntentStore(conn);
        // Retention may prune a row between the two reads.
        return ids.Select(store.Get).OfType<BranchIntent>().ToList();
    }

    private async Task CreateFeatureAt(string commit)
    {
        _remote.BranchHeads["feature"] = commit;
        await _service.EnsureSnapshotAsync(Request(commit, "feature") with { ExpectedHeadCommit = "" });
        await WaitUntil(() => PendingRefreshCount() == 0 && QueuedEnsureCount() == 0);
        Assert.AreEqual(commit, HeadCommit("feature"));
    }

    // Every intent of a branch, in queue order.
    private List<BranchIntent> QueueOrder(string branch)
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM branch_intents WHERE branch = @branch
            ORDER BY COALESCE(queue_key, admission_key, id), id;
            """;
        cmd.Parameters.AddWithValue("@branch", branch);
        var ids = new List<long>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        var store = new BranchIntentStore(conn);
        return ids.Select(store.Get).OfType<BranchIntent>().ToList();
    }

    private BranchIntent IntentById(long id)
    {
        using var conn = _db.OpenReadConnection();
        return new BranchIntentStore(conn).Get(id)
            ?? throw new AssertFailedException($"Branch intent {id} was not recorded.");
    }

    private BranchIntent? DeleteIntent(string branch)
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM branch_intents WHERE kind = 'delete' AND branch = @branch ORDER BY id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@branch", branch);
        return cmd.ExecuteScalar() is long id ? new BranchIntentStore(conn).Get(id) : null;
    }

    private SnapshotService StartService(bool useRemote = true, TimeSpan? queuedEnsureMaxAge = null)
    {
        var options = ServiceTestFixtures.NewOptions(_dbPath) with
        {
            FreshnessCheckInterval = TimeSpan.Zero,
            Maintenance = new MaintenancePolicy { Interval = TimeSpan.Zero }
        };
        if (queuedEnsureMaxAge is { } maxAge)
            options = options with { QueuedEnsureMaxAge = maxAge };
        var service = SnapshotService.Start(
            options, _worker, _db, remoteDefaults: useRemote ? _remote : null, timeProvider: _clock,
            remoteBranches: useRemote ? _remote : null);
        service.ProgressForTesting = () => _progress.Release();
        service.EnableWriterTurnGuardForTesting();
        return service;
    }

    private async Task SeedMainAt(string commit)
    {
        _remote.Head = commit;
        var seeded = await _service.EnsureSnapshotAsync(Request(commit, "main") with { ExpectedHeadCommit = "" });
        Assert.AreEqual(SnapshotJobStatus.Complete, seeded.Status);
        Assert.AreEqual(commit, HeadCommit("main"));
        await WaitUntil(() => PendingRefreshCount() == 0 && _service.WriteGateForTesting.CurrentCount == 1);
    }

    private Task<EnsureSnapshotResult> HoldWriter()
    {
        _ = _releaseHold.Task;
        return _service.EnsureSnapshotAsync(Request("hold", "other") with { BranchUpdate = BranchUpdateMode.None });
    }

    private Task<EnsureSnapshotResult> Push(string commit, string expected, bool forced = false) =>
        _service.EnsureSnapshotAsync(Request(commit, "main") with
        {
            ExpectedHeadCommit = expected,
            Forced = forced,
            Refresh = true
        });

    private Task<EnsureSnapshotResult> AdmitPush(string commit, string expected, bool forced = false) =>
        _service.BeginEnsureSnapshotAsync(Request(commit, "main") with
        {
            ExpectedHeadCommit = expected,
            Forced = forced,
            Refresh = true
        });

    private int PendingRefreshCount(bool claimedOnly = false)
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = claimedOnly
            ? "SELECT COUNT(*) FROM pending_refreshes WHERE claimed_by_turn IS NOT NULL;"
            : "SELECT COUNT(*) FROM pending_refreshes;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int PendingRefreshAttempts()
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(attempts), 0) FROM pending_refreshes;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static EnsureSnapshotRequest Request(string commit, string branch) =>
        new() { RepositoryRemoteUrl = Repo, CommitSha = commit, BranchName = branch };

    private void BlockCommit(string commit)
    {
        _blockedCommit = commit;
        _blockedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaseBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<SnapshotWorkResult> ProduceAsync(
        FakeSnapshotWorker self, EnsureSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (request.CommitSha == "hold")
        {
            _holdStarted.TrySetResult();
            await _releaseHold.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        if (request.CommitSha == _blockedCommit)
        {
            var started = _blockedStarted!;
            var release = _releaseBlocked!;
            started.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        _built.Enqueue(request.CommitSha);
        _onProduce?.Invoke(request);
        if (_unsupportedCommits.ContainsKey(request.CommitSha))
            return SnapshotWorkResult.Unsupported("test unsupported");
        if (_returnedFailOnce.TryRemove(request.CommitSha, out _))
            return SnapshotWorkResult.Failed("test worker failure");
        if (_terminalFailOnce.TryRemove(request.CommitSha, out _))
            throw new InvalidOperationException("test worker failure");
        if (_failOnce.TryGetValue(request.CommitSha, out var remaining) && remaining > 0
            && _failOnce.TryUpdate(request.CommitSha, remaining - 1, remaining))
            throw new TransientProvisioningException("temporary test failure");

        var snapshotId = ServiceTestFixtures.PublishComplete(self.Database, request, recordCommit: true);
        if (request.RebuildGeneration is not null)
            new SnapshotCoverageStore(self.Database.GetConnection()).Record(snapshotId, new SnapshotCoverage
            {
                Verdict = "complete",
                Rebuild = new SnapshotRebuild
                {
                    Generation = request.RebuildGeneration,
                    OriginalIdentityHash = (request with { RebuildGeneration = null }).ToIdentity().Hash
                }
            }, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        if (!request.SuppressesBranchUpdate)
        {
            var store = new SnapshotStore(self.Database.GetConnection());
            if (store.GetRepositoryId(request.RepositoryRemoteUrl) is long repoId)
            {
                var branchId = store.GetBranchId(repoId, request.BranchName ?? "main");
                if (request.ExpectedHeadCommit is not { } expected
                    || store.BranchHeadMatches(branchId, expected, repoId, request.BranchName))
                {
                    var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                    var ensured = store.EnsureBranch(repoId, request.BranchName ?? "main",
                        request.IsDefaultBranch == true || request.BranchName == "main", now);
                    if (request.ExpectedHeadCommit is { } guarded)
                        store.AdvanceBranchPointerIfHeadMatches(ensured, snapshotId, guarded, now,
                            preservePrevious: request.RebuildGeneration is not null);
                    else
                        store.AdvanceBranchPointerForwardOnly(ensured, snapshotId, request.BranchHeadSequence, now);
                }
            }
        }
        return _partialCommits.ContainsKey(request.CommitSha)
            ? SnapshotWorkResult.Partial(snapshotId, "test partial coverage")
            : SnapshotWorkResult.Complete(snapshotId);
    }

    private int QueuedEnsureCount()
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM queued_ensures;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private bool IsEnsureQueued(string commit)
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM queued_ensures WHERE json_extract(request_json, '$.commit_sha') = @commit);";
        cmd.Parameters.AddWithValue("@commit", commit);
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    private IReadOnlyList<PendingRefresh> ReadPendingRefreshes()
    {
        using var conn = _db.OpenReadConnection();
        return new PendingRefreshStore(conn).ListPending();
    }

    private bool HasRetireIntent(string branch)
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM branch_intents WHERE kind = 'retire' AND branch = @branch);";
        cmd.Parameters.AddWithValue("@branch", branch);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    private long LatestRetireIntentId(string branch)
    {
        using var conn = _db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM branch_intents
            WHERE kind = 'retire' AND branch = @branch ORDER BY id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@branch", branch);
        return (long)cmd.ExecuteScalar()!;
    }

    private BranchIntent IntentByAdmissionId(string admissionId)
    {
        using var conn = _db.OpenReadConnection();
        return new BranchIntentStore(conn).GetByAdmissionId(admissionId)
            ?? throw new AssertFailedException($"Branch intent for admission {admissionId} was not recorded.");
    }

    private string? HeadCommit(string branch)
    {
        using var conn = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        return snapshots.GetRepositoryId(Repo) is long repoId
               && snapshots.GetBranchId(repoId, branch) is long branchId
               && snapshots.GetBranchSnapshotId(branchId) is long snapshotId
            ? snapshots.GetCommitSha(snapshots.GetById(snapshotId)!.CommitId)
            : null;
    }

    private string? ServedDefault()
    {
        using var conn = _db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        return snapshots.GetRepositoryId(Repo) is long repo
            && snapshots.GetDefaultBranchId(repo) is long branch
            ? snapshots.GetBranchById(branch)?.Name : null;
    }

    private Task WaitUntil(Func<bool> condition) => WaitUntil(condition, null);

    private async Task WaitUntil(Func<bool> condition, Func<string>? diagnostics)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(10))
                Assert.Fail("timed out waiting for the condition" + (diagnostics is null ? "" : Environment.NewLine + diagnostics()));
            await _progress.WaitAsync(TimeSpan.FromMilliseconds(100));
        }
    }

    private sealed class RefreshRemote : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver, ICommitAncestryChecker,
        IRemoteBranchLister
    {
        public string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken) =>
            ResolveDefaultBranch(repositoryRemoteUrl);

        private int _timeoutHeadReads;
        private HeadReadBlock? _headReadBlock;
        private HeadReadBlock? _listingBlock;
        private HeadReadBlock? _defaultReadBlock;
        private HeadReadBlock? _stateReadBlock;

        public string? Head { get; set; }
        public ConcurrentDictionary<string, string> BranchHeads { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MissingBranches { get; } = new(StringComparer.Ordinal);
        public string? DefaultBranch { get; set; } = "main";
        public bool NonAncestor { get; set; }
        public bool AllAncestors { get; set; }
        public bool AncestryUnavailable { get; set; }
        public Action? OnHeadRead { get; set; }
        public Action? AfterStateRead { get; set; }
        public int TimeoutHeadReads
        {
            get => Volatile.Read(ref _timeoutHeadReads);
            set => Volatile.Write(ref _timeoutHeadReads, value);
        }

        public string? ResolveDefaultBranch(string repositoryRemoteUrl)
        {
            var observed = DefaultBranch;
            var block = Interlocked.Exchange(ref _defaultReadBlock, null);
            if (block is not null)
            {
                block.Started.TrySetResult();
                block.Release.Task.GetAwaiter().GetResult();
            }
            return observed;
        }

        public string? ResolveBranchHead(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default)
        {
            OnHeadRead?.Invoke();
            var observed = MissingBranches.Contains(branch) ? null
                : BranchHeads.TryGetValue(branch, out var head) ? head
                : branch == "main" || branch == DefaultBranch || branch == "trunk" ? Head : null;
            var block = Interlocked.Exchange(ref _headReadBlock, null);
            if (block is not null)
            {
                block.Started.TrySetResult();
                block.Release.Task.GetAwaiter().GetResult();
            }
            if (Interlocked.Decrement(ref _timeoutHeadReads) >= 0)
                throw new TimeoutException("test remote-head lookup timed out");
            return observed;
        }

        public RemoteBranchHeadObservation? ObserveBranchHead(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default)
        {
            var head = ResolveBranchHead(repositoryRemoteUrl, branch, cancellationToken);
            return head is not null || MissingBranches.Contains(branch) ? new RemoteBranchHeadObservation(head) : null;
        }

        public RemoteBranchStateObservation? ObserveBranchState(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default)
        {
            var block = Interlocked.Exchange(ref _stateReadBlock, null);
            if (block is not null)
            {
                block.Started.TrySetResult();
                block.Release.Task.GetAwaiter().GetResult();
            }
            var defaultBranch = DefaultBranch;
            var head = MissingBranches.Contains(branch) ? null
                : BranchHeads.TryGetValue(branch, out var value) ? value
                : branch == "main" || branch == defaultBranch || branch == "trunk" ? Head : null;
            var state = defaultBranch is null || head is null ? null : new RemoteBranchStateObservation(defaultBranch, head);
            var after = AfterStateRead;
            AfterStateRead = null;
            after?.Invoke();
            return state;
        }

        public (TaskCompletionSource Started, TaskCompletionSource Release) BlockNextStateRead()
        {
            var block = new HeadReadBlock();
            if (Interlocked.CompareExchange(ref _stateReadBlock, block, null) is not null)
                throw new InvalidOperationException("a state block is already armed");
            return (block.Started, block.Release);
        }

        public IReadOnlyDictionary<string, string>? ListBranches(string repositoryRemoteUrl)
        {
            var heads = BranchHeads.Where(pair => !MissingBranches.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (Head is not null && DefaultBranch is not null)
                heads[DefaultBranch] = Head;
            var block = Interlocked.Exchange(ref _listingBlock, null);
            if (block is not null)
            {
                block.Started.TrySetResult();
                block.Release.Task.GetAwaiter().GetResult();
            }
            return heads;
        }

        public (TaskCompletionSource Started, TaskCompletionSource Release) BlockNextListing()
        {
            var block = new HeadReadBlock();
            if (Interlocked.CompareExchange(ref _listingBlock, block, null) is not null)
                throw new InvalidOperationException("a listing block is already armed");
            return (block.Started, block.Release);
        }

        public (TaskCompletionSource Started, TaskCompletionSource Release) BlockNextHeadRead()
        {
            var block = new HeadReadBlock();
            if (Interlocked.CompareExchange(ref _headReadBlock, block, null) is not null)
                throw new InvalidOperationException("a head-read block is already armed");
            return (block.Started, block.Release);
        }

        public (TaskCompletionSource Started, TaskCompletionSource Release) BlockNextDefaultRead()
        {
            var block = new HeadReadBlock();
            if (Interlocked.CompareExchange(ref _defaultReadBlock, block, null) is not null)
                throw new InvalidOperationException("a default-read block is already armed");
            return (block.Started, block.Release);
        }

        public bool? IsAncestor(
            string repositoryRemoteUrl, string ancestor, string descendant, CancellationToken cancellationToken = default) =>
            AncestryUnavailable ? null : AllAncestors || string.Equals(ancestor, descendant, StringComparison.OrdinalIgnoreCase)
            || (!NonAncestor &&
               ((string.Equals(ancestor, "A", StringComparison.OrdinalIgnoreCase)
                && descendant is "B" or "C" or "E")
            || (string.Equals(ancestor, "B", StringComparison.OrdinalIgnoreCase) && descendant == "C")
            || (string.Equals(ancestor, "X", StringComparison.OrdinalIgnoreCase) && descendant == "C")));

        private sealed class HeadReadBlock
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
