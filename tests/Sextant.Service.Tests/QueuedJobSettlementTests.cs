using Sextant.Core;
using Sextant.Service.Observability;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #327: <c>queued</c> means "admitted by this service run". A queued row an earlier run left behind has no live
/// turn, so startup settles it (coalesced when a newer job of its branch already ran, cancelled otherwise) instead of
/// leaving it to block idle-branch retirement and repository reclaim and to inflate <c>sextant_jobs_queued</c>.
/// </summary>
[TestClass]
public class QueuedJobSettlementTests
{
    private const string Repo = "https://github.com/acme/settle";

    private string _dbPath = null!;
    private IndexDatabase? _db;
    private SnapshotService? _service;

    [TestInitialize]
    public void Init() => _dbPath = ServiceTestFixtures.NewDbPath();

    [TestCleanup]
    public void Cleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    private static MaintenancePolicy NoLoop() => new() { Interval = TimeSpan.Zero };

    // The earlier service run: seeds the catalog, then goes away.
    private void EarlierRun(Action<IndexDatabase> seed)
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        seed(db);
    }

    private FakeSnapshotWorker StartService(MaintenancePolicy? maintenance = null)
    {
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var worker = new FakeSnapshotWorker(_db);
        var options = ServiceTestFixtures.NewOptions(_dbPath);
        if (maintenance is not null)
            options = options with { Maintenance = maintenance };
        _service = SnapshotService.Start(options, worker, _db);
        return worker;
    }

    private static long Job(IndexDatabase db, string identity, string repoUrl, string commit, string? branch) =>
        new SnapshotJobStore(db.GetConnection()).EnsureJob(identity, repoUrl, commit, branch).job.Id;

    [TestMethod]
    public void Startup_SettlesSupersededAsCoalesced_OthersAsCancelled_AndLeavesLaterRowsAlone()
    {
        long superseded = 0, newerBranch = 0, noBranch = 0, ran = 0;
        EarlierRun(db =>
        {
            superseded = Job(db, "id-old", Repo + ".git", "c1", "main");
            newerBranch = Job(db, "id-feature", Repo, "c1", "feature");
            noBranch = Job(db, "id-nobranch", Repo, "c0", null);
            ran = Job(db, "id-new", Repo, "c2", "main");
            new SnapshotJobStore(db.GetConnection()).MarkResult(ran, SnapshotJobStatus.Failed, null, "boom");
        });

        StartService();
        var service = _service!;

        var coalesced = service.GetStatus(superseded)!.Job;
        Assert.AreEqual(SnapshotJobStatus.Coalesced, coalesced.Status, "a newer job of the branch already ran");
        StringAssert.Contains(coalesced.LastError, "superseded");
        foreach (var id in new[] { newerBranch, noBranch })
        {
            var job = service.GetStatus(id)!.Job;
            Assert.AreEqual(SnapshotJobStatus.Cancelled, job.Status);
            StringAssert.Contains(job.LastError, "service run ended before its turn");
        }
        Assert.AreEqual(SnapshotJobStatus.Failed, service.GetStatus(ran)!.Job.Status, "settled rows are untouched");

        var metrics = service.CollectMetrics();
        Assert.AreEqual(0, metrics.Jobs.Queued);
        Assert.AreEqual(1, metrics.Maintenance.StartupJobsSettledCoalesced);
        Assert.AreEqual(2, metrics.Maintenance.StartupJobsSettledCancelled);
        StringAssert.Contains(PrometheusExposition.Render(metrics),
            "sextant_startup_jobs_settled_total{kind=\"cancelled\"} 2");
        Assert.IsTrue(service.RecentAudit(action: AuditAction.Reconcile)
            .Any(a => a.Detail == "settled_queued_coalesced_1_cancelled_2"));

        // A row admitted by THIS run is queued with a live turn and is not settled by anything.
        var live = Job(_db!, "id-live", Repo, "c9", "main");
        Assert.AreEqual(SnapshotJobStatus.Queued, service.GetStatus(live)!.Job.Status);
        Assert.AreEqual(1, service.CollectMetrics().Jobs.Queued);
    }

    [TestMethod]
    public void Startup_SettlesMoreRowsThanOneBatch()
    {
        EarlierRun(db =>
        {
            for (var i = 0; i < 1100; i++)
                Job(db, $"id-{i}", Repo, $"c{i}", null);
        });

        StartService();

        Assert.AreEqual(0, _service!.CollectMetrics().Jobs.Queued);
        Assert.AreEqual(1100, _service.CollectMetrics().Maintenance.StartupJobsSettledCancelled);
        Assert.AreEqual(SnapshotJobStatus.Cancelled, _service.GetStatusByIdentity("id-1099")!.Job.Status);
    }

    [TestMethod]
    public void SettledRows_DoNotChangeTheRatesOrTheLatencySamples()
    {
        long ran = 0;
        EarlierRun(db =>
        {
            var jobs = new SnapshotJobStore(db.GetConnection());
            Job(db, "id-orphan-coalesced", Repo, "c1", "main");
            ran = Job(db, "id-ran", Repo + ".git", "c2", "main");
            Job(db, "id-orphan-cancelled", Repo, "c3", null);
            jobs.MarkRunning(ran, "token");
            jobs.MarkResult(ran, SnapshotJobStatus.Failed, null, "boom");
            using var cmd = db.GetConnection().CreateCommand();
            cmd.CommandText = "UPDATE snapshot_jobs SET started_at = created_at + 5 WHERE identity_hash = 'id-orphan-cancelled';";
            cmd.ExecuteNonQuery();
        });

        StartService();
        var metrics = _service!.CollectMetrics();

        Assert.AreEqual(1, metrics.Maintenance.StartupJobsSettledCoalesced);
        Assert.AreEqual(1, metrics.Maintenance.StartupJobsSettledCancelled);
        Assert.AreEqual(1, metrics.Jobs.Terminal, "only the job that ran is an outcome");
        Assert.AreEqual(0, metrics.Jobs.Cancelled);
        Assert.AreEqual(0, metrics.Jobs.Coalesced);
        Assert.AreEqual(0.0, metrics.Jobs.SuccessRate);
        Assert.AreEqual(1, metrics.RecentJobs.SampleCount);
        Assert.AreEqual(0, metrics.RecentJobs.Cancelled);
        Assert.AreEqual(1, metrics.RecentJobs.Failed);
        Assert.AreEqual(1, metrics.QueueDelay.Count, "a settled row with a stale started_at is not a sample");
        Assert.AreEqual(1, metrics.IndexingLatency.Count);
    }

    [TestMethod]
    public void SettlementKeepsEveryTimestamp_AndCompletedAtStaysNull()
    {
        long id = 0;
        EarlierRun(db =>
        {
            id = Job(db, "id-ts", Repo, "c1", null);
            using var cmd = db.GetConnection().CreateCommand();
            cmd.CommandText = "UPDATE snapshot_jobs SET created_at = 1000, updated_at = 2000;";
            cmd.ExecuteNonQuery();
        });

        StartService();

        using var read = _db!.GetConnection().CreateCommand();
        read.CommandText = "SELECT status, created_at, updated_at, completed_at FROM snapshot_jobs WHERE id = @id;";
        read.Parameters.AddWithValue("@id", id);
        using var r = read.ExecuteReader();
        Assert.IsTrue(r.Read());
        Assert.AreEqual(SnapshotJobStatus.Cancelled, r.GetString(0));
        Assert.AreEqual(1000L, r.GetInt64(1));
        Assert.AreEqual(2000L, r.GetInt64(2));
        Assert.IsTrue(r.IsDBNull(3));
    }

    [TestMethod]
    public void BothSpellingsOfARepository_SettleCorrectlyAcrossBatches()
    {
        long first = 0, last = 0, after = 0;
        EarlierRun(db =>
        {
            for (var i = 0; i < 600; i++)
            {
                var id = Job(db, $"id-old-{i}", i % 2 == 0 ? Repo + ".git" : Repo, $"c{i}", "main");
                if (i == 0) first = id;
                last = id;
            }
            var ran = Job(db, "id-ran", Repo, "c-ran", "main");
            new SnapshotJobStore(db.GetConnection()).MarkResult(ran, SnapshotJobStatus.Failed, null, "x");
            after = Job(db, "id-after", Repo + ".git", "c-after", "main");
        });

        StartService();
        var service = _service!;

        Assert.AreEqual(600, service.CollectMetrics().Maintenance.StartupJobsSettledCoalesced);
        Assert.AreEqual(1, service.CollectMetrics().Maintenance.StartupJobsSettledCancelled);
        Assert.AreEqual(SnapshotJobStatus.Coalesced, service.GetStatus(first)!.Job.Status);
        Assert.AreEqual(SnapshotJobStatus.Coalesced, service.GetStatus(last)!.Job.Status);
        Assert.AreEqual(SnapshotJobStatus.Cancelled, service.GetStatus(after)!.Job.Status,
            "a row admitted after the job that ran is not superseded by it");
    }

    [TestMethod]
    public async Task ACancelledIdentity_IsProducedAgainByALaterEnsure()
    {
        var request = ServiceTestFixtures.Request();
        long jobId = 0;
        EarlierRun(db => jobId = Job(db, request.ToIdentity().Hash, request.RepositoryRemoteUrl, request.CommitSha, null));

        var worker = StartService();
        Assert.AreEqual(SnapshotJobStatus.Cancelled, _service!.GetStatus(jobId)!.Job.Status);
        Assert.AreEqual(0, worker.Calls);

        var result = await _service.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(jobId, result.JobId, "the same durable job is reused");
        Assert.AreEqual(1, worker.Calls, "the identity was produced, not served from its cancelled row");
    }

    [TestMethod]
    public async Task ACoalescedIdentity_IsProducedAgainByALaterEnsure()
    {
        var request = ServiceTestFixtures.Request(commit: "c1", branch: "main");
        EarlierRun(db =>
        {
            Job(db, request.ToIdentity().Hash, request.RepositoryRemoteUrl, "c1", "main");
            var newer = Job(db, "id-newer", request.RepositoryRemoteUrl, "c2", "main");
            new SnapshotJobStore(db.GetConnection()).MarkResult(newer, SnapshotJobStatus.Failed, null, "x");
        });

        var worker = StartService();
        Assert.AreEqual(SnapshotJobStatus.Coalesced, _service!.GetStatusByIdentity(request.ToIdentity().Hash)!.Job.Status);

        var result = await _service.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(1, worker.Calls);
    }

    [TestMethod]
    public async Task ABranchWithOnlySettledRows_IsRetirable()
    {
        var movedAt = DateTimeOffset.UtcNow.AddDays(-20).ToUnixTimeMilliseconds();
        EarlierRun(db =>
        {
            var store = new SnapshotStore(db.GetConnection());
            var repoId = store.EnsureRepository(Repo, movedAt);
            foreach (var (branch, isDefault) in new[] { ("main", true), ("feature", false) })
            {
                var snapshot = ServiceTestFixtures.PublishComplete(
                    db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = $"commit-{branch}" }, recordCommit: true);
                store.SetBranchPointer(store.EnsureBranch(repoId, branch, isDefault, movedAt), snapshot, movedAt);
            }
            Job(db, "id-stale", Repo + ".git", "commit-stale", "feature");
        });
        using (var probe = new IndexDatabase(_dbPath))
            Assert.IsTrue(new SnapshotJobStore(probe.GetConnection()).BranchesWithPendingWork()
                .Contains((RemoteUrlIdentity.Normalize(Repo), "feature")), "before startup the stale row blocks it");

        StartService(NoLoop());

        var report = await _service!.RetireIdleBranchesAsync();

        Assert.AreEqual(1, report.Idle);
        Assert.AreEqual(0, report.Busy);
        Assert.AreEqual(1, report.Retired);
    }

    [TestMethod]
    public void ARepositoryWithOnlySettledRows_HasNoPendingWork()
    {
        EarlierRun(db =>
        {
            Job(db, "id-stale", Repo, "c1", null);
            new SnapshotStore(db.GetConnection()).EnsureRepository(Repo, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.IsTrue(new RepositoryActivityStore(db.GetConnection()).List().Single().HasPendingWork,
                "before startup the stale row counts as pending work");
        });

        StartService();

        Assert.IsFalse(new RepositoryActivityStore(_db!.GetConnection()).List().Single().HasPendingWork);
    }
}
