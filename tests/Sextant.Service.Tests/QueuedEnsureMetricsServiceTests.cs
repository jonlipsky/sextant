using Sextant.Core;
using Sextant.TestSupport;
using Sextant.Store;

namespace Sextant.Service.Tests;

[TestClass]
public sealed class QueuedEnsureMetricsServiceTests
{
    private const string Repository = "https://github.com/metrics/queue-service";
    private static readonly DateTimeOffset ClockStart = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task LiveWaitingEnsure_ReportsDepthAndAdmissionAgeUntilItsTurnStarts()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var clock = new ManualClock(ClockStart);
        var worker = new FirstRunGateWorker(db);
        var options = ServiceTestFixtures.NewOptions(dbPath);
        var service = SnapshotService.Start(
            options,
            worker, db, timeProvider: clock);

        try
        {
            var holder = service.EnsureSnapshotAsync(Request("holder", "main", isDefault: true));
            await worker.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var waiting = service.EnsureSnapshotAsync(Request("waiting", "feature"));

            var before = service.CollectQueuedEnsureMetrics();
            Assert.AreEqual(1, before.Total.Depth);
            Assert.AreEqual(1, before.Normal.Depth);
            Assert.AreEqual(0, before.Expedited.Depth);

            clock.Advance(TimeSpan.FromMinutes(2));
            var aged = service.CollectQueuedEnsureMetrics();
            Assert.AreEqual(1, aged.Total.Depth);
            Assert.AreEqual(120, aged.Total.OldestAgeSeconds, 0.001);
            Assert.AreEqual(120, aged.Normal.OldestAgeSeconds, 0.001);

            worker.ReleaseFirst.TrySetResult();
            await worker.SecondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(holder, waiting).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            worker.ReleaseFirst.TrySetResult();
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    public async Task RestartReplay_DeduplicatesDurableRowsAndUsesOriginalAdmissionTime()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var nowMs = ClockStart.ToUnixTimeMilliseconds();
        var options = ServiceTestFixtures.NewOptions(dbPath);
        AddDurableAdmission(db, options, 101, Request("replay-holder", "main", isDefault: true), nowMs - 300_000);
        AddDurableAdmission(db, options, 102, Request("replay-waiting", "feature"), nowMs - 120_000);

        var clock = new ManualClock(ClockStart);
        var worker = new FirstRunGateWorker(db);
        var service = SnapshotService.Start(
            options,
            worker, db, timeProvider: clock);

        try
        {
            await worker.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var replayed = service.CollectQueuedEnsureMetrics();
            Assert.AreEqual(1, replayed.Total.Depth);
            Assert.AreEqual(1, replayed.Normal.Depth);
            Assert.AreEqual(120, replayed.Total.OldestAgeSeconds, 0.001);

            clock.Advance(TimeSpan.FromSeconds(30));
            var aged = service.CollectQueuedEnsureMetrics();
            Assert.AreEqual(1, aged.Total.Depth);
            Assert.AreEqual(150, aged.Normal.OldestAgeSeconds, 0.001);

            worker.ReleaseFirst.TrySetResult();
            await worker.SecondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            worker.ReleaseFirst.TrySetResult();
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    private static EnsureSnapshotRequest Request(string commit, string branch, bool isDefault = false) => new()
    {
        RepositoryRemoteUrl = Repository,
        CommitSha = commit,
        BranchName = branch,
        IsDefaultBranch = isDefault
    };

    private static void AddDurableAdmission(
        IndexDatabase db, ServiceOptions options, long seq, EnsureSnapshotRequest request, long admittedAt)
    {
        using var cmd = db.GetConnection().CreateCommand();
        cmd.CommandText = """
            INSERT INTO queued_ensures(seq, identity_hash, request_json, admitted_at)
            VALUES (@seq, @hash, @request, @admitted);
            """;
        cmd.Parameters.AddWithValue("@seq", seq);
        cmd.Parameters.AddWithValue("@hash", request.ToIdentity(
            options.DefaultConfigHash, options.DefaultCapabilityFingerprint,
            options.SdkPinIdentityComponent, options.RestoreIdentityComponent).Hash);
        cmd.Parameters.AddWithValue("@request", System.Text.Json.JsonSerializer.Serialize(request, ServiceJson.Options));
        cmd.Parameters.AddWithValue("@admitted", admittedAt);
        cmd.ExecuteNonQuery();
    }

    private sealed class FirstRunGateWorker(IndexDatabase db) : ISnapshotWorker
    {
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SnapshotWorkResult> ProduceAsync(
            EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            var result = SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(db, request));
            if (call == 2)
                SecondCompleted.TrySetResult();
            return result;
        }
    }
}
