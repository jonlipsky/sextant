namespace Sextant.Service.Observability;

/// <summary>
/// The service's live, in-process metric counters (criterion 5). It holds the signals that can only be
/// observed as events happen — query latency, ensure idempotent-reuse, and snapshot page-cache reuse —
/// while the durable, catalog-derived signals (indexing latency, queue delay, success/completeness,
/// storage) are computed at collection time from the job ledger + on-disk sizes by
/// <see cref="MetricsCollector"/>. One instance lives on the running <see cref="SnapshotService"/> and is
/// shared with the host's query-timing middleware, so both write to the same counters. All members are
/// thread-safe (interlocked counters + a locked reservoir). It is inert and free on the zero-config local
/// path, which never constructs the service.
/// </summary>
public sealed class ServiceMetrics
{
    private long _ensureTotal;
    private long _ensureAttached;
    private long _pageCacheHits;
    private long _pageCacheMisses;
    private long _queryErrors;

    /// <summary>Query-plane request latency (HTTP MCP + snapshot paging), recorded by the host middleware.</summary>
    public LatencyHistogram QueryLatency { get; } = new();

    /// <summary>Records one query-plane request's wall-clock latency and whether it failed (5xx).</summary>
    public void RecordQuery(double milliseconds, bool failed)
    {
        QueryLatency.Record(milliseconds);
        if (failed)
            Interlocked.Increment(ref _queryErrors);
    }

    /// <summary>
    /// Records the outcome of an ensure-snapshot request: whether it ATTACHED to an existing durable job
    /// (idempotent reuse — criterion 1) rather than starting fresh work. The attach rate is a cache-reuse
    /// signal (criterion 5).
    /// </summary>
    public void RecordEnsure(bool attached)
    {
        Interlocked.Increment(ref _ensureTotal);
        if (attached)
            Interlocked.Increment(ref _ensureAttached);
    }

    /// <summary>Records a snapshot-page federation cache hit or miss (cache-reuse signal).</summary>
    public void RecordPageCache(bool hit)
    {
        if (hit)
            Interlocked.Increment(ref _pageCacheHits);
        else
            Interlocked.Increment(ref _pageCacheMisses);
    }

    public long EnsureTotal => Interlocked.Read(ref _ensureTotal);
    public long EnsureAttached => Interlocked.Read(ref _ensureAttached);
    public long PageCacheHits => Interlocked.Read(ref _pageCacheHits);
    public long PageCacheMisses => Interlocked.Read(ref _pageCacheMisses);
    public long QueryErrors => Interlocked.Read(ref _queryErrors);
}
