using System.Security.Cryptography;
using System.Text;
using Sextant.Core;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #336: a scheduled pass must close its day under steady small churn. A trickle of file versions or source texts
/// freed by indexing is not progress on the backlog the pass started with.
/// </summary>
[TestClass]
public class RetentionChurnConvergenceTests
{
    private string _dbPath = null!;
    private string _dataRoot = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;

    [TestInitialize]
    public void Init()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath, _dataRoot,
            retention: new RetentionPolicy { KeepCompleteGenerations = 0 }), new FakeSnapshotWorker(_db), _db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _service.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }

    [TestMethod]
    public void TrickleOfFileVersionAndTextDeletions_DoesNotKeepTheDayOpen()
    {
        var trickle = new RetentionReport
        {
            DryRun = false, MoreRemaining = true, StopReason = "sweep_budget", Batches = 3,
            FileVersionsDeleted = 96, SourceTextsDeleted = 4
        };

        Assert.IsFalse(SnapshotService.MadeStopReasonProgress(trickle));
        for (var i = 0; i < SnapshotService.MaxScheduledNoProgressContinues; i++)
            Assert.IsTrue(_service.ScheduledPassContinues(trickle), $"continue {i + 1}");
        Assert.IsFalse(_service.ScheduledPassContinues(trickle), "the trickle neither resets nor outlives the cap");
    }

    [TestMethod]
    public void AdvancingTheSourceTextWalk_IsProgress_AndPlannedDeletionsAre()
    {
        var walking = new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = "sweep_budget", Batches = 1, SourceTextSweepAdvanced = true };
        Assert.IsTrue(SnapshotService.MadeStopReasonProgress(walking));
        Assert.IsTrue(SnapshotService.MadeStopReasonProgress(
            new RetentionReport { DryRun = false, MoreRemaining = true, Batches = 1, SnapshotsDeleted = 1 }));
    }

    [TestMethod]
    public void AdvancingTheFileSweep_IsProgress()
    {
        Assert.IsTrue(SnapshotService.MadeStopReasonProgress(
            new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = "sweep_budget", Batches = 1, FileSweepAdvanced = true }));
    }

    [TestMethod]
    public void AKeyBuildBudgetStop_IsNotProgress_AndContinuesOnlyABoundedNumberOfTimes()
    {
        var stop = new RetentionReport
        {
            DryRun = false, MoreRemaining = true, StopReason = RetentionService.KeysBudgetStopReason, Batches = 1
        };

        Assert.AreEqual("source_text_keys_budget", RetentionService.KeysBudgetStopReason);
        Assert.IsFalse(SnapshotService.MadeStopReasonProgress(stop));
        Assert.IsTrue(SnapshotService.ScheduledRetentionContinues(stop));
        for (var i = 0; i < SnapshotService.MaxScheduledNoProgressContinues; i++)
            Assert.IsTrue(_service.ScheduledPassContinues(stop), $"continue {i + 1}");
        Assert.IsFalse(_service.ScheduledPassContinues(stop));
    }

}

