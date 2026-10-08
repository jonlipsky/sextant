using Sextant.Service;
using Sextant.Service.Observability;
using Sextant.Store;

namespace Sextant.Service.Tests;

[TestClass]
public class MetricsCollectorTests
{
    private const string Complete = SnapshotJobStatus.Complete;
    private const string Partial = SnapshotJobStatus.Partial;
    private const string Failed = SnapshotJobStatus.Failed;
    private const string Queued = SnapshotJobStatus.Queued;
    private const string Running = SnapshotJobStatus.Running;

    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private long _nextId;

    [TestInitialize]
    public void TestInitialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_metrics_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "catalog.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.DeleteDirectory(_root, _db);

    [TestMethod]
    public void RecentJobs_UsesInclusiveStartExclusiveEnd_AndOnlyStampedTerminalRows()
    {
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var end = now.ToUnixTimeMilliseconds();
        var start = now.AddHours(-24).ToUnixTimeMilliseconds();
        Add(Complete, start - 1);
        Add(Complete, start);
        Add(Partial, end - 1);
        Add(Partial, end);
        Add(Queued, null);
        Add(Running, end - 2);
        Add(Failed, null);

        var snapshot = Collect(now);

        Assert.AreEqual(start, snapshot.RecentJobs.WindowStartUnixMs);
        Assert.AreEqual(end, snapshot.RecentJobs.WindowEndUnixMs);
        Assert.AreEqual(2, snapshot.RecentJobs.SampleCount);
        Assert.AreEqual(1, snapshot.RecentJobs.Complete);
        Assert.AreEqual(1, snapshot.RecentJobs.Partial);
        Assert.AreEqual(0.5, snapshot.RecentJobs.CompletenessRate);
    }

    [TestMethod]
    public void RecentJobs_HealthyWindowRecoversFromOldPartialJobs_WithoutChangingCumulativeMetrics()
    {
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var start = now.AddHours(-24).ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++)
            Add(Partial, start - 1 - i);
        for (var i = 0; i < 5; i++)
            Add(Complete, now.AddMinutes(-i - 1).ToUnixTimeMilliseconds());

        var snapshot = Collect(now);

        Assert.AreEqual(11, snapshot.Jobs.Terminal);
        Assert.AreEqual(6, snapshot.Jobs.Partial);
        Assert.AreEqual(5, snapshot.Jobs.Complete);
        Assert.AreEqual(5d / 11, snapshot.Jobs.CompletenessRate, 0.00001);
        Assert.AreEqual(1.0, snapshot.Jobs.SuccessRate);
        Assert.AreEqual(5, snapshot.RecentJobs.SampleCount);
        Assert.AreEqual(1.0, snapshot.RecentJobs.CompletenessRate);
        Assert.IsFalse(snapshot.Alerts.Any(alert => alert.Id == "low_completeness_rate"));
    }

    [TestMethod]
    public void RecentJobs_PartialWindowIsNotMaskedByOldCompleteJobs()
    {
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var start = now.AddHours(-24).ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
            Add(Complete, start - 1 - i);
        for (var i = 0; i < 5; i++)
            Add(Partial, now.AddMinutes(-i - 1).ToUnixTimeMilliseconds());

        var snapshot = Collect(now);

        Assert.AreEqual(0.5, snapshot.Jobs.CompletenessRate);
        Assert.AreEqual(0.0, snapshot.RecentJobs.CompletenessRate);
        Assert.AreEqual(AlertLevel.Critical,
            snapshot.Alerts.Single(alert => alert.Id == "low_completeness_rate").Level);
    }

    [TestMethod]
    public void RecentJobs_InsufficientSamplesAreNotAssessedEvenWhenCumulativeHistoryIsLarge()
    {
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var start = now.AddHours(-24).ToUnixTimeMilliseconds();
        for (var i = 0; i < 20; i++)
            Add(Complete, start - 1 - i);
        for (var i = 0; i < 4; i++)
            Add(Partial, now.AddMinutes(-i - 1).ToUnixTimeMilliseconds());

        var snapshot = Collect(now);

        Assert.AreEqual(24, snapshot.Jobs.Terminal);
        Assert.AreEqual(4, snapshot.RecentJobs.SampleCount);
        Assert.IsFalse(snapshot.RecentJobs.RatesAssessed);
        Assert.IsFalse(snapshot.Alerts.Any(alert => alert.Id == "low_completeness_rate"));
    }

    [TestMethod]
    public void PhaseLatency_AggregatesEachPhaseOverRecordedJobTimings_InExecutionOrder()
    {
        // Issue #267: per-phase p50/p95 over the jobs that recorded timings; a job without timings adds nothing.
        var jobs = new SnapshotJobStore(_db.GetConnection());
        long[] loadMs = [100, 200, 300, 400];
        foreach (var load in loadMs)
        {
            Add(Complete, 1);
            jobs.ReplaceTimings(jobs.MaxJobId(), JobTimingsJson.Serialize(new JobTimings
            {
                Phases = [new JobPhaseTiming("restore", 10, 0, null), new JobPhaseTiming("load", load, 0, null)]
            }));
        }
        Add(Complete, 1);
        jobs.ReplaceTimings(jobs.MaxJobId(), "{not json");

        var snapshot = Collect(DateTimeOffset.UtcNow);

        CollectionAssert.AreEqual(new[] { "restore", "load" }, snapshot.PhaseLatency.Keys.ToArray());
        Assert.AreEqual(4, snapshot.PhaseLatency["load"].Count);
        Assert.AreEqual(200, snapshot.PhaseLatency["load"].P50Ms);
        Assert.AreEqual(400, snapshot.PhaseLatency["load"].MaxMs);

        var prometheus = PrometheusExposition.Render(snapshot);
        StringAssert.Contains(prometheus, "sextant_job_phase_p50_ms{phase=\"load\"} 200");
        StringAssert.Contains(prometheus, "sextant_job_phase_p95_ms{phase=\"restore\"} 10");
    }

    [TestMethod]
    public void PhaseLatency_IsEmpty_WhenNoJobRecordedTimings()
    {
        Add(Complete, 1);
        var snapshot = Collect(DateTimeOffset.UtcNow);
        Assert.AreEqual(0, snapshot.PhaseLatency.Count);
        Assert.IsFalse(PrometheusExposition.Render(snapshot).Contains("sextant_job_phase_", StringComparison.Ordinal));
    }

    private MetricsSnapshot Collect(DateTimeOffset now, AlertThresholds? thresholds = null)
    {
        var paths = new ServicePaths(ServiceVolumes.Rooted(Path.Combine(_root, "volumes")));
        return new MetricsCollector(
            _db.GetConnection(), paths, _dbPath, new ServiceMetrics(), hasWorkerCapacity: true,
            timeProvider: new ManualClock(now)).Collect(thresholds);
    }

    private void Add(string status, long? completedAt)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshot_jobs(
                identity_hash, repository_url, commit_sha, status, created_at, updated_at, completed_at)
            VALUES (@identity, 'https://github.com/test/repo', @commit, @status, 0, 0, @completed);
            """;
        cmd.Parameters.AddWithValue("@identity", $"metrics-test-{++_nextId}");
        cmd.Parameters.AddWithValue("@commit", $"commit-{_nextId}");
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@completed", (object?)completedAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
