using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Control-plane behavior of the standalone index service: idempotent ensure attaching to ONE durable job
/// (criterion 1), restart reconciliation of orphaned jobs + preserved catalog (criterion 2), and
/// structured per-project diagnostics for partial/failed/unsupported outcomes (criterion 5).
/// </summary>
[TestClass]
public class SnapshotServiceTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task Ensure_SameIdentity_AttachesToOneJob_WorkerRunsOnce()
    {
        var worker = new FakeSnapshotWorker(NewDb());
        var service = StartWith(worker);
        var request = ServiceTestFixtures.Request();

        var first = await service.EnsureSnapshotAsync(request);
        var second = await service.EnsureSnapshotAsync(request);

        Assert.AreEqual(first.JobId, second.JobId, "repeated ensure attaches to the same durable job (criterion 1)");
        Assert.IsFalse(first.Attached, "the first ensure created the job");
        Assert.IsTrue(second.Attached, "the second ensure attached to the existing terminal job");
        Assert.AreEqual(SnapshotJobStatus.Complete, first.Status);
        Assert.AreEqual(1, worker.Calls, "the worker produced the snapshot exactly once");
    }

    [TestMethod]
    public async Task Ensure_ConcurrentIdenticalRequests_ProduceOnce()
    {
        var worker = new FakeSnapshotWorker(NewDb()) { UseGate = true };
        var service = StartWith(worker);
        var request = ServiceTestFixtures.Request();

        var a = service.EnsureSnapshotAsync(request);
        var b = service.EnsureSnapshotAsync(request);
        var c = service.EnsureSnapshotAsync(request);
        worker.Gate.SetResult();
        var results = await Task.WhenAll(a, b, c);

        var jobIds = results.Select(r => r.JobId).Distinct().ToArray();
        Assert.AreEqual(1, jobIds.Length, "all concurrent ensures attach to one job (criterion 1)");
        Assert.AreEqual(1, worker.Calls, "only one worker run despite the race");
        Assert.IsTrue(results.All(r => r.Status == SnapshotJobStatus.Complete));
    }

    [TestMethod]
    public async Task Ensure_AlreadyPublishedSnapshot_AttachesWithoutReindexing()
    {
        var db = NewDb();
        var request = ServiceTestFixtures.Request();
        // A complete snapshot already exists in the catalog (e.g. produced by an earlier process).
        ServiceTestFixtures.PublishComplete(db, request);

        var worker = new FakeSnapshotWorker(db, FakeSnapshotWorker.Throws("worker should not run"));
        var service = StartWith(worker);

        var result = await service.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.IsNotNull(result.SnapshotId);
        Assert.AreEqual(0, worker.Calls, "an already-published snapshot is attached without re-indexing");
    }

    [TestMethod]
    public void Restart_ReconcilesOrphanedRunningJob_AndPreservesCatalog()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        var request = ServiceTestFixtures.Request();
        long jobId;

        // First "process": publish a snapshot and leave a job stuck 'running' under a dead worker token.
        using (var db = new IndexDatabase(_dbPath))
        {
            db.RunMigrations();
            ServiceTestFixtures.PublishComplete(db, request);
            var jobs = new SnapshotJobStore(db.GetConnection());
            var identity = request.ToIdentity();
            var (job, _) = jobs.EnsureJob(identity.Hash, request.RepositoryRemoteUrl, request.CommitSha, null);
            jobs.MarkRunning(job.Id, "dead-worker-token");
            jobId = job.Id;
        }

        // Second "process": the service restarts on the same catalog.
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var worker = new FakeSnapshotWorker(_db);
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);

        var status = _service.GetStatus(jobId);
        Assert.IsNotNull(status, "the durable job survived restart (criterion 2)");
        Assert.AreEqual(SnapshotJobStatus.Queued, status!.Job.Status,
            "a job orphaned 'running' by a dead worker is reconciled to queued on restart (criterion 2)");

        // The published catalog data is intact and still resolvable after restart.
        var resolved = _service.GetStatusByIdentity(request.ToIdentity().Hash);
        Assert.IsNotNull(resolved, "the catalog job ledger is preserved across restart");
    }

    [TestMethod]
    public async Task Ensure_UnsupportedWorker_RecordsUnsupportedWithDiagnostics()
    {
        var worker = new FakeSnapshotWorker(NewDb(), FakeSnapshotWorker.Unsupported("target framework not installed"));
        var service = StartWith(worker);

        var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Unsupported, result.Status);
        var status = service.GetStatus(result.JobId)!;
        Assert.AreEqual(1, status.Diagnostics.Count, "unsupported jobs expose per-project diagnostics (criterion 5)");
        Assert.AreEqual("unsupported_target", status.Diagnostics[0].Code);
    }

    [TestMethod]
    public async Task Ensure_FailedWorker_RecordsFailedWithDiagnostics()
    {
        var worker = new FakeSnapshotWorker(NewDb(), FakeSnapshotWorker.FailedWithDiagnostics("project failed to load"));
        var service = StartWith(worker);

        var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        var status = service.GetStatus(result.JobId)!;
        Assert.AreEqual(JobDiagnosticSeverity.Error, status.Diagnostics.Single().Severity);
    }

    [TestMethod]
    public async Task Ensure_WorkerClaimsSuccessButPublishesNothing_DowngradedToFailed()
    {
        var worker = new FakeSnapshotWorker(NewDb(), FakeSnapshotWorker.ClaimsCompleteButPublishesNothing());
        var service = StartWith(worker);

        var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status,
            "a worker that reports success without a published complete snapshot is downgraded to failed");
        Assert.IsNull(result.SnapshotId);
    }

    [TestMethod]
    public async Task Ensure_WorkerThrows_RecordsFailedWithExceptionDiagnostic()
    {
        var worker = new FakeSnapshotWorker(NewDb(), FakeSnapshotWorker.Throws("boom"));
        var service = StartWith(worker);

        var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        var status = service.GetStatus(result.JobId)!;
        Assert.AreEqual("worker_exception", status.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Ensure_WhenPublishedSnapshotWasReclaimed_RegeneratesInsteadOfPhantomComplete()
    {
        var worker = new FakeSnapshotWorker(NewDb());
        var service = StartWith(worker);
        var request = ServiceTestFixtures.Request();

        var first = await service.EnsureSnapshotAsync(request);
        Assert.AreEqual(SnapshotJobStatus.Complete, first.Status);
        Assert.AreEqual(1, worker.Calls);

        // Simulate retention reclaiming the published snapshot's data (issue #46): retention deletes the
        // snapshot's project-version rows and then the snapshot row, leaving a 'complete' job whose data is
        // gone. A naive idempotent attach would report that phantom-complete forever.
        using (var cmd = _db.GetConnection().CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM projects WHERE snapshot_id = @id;
                DELETE FROM snapshots WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", first.SnapshotId!.Value);
            cmd.ExecuteNonQuery();
        }

        var second = await service.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Complete, second.Status);
        Assert.AreEqual(first.JobId, second.JobId, "the same durable job is reused (criterion 1)");
        Assert.AreEqual(2, worker.Calls,
            "a complete job whose snapshot was reclaimed is regenerated, not reported as a phantom-complete");
        Assert.IsNotNull(second.SnapshotId);
    }

    [TestMethod]
    public async Task Ensure_WorkerPublishesSnapshotForDifferentIdentity_DowngradedToFailed()
    {
        var worker = new FakeSnapshotWorker(NewDb(), FakeSnapshotWorker.PublishesMismatchedIdentity());
        var service = StartWith(worker);

        var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status,
            "a worker that publishes a complete snapshot for a DIFFERENT identity is not trusted as this identity's result");
        Assert.IsNull(result.SnapshotId);
    }

    [TestMethod]
    public async Task Ensure_CancelledMidRun_IsRequeuedForRetry_NotPermanentFailure()
    {
        var db = NewDb();
        var attempts = 0;
        var worker = new FakeSnapshotWorker(db, (self, request) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new OperationCanceledException();
            var snapId = ServiceTestFixtures.PublishComplete(self.Database, request);
            return SnapshotWorkResult.Complete(snapId);
        });
        var service = StartWith(worker);
        var request = ServiceTestFixtures.Request();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.EnsureSnapshotAsync(request));

        // A cancelled run is NOT a durable terminal failure: a later ensure re-attempts it and completes.
        var retry = await service.EnsureSnapshotAsync(request);
        Assert.AreEqual(SnapshotJobStatus.Complete, retry.Status,
            "a cancelled run is requeued so a later ensure re-attempts it rather than attaching to a permanent failure");
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public void ToIdentity_FallbackConfigHash_UsedOnlyWhenRequestOmitsConfigHash()
    {
        var withoutConfig = ServiceTestFixtures.Request() with { ConfigHash = null };
        var withConfig = ServiceTestFixtures.Request() with { ConfigHash = "explicit" };

        Assert.AreNotEqual(withoutConfig.ToIdentity().Hash, withoutConfig.ToIdentity("node-profile").Hash,
            "the node profile hash is folded in when the request omits ConfigHash (so the identity matches the worker's published hash)");
        Assert.AreEqual(withConfig.ToIdentity().Hash, withConfig.ToIdentity("node-profile").Hash,
            "an explicit request ConfigHash is authoritative and not overridden by the fallback");
    }

    [TestMethod]
    public async Task Ensure_NoWorkerConfigured_IsUnsupportedAndReportsNoCapacity()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        // No worker → the default UnavailableSnapshotWorker.
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker: null, database: _db);

        Assert.IsTrue(_service.IsAvailable, "a query-only node is still available");
        Assert.IsFalse(_service.HasWorkerCapacity, "a query-only node reports no worker capacity");

        var result = await _service.EnsureSnapshotAsync(ServiceTestFixtures.Request());
        Assert.AreEqual(SnapshotJobStatus.Unsupported, result.Status);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private IndexDatabase NewDb()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        return _db;
    }

    private SnapshotService StartWith(FakeSnapshotWorker worker)
    {
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        return _service;
    }
}
