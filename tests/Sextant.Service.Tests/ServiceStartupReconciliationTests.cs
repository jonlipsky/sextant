using Sextant.Core;
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

    [TestMethod]
    public void RegisterPullRequestSnapshot_CommitOnly_ResolvesAndProtectsThroughRetention()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var repoUrl = "https://github.com/org/app";
        var repo = snapshots.EnsureRepository(repoUrl, now);

        // PR-head snapshot on an OLDER deletable generation; a NEWER servable generation supersedes it, so
        // ONLY the open-PR root can spare it. The head commit is real (commit_id set) so the service can
        // resolve it from the head SHA alone — the documented commit-only call shape.
        var prSnap = CompleteSnapshotAtCommit(snapshots, conn, repo, repoUrl, "pr-head", now + 1);
        CompleteSnapshotAtCommit(snapshots, conn, repo, repoUrl, "main-head", now + 2);

        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), new FakeSnapshotWorker(_db), _db);

        // Register with ONLY the head commit (snapshotId defaulted null): the service must resolve it to
        // prSnap, not persist an unprotected NULL-snapshot root.
        Assert.IsTrue(_service.RegisterPullRequestSnapshot(repoUrl, 42, "pr-head"),
            "commit-only registration resolves the head to its complete snapshot");

        _service.RunRetention(execute: true);

        Assert.IsNotNull(snapshots.GetById(prSnap),
            "the open-PR head snapshot survives retention when registered by commit alone (criterion 4)");
    }

    [TestMethod]
    public void RegisterPullRequestSnapshot_CommitOnly_FailsClosed_WhenNoSnapshotResolves()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var repoUrl = "https://github.com/org/app";
        snapshots.EnsureRepository(repoUrl, now);

        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), new FakeSnapshotWorker(_db), _db);

        // No snapshot exists for this head commit: the service must FAIL CLOSED (a NULL-snapshot root
        // protects nothing, so it is never persisted) rather than silently create an inert root.
        Assert.IsFalse(_service.RegisterPullRequestSnapshot(repoUrl, 7, "never-indexed"),
            "commit-only registration fails closed when no complete snapshot resolves for the head (criterion 4)");
        Assert.AreEqual(0, new PullRequestSnapshotStore(conn).GetOpen().Count,
            "no open-PR root is persisted when resolution fails");
    }

    private static long CompleteSnapshotAtCommit(
        SnapshotStore snapshots, Microsoft.Data.Sqlite.SqliteConnection conn,
        long repo, string repoUrl, string commit, long now)
    {
        var runStore = new IndexRunStore(conn);
        var runId = runStore.BeginRun("full", now, IndexProfileDescriptor.Full.ConfigurationHash,
            IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, now, 1);
        var commitId = snapshots.EnsureCommit(repo, commit, null, now);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = repoUrl,
            CommitSha = commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = snapshots.BeginPending(identity, repo, commitId, runId, now);
        snapshots.MarkComplete(id, now);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE snapshots SET created_at = @c WHERE id = @id;";
        cmd.Parameters.AddWithValue("@c", now);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        return id;
    }
}
