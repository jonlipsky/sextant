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

    /// <summary>
    /// Jobs skipped while queued because a newer commit of the same branch superseded them (issue #273). Settled but not
    /// terminal: they are not outcomes, so they count toward neither <see cref="Terminal"/> nor the rates.
    /// </summary>
    public long Coalesced { get; init; }

    /// <summary>Total jobs that reached a terminal state.</summary>
    public long Terminal => Complete + Partial + Failed + Unsupported + Cancelled;

    /// <summary>Fraction of terminal jobs that published a snapshot (complete or partial). 1.0 when none yet.</summary>
    public double SuccessRate => Terminal == 0 ? 1.0 : (double)(Complete + Partial) / Terminal;

    /// <summary>Fraction of terminal jobs that were fully complete (not partial/failed). 1.0 when none yet.</summary>
    public double CompletenessRate => Terminal == 0 ? 1.0 : (double)Complete / Terminal;
}

/// <summary>Terminal job outcomes completed inside an explicitly bounded rolling window.</summary>
public sealed record RecentJobMetrics
{
    public long WindowStartUnixMs { get; init; }
    public long WindowEndUnixMs { get; init; }
    public long MinimumSamples { get; init; }
    public long Complete { get; init; }
    public long Partial { get; init; }
    public long Failed { get; init; }
    public long Unsupported { get; init; }
    public long Cancelled { get; init; }

    public long SampleCount => Complete + Partial + Failed + Unsupported + Cancelled;
    public bool RatesAssessed => SampleCount >= Math.Max(1, MinimumSamples);
    public double? SuccessRate => SampleCount == 0 ? null : (double)(Complete + Partial) / SampleCount;
    public double? CompletenessRate => SampleCount == 0 ? null : (double)Complete / SampleCount;
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

/// <summary>
/// The service-owned maintenance loop (issue #299): idle-branch retirement per tick and the daily scheduled retention
/// pass. Counters are since this service instance started; the <c>Last*</c> fields describe the latest pass (0/null
/// before the first).
/// </summary>
public sealed record MaintenanceMetrics
{
    /// <summary>Ticks whose idle-branch pass completed (including passes with the rule disabled).</summary>
    public long Ticks { get; init; }

    /// <summary>Ticks that failed with an exception (logged; the loop continues).</summary>
    public long Failures { get; init; }

    public long IdleBranchesRetiredTotal { get; init; }

    /// <summary>Idle-branch retirements that applied but retired nothing (pointer moved, work appeared, gone), since start (issue #333).</summary>
    public long IdleBranchRetiresSkippedTotal { get; init; }
    public long LastIdleBranchesRetired { get; init; }
    public long LastIdlePassAtUnixMs { get; init; }
    public double LastIdlePassDurationMs { get; init; }

    /// <summary>Repositories whose upstream branches were listed (successfully or not) since start (issue #338).</summary>
    public long UpstreamRepositoriesCheckedTotal { get; init; }

    /// <summary>Branch pointers retired because their branch no longer exists upstream, since start.</summary>
    public long UpstreamBranchesRetiredTotal { get; init; }

    /// <summary>Upstream-branch retirements that applied but retired nothing (pointer moved, work appeared, gone), since start.</summary>
    public long UpstreamBranchRetiresSkippedTotal { get; init; }

    /// <summary>Upstream listings that failed or were not trustworthy (timeout, error, unparseable, empty), since start.</summary>
    public long UpstreamListingFailuresTotal { get; init; }
    public long LastUpstreamPassAtUnixMs { get; init; }
    public double LastUpstreamPassDurationMs { get; init; }

    /// <summary>Inactive repositories whose checkout and caches were deleted since start (issue #291).</summary>
    public long RepositoriesReclaimedTotal { get; init; }

    /// <summary>Bytes freed by those deletions since start (measured before deleting).</summary>
    public long RepositoryBytesReclaimedTotal { get; init; }

    /// <summary>Repository reclaims that failed (logged; retried by a later tick) since start.</summary>
    public long RepositoryReclaimFailuresTotal { get; init; }

    public long LastRepositoriesReclaimed { get; init; }
    public long LastRepositoryBytesReclaimed { get; init; }
    public long LastReclaimPassAtUnixMs { get; init; }
    public double LastReclaimPassDurationMs { get; init; }

    /// <summary>Job-memory reclaims that ran (after a job or at the start of the next one) since start (issue #325).</summary>
    public long MemoryReclaimsRun { get; init; }

    /// <summary>Job-memory reclaims that did not run, by reason (<see cref="MemoryReclaimSkipReasons"/>); absent = 0.</summary>
    public IReadOnlyDictionary<string, long> MemoryReclaimsSkipped { get; init; } = new Dictionary<string, long>();

