using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>Issue #344: Release() ends a batch without re-beginning; the next write re-opens it lazily.</summary>
[TestClass]
public class WriteSessionReleaseTests
{
    private string _path = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _path = Path.Combine(Path.GetTempPath(), $"sextant_release_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_path);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup() => SqliteTestDatabase.Delete(_path, _db);

    private bool OtherConnectionCanTakeTheLock()
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 0;";
            pragma.ExecuteNonQuery();
        }
        try
        {
            using var begin = conn.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            using var rollback = conn.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    [TestMethod]
    public void Release_LeavesNoOpenTransaction_AndTheNextBeginReopensIt()
    {
        using var session = _db.BeginWriteSession();
        session.Begin();
        session.RowsWritten();
        Assert.IsFalse(OtherConnectionCanTakeTheLock(), "an open session holds the write lock");

        session.Release();
        Assert.IsFalse(session.IsOpen);
        Assert.IsTrue(OtherConnectionCanTakeTheLock(), "a released session holds no lock");
        Assert.AreEqual(1, session.CommittedBatches);

        session.Release();
        Assert.AreEqual(1, session.CommittedBatches, "Release is idempotent");

        session.Begin();
        Assert.IsTrue(session.IsOpen);
        session.Begin();
        Assert.IsFalse(OtherConnectionCanTakeTheLock());
        session.Complete();
        Assert.IsTrue(OtherConnectionCanTakeTheLock());
    }

    [TestMethod]
    public void RowsWritten_WithoutAnOpenTransaction_Throws()
    {
        using var session = _db.BeginWriteSession();
        Assert.ThrowsExactly<InvalidOperationException>(() => session.RowsWritten());
        session.Begin();
        session.Release();
        Assert.ThrowsExactly<InvalidOperationException>(() => session.RowsWritten());
        session.Begin();
        session.RowsWritten();
        session.Complete();
    }

    [TestMethod]
    public void TwoWaiters_BothTakeTheLockBeforeTheSessionBeginsAgain()
    {
        var tracker = new WriteLockTracker();
        _db.WriteLockTracker = tracker;
        var order = new List<string>();
        using var session = _db.BeginWriteSession(new IndexWriteOptions { YieldToWaitersCap = TimeSpan.FromSeconds(20) });
        session.Begin();
        session.RowsWritten();

        Task Waiter(string name) => Task.Run(() =>
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 20000;";
                pragma.ExecuteNonQuery();
            }
            var started = tracker.BeginAcquire();
            using var begin = conn.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.CommandTimeout = 20;
            begin.ExecuteNonQuery();
            var hold = tracker.Acquired(name, null, started);
            lock (order) order.Add(name);
            Thread.Sleep(100);
            using var commit = conn.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
            tracker.Released(hold);
        });

        var a = Waiter("waiter-1");
        var b = Waiter("waiter-2");
        var end = Environment.TickCount64 + 10_000;
        while (tracker.WaitingWriters < 2 && Environment.TickCount64 < end) Thread.Sleep(5);
        Assert.AreEqual(2, tracker.WaitingWriters);

        session.Release();
        session.Begin();
        lock (order) order.Add("session-began-again");

        Assert.IsTrue(Task.WaitAll([a, b], TimeSpan.FromSeconds(20)));
        Assert.AreEqual("session-began-again", order[^1]);
        Assert.AreEqual(3, order.Count);
        session.Complete();
    }

    [TestMethod]
    public void LibraryDefaults_KeepTheBatchCapAndYieldOff()
    {
        Assert.AreEqual(TimeSpan.Zero, IndexWriteOptions.Default.MaxBatchHold);
        Assert.AreEqual(TimeSpan.Zero, IndexWriteOptions.Default.YieldToWaitersCap);
    }
}
