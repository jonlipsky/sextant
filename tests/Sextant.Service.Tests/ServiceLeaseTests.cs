using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// The service is the single-writer lease owner (issue #38): retention/publish/GC must never race a live
/// writer. Starting a second service against the same catalog fails CLOSED while the first holds the
/// lease, and the lease is released when the owning service is disposed so a clean restart re-acquires it.
/// </summary>
[TestClass]
public class ServiceLeaseTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), new FakeSnapshotWorker(_db), _db);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public void SecondService_OnSameCatalog_FailsClosedWhileLeaseHeld()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath)),
            "a second writer must fail closed against a live single-writer lease (#38)");
        StringAssert.Contains(ex.Message, "single-writer lease");
    }

    [TestMethod]
    public void DirectLeaseAcquire_WhileServiceHoldsIt_ReturnsNull()
    {
        using var contender = WriterLease.TryAcquire(_dbPath, "rogue-writer", TimeSpan.FromSeconds(30));
        Assert.IsNull(contender, "no other writer can acquire the lease the service holds (#38)");
    }

    [TestMethod]
    public void Dispose_ReleasesLease_SoAFreshServiceCanStart()
    {
        _service.Dispose();
        _service = null!;

        using var restarted = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath));
        Assert.IsTrue(restarted.IsAvailable, "a fresh service re-acquires the released lease after a clean shutdown");
    }
}
