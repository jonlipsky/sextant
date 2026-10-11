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

    // Issue #299: the maintenance loop's per-tick record, replaced whole under _maintenanceLock.
    private readonly Lock _maintenanceLock = new();
    private MaintenanceMetrics _maintenance = new();

    /// <summary>Records one maintenance tick's idle-branch retire pass (issue #299).</summary>
    public void RecordIdleBranchRetirePass(int retired, long atUnixMs, double durationMs)
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with
            {
                Ticks = _maintenance.Ticks + 1,
                IdleBranchesRetiredTotal = _maintenance.IdleBranchesRetiredTotal + retired,
                LastIdleBranchesRetired = retired,
                LastIdlePassAtUnixMs = atUnixMs,
                LastIdlePassDurationMs = durationMs
            };
    }

    /// <summary>Records a scheduled retention pass that ran (issue #299).</summary>
    public void RecordScheduledRetention(Sextant.Store.RetentionReport report, long atUnixMs, double durationMs)
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with
            {
                ScheduledRetentionRuns = _maintenance.ScheduledRetentionRuns + 1,
                LastScheduledRetentionAtUnixMs = atUnixMs,
                LastScheduledRetentionDurationMs = durationMs,
                LastScheduledRetentionSnapshotsDeleted = report.SnapshotsDeleted,
                LastScheduledRetentionMoreRemaining = report.MoreRemaining,
                LastScheduledRetentionStopReason = report.StopReason
            };
    }

    /// <summary>Records a due scheduled retention pass skipped because indexing held the writer (issue #299).</summary>
    public void RecordScheduledRetentionSkippedBusy()
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with { ScheduledRetentionSkippedBusy = _maintenance.ScheduledRetentionSkippedBusy + 1 };
    }

    /// <summary>Records a maintenance tick that failed with an exception (issue #299); the loop carries on.</summary>
    public void RecordMaintenanceFailure()
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with { Failures = _maintenance.Failures + 1 };
    }

    /// <summary>The maintenance loop's counters and last-pass record (issue #299).</summary>
    public MaintenanceMetrics Maintenance
    {
        get
        {
            lock (_maintenanceLock)
                return _maintenance;
        }
    }

    public long EnsureTotal => Interlocked.Read(ref _ensureTotal);
    public long EnsureAttached => Interlocked.Read(ref _ensureAttached);
    public long PageCacheHits => Interlocked.Read(ref _pageCacheHits);
    public long PageCacheMisses => Interlocked.Read(ref _pageCacheMisses);
    public long QueryErrors => Interlocked.Read(ref _queryErrors);
}
