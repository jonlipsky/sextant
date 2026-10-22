using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>Issue #344: time-capped batches, yielding to waiting writers, and the waiter count behind it.</summary>
[TestClass]
public class WriteLockYieldTests
{
    private sealed class TickClock : TimeProvider
    {
        private long _ticks;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalSeconds * TimestampFrequency));
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override long TimestampFrequency => 1_000_000;
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private string _path = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _path = Path.Combine(Path.GetTempPath(), $"sextant_yield_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_path);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup() => SqliteTestDatabase.Delete(_path, _db);

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > end) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    [TestMethod]
    public void Tracker_CountsWaitersBetweenBeginAcquireAndItsOutcome()
    {
        var tracker = new WriteLockTracker();
        var a = tracker.BeginAcquire();
        var b = tracker.BeginAcquire();
        Assert.AreEqual(2, tracker.WaitingWriters);
        tracker.AcquireFailed("a", null, a);
        Assert.AreEqual(1, tracker.WaitingWriters);
        var hold = tracker.Acquired("b", null, b);
        Assert.AreEqual(0, tracker.WaitingWriters);
        tracker.Released(hold);
        Assert.AreEqual(0, tracker.Snapshot().WaitingWriters);
    }

    [TestMethod]
    public void Tracker_TimestampAndTheUntrackedInstance_CountNoWaiters()
    {
        var tracker = new WriteLockTracker();
        tracker.Timestamp();
        Assert.AreEqual(0, tracker.WaitingWriters);

        var started = WriteLockTracker.Untracked.BeginAcquire();
        Assert.AreEqual(0, WriteLockTracker.Untracked.WaitingWriters);
        Assert.IsFalse(WriteLockTracker.Untracked.YieldToWaiters(TimeSpan.FromSeconds(1)));
        WriteLockTracker.Untracked.AcquireFailed("x", null, started);
    }

    [TestMethod]
    public void YieldToWaiters_WithNobodyWaiting_ReturnsAtOnce_AndCountsNothing()
    {
        var tracker = new WriteLockTracker();
        Assert.IsFalse(tracker.YieldToWaiters(TimeSpan.FromSeconds(30)));
        Assert.AreEqual(0, tracker.Snapshot().YieldsToWaiters);
    }

    [TestMethod]
    public void YieldToWaiters_ReturnsWhenAWaiterAcquires()
    {
        var tracker = new WriteLockTracker();
        var started = tracker.BeginAcquire();
        var yielded = Task.Run(() => tracker.YieldToWaiters(TimeSpan.FromSeconds(30)));
        Assert.IsTrue(WaitFor(() => tracker.Snapshot().YieldsToWaiters == 1));
        Assert.IsFalse(yielded.IsCompleted, "still waiting while the waiter has not acquired");

        tracker.Acquired("w", null, started);

        Assert.IsTrue(yielded.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(yielded.Result);
        Assert.AreEqual(0, tracker.Snapshot().YieldTimeouts);
    }

    [TestMethod]
    public void YieldToWaiters_GivesUpAtTheCap()
    {
        var tracker = new WriteLockTracker();
        var started = tracker.BeginAcquire();

        Assert.IsTrue(tracker.YieldToWaiters(TimeSpan.FromMilliseconds(50)));

        var m = tracker.Snapshot();
        Assert.AreEqual(1, m.YieldsToWaiters);
        Assert.AreEqual(1, m.YieldTimeouts);
        Assert.AreEqual(1, m.WaitingWriters);
        tracker.AcquireFailed("w", null, started);
    }

    [TestMethod]
    public void Session_CommitsAtTheNextRow_OnceTheBatchHeldTheLockPastMaxBatchHold()
    {
        var clock = new TickClock();
        _db.WriteLockTracker = new WriteLockTracker(timeProvider: clock);
        using var session = _db.BeginWriteSession(new IndexWriteOptions { MaxBatchHold = TimeSpan.FromSeconds(2) });
        session.Begin();

        clock.Advance(TimeSpan.FromSeconds(1.9));
        session.RowsWritten();
        Assert.AreEqual(0, session.CommittedBatches, "under the cap");

        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.AreEqual(0, session.CommittedBatches, "the cap is checked when a row is written");
        session.RowsWritten();
        Assert.AreEqual(1, session.CommittedBatches);
        Assert.IsTrue(session.IsOpen, "a new batch began");

        session.RowsWritten();
        Assert.AreEqual(1, session.CommittedBatches, "the new batch's hold restarted");
        session.Complete();
    }

    [TestMethod]
    public void Session_WithMaxBatchHoldZero_NeverCommitsOnTime()
    {
        var clock = new TickClock();
        _db.WriteLockTracker = new WriteLockTracker(timeProvider: clock);
        using var session = _db.BeginWriteSession(new IndexWriteOptions { MaxBatchHold = TimeSpan.Zero });
        session.Begin();
        clock.Advance(TimeSpan.FromHours(1));
        session.RowsWritten();
        Assert.AreEqual(0, session.CommittedBatches);
        session.Complete();
    }

    [TestMethod]
    public void Session_AfterACommit_LetsAWaitingWriterTakeTheLockBeforeItBeginsAgain()
    {
        var tracker = new WriteLockTracker();
        _db.WriteLockTracker = tracker;
        var order = new List<string>();
        using var session = _db.BeginWriteSession(new IndexWriteOptions { YieldToWaitersCap = TimeSpan.FromSeconds(20) });
        session.Begin();

        // The waiter runs on its own connection, like a grant write: BEGIN IMMEDIATE blocks on our lock.
        var waiter = Task.Run(() =>
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
            lock (order) order.Add("waiter-acquired");
            var hold = tracker.Acquired("test-waiter", null, started);
            using var commit = conn.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
            tracker.Released(hold);
        });
        Assert.IsTrue(WaitFor(() => tracker.WaitingWriters == 1), "the waiter is queued on the lock");

        session.CommitBatch();
        lock (order) order.Add("session-began-again");

        Assert.IsTrue(waiter.Wait(TimeSpan.FromSeconds(20)));
        CollectionAssert.AreEqual(new[] { "waiter-acquired", "session-began-again" }, order);
        var m = tracker.Snapshot();
        Assert.AreEqual(1, m.YieldsToWaiters);
        Assert.AreEqual(0, m.YieldTimeouts);
        session.Complete();
    }

    [TestMethod]
    public void Session_YieldIsBoundedByTheCap_WhenTheWaiterNeverAcquires()
    {
        var tracker = new WriteLockTracker();
        _db.WriteLockTracker = tracker;
        using var session = _db.BeginWriteSession(new IndexWriteOptions { YieldToWaitersCap = TimeSpan.FromMilliseconds(50) });
        session.Begin();
        var stuck = tracker.BeginAcquire();

        session.CommitBatch();

        Assert.IsTrue(session.IsOpen, "the session carried on after the cap");
        Assert.AreEqual(1, tracker.Snapshot().YieldTimeouts);
        tracker.AcquireFailed("stuck", null, stuck);
        session.Complete();
    }

    [TestMethod]
    public void Session_WithYieldCapZero_DoesNotYield()
    {
        var tracker = new WriteLockTracker();
        _db.WriteLockTracker = tracker;
        using var session = _db.BeginWriteSession(new IndexWriteOptions { YieldToWaitersCap = TimeSpan.Zero });
        session.Begin();
        var stuck = tracker.BeginAcquire();
        session.CommitBatch();
        Assert.AreEqual(0, tracker.Snapshot().YieldsToWaiters);
        tracker.AcquireFailed("stuck", null, stuck);
        session.Complete();
    }

    [TestMethod]
    public void Metrics_ExposeTheWaiterAndYieldFields()
    {
        var tracker = new WriteLockTracker();
        var started = tracker.BeginAcquire();
        tracker.YieldToWaiters(TimeSpan.FromMilliseconds(10));
        var m = tracker.Snapshot();
        Assert.AreEqual(1, m.WaitingWriters);
        Assert.AreEqual(1, m.YieldsToWaiters);
        Assert.AreEqual(1, m.YieldTimeouts);
        var json = System.Text.Json.JsonSerializer.Serialize(m,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        StringAssert.Contains(json, "\"waiting_writers\":1");
        StringAssert.Contains(json, "\"yields_to_waiters\":1");
        StringAssert.Contains(json, "\"yield_timeouts\":1");
        tracker.AcquireFailed("x", null, started);
    }
}
