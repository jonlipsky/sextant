namespace Sextant.Store.Tests;

[TestClass]
public class WriteLockTrackerTests
{
    private sealed class TickClock : TimeProvider
    {
        private long _ticks;
        public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * TimestampFrequency);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => 1_000_000;
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private readonly TickClock _clock = new();
    private readonly List<WriteLockEvent> _events = [];

    private WriteLockTracker Tracker() => new(TimeSpan.FromSeconds(5), _events.Add, _clock);

    [TestMethod]
    public void HoldOverThreshold_WarnsWithOwner_AndUpdatesTheLongestHold()
    {
        var tracker = Tracker();
        var wait = tracker.BeginAcquire();
        var hold = tracker.Acquired("grant-write", "x", wait);
        _clock.Advance(TimeSpan.FromSeconds(6));
        tracker.Released(hold);

        Assert.AreEqual(1, _events.Count);
        Assert.AreEqual(WriteLockEventKind.LongHold, _events[0].Kind);
        Assert.AreEqual("grant-write", _events[0].Owner);
        StringAssert.Contains(_events[0].Describe(), "'grant-write'");
        var m = tracker.Snapshot();
        Assert.AreEqual(1, m.HoldsTotal);
        Assert.AreEqual(1, m.HoldsOverThreshold);
        Assert.AreEqual(6000, m.LongestHoldMs, 1);
        Assert.AreEqual("grant-write", m.LongestHoldOwner);
        Assert.IsNotNull(m.LongestHoldAtUnixMs);
    }

    [TestMethod]
    public void HoldUnderThreshold_DoesNotWarn_ButIsCounted()
    {
        var tracker = Tracker();
        var hold = tracker.Acquired("a", null, tracker.BeginAcquire());
        _clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Released(hold);

        Assert.AreEqual(0, _events.Count);
        Assert.AreEqual(1, tracker.Snapshot().HoldsTotal);
        Assert.AreEqual(0, tracker.Snapshot().HoldsOverThreshold);
    }

    [TestMethod]
    public void Waits_AreMeasured_AndWarnOverThreshold_EvenWhenAcquisitionFails()
    {
        var tracker = Tracker();
        var wait = tracker.BeginAcquire();
        _clock.Advance(TimeSpan.FromSeconds(7));
        tracker.AcquireFailed("grant-write", null, wait);

        var shortWait = tracker.BeginAcquire();
        _clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Released(tracker.Acquired("other", null, shortWait));

        Assert.AreEqual(1, _events.Count(e => e.Kind == WriteLockEventKind.LongWait));
        var m = tracker.Snapshot();
        Assert.AreEqual(7000, m.LongestWaitMs, 1);
        Assert.AreEqual("grant-write", m.LongestWaitOwner);
        Assert.AreEqual(1, m.WaitsOverThreshold);
    }

    [TestMethod]
    public void ASinkThatThrows_NeverBreaksTheCaller()
    {
        var tracker = new WriteLockTracker(TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException(), _clock);
        var hold = tracker.Acquired("a", null, tracker.BeginAcquire());
        _clock.Advance(TimeSpan.FromSeconds(2));
        tracker.Released(hold);

        Assert.AreEqual(1, tracker.Snapshot().HoldsOverThreshold);
    }

    [TestMethod]
    public void IndexWriteSession_ReportsOneHoldPerBatch_NotPerSession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_wl_{Guid.NewGuid():N}.db");
        using var db = new IndexDatabase(path);
        try
        {
            db.RunMigrations();
            var tracker = Tracker();
            db.WriteLockTracker = tracker;

            using (var session = db.BeginWriteSession(owner: "test-session"))
            {
                session.Begin();
                session.CommitBatch();
                session.CommitBatch();
                session.Complete();
            }

            var m = tracker.Snapshot();
            Assert.AreEqual(3, m.HoldsTotal, "Begin->CommitBatch, CommitBatch, and the final batch each count once");
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    [TestMethod]
    public void IndexWriteSession_Rollback_ReleasesTheHold()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_wl_{Guid.NewGuid():N}.db");
        using var db = new IndexDatabase(path);
        try
        {
            db.RunMigrations();
            var tracker = Tracker();
            db.WriteLockTracker = tracker;

            using (var session = db.BeginWriteSession(owner: "test-session"))
            {
                session.Begin();
                session.Rollback();
            }

            Assert.AreEqual(1, tracker.Snapshot().HoldsTotal);
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    [TestMethod]
    public void CheckpointTruncate_RecordsAHold_OnlyWhenItRan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_wl_{Guid.NewGuid():N}.db");
        using var db = new IndexDatabase(path);
        try
        {
            db.RunMigrations();
            var tracker = Tracker();

            Assert.IsTrue(IncrementalVacuum.CheckpointTruncate(db.GetConnection(), tracker));
            Assert.AreEqual(1, tracker.Snapshot().HoldsTotal);

            // A write transaction on another connection makes the checkpoint return busy at once.
            using var other = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            other.Open();
            using (var begin = other.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                begin.ExecuteNonQuery();
            }
            using (var write = db.GetConnection().CreateCommand())
            {
                write.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                write.ExecuteNonQuery();
            }

            Assert.IsFalse(IncrementalVacuum.CheckpointTruncate(db.GetConnection(), tracker));
            Assert.AreEqual(1, tracker.Snapshot().HoldsTotal, "a checkpoint that returned busy held nothing");
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }
}
