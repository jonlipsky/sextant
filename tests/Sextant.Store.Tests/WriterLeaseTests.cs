using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #38: the single-node, cross-process SINGLE-WRITER lease. Retention/publish/GC must not race a
/// live daemon/service writer. A live lease blocks a second writer (fail closed); a released lease frees
/// it; an expired lease may be stolen so a crashed holder never wedges the database forever.
/// </summary>
[TestClass]
public class WriterLeaseTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_lease_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void SecondAcquire_WhileHeld_FailsClosed()
    {
        using var first = WriterLease.TryAcquire(_dbPath, "holder-1", autoHeartbeat: false);
        Assert.IsNotNull(first, "the first writer acquires the lease");

        var second = WriterLease.TryAcquire(_dbPath, "holder-2", autoHeartbeat: false);
        Assert.IsNull(second, "a second writer must be refused while the lease is live (fail closed)");
    }

    [TestMethod]
    public void AcquireOrThrow_WhileHeld_ThrowsFailClosed()
    {
        // #59: the daemon and one-shot CLI index adopt the same fail-closed guard as the service. When a
        // live writer already owns the database, AcquireOrThrow must refuse the second writer LOUDLY rather
        // than return a racing lease, so two writers can never corrupt a publish (criterion 3).
        using var first = WriterLease.TryAcquire(_dbPath, "holder-1", autoHeartbeat: false);
        Assert.IsNotNull(first);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => WriterLease.AcquireOrThrow(_dbPath, "holder-2"));
        StringAssert.Contains(ex.Message, "single-writer lease");
    }

    [TestMethod]
    public void AcquireOrThrow_WhenFree_Succeeds()
    {
        using var lease = WriterLease.AcquireOrThrow(_dbPath, "sole-writer");
        Assert.IsNotNull(lease, "the only writer acquires the lease");
        Assert.IsFalse(lease.IsLost);
    }

    [TestMethod]
    public void Release_FreesTheLease_ForTheNextWriter()
    {
        var first = WriterLease.TryAcquire(_dbPath, "holder-1", autoHeartbeat: false);
        Assert.IsNotNull(first);
        first!.Release();

        using var second = WriterLease.TryAcquire(_dbPath, "holder-2", autoHeartbeat: false);
        Assert.IsNotNull(second, "once the lease is released a new writer may acquire it");

        first.Dispose();
    }

    [TestMethod]
    public void ExpiredLease_IsStealable_SoACrashedHolderNeverWedges()
    {
        // A holder that never renews (simulating a crash) with a tiny TTL leaves a STALE lease.
        var crashed = WriterLease.TryAcquire(_dbPath, "crashed", TimeSpan.FromMilliseconds(1), autoHeartbeat: false);
        Assert.IsNotNull(crashed);
        Thread.Sleep(50);

        using var next = WriterLease.TryAcquire(_dbPath, "recovered", autoHeartbeat: false);
        Assert.IsNotNull(next, "an expired lease may be stolen so a crashed writer never wedges the database");

        crashed!.Dispose();
    }

    [TestMethod]
    public void GetCurrent_ReportsTheHolder()
    {
        using var lease = WriterLease.TryAcquire(_dbPath, "reporting-holder", autoHeartbeat: false);
        Assert.IsNotNull(lease);

        var current = WriterLease.GetCurrent(_db.GetConnection());
        Assert.IsNotNull(current);
        Assert.AreEqual("reporting-holder", current!.Holder);
        Assert.AreEqual(lease!.OwnerToken, current.OwnerToken);
    }

    [TestMethod]
    public void Release_RemovesTheLeaseRow()
    {
        var lease = WriterLease.TryAcquire(_dbPath, "holder", autoHeartbeat: false);
        Assert.IsNotNull(lease);
        lease!.Release();

        Assert.IsNull(WriterLease.GetCurrent(_db.GetConnection()), "a released lease leaves no holder row");
        lease.Dispose();
    }

    [TestMethod]
    public void Renew_AfterLeaseStolen_ReturnsFalse_AndFlagsLost()
    {
        // A holder with a tiny TTL that stops renewing (a stalled heartbeat / slept host): once its lease
        // expires and another writer steals it, the holder's next renew must fail AND flag the lease lost
        // so the owning service stops writing instead of racing the new owner (issue #38).
        var holder = WriterLease.TryAcquire(_dbPath, "holder", TimeSpan.FromMilliseconds(1), autoHeartbeat: false);
        Assert.IsNotNull(holder);
        Assert.IsFalse(holder!.IsLost, "a freshly-acquired lease is not lost");

        Thread.Sleep(50);
        using var thief = WriterLease.TryAcquire(_dbPath, "thief", autoHeartbeat: false);
        Assert.IsNotNull(thief, "the expired lease is stealable");

        Assert.IsFalse(holder.Renew(), "the original holder can no longer renew a stolen lease");
        Assert.IsTrue(holder.IsLost, "a stolen lease is flagged lost so the owner stops writing");

        holder.Dispose();
    }

    [TestMethod]
    public void Renew_WhileStillOwned_DoesNotFlagLost()
    {
        using var lease = WriterLease.TryAcquire(_dbPath, "holder", TimeSpan.FromSeconds(30), autoHeartbeat: false);
        Assert.IsNotNull(lease);

        Assert.IsTrue(lease!.Renew(), "the owner renews its own live lease");
        Assert.IsFalse(lease.IsLost, "renewing a still-owned lease never flags it lost");
    }

    [TestMethod]
    public void Renew_ExtendsExpiry_WhileOwned()
    {
        using var lease = WriterLease.TryAcquire(_dbPath, "holder", TimeSpan.FromSeconds(30), autoHeartbeat: false);
        Assert.IsNotNull(lease);
        var before = WriterLease.GetCurrent(_db.GetConnection())!.ExpiresAt;

        Thread.Sleep(5);
        Assert.IsTrue(lease!.Renew(), "the owner can renew its own lease");

        var after = WriterLease.GetCurrent(_db.GetConnection())!.ExpiresAt;
        Assert.IsTrue(after >= before, "renew never moves the expiry backwards");
    }
}
