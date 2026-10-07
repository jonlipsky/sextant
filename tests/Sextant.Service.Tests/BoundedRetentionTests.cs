using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

[TestClass]
public class BoundedRetentionTests
{
    private string _dbPath = null!;
    private string _dataRoot = null!;
    private IndexDatabase _db = null!;
    private SnapshotService? _service;

    [TestInitialize]
    public void Init()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }

    private SnapshotService Start(FakeSnapshotWorker worker)
    {
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath, _dataRoot,
            retention: new RetentionPolicy { KeepCompleteGenerations = 0 }), worker, _db);
        return _service;
    }

    private static async Task WaitForWorkerAsync(FakeSnapshotWorker worker)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (worker.Calls == 0 && DateTime.UtcNow < until)
            await Task.Delay(10);
        Assert.AreEqual(1, worker.Calls, "the worker owns the writer gate before retention is submitted");
    }

    [TestMethod]
    public async Task Plan_WhileIndexOwnsWriterGate_IsReadOnlyAndDoesNotWait()
    {
        var worker = new FakeSnapshotWorker(_db) { UseGate = true };
        var service = Start(worker);
        var ensure = service.EnsureSnapshotAsync(ServiceTestFixtures.Request());
        await WaitForWorkerAsync(worker);
        try
        {
            var admissions = service.WriteAdmissions;
            var wal = _db.WalBytes;
            var report = await service.RunRetentionAsync(false).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(report.DryRun);
            Assert.IsFalse(report.MoreRemaining);
            Assert.AreEqual(admissions, service.WriteAdmissions, "plan never enters the service writer queue");
            Assert.AreEqual(wal, _db.WalBytes, "plan never writes WAL pages");
            Assert.AreEqual(0, service.RecentAudit(action: AuditAction.Retention).Count, "plan writes no audit row");
        }
        finally
        {
            worker.Gate.TrySetResult();
            await ensure.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task Execute_WriterGateWaitCountsAgainstPassBudget()
    {
        var worker = new FakeSnapshotWorker(_db) { UseGate = true };
        var service = Start(worker);
        var ensure = service.EnsureSnapshotAsync(ServiceTestFixtures.Request());
        await WaitForWorkerAsync(worker);
        try
        {
            var report = await service.RunRetentionAsync(true)
                .WaitAsync(RetentionService.PassTimeLimit + TimeSpan.FromSeconds(5));
            Assert.IsTrue(report.MoreRemaining);
            Assert.AreEqual("time_budget", report.StopReason);
            Assert.AreEqual(0, report.SnapshotsDeleted);
            Assert.AreEqual(0, service.RecentAudit(action: AuditAction.Retention).Count, "no batch got the writer");
        }
        finally
        {
            worker.Gate.TrySetResult();
            await ensure.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var completed = await service.RunRetentionAsync(true);
        Assert.IsFalse(completed.MoreRemaining, "timed-out waiter did not leak the semaphore");
    }

    [TestMethod]
    public async Task Execute_RequestCancellationWhileQueued_DoesNotLeaveBackgroundDeletes()
    {
        var worker = new FakeSnapshotWorker(_db) { UseGate = true };
        var service = Start(worker);
        var ensure = service.EnsureSnapshotAsync(ServiceTestFixtures.Request());
        await WaitForWorkerAsync(worker);
        try
        {
            using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await service.RunRetentionAsync(true, cancellationToken: caller.Token));
        }
        finally
        {
            worker.Gate.TrySetResult();
            await ensure.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(0, service.RecentAudit(action: AuditAction.Retention).Count);
    }

    [TestMethod]
    public async Task Execute_YieldsToQueuedEnsure_AndRechecksNewBranchProtection()
    {
        var first = ServiceTestFixtures.PublishComplete(_db, ServiceTestFixtures.Request(commit: "old-A"));
        var request = ServiceTestFixtures.Request(commit: "old-B", branch: "release");
        var second = ServiceTestFixtures.PublishComplete(_db, request);
        ServiceTestFixtures.PublishComplete(_db, ServiceTestFixtures.Request(commit: "current"));
        var worker = new FakeSnapshotWorker(_db);
        var service = Start(worker);
        var conn = _db.GetConnection();
        Task<EnsureSnapshotResult>? ensure = null;
        conn.CreateFunction("queue_ensure", () =>
        {
            ensure ??= service.EnsureSnapshotAsync(request);
            return 1;
        });
        using (var trigger = conn.CreateCommand())
        {
            trigger.CommandText = $"""
                CREATE TEMP TRIGGER retention_interleave BEFORE DELETE ON projects
                WHEN OLD.snapshot_id = {first}
                BEGIN SELECT queue_ensure(); END;
                """;
            trigger.ExecuteNonQuery();
        }

        var report = await service.RunRetentionAsync(true).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsNotNull(ensure, "ensure was queued during the first retention batch");
        await ensure.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(report.MoreRemaining);
        Assert.AreEqual(1, report.SnapshotsDeleted, "the second snapshot was protected between batches");
        Assert.IsNull(new SnapshotStore(conn).GetById(first));
        Assert.IsNotNull(new SnapshotStore(conn).GetById(second));
        Assert.AreEqual(1, new SnapshotStore(conn).GetSnapshotProjectIds(second).Count);
        Assert.AreEqual(0, worker.Calls, "ensure reselected the immutable snapshot rather than rebuilding GC'd data");
    }
}
