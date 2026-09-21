using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 slice 2, acceptance criterion 3 (worker/service loss at EVERY stage reconciles to
/// retryable/failed/complete WITHOUT corrupt publication): the service's <c>ReconcileOnStartup</c> sweep.
/// Besides the orphaned-<c>running</c> reconcile already covered in <see cref="SnapshotServiceTests"/>,
/// startup must also (a) downgrade a PHANTOM terminal job — one recorded <c>complete</c>/<c>partial</c>
/// whose <c>snapshot_id</c> is now NULL (a crash between the status write and the publish commit, or the
/// snapshot later reclaimed) — back to <c>queued</c> so a later ensure regenerates it, never reports a
/// phantom-complete; and (b) sweep orphaned per-job scratch a crashed worker left behind, confined to the
/// scratch root so a published volume can never be touched.
/// </summary>
[TestClass]
public class ServiceStartupReconciliationTests
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
    public void Restart_PhantomCompleteJob_WithNullSnapshot_IsReconciledToQueued()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        var request = ServiceTestFixtures.Request();
        long jobId;

        // First "process": a job recorded COMPLETE but whose published snapshot id is NULL — the exact
        // shape a crash-between-status-and-publish (or a later retention reclaim via ON DELETE SET NULL)
        // leaves. A naive attach would report this phantom-complete forever.
        using (var db = new IndexDatabase(_dbPath))
        {
            db.RunMigrations();
            var jobs = new SnapshotJobStore(db.GetConnection());
            var identity = request.ToIdentity();
            var (job, _) = jobs.EnsureJob(identity.Hash, request.RepositoryRemoteUrl, request.CommitSha, null);
            jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, snapshotId: null);
            jobId = job.Id;
        }

        // Second "process": the service restarts and reconciles.
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), new FakeSnapshotWorker(_db), _db);

        var status = _service.GetStatus(jobId)!;
        Assert.AreEqual(SnapshotJobStatus.Queued, status.Job.Status,
            "a phantom-complete job with a NULL snapshot is reconciled to queued on restart (criterion 3)");
    }

    [TestMethod]
    public async Task Restart_PhantomComplete_ThenEnsure_RegeneratesRealSnapshot()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        var request = ServiceTestFixtures.Request();

        using (var db = new IndexDatabase(_dbPath))
        {
            db.RunMigrations();
            var jobs = new SnapshotJobStore(db.GetConnection());
            var identity = request.ToIdentity();
            var (job, _) = jobs.EnsureJob(identity.Hash, request.RepositoryRemoteUrl, request.CommitSha, null);
            jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, snapshotId: null);
        }

        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var worker = new FakeSnapshotWorker(_db);
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);

        var result = await _service.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.IsNotNull(result.SnapshotId, "the regenerated job publishes a real snapshot, not a phantom-complete");
        Assert.AreEqual(1, worker.Calls, "the reconciled-to-queued job is re-run exactly once by the next ensure");
    }

    [TestMethod]
    public void Restart_SweepsOrphanedScratch_ButNeverTouchesPersistentVolumes()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        var dataRoot = ServiceTestFixtures.NewDataRoot();
        var options = ServiceTestFixtures.NewOptions(_dbPath, dataRoot);

        // Seed a leftover scratch directory (a crashed worker's abandoned per-job scratch) plus a marker
        // file on the persistent artifact volume that the sweep must NOT touch.
        var paths = new ServicePaths(options.Volumes);
        var orphan = paths.AllocateScratch("crashed-job");
        File.WriteAllText(Path.Combine(orphan, "staged.tmp"), "partial");
        var artifactMarker = Path.Combine(paths.ArtifactRoot, "published.bin");
        File.WriteAllText(artifactMarker, "durable");

        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _service = SnapshotService.Start(options, new FakeSnapshotWorker(_db), _db);

        Assert.IsFalse(Directory.Exists(orphan), "startup sweeps the crashed worker's orphaned scratch (criterion 3)");
        Assert.IsTrue(File.Exists(artifactMarker), "the sweep is confined to scratch and never deletes published data");
    }
}
