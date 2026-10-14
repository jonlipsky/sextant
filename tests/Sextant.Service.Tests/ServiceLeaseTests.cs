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
    public async Task PreviousRunThatNeverReleasedTheLease_IsLoggedAndExposedAsAMetric()
    {
        _service.Dispose();
        _service = null!;

        // A holder that dies without releasing: abandoned (row left to expire) with no recorded reason, as a kill leaves it.
        var crashed = WriterLease.TryAcquire(_dbPath, "crashed", TimeSpan.FromMilliseconds(300), autoHeartbeat: false);
        Assert.IsNotNull(crashed);
        crashed.Abandon();
        crashed.Dispose();
        ExecSql("UPDATE writer_run_state SET end_reason = NULL;");
        await Task.Delay(500);

        var restarted = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), database: _db);
        _service = restarted;
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            Assert.IsTrue(WriterLease.GetRunState(conn)!.PreviousRunUnclean);
        }

        var metrics = restarted.CollectMetrics();
        Assert.AreEqual(1, metrics.Crash.UncleanShutdownsTotal);
        Assert.IsTrue(metrics.Crash.PreviousRunUnclean);
        var text = Sextant.Service.Observability.PrometheusExposition.Render(metrics);
        StringAssert.Contains(text, "sextant_service_unclean_shutdowns_total 1");
        StringAssert.Contains(text, "sextant_service_previous_run_unclean 1");

        var acquisition = new WriterLeaseAcquisition
        {
            PreviousRunUnclean = true, PreviousRunHeartbeatAt = 1_700_000_000_000,
            PreviousRunEndReason = WriterEndReason.Unclean, PreviousRunKind = WriterKind.Service
        };
        using var log = new StringWriter();
        SnapshotService.LogLeaseAcquisition(acquisition, log);
        StringAssert.Contains(log.ToString(), "WARNING: previous service run ended without a clean shutdown at ~2023-11-14");
        StringAssert.Contains(log.ToString(), "possible crash or kill (e.g. OOM)");
    }

    [TestMethod]
    public void AbandonedAndFailedReleaseRuns_AreNotReportedAsCrashes()
    {
        using var abandonedLog = new StringWriter();
        SnapshotService.LogLeaseAcquisition(new WriterLeaseAcquisition
        {
            PreviousRunUnclean = true, PreviousRunEndReason = WriterEndReason.Abandoned, PreviousRunKind = WriterKind.Service
        }, abandonedLog);
        StringAssert.Contains(abandonedLog.ToString(), "previous service run abandoned the lease");
        Assert.IsFalse(abandonedLog.ToString().Contains("crash or kill"));

        using var failedLog = new StringWriter();
        SnapshotService.LogLeaseAcquisition(new WriterLeaseAcquisition
        {
            PreviousRunUnclean = true, PreviousRunEndReason = WriterEndReason.ReleaseFailed, PreviousRunKind = WriterKind.Daemon
        }, failedLog);
        StringAssert.Contains(failedLog.ToString(), "previous daemon run could not release the lease");
    }

    [TestMethod]
    public async Task AbandonedPreviousRun_IsCountedSeparately_AndExposed()
    {
        _service.Dispose();
        _service = null!;

        var straggler = WriterLease.TryAcquire(
            _dbPath, "straggler", TimeSpan.FromMilliseconds(300), autoHeartbeat: false, kind: WriterKind.Service);
        Assert.IsNotNull(straggler);
        straggler.Abandon();
        straggler.Dispose();
        await Task.Delay(500);

        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), database: _db);
        var metrics = _service.CollectMetrics();
        Assert.AreEqual(0, metrics.Crash.UncleanShutdownsTotal);
        Assert.IsFalse(metrics.Crash.PreviousRunUnclean);
        Assert.AreEqual(1, metrics.Crash.AbandonedRunsTotal);
        var text = Sextant.Service.Observability.PrometheusExposition.Render(metrics);
        StringAssert.Contains(text, "sextant_service_abandoned_runs_total 1");
        StringAssert.Contains(text, "sextant_service_previous_run_unclean 0");
        StringAssert.Contains(text, "sextant_service_previous_run_last_heartbeat_timestamp_seconds");
        Assert.IsFalse(text.Contains("previous_run_last_heartbeat_unix_ms"));
    }

    [TestMethod]
    public void MetricsCollection_WithoutTheRunStateTable_DegradesToEmptyCrashMetrics()
    {
        ExecSql("DROP TABLE writer_run_state;");

        var metrics = _service.CollectMetrics();
        Assert.AreEqual(0, metrics.Crash.UncleanShutdownsTotal);
        Assert.IsFalse(metrics.Crash.PreviousRunUnclean);
        StringAssert.Contains(Sextant.Service.Observability.PrometheusExposition.Render(metrics),
            "sextant_service_unclean_shutdowns_total 0");
    }

    private void ExecSql(string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void CleanShutdown_LeavesNoUncleanMarker()
    {
        _service.Dispose();
        _service = null!;

        using var restarted = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), database: _db);
        var metrics = restarted.CollectMetrics();
        Assert.AreEqual(0, metrics.Crash.UncleanShutdownsTotal);
        Assert.IsFalse(metrics.Crash.PreviousRunUnclean);
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
