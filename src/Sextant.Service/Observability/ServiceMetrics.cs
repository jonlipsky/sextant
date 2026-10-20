namespace Sextant.Service.Observability;

/// <summary>The reasons a job-memory reclaim did not run (issue #325); the metric label values.</summary>
public static class MemoryReclaimSkipReasons
{
    public const string Busy = "busy";
    public const string BelowThreshold = "below_threshold";
    public const string ShuttingDown = "shutting_down";
    public const string Superseded = "superseded";
    public const string Deferred = "deferred";

    public static readonly IReadOnlyList<string> All = [Busy, BelowThreshold, ShuttingDown, Superseded, Deferred];
}

/// <summary>When a job-memory reclaim was attempted (issue #325); the <c>phase</c> metric label values.</summary>
public static class MemoryReclaimPhases
{
    public const string PostJob = "post_job";
    public const string PreJob = "pre_job";

    public static readonly IReadOnlyList<string> All = [PostJob, PreJob];

    /// <summary>The key of the per-phase dictionaries: <c>phase</c>, or <c>phase/reason</c> for a skip.</summary>
    public static string Key(string phase, string reason) => phase + "/" + reason;
}

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
                LastIdleBranchesRetired = retired,
                LastIdlePassAtUnixMs = atUnixMs,
                LastIdlePassDurationMs = durationMs
            };
    }

    /// <summary>
    /// Records one applied idle-branch retirement (issue #333), when it applies: a retirement the pass stopped waiting for
    /// still counts, instead of vanishing from the totals.
    /// </summary>
    public void RecordIdleBranchRetireApplied(bool retired)
    {
        lock (_maintenanceLock)
            _maintenance = retired
                ? _maintenance with { IdleBranchesRetiredTotal = _maintenance.IdleBranchesRetiredTotal + 1 }
                : _maintenance with { IdleBranchRetiresSkippedTotal = _maintenance.IdleBranchRetiresSkippedTotal + 1 };
    }

    /// <summary>Records one maintenance tick's upstream-branch reconcile pass (issue #338).</summary>
    public void RecordUpstreamBranchPass(int repositoriesChecked, int listingFailures, long atUnixMs, double durationMs)
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with
            {
                UpstreamRepositoriesCheckedTotal = _maintenance.UpstreamRepositoriesCheckedTotal + repositoriesChecked,
                UpstreamListingFailuresTotal = _maintenance.UpstreamListingFailuresTotal + listingFailures,
                LastUpstreamPassAtUnixMs = atUnixMs,
                LastUpstreamPassDurationMs = durationMs
            };
    }

    /// <summary>Records one applied upstream-branch retirement (issue #338), when it applies.</summary>
    public void RecordUpstreamBranchRetireApplied(bool retired)
    {
        lock (_maintenanceLock)
            _maintenance = retired
                ? _maintenance with { UpstreamBranchesRetiredTotal = _maintenance.UpstreamBranchesRetiredTotal + 1 }
                : _maintenance with { UpstreamBranchRetiresSkippedTotal = _maintenance.UpstreamBranchRetiresSkippedTotal + 1 };
    }

    /// <summary>Records one maintenance tick's repository reclaim pass (issue #291).</summary>
    public void RecordRepositoryReclaimPass(int reclaimed, long bytes, int failed, long atUnixMs, double durationMs)
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with
            {
                RepositoriesReclaimedTotal = _maintenance.RepositoriesReclaimedTotal + reclaimed,
                RepositoryBytesReclaimedTotal = _maintenance.RepositoryBytesReclaimedTotal + bytes,
                RepositoryReclaimFailuresTotal = _maintenance.RepositoryReclaimFailuresTotal + failed,
                LastRepositoriesReclaimed = reclaimed,
                LastRepositoryBytesReclaimed = bytes,
                LastReclaimPassAtUnixMs = atUnixMs,
                LastReclaimPassDurationMs = durationMs
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

    /// <summary>Records how long a maintenance write waited for its priority turn on the writer gate (issue #333).</summary>
    public void RecordMaintenanceWriterWait(double waitMs)
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with
            {
                MaintenanceWriterTurns = _maintenance.MaintenanceWriterTurns + 1,
                MaintenanceWriterWaitMsTotal = _maintenance.MaintenanceWriterWaitMsTotal + waitMs,
                MaintenanceWriterWaitMsMax = Math.Max(_maintenance.MaintenanceWriterWaitMsMax, waitMs)
            };
    }

    /// <summary>Records a maintenance step deferred to the next tick (issue #333).</summary>
    public void RecordMaintenanceDeferral(string step)
    {
        lock (_maintenanceLock)
        {
            var byStep = new Dictionary<string, long>(_maintenance.MaintenanceDeferrals);
            byStep[step] = byStep.GetValueOrDefault(step) + 1;
            _maintenance = _maintenance with { MaintenanceDeferrals = byStep };
        }
    }

    /// <summary>Records a due scheduled retention pass skipped because indexing held the writer (issue #299).</summary>
    public void RecordScheduledRetentionSkippedBusy()
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with { ScheduledRetentionSkippedBusy = _maintenance.ScheduledRetentionSkippedBusy + 1 };
    }

    /// <summary>Records a job-memory reclaim that ran, after a job or before the next one (issue #325).</summary>
    public void RecordMemoryReclaimRun(string phase)
    {
        lock (_maintenanceLock)
        {
            var byPhase = new Dictionary<string, long>(_maintenance.MemoryReclaimsRunByPhase);
            byPhase[phase] = byPhase.GetValueOrDefault(phase) + 1;
            _maintenance = _maintenance with
            {
                MemoryReclaimsRun = _maintenance.MemoryReclaimsRun + 1, MemoryReclaimsRunByPhase = byPhase
            };
        }
    }

    /// <summary>
    /// Records a job-memory reclaim that did not run, by one of <see cref="MemoryReclaimSkipReasons"/> and the
    /// <see cref="MemoryReclaimPhases"/> it was attempted in (issue #325).
    /// </summary>
    public void RecordMemoryReclaimSkipped(string reason, string phase)
    {
        lock (_maintenanceLock)
        {
            var skipped = new Dictionary<string, long>(_maintenance.MemoryReclaimsSkipped);
            skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
            var byPhase = new Dictionary<string, long>(_maintenance.MemoryReclaimsSkippedByPhase);
            var key = MemoryReclaimPhases.Key(phase, reason);
            byPhase[key] = byPhase.GetValueOrDefault(key) + 1;
            _maintenance = _maintenance with { MemoryReclaimsSkipped = skipped, MemoryReclaimsSkippedByPhase = byPhase };
        }
    }

    /// <summary>Records an executed retention pass whose closing audit row was skipped (issue #321).</summary>
    public void RecordRetentionAuditSkipped()
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with { RetentionAuditSkipped = _maintenance.RetentionAuditSkipped + 1 };
    }

    /// <summary>Records the orphaned queued jobs settled at startup (issue #327).</summary>
    public void RecordStartupJobsSettled(int coalesced, int cancelled)
    {
        lock (_maintenanceLock)
            _maintenance = _maintenance with
            {
                StartupJobsSettledCoalesced = _maintenance.StartupJobsSettledCoalesced + coalesced,
                StartupJobsSettledCancelled = _maintenance.StartupJobsSettledCancelled + cancelled
            };
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
