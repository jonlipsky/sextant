using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>Issue #344: a renewal never waits for the write lock longer than a fraction of the TTL.</summary>
[TestClass]
public class WriterLeaseRenewalBusyTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_lease_busy_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    private SqliteConnection OpenLocker()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > end) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    [TestMethod]
    public void RenewalWait_IsASixthOfTheTtl_WithAOneSecondFloor()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), WriterLease.RenewalWaitFor(30_000));
        Assert.AreEqual(TimeSpan.FromSeconds(2), WriterLease.RenewalWaitFor(12_000));
        Assert.AreEqual(TimeSpan.FromSeconds(1), WriterLease.RenewalWaitFor(900));
    }

    [TestMethod]
    public void BusyRenewal_FailsWithinTheBound_NeverMarksTheLeaseLost_AndRecoversWhenTheLockFrees()
    {
        using var lease = WriterLease.TryAcquire(_dbPath, "holder", TimeSpan.FromSeconds(3), autoHeartbeat: false)!;
        var warnings = new List<string>();
        lease.Warn = warnings.Add;
        using var locker = OpenLocker();
        Exec(locker, "BEGIN IMMEDIATE;");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        lease.SafeRenew();
        watch.Stop();

        // Without the bound this waits Microsoft.Data.Sqlite's 30 s default command timeout.
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.IsFalse(lease.IsLost, "a busy renewal is not a lost lease");
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "renewal failed");

        Exec(locker, "COMMIT;");
        Assert.IsTrue(lease.Renew());
        Assert.IsFalse(lease.IsLost);
    }

    [TestMethod]
    public void Heartbeat_SurvivesABusyRenewal_AndKeepsRenewing()
    {
        using var lease = WriterLease.TryAcquire(_dbPath, "holder", TimeSpan.FromSeconds(3))!;
        var warnings = new List<string>();
        lease.Warn = m => { lock (warnings) warnings.Add(m); };
        using var locker = OpenLocker();
        Exec(locker, "BEGIN IMMEDIATE;");

        Assert.IsTrue(WaitFor(() => { lock (warnings) return warnings.Count > 0; }, 20_000), "a renewal failed busy and was reported");
        var before = WriterLease.GetCurrent(locker)!.HeartbeatAt;
        Exec(locker, "COMMIT;");

        Assert.IsTrue(WaitFor(() => WriterLease.GetCurrent(locker)!.HeartbeatAt > before, 20_000),
            "the heartbeat kept running and renewed once the lock was free");
        Assert.IsFalse(lease.IsLost);
    }
}
