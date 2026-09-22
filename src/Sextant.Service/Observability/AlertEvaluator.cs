namespace Sextant.Service.Observability;

/// <summary>Thresholds the <see cref="AlertEvaluator"/> compares a <see cref="MetricsSnapshot"/> against.</summary>
public sealed record AlertThresholds
{
    /// <summary>Queue delay p95 (ms) above which a warning/critical queue-backup alert fires.</summary>
    public double QueueDelayP95WarnMs { get; init; } = 60_000;
    public double QueueDelayP95CritMs { get; init; } = 300_000;

    /// <summary>Terminal-job success rate below which a warning/critical low-success alert fires.</summary>
    public double SuccessRateWarn { get; init; } = 0.90;
    public double SuccessRateCrit { get; init; } = 0.50;

    /// <summary>Queued-job depth (with no worker capacity) above which a worker-exhaustion alert fires.</summary>
    public long WorkerExhaustionQueueWarn { get; init; } = 1;

    /// <summary>Total storage (bytes) above which a warning/critical storage-pressure alert fires. 0 disables.</summary>
    public long StorageWarnBytes { get; init; } = 0;
    public long StorageCritBytes { get; init; } = 0;

    /// <summary>The minimum terminal-job sample size before the success-rate alert is meaningful.</summary>
    public long SuccessRateMinSamples { get; init; } = 5;

    public static readonly AlertThresholds Default = new();
}

/// <summary>
/// Evaluates operational alerts over a <see cref="MetricsSnapshot"/> (criterion 5: alerts). Pure and
/// deterministic — no I/O — so the same snapshot always yields the same alerts, which is what the pilot
/// gate and the tests rely on. Fires on queue backup, low success/completeness, worker exhaustion, and
/// (when configured) storage pressure. Returns an empty list when everything is healthy.
/// </summary>
public static class AlertEvaluator
{
    public static IReadOnlyList<Alert> Evaluate(MetricsSnapshot snapshot, AlertThresholds? thresholds = null)
    {
        var t = thresholds ?? AlertThresholds.Default;
        var alerts = new List<Alert>();

        // Queue backup: work is waiting far too long before a worker picks it up.
        var queueP95 = snapshot.QueueDelay.P95Ms;
        if (snapshot.QueueDelay.Count > 0)
        {
            if (queueP95 >= t.QueueDelayP95CritMs)
                alerts.Add(new Alert
                {
                    Id = "queue_delay_high",
                    Level = AlertLevel.Critical,
                    Message = $"Queue delay p95 {queueP95:F0}ms exceeds critical threshold {t.QueueDelayP95CritMs:F0}ms."
                });
            else if (queueP95 >= t.QueueDelayP95WarnMs)
                alerts.Add(new Alert
                {
                    Id = "queue_delay_high",
                    Level = AlertLevel.Warning,
                    Message = $"Queue delay p95 {queueP95:F0}ms exceeds warning threshold {t.QueueDelayP95WarnMs:F0}ms."
                });
        }

        // Low success/completeness: too many terminal jobs failed/unsupported.
        if (snapshot.Jobs.Terminal >= t.SuccessRateMinSamples)
        {
            var success = snapshot.Jobs.SuccessRate;
            if (success < t.SuccessRateCrit)
                alerts.Add(new Alert
                {
                    Id = "low_success_rate",
                    Level = AlertLevel.Critical,
                    Message = $"Job success rate {success:P0} is below critical threshold {t.SuccessRateCrit:P0}."
                });
            else if (success < t.SuccessRateWarn)
                alerts.Add(new Alert
                {
                    Id = "low_success_rate",
                    Level = AlertLevel.Warning,
                    Message = $"Job success rate {success:P0} is below warning threshold {t.SuccessRateWarn:P0}."
                });
        }

        // Worker exhaustion: jobs are queued but this node has no worker capacity to run them.
        if (!snapshot.WorkerCapacity.HasCapacity &&
            snapshot.WorkerCapacity.Queued >= t.WorkerExhaustionQueueWarn)
        {
            alerts.Add(new Alert
            {
                Id = "worker_exhaustion",
                Level = AlertLevel.Critical,
                Message = $"{snapshot.WorkerCapacity.Queued} job(s) queued but this node has no worker capacity."
            });
        }

        // Storage pressure (only when a budget is configured).
        var total = snapshot.Storage.TotalBytes;
        if (t.StorageCritBytes > 0 && total >= t.StorageCritBytes)
            alerts.Add(new Alert
            {
                Id = "storage_pressure",
                Level = AlertLevel.Critical,
                Message = $"Storage {total} bytes exceeds critical threshold {t.StorageCritBytes} bytes."
            });
        else if (t.StorageWarnBytes > 0 && total >= t.StorageWarnBytes)
            alerts.Add(new Alert
            {
                Id = "storage_pressure",
                Level = AlertLevel.Warning,
                Message = $"Storage {total} bytes exceeds warning threshold {t.StorageWarnBytes} bytes."
            });

        return alerts;
    }
}