/// <summary>
/// Issue #336: each blob sweep runs at most one full cycle per scheduled retention day, so two sweeps that finish on
/// different passes under steady churn still let the day close. Time comes from a <see cref="ManualClock"/>; one file
/// span per pass makes a modest backlog take several passes.
/// </summary>
[TestClass]
public class ScheduledSweepDayTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 3, 10, 3, 30, 0, TimeSpan.Zero);
    private string _dbPath = null!;
    private string _dataRoot = null!;
    private IndexDatabase _db = null!;
    private ManualClock _clock = null!;
    private SnapshotService _service = null!;
    private long _fileId;
    private int _blobCounter;

    [TestInitialize]
    public void Init()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var projectId = new ProjectStore(_db.GetConnection()).Insert(new ProjectIdentity
        {
            CanonicalId = "proj_sweepday_0123456789",
            GitRemoteUrl = "https://github.com/org/repo",
            RepoRelativePath = "src/App/App.csproj"
        }, 1);
        _fileId = Scalar($"INSERT INTO files (project_id, repo_relative_path) VALUES ({projectId}, 'src/A.cs') RETURNING id;");
        _clock = new ManualClock(Day1);
        StartService(3);
    }

    private void StartService(int retentionHourUtc)
    {
        _service?.Dispose();
        var options = ServiceTestFixtures.NewOptions(_dbPath, _dataRoot) with
        {
            Maintenance = new MaintenancePolicy
            {
                Interval = TimeSpan.Zero, BranchIdleRetireDays = 0, RepositoryReclaimDays = 0, RetentionHourUtc = retentionHourUtc
            }
        };
        _service = SnapshotService.Start(options, new FakeSnapshotWorker(_db), _db, timeProvider: _clock);
        _service.RetentionSweepSpansPerPass = 1;
    }

    [TestCleanup]
    public void Cleanup()
    {
        _service.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }

    private long Scalar(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void SeedFileVersions(int count)
    {
        using var tx = _db.GetConnection().BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            using var cmd = _db.GetConnection().CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES ({_fileId}, randomblob(32), 1);";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private void SeedBlobs(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var content = Encoding.UTF8.GetBytes($"class Blob{_blobCounter++} {{ }}");
            _service.SourceTexts.Put(SHA256.HashData(content), content);
        }
    }

    private async Task<MaintenanceTickReport> TickAsync(TimeSpan advance)
    {
        _clock.Advance(advance);
        return await _service.RunMaintenanceTickAsync();
    }

    [TestMethod]
    public async Task OutOfPhaseCycles_UnderSteadyChurn_CloseTheDayAfterBothFinishOneCycle()
    {
        // Files need three passes (256 per span, 700 rows); texts two (256 per pass, 300 blobs).
        SeedFileVersions(700);
        SeedBlobs(300);

        var ran = new List<RetentionReport>();
        for (var pass = 1; pass <= 3; pass++)
        {
            var tick = await TickAsync(TimeSpan.FromMinutes(5));
            Assert.AreEqual("ran", tick.Retention, $"pass {pass}");
            ran.Add(tick.RetentionReport!);
            // Steady churn between passes: a few fresh orphans of each kind.
            SeedFileVersions(3);
            SeedBlobs(3);
        }

        Assert.IsTrue(ran[0].MoreRemaining && ran[1].MoreRemaining, "both sweeps still have a cycle to finish");
        StringAssert.Contains(ran[0].LeftWork, "files=cursor@");
        Assert.IsTrue(ran[1].SourceTextCycleCompleted, "texts finish on pass 2, files only on pass 3");
        StringAssert.Contains(ran[1].LeftWork, "files=cursor@");
        Assert.IsFalse(ran[2].MoreRemaining, ran[2].StopReason);
        StringAssert.Contains(ran[2].LeftWork, "files=0");
        StringAssert.EndsWith(ran[2].LeftWork, "texts=0");
        Assert.AreEqual(0, ran[2].SourceTextsDeleted, "the finished text sweep is skipped on pass 3");

        Assert.AreEqual("not_due", (await TickAsync(TimeSpan.FromMinutes(5))).Retention, "the day is closed");
    }

    [TestMethod]
    public async Task FileVersionBacklogLargerThanOneSlice_KeepsTheDayOpenUntilItsCycleCompletes()
    {
        SeedFileVersions(700);

        var first = await TickAsync(TimeSpan.FromMinutes(5));
        var second = await TickAsync(TimeSpan.FromMinutes(5));

        foreach (var tick in new[] { first, second })
        {
            Assert.AreEqual("ran", tick.Retention);
            Assert.IsTrue(tick.RetentionReport!.MoreRemaining);
            Assert.IsTrue(tick.RetentionReport.FileSweepAdvanced);
            Assert.IsTrue(tick.ContinueSoon, "a moving sweep continues soon");
        }

        var last = await TickAsync(TimeSpan.FromMinutes(5));
        Assert.IsFalse(last.RetentionReport!.MoreRemaining, last.RetentionReport.StopReason);
        Assert.AreEqual(0L, Scalar("SELECT COUNT(*) FROM file_versions;"));
        Assert.AreEqual("not_due", (await TickAsync(TimeSpan.FromMinutes(5))).Retention);
    }

    [TestMethod]
    public async Task TheNextUtcDay_RunsBothSweepsAgain_AndManualPassesStillSweep()
    {
        SeedFileVersions(5);
        SeedBlobs(5);
        var day1 = await TickAsync(TimeSpan.FromMinutes(5));
        Assert.IsFalse(day1.RetentionReport!.MoreRemaining);
        Assert.AreEqual(5, day1.RetentionReport.FileVersionsDeleted);
        Assert.AreEqual(5, day1.RetentionReport.SourceTextsDeleted);

        // A manual pass keeps sweeping a slice per call and leaves the closed day closed.
        SeedBlobs(2);
        var manual = await _service.RunRetentionAsync(true, passLimit: TimeSpan.FromSeconds(60));
        Assert.AreEqual(2, manual.SourceTextsDeleted);
        Assert.AreEqual("not_due", (await TickAsync(TimeSpan.FromMinutes(5))).Retention);

        SeedFileVersions(4);
        SeedBlobs(4);
        var day2 = await TickAsync(TimeSpan.FromHours(24));
        Assert.AreEqual("ran", day2.Retention);
        Assert.AreEqual(4, day2.RetentionReport!.FileVersionsDeleted, "the file sweep runs again");
        Assert.AreEqual(4, day2.RetentionReport.SourceTextsDeleted, "the text sweep runs again");
        Assert.IsFalse(day2.RetentionReport.MoreRemaining, day2.RetentionReport.StopReason);
    }

    [TestMethod]
    public async Task AChainThatCrossesMidnight_KeepsItsCycles_AndOnlyTheNextScheduledDayResetsThem()
    {
        StartService(0);
        _clock.Advance(TimeSpan.FromHours(20) + TimeSpan.FromMinutes(5));
        SeedFileVersions(700);
        SeedBlobs(5);

        var first = await TickAsync(TimeSpan.FromMinutes(5));
        Assert.AreEqual("ran", first.Retention);
        Assert.IsTrue(first.RetentionReport!.SourceTextCycleCompleted, "texts finish their cycle at 23:40");
        Assert.IsTrue(first.RetentionReport.MoreRemaining, "the file sweep still has work");
        SeedBlobs(3);

        var second = await TickAsync(TimeSpan.FromMinutes(10));
        Assert.AreEqual(0, second.RetentionReport!.SourceTextsDeleted);
        Assert.IsTrue(second.RetentionReport.MoreRemaining);

        var past = await TickAsync(TimeSpan.FromMinutes(15));
        Assert.AreEqual(new DateOnly(2026, 3, 11), DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));
        Assert.AreEqual("ran", past.Retention);
        Assert.AreEqual(0, past.RetentionReport!.SourceTextsDeleted, "midnight did not reset the finished text sweep");
        Assert.IsFalse(past.RetentionReport.MoreRemaining, "the file cycle continued, it did not restart");
        Assert.AreEqual(0L, Scalar("SELECT COUNT(*) FROM file_versions;"));

        SeedFileVersions(4);
        SeedBlobs(2);
        var next = await TickAsync(TimeSpan.FromHours(24));
        Assert.AreEqual("ran", next.Retention);
        Assert.AreEqual(5, next.RetentionReport!.SourceTextsDeleted, "the next scheduled day sweeps the texts again");
        Assert.AreEqual(4, next.RetentionReport.FileVersionsDeleted);
    }

    [TestMethod]
    public async Task APassThatThrows_EndsItsChain_SoTheNextDayResetsTheCycles()
    {
        StartService(0);
        _clock.Advance(TimeSpan.FromHours(20) + TimeSpan.FromMinutes(5));
        SeedFileVersions(5);
        SeedBlobs(5);
        var clean = await TickAsync(TimeSpan.FromMinutes(5));
        Assert.IsFalse(clean.RetentionReport!.MoreRemaining, "both sweeps finished their day");

        // The next day's first pass throws; the chain it opened must not outlive it.
        SeedFileVersions(4);
        SeedBlobs(4);
        _service.ScheduledRetentionFault = () => throw new InvalidOperationException("boom");
        _clock.Advance(TimeSpan.FromHours(24));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.RunMaintenanceTickAsync());
        _service.ScheduledRetentionFault = null;

        // Two days later the stale key would be neither today nor yesterday: both sweeps must run again.
        SeedFileVersions(3);
        SeedBlobs(3);
        var later = await TickAsync(TimeSpan.FromHours(24));
        Assert.AreEqual("ran", later.Retention);
        Assert.AreEqual(7, later.RetentionReport!.FileVersionsDeleted, "the file sweep runs again");
        Assert.AreEqual(7, later.RetentionReport.SourceTextsDeleted, "the text sweep runs again");
    }

    [TestMethod]
    public async Task AManualPassBetweenScheduledSlices_DoesNotMoveTheScheduledCursor()
    {
        SeedBlobs(600);

        var first = await TickAsync(TimeSpan.FromMinutes(5));
        Assert.AreEqual("source_text_cap", first.RetentionReport!.StopReason);
        Assert.IsFalse(first.RetentionReport.SourceTextCycleCompleted);
        var scheduledPosition = _service.ScheduledSourceTextCursor.Position;
        Assert.IsNotNull(scheduledPosition);

        var manual = await _service.RunRetentionAsync(true, passLimit: TimeSpan.FromSeconds(60));
        Assert.AreEqual(256, manual.SourceTextsDeleted);
        Assert.IsNotNull(_service.ManualSourceTextCursor.Position);
        Assert.AreNotEqual(scheduledPosition, _service.ManualSourceTextCursor.Position);
        Assert.AreEqual(scheduledPosition, _service.ScheduledSourceTextCursor.Position, "the manual pass has its own cursor");

        var second = await TickAsync(TimeSpan.FromMinutes(5));
        Assert.IsTrue(second.RetentionReport!.SourceTextCycleCompleted, "the scheduled cursor walked on from its own position to the end");
        Assert.AreEqual(88, second.RetentionReport.SourceTextsDeleted);
    }
}