    /// <summary>Reclaims that ran, by phase (<see cref="MemoryReclaimPhases"/>); absent = 0.</summary>
    public IReadOnlyDictionary<string, long> MemoryReclaimsRunByPhase { get; init; } = new Dictionary<string, long>();

    /// <summary>Reclaims that did not run, keyed <c>phase/reason</c> (<see cref="MemoryReclaimPhases.Key"/>); absent = 0.</summary>
    public IReadOnlyDictionary<string, long> MemoryReclaimsSkippedByPhase { get; init; } = new Dictionary<string, long>();

    public long ScheduledRetentionRuns { get; init; }

    /// <summary>Queued jobs with no live turn that startup settled as coalesced (superseded), since start (issue #327).</summary>
    public long StartupJobsSettledCoalesced { get; init; }

    /// <summary>Queued jobs with no live turn that startup settled as cancelled, since start (issue #327).</summary>
    public long StartupJobsSettledCancelled { get; init; }

    /// <summary>Always 0 since issue #333: a due pass no longer skips while indexing holds the writer (it waits for a priority turn). Kept so dashboards keep resolving.</summary>
    public long ScheduledRetentionSkippedBusy { get; init; }

    /// <summary>Maintenance writes that took a priority turn on the writer gate (issue #333).</summary>
    public long MaintenanceWriterTurns { get; init; }

    /// <summary>Total milliseconds maintenance writes waited for their priority turn (issue #333).</summary>
    public double MaintenanceWriterWaitMsTotal { get; init; }

    /// <summary>Longest single wait of a maintenance write for its priority turn (issue #333).</summary>
    public double MaintenanceWriterWaitMsMax { get; init; }

    /// <summary>Maintenance steps deferred to the next tick because the writer was not available in time, by step (issue #333).</summary>
    public IReadOnlyDictionary<string, long> MaintenanceDeferrals { get; init; } = new Dictionary<string, long>();

    /// <summary>Executed retention passes whose closing audit row was skipped because the writer stayed busy (issue #321).</summary>
    public long RetentionAuditSkipped { get; init; }

    public long LastScheduledRetentionAtUnixMs { get; init; }
    public double LastScheduledRetentionDurationMs { get; init; }
    public long LastScheduledRetentionSnapshotsDeleted { get; init; }
    public bool LastScheduledRetentionMoreRemaining { get; init; }
    public string? LastScheduledRetentionStopReason { get; init; }
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
/// Whether the service's previous runs ended without releasing the writer lease (issue #320): a crash or kill such as
/// the kernel OOM killer. <see cref="UncleanShutdownsTotal"/> is durable and monotonic (alert on its increase);
/// <see cref="PreviousRunUnclean"/> is 1 for the current run when its predecessor never shut down cleanly.
/// </summary>
public sealed record CrashMetrics
{
    /// <summary>Runs that ended with no recorded reason (crash or kill). Abandoned and failed-release runs are counted separately.</summary>
    public long UncleanShutdownsTotal { get; init; }
    public bool PreviousRunUnclean { get; init; }
    public long? PreviousRunLastHeartbeatUnixMs { get; init; }

    /// <summary>Why the previous run ended when not clean: unclean | abandoned | release_failed.</summary>
    public string? PreviousRunEndReason { get; init; }
    public long AbandonedRunsTotal { get; init; }
    public long ReleaseFailuresTotal { get; init; }
    public long StaleLeaseTakeoversTotal { get; init; }
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

    /// <summary>
    /// Wall-clock time per job phase (issue #267) over the most recent jobs that recorded timings, keyed by phase name
    /// (<c>restore</c>, <c>load</c>, <c>extracting_symbols</c>, …) in <c>IndexPhaseNames.ExecutionOrder</c>; a phase that
    /// list does not know comes last.
    /// </summary>
    public IReadOnlyDictionary<string, LatencyStats> PhaseLatency { get; init; } = new Dictionary<string, LatencyStats>();

    public required LatencyStats QueryLatency { get; init; }
    public required JobMetrics Jobs { get; init; }
    public RecentJobMetrics RecentJobs { get; init; } = new();
    public required WorkerCapacityMetrics WorkerCapacity { get; init; }
    public required StorageMetrics Storage { get; init; }
    public required CacheReuseMetrics CacheReuse { get; init; }
    public long QueryErrors { get; init; }

    /// <summary>The maintenance loop's idle-branch and scheduled-retention record (issue #299).</summary>
    public MaintenanceMetrics Maintenance { get; init; } = new();

    /// <summary>Crash visibility from the catalog's writer run state (issue #320).</summary>
    public CrashMetrics Crash { get; init; } = new();

    public IReadOnlyList<Alert> Alerts { get; init; } = [];
    public IReadOnlyList<Sextant.Store.AuditCostAttribution> CostByRepository { get; init; } = [];
}
