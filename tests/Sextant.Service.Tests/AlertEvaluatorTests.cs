using Sextant.Service.Observability;

namespace Sextant.Service.Tests;

[TestClass]
public class AlertEvaluatorTests
{
    [TestMethod]
    [DataRow(10, 0, -1)]
    [DataRow(9, 1, -1)]
    [DataRow(8, 2, (int)AlertLevel.Warning)]
    [DataRow(5, 5, (int)AlertLevel.Warning)]
    [DataRow(4, 6, (int)AlertLevel.Critical)]
    [DataRow(0, 5, (int)AlertLevel.Critical)]
    public void Completeness_UsesStrictThresholds_AndPartialStillSucceeds(
        int complete, int partial, int expectedLevel)
    {
        var snapshot = Snapshot(new JobMetrics { Complete = complete, Partial = partial });
        var alerts = AlertEvaluator.Evaluate(snapshot);

        Assert.AreEqual(1.0, snapshot.Jobs.SuccessRate);
        Assert.IsFalse(alerts.Any(a => a.Id == "low_success_rate"));
        AssertCompletenessAlert(alerts, expectedLevel);
    }

    [TestMethod]
    [DataRow(8, 2, -1)]
    [DataRow(7, 3, (int)AlertLevel.Warning)]
    [DataRow(2, 8, (int)AlertLevel.Warning)]
    [DataRow(1, 9, (int)AlertLevel.Critical)]
    public void Completeness_UsesConfiguredThresholds(int complete, int partial, int expectedLevel)
    {
        var thresholds = new AlertThresholds { CompletenessRateWarn = 0.8, CompletenessRateCrit = 0.2 };
        var alerts = AlertEvaluator.Evaluate(
            Snapshot(new JobMetrics { Complete = complete, Partial = partial }), thresholds);

        AssertCompletenessAlert(alerts, expectedLevel);
    }

    [TestMethod]
    public void Completeness_WaitsForSharedMinimumSamples()
    {
        var snapshot = Snapshot(new JobMetrics { Partial = 5 });
        AssertCompletenessAlert(AlertEvaluator.Evaluate(
            snapshot, new AlertThresholds { SuccessRateMinSamples = 6 }), -1);
        AssertCompletenessAlert(AlertEvaluator.Evaluate(
            snapshot, new AlertThresholds { SuccessRateMinSamples = 5 }), (int)AlertLevel.Critical);
        AssertCompletenessAlert(AlertEvaluator.Evaluate(Snapshot(new JobMetrics { Partial = 4 })), -1);
    }

    [TestMethod]
    public void Completeness_WithoutTerminalSamples_IsNotEvaluated()
    {
        var snapshot = Snapshot(new JobMetrics { Queued = 10, Running = 1 });
        AssertCompletenessAlert(AlertEvaluator.Evaluate(snapshot), -1);
        AssertCompletenessAlert(AlertEvaluator.Evaluate(snapshot,
            new AlertThresholds { SuccessRateMinSamples = 0, CompletenessRateWarn = 1.1 }), -1);
    }

    [TestMethod]
    public void ExistingAlerts_ArePreserved_AlongsideCompleteness()
    {
        var snapshot = Snapshot(new JobMetrics { Failed = 5 }) with
        {
            QueueDelay = LatencyStats.FromSamples([300_000]),
            WorkerCapacity = new WorkerCapacityMetrics { HasCapacity = false, InFlight = 0, Queued = 1 },
            Storage = new StorageMetrics { CatalogBytes = 10 }
        };
        var alerts = AlertEvaluator.Evaluate(snapshot, new AlertThresholds { StorageCritBytes = 10 });

        CollectionAssert.AreEquivalent(
            new[] { "queue_delay_high", "low_success_rate", "low_completeness_rate", "worker_exhaustion", "storage_pressure" },
            alerts.Select(a => a.Id).ToArray());
        Assert.IsTrue(alerts.All(a => a.Level == AlertLevel.Critical));
    }

    private static void AssertCompletenessAlert(IReadOnlyList<Alert> alerts, int expectedLevel)
    {
        var alert = alerts.SingleOrDefault(a => a.Id == "low_completeness_rate");
        if (expectedLevel == -1)
            Assert.IsNull(alert);
        else
        {
            Assert.IsNotNull(alert);
            Assert.AreEqual((AlertLevel)expectedLevel, alert.Level);
            StringAssert.Contains(alert.Message, "coverage.reasons");
            StringAssert.Contains(alert.Message, "solutions");
        }
    }

    private static MetricsSnapshot Snapshot(JobMetrics jobs) => new()
    {
        CollectedAt = 0,
        IndexingLatency = LatencyStats.FromSamples([]),
        QueueDelay = LatencyStats.FromSamples([]),
        QueryLatency = LatencyStats.FromSamples([]),
        Jobs = jobs,
        WorkerCapacity = new WorkerCapacityMetrics { HasCapacity = true, InFlight = 0, Queued = 0 },
        Storage = new StorageMetrics(),
        CacheReuse = new CacheReuseMetrics()
    };
}
