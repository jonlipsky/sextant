using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #329: a converged catalog must not report "more remaining" forever. Every continuation carries an explicit
/// reason, vacuumed pages alone are not progress on the work a stop reason names, and a zero-progress stop continues
/// at most a bounded number of times in a row.
/// </summary>
[TestClass]
public class RetentionConvergenceTests
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

    private SnapshotService Start()
    {
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath, _dataRoot,
            retention: new RetentionPolicy { KeepCompleteGenerations = 0 }), new FakeSnapshotWorker(_db), _db);
        return _service;
    }

    [TestMethod]
    public async Task ConvergedCatalog_EveryPassIsDone_WithNoMoreRemaining_AndNamesWhatIsLeft()
    {
        ServiceTestFixtures.PublishComplete(_db, ServiceTestFixtures.Request(commit: "current"));
        var service = Start();

        for (var pass = 0; pass < 3; pass++)
        {
            var report = await service.RunRetentionAsync(true, passLimit: TimeSpan.FromSeconds(60));
            Assert.IsFalse(report.MoreRemaining, $"pass {pass}: {report.StopReason}");
            Assert.IsNull(report.StopReason, $"pass {pass}");
            Assert.IsFalse(SnapshotService.ScheduledRetentionContinues(report), $"pass {pass} closes the day");
            StringAssert.StartsWith(report.LeftWork, "snapshots=0 runs=0 api=0 files=0");
            StringAssert.EndsWith(report.LeftWork, "texts=0");
        }
    }

    [TestMethod]
    public void MoreRemaining_NeverCarriesNoReason_AndVacuumIsProgressOnlyWhilePagesAreLeft()
    {
        var vacuumOnly = new RetentionReport { DryRun = false, MoreRemaining = true, PagesVacuumed = 500, StopReason = "sweep_budget", Batches = 2 };
        Assert.IsFalse(SnapshotService.MadeDeletionProgress(vacuumOnly));
        Assert.IsTrue(SnapshotService.MadeRetentionProgress(vacuumOnly));
        Assert.IsTrue(SnapshotService.ScheduledRetentionContinues(vacuumOnly), "a retryable stop continues (bounded)");

        var vacuumLeft = vacuumOnly with { VacuumLeft = true };
        foreach (var reason in new[] { "vacuum_budget", "source_text_undeletable", "work_remaining", "api_deferred", "sweep_budget" })
            Assert.IsTrue(SnapshotService.ScheduledRetentionContinues(vacuumLeft with { StopReason = reason }),
                $"vacuumed pages with pages left keep the day open ({reason})");

        Assert.IsFalse(SnapshotService.ScheduledRetentionContinues(
            vacuumOnly with { StopReason = "source_text_undeletable" }), "a stuck blob with vacuum finished ends the day");
        Assert.IsFalse(SnapshotService.ScheduledRetentionContinues(
            vacuumOnly with { StopReason = "work_remaining" }));
        Assert.IsFalse(SnapshotService.ScheduledRetentionContinues(
            vacuumLeft with { PagesVacuumed = 0, StopReason = "source_text_undeletable" }), "nothing vacuumed is no progress");
    }

    [TestMethod]
    public void VacuumProgress_ResetsTheNoProgressBound()
    {
        var service = Start();
        var report = new RetentionReport
        {
            DryRun = false, MoreRemaining = true, StopReason = "api_deferred", PagesVacuumed = 100, VacuumLeft = true
        };

        for (var i = 0; i < SnapshotService.MaxScheduledNoProgressContinues * 3; i++)
            Assert.IsTrue(service.ScheduledPassContinues(report), $"continue {i + 1}");
    }

    [TestMethod]
    [DataRow("unit_budget")]
    [DataRow("batch_budget")]
    [DataRow("sweep_budget")]
    [DataRow("api_deferred")]
    [DataRow("sqlite_busy")]
    public void ZeroProgressStop_ContinuesOnlyABoundedNumberOfTimes(string reason)
    {
        var service = Start();
        var stuck = new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = reason, PagesVacuumed = 100 };

        for (var i = 0; i < SnapshotService.MaxScheduledNoProgressContinues; i++)
            Assert.IsTrue(service.ScheduledPassContinues(stuck), $"continue {i + 1}");
        Assert.IsFalse(service.ScheduledPassContinues(stuck), "vacuumed pages do not keep a stuck stop alive");
    }

    [TestMethod]
    public void LogLine_CarriesSkippedAndWhatIsLeft()
    {
        var report = new RetentionReport
        {
            DryRun = false, MoreRemaining = true, StopReason = "sweep_budget", SnapshotsSkipped = 4,
            LeftWork = "snapshots=0 runs=0 api=0 files=cursor@120 texts=more"
        };

        var line = SnapshotService.ScheduledRetentionLogLine(report, 1234);

        StringAssert.Contains(line, "more remaining (sweep_budget)");
        StringAssert.Contains(line, "skipped=4");
        StringAssert.Contains(line, "left: snapshots=0 runs=0 api=0 files=cursor@120 texts=more");

        var done = SnapshotService.ScheduledRetentionLogLine(new RetentionReport { DryRun = false, LeftWork = "snapshots=0" }, 5);
        Assert.IsFalse(done.Contains("more remaining", StringComparison.Ordinal));
        StringAssert.Contains(done, "skipped=0");
    }
}
