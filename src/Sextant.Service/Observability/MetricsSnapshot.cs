namespace Sextant.Service.Observability;

/// <summary>Terminal + in-flight job counts and the derived success/completeness rates (criterion 5).</summary>
public sealed record JobMetrics
{
    public long Queued { get; init; }
    public long Running { get; init; }
    public long Complete { get; init; }
    public long Partial { get; init; }
    public long Failed { get; init; }
    public long Unsupported { get; init; }
    public long Cancelled { get; init; }

    /// <summary>Total jobs that reached a terminal state.</summary>
    public long Terminal => Complete + Partial + Failed + Unsupported + Cancelled;

    /// <summary>Fraction of terminal jobs that published a snapshot (complete or partial). 1.0 when none yet.</summary>
    public double SuccessRate => Terminal == 0 ? 1.0 : (double)(Complete + Partial) / Terminal;

    /// <summary>Fraction of terminal jobs that were fully complete (not partial/failed). 1.0 when none yet.</summary>
    public double CompletenessRate => Terminal == 0 ? 1.0 : (double)Complete / Terminal;
}

/// <summary>Worker capacity signal (criterion 5): can this node index, and how many jobs are in flight.</summary>
public sealed record WorkerCapacityMetrics
{
    public required bool HasCapacity { get; init; }
    public required long InFlight { get; init; }
    public required long Queued { get; init; }
}

/// <summary>On-disk storage attributed to the durable volumes + catalog (criterion 5: storage).</summary>
public sealed record StorageMetrics
{
    public long CatalogBytes { get; init; }
    public long ArtifactBytes { get; init; }
    public long CacheBytes { get; init; }
    public long CheckoutBytes { get; init; }
    public long TotalBytes => CatalogBytes + ArtifactBytes + CacheBytes + CheckoutBytes;
}

/// <summary>Cache/idempotent-reuse signals (criterion 5: cache reuse).</summary>
public sealed record CacheReuseMetrics
{
    public long EnsureTotal { get; init; }
    public long EnsureAttached { get; init; }

    /// <summary>Fraction of ensure requests that attached to an existing job instead of starting fresh work.</summary>
    public double EnsureReuseRate => EnsureTotal == 0 ? 0.0 : (double)EnsureAttached / EnsureTotal;

    public long PageCacheHits { get; init; }
    public long PageCacheMisses { get; init; }

    /// <summary>Snapshot-page federation cache hit ratio.</summary>
    public double PageCacheHitRate =>
        PageCacheHits + PageCacheMisses == 0 ? 0.0 : (double)PageCacheHits / (PageCacheHits + PageCacheMisses);
}

/// <summary>An alert severity level.</summary>
public enum AlertLevel
{
    Ok,
    Warning,
    Critical
}

/// <summary>A single evaluated alert (criterion 5: alerts).</summary>
public sealed record Alert
{
    public required string Id { get; init; }
    public required AlertLevel Level { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// A point-in-time observability snapshot of the standalone index service (criterion 5). It carries every
/// signal the dashboards must report — indexing latency, queue delay, success/completeness, worker
/// capacity, storage, cache reuse, and query latency — plus the alerts evaluated over them and per-
/// repository cost attribution. Produced by <see cref="MetricsCollector"/> and rendered by the host as
/// JSON or Prometheus text on the CONTROL plane (never the query plane — criterion-1 leakage guard).
/// </summary>
public sealed record MetricsSnapshot
{
    public required long CollectedAt { get; init; }
    public required LatencyStats IndexingLatency { get; init; }
    public required LatencyStats QueueDelay { get; init; }
    public required LatencyStats QueryLatency { get; init; }
    public required JobMetrics Jobs { get; init; }
    public required WorkerCapacityMetrics WorkerCapacity { get; init; }
    public required StorageMetrics Storage { get; init; }
    public required CacheReuseMetrics CacheReuse { get; init; }
    public long QueryErrors { get; init; }
    public IReadOnlyList<Alert> Alerts { get; init; } = [];
    public IReadOnlyList<Sextant.Store.AuditCostAttribution> CostByRepository { get; init; } = [];
}
