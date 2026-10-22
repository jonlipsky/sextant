using System.Globalization;
using System.Text;

namespace Sextant.Service.Observability;

/// <summary>
/// Renders a <see cref="MetricsSnapshot"/> as Prometheus text exposition (version 0.0.4) so an operator's
/// scraper / dashboard can ingest the service's signals directly (criterion 5). Emitted ONLY on the
/// control plane. Deliberately dependency-free — a small, explicit text writer rather than a metrics
/// library — matching the green-field "clean metrics surface, not a heavyweight dependency" intent.
///
/// Cost attribution is emitted PER REPOSITORY as a label; this is operator-only data (the whole control
/// plane is control-token gated) so it is never a cross-tenant leak on the query plane.
/// </summary>
public static class PrometheusExposition
{
    private static readonly string[] MaintenanceDeferralSteps =
        ["idle_branch_retire", "retired_head_prune", "repository_reclaim", "reclaim_audit", "scheduled_retention"];

    public static string Render(MetricsSnapshot s)
    {
        var sb = new StringBuilder();

        Gauge(sb, "sextant_indexing_latency_p50_ms", "Indexing latency p50 (ms).", s.IndexingLatency.P50Ms);
        Gauge(sb, "sextant_indexing_latency_p95_ms", "Indexing latency p95 (ms).", s.IndexingLatency.P95Ms);
        Gauge(sb, "sextant_queue_delay_p50_ms", "Queue delay p50 (ms).", s.QueueDelay.P50Ms);
        Gauge(sb, "sextant_queue_delay_p95_ms", "Queue delay p95 (ms).", s.QueueDelay.P95Ms);
        // Per-phase job wall time (issue #267), one series per phase.
        if (s.PhaseLatency.Count > 0)
        {
            foreach (var (metric, help, pick) in new (string, string, Func<LatencyStats, double>)[]
                     {
                         ("sextant_job_phase_p50_ms", "Job phase wall time p50 (ms).", l => l.P50Ms),
                         ("sextant_job_phase_p95_ms", "Job phase wall time p95 (ms).", l => l.P95Ms)
                     })
            {
                sb.Append("# HELP ").Append(metric).Append(' ').Append(help).Append('\n');
                sb.Append("# TYPE ").Append(metric).Append(" gauge\n");
                foreach (var (phase, stats) in s.PhaseLatency)
                    sb.Append(metric).Append("{phase=\"").Append(Escape(phase)).Append("\"} ")
                      .Append(pick(stats).ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        Gauge(sb, "sextant_query_latency_p50_ms", "Query latency p50 (ms).", s.QueryLatency.P50Ms);
        Gauge(sb, "sextant_query_latency_p95_ms", "Query latency p95 (ms).", s.QueryLatency.P95Ms);

        Gauge(sb, "sextant_jobs_queued", "Jobs queued.", s.Jobs.Queued);
        Gauge(sb, "sextant_jobs_running", "Jobs running.", s.Jobs.Running);
        Gauge(sb, "sextant_jobs_complete", "Jobs complete.", s.Jobs.Complete);
        Gauge(sb, "sextant_jobs_partial", "Jobs partial.", s.Jobs.Partial);
        Gauge(sb, "sextant_jobs_failed", "Jobs failed.", s.Jobs.Failed);
        Gauge(sb, "sextant_jobs_unsupported", "Jobs unsupported.", s.Jobs.Unsupported);
        Gauge(sb, "sextant_jobs_coalesced", "Jobs skipped while queued because a newer commit of their branch superseded them.", s.Jobs.Coalesced);
        Gauge(sb, "sextant_job_success_rate", "Terminal-job success rate.", s.Jobs.SuccessRate);
        Gauge(sb, "sextant_job_completeness_rate", "Terminal-job completeness rate.", s.Jobs.CompletenessRate);
        Gauge(sb, "sextant_recent_jobs_window_start_unix_ms", "Inclusive start of the recent terminal-job window (Unix ms).",
            s.RecentJobs.WindowStartUnixMs);
        Gauge(sb, "sextant_recent_jobs_window_end_unix_ms", "Exclusive end of the recent terminal-job window (Unix ms).",
            s.RecentJobs.WindowEndUnixMs);
        Gauge(sb, "sextant_recent_jobs_sample_count", "Terminal jobs completed in the recent window.",
            s.RecentJobs.SampleCount);
        Gauge(sb, "sextant_recent_jobs_minimum_samples", "Minimum sample count required to assess recent job rates.",
            s.RecentJobs.MinimumSamples);
        Gauge(sb, "sextant_recent_jobs_rates_assessed", "Whether recent terminal-job rates meet the sample minimum (1/0).",
            s.RecentJobs.RatesAssessed ? 1 : 0);
        if (s.RecentJobs.SuccessRate is double recentSuccessRate)
            Gauge(sb, "sextant_recent_job_success_rate", "Recent terminal-job success rate.", recentSuccessRate);
        if (s.RecentJobs.CompletenessRate is double recentCompletenessRate)
            Gauge(sb, "sextant_recent_job_completeness_rate", "Recent terminal-job completeness rate.", recentCompletenessRate);

        Gauge(sb, "sextant_worker_capacity", "Whether this node has worker capacity (1/0).",
            s.WorkerCapacity.HasCapacity ? 1 : 0);
        Gauge(sb, "sextant_worker_in_flight", "In-flight jobs on this node.", s.WorkerCapacity.InFlight);

        Gauge(sb, "sextant_storage_catalog_bytes", "Catalog on-disk bytes.", s.Storage.CatalogBytes);
        Gauge(sb, "sextant_storage_artifact_bytes", "Artifact volume bytes.", s.Storage.ArtifactBytes);
        Gauge(sb, "sextant_storage_cache_bytes", "Cache volume bytes.", s.Storage.CacheBytes);
        Gauge(sb, "sextant_storage_checkout_bytes", "Checkout volume bytes.", s.Storage.CheckoutBytes);
        Gauge(sb, "sextant_storage_total_bytes", "Total durable storage bytes.", s.Storage.TotalBytes);

        Gauge(sb, "sextant_ensure_reuse_rate", "Fraction of ensure requests that reused prior work.",
            s.CacheReuse.EnsureReuseRate);
        Gauge(sb, "sextant_page_cache_hit_rate", "Snapshot-page federation cache hit rate.",
            s.CacheReuse.PageCacheHitRate);
        Gauge(sb, "sextant_query_errors", "Query-plane 5xx count.", s.QueryErrors);

        Gauge(sb, "sextant_alerts_active", "Active alerts.", s.Alerts.Count);

        // Issue #342: catalog write-lock holds and waits.
        Gauge(sb, "sextant_write_lock_longest_hold_ms", "Longest catalog write-lock hold since start, in milliseconds.",
            s.WriteLock.LongestHoldMs);
        Counter(sb, "sextant_write_lock_holds_over_threshold_total", "Write-lock holds over the warning threshold since start.",
            s.WriteLock.HoldsOverThreshold);
        Gauge(sb, "sextant_write_lock_longest_wait_ms", "Longest wait to acquire the catalog write lock since start, in milliseconds.",
            s.WriteLock.LongestWaitMs);
        Counter(sb, "sextant_write_lock_yields_total", "Times an index write session held off its next batch for a waiting writer (issue #344).",
            s.WriteLock.YieldsToWaiters);
        Counter(sb, "sextant_write_lock_yield_timeouts_total", "Yields that reached their cap without a waiter acquiring the lock.",
            s.WriteLock.YieldTimeouts);
        Gauge(sb, "sextant_write_lock_waiting_writers", "In-process writers waiting for the catalog write lock now.",
            s.WriteLock.WaitingWriters);
        Gauge(sb, "sextant_alerts_critical", "Active critical alerts.",
            s.Alerts.Count(a => a.Level == AlertLevel.Critical));

        // Issue #299: the maintenance loop (idle-branch retirement + scheduled retention).
        Gauge(sb, "sextant_maintenance_ticks", "Maintenance ticks completed since start.", s.Maintenance.Ticks);
        Gauge(sb, "sextant_maintenance_failures", "Maintenance ticks that failed since start.", s.Maintenance.Failures);
        Gauge(sb, "sextant_idle_branches_retired_total", "Idle non-default branch pointers retired since start.",
            s.Maintenance.IdleBranchesRetiredTotal);
        Gauge(sb, "sextant_idle_branch_retires_skipped_total", "Idle-branch retirements that applied but retired nothing since start.",
            s.Maintenance.IdleBranchRetiresSkippedTotal);
        Gauge(sb, "sextant_idle_branches_retired_last_pass", "Idle branch pointers retired by the latest pass.",
            s.Maintenance.LastIdleBranchesRetired);
        Gauge(sb, "sextant_upstream_repositories_checked_total", "Repositories whose upstream branches were listed since start.",
            s.Maintenance.UpstreamRepositoriesCheckedTotal);
        Gauge(sb, "sextant_upstream_branches_retired_total", "Branch pointers retired because the branch no longer exists upstream since start.",
            s.Maintenance.UpstreamBranchesRetiredTotal);
        Gauge(sb, "sextant_upstream_branch_retires_skipped_total", "Upstream-branch retirements that applied but retired nothing since start.",
            s.Maintenance.UpstreamBranchRetiresSkippedTotal);
        Gauge(sb, "sextant_upstream_listing_failures_total", "Upstream branch listings that failed or were not trustworthy since start.",
            s.Maintenance.UpstreamListingFailuresTotal);
        Gauge(sb, "sextant_upstream_pass_duration_ms", "Duration of the latest upstream-branch pass in milliseconds.",
            s.Maintenance.LastUpstreamPassDurationMs);
        Gauge(sb, "sextant_repositories_reclaimed_total", "Inactive repositories whose checkout and caches were deleted since start.",
            s.Maintenance.RepositoriesReclaimedTotal);
        Gauge(sb, "sextant_repository_bytes_reclaimed_total", "Bytes freed by repository reclaims since start.",
            s.Maintenance.RepositoryBytesReclaimedTotal);
        Gauge(sb, "sextant_repository_reclaim_failures_total", "Repository reclaims that failed since start.",
            s.Maintenance.RepositoryReclaimFailuresTotal);
        sb.Append("# HELP sextant_startup_jobs_settled_total Queued jobs with no live turn settled at startup, by kind.\n");
        sb.Append("# TYPE sextant_startup_jobs_settled_total counter\n");
        sb.Append("sextant_startup_jobs_settled_total{kind=\"coalesced\"} ")
            .Append(s.Maintenance.StartupJobsSettledCoalesced.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("sextant_startup_jobs_settled_total{kind=\"cancelled\"} ")
            .Append(s.Maintenance.StartupJobsSettledCancelled.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# HELP sextant_memory_reclaims_total Job-memory reclaims that ran, by phase (post_job or pre_job).\n");
        sb.Append("# TYPE sextant_memory_reclaims_total counter\n");
        foreach (var phase in MemoryReclaimPhases.All)
            sb.Append("sextant_memory_reclaims_total{phase=\"").Append(phase).Append("\"} ")
                .Append(s.Maintenance.MemoryReclaimsRunByPhase.GetValueOrDefault(phase).ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# HELP sextant_memory_reclaims_skipped_total Job-memory reclaims that did not run, by phase and reason.\n");
        sb.Append("# TYPE sextant_memory_reclaims_skipped_total counter\n");
        foreach (var phase in MemoryReclaimPhases.All)
            foreach (var reason in MemoryReclaimSkipReasons.All)
                sb.Append("sextant_memory_reclaims_skipped_total{phase=\"").Append(phase).Append("\",reason=\"").Append(reason).Append("\"} ")
                    .Append(s.Maintenance.MemoryReclaimsSkippedByPhase.GetValueOrDefault(MemoryReclaimPhases.Key(phase, reason))
                        .ToString(CultureInfo.InvariantCulture)).Append('\n');
        Gauge(sb, "sextant_scheduled_retention_runs", "Scheduled retention passes run since start.",
            s.Maintenance.ScheduledRetentionRuns);
        Gauge(sb, "sextant_scheduled_retention_skipped_busy", "Always 0 since issue 333 (kept for dashboards): a due pass waits for its priority turn instead of skipping.",
            s.Maintenance.ScheduledRetentionSkippedBusy);
        Counter(sb, "sextant_maintenance_writer_turns_total", "Maintenance writes that took a priority turn on the writer gate.",
            s.Maintenance.MaintenanceWriterTurns);
        Counter(sb, "sextant_maintenance_writer_wait_ms_total", "Milliseconds maintenance writes waited for their priority turn.",
            s.Maintenance.MaintenanceWriterWaitMsTotal);
        Gauge(sb, "sextant_maintenance_writer_wait_ms_max", "Longest wait of a maintenance write for its priority turn (ms).",
            s.Maintenance.MaintenanceWriterWaitMsMax);
        sb.Append("# HELP sextant_maintenance_deferrals_total Maintenance steps deferred to the next tick because the writer was not available in time, by step.\n");
        sb.Append("# TYPE sextant_maintenance_deferrals_total counter\n");
        foreach (var step in MaintenanceDeferralSteps)
            sb.Append("sextant_maintenance_deferrals_total{step=\"").Append(step).Append("\"} ")
                .Append(s.Maintenance.MaintenanceDeferrals.GetValueOrDefault(step).ToString(CultureInfo.InvariantCulture)).Append('\n');
        Counter(sb, "sextant_retention_audit_skipped_total", "Executed retention passes whose closing audit row was skipped.",
            s.Maintenance.RetentionAuditSkipped);
        Gauge(sb, "sextant_scheduled_retention_last_duration_ms", "Duration of the latest scheduled retention pass (ms).",
            s.Maintenance.LastScheduledRetentionDurationMs);

        // Per-repository cost attribution (operator-only; control-plane gated).
        // Issue #320: crash visibility. The total is durable across restarts; alert on its increase.
        Counter(sb, "sextant_service_unclean_shutdowns_total",
            "Service runs that ended with no recorded reason (crash or kill, e.g. OOM), durable across restarts.",
            s.Crash.UncleanShutdownsTotal);
        Counter(sb, "sextant_service_stale_lease_takeovers_total",
            "Writer leases taken over from a provably dead holder instead of waiting for expiry.",
            s.Crash.StaleLeaseTakeoversTotal);
        Gauge(sb, "sextant_service_previous_run_unclean",
            "Whether the run before this one ended with no recorded reason, i.e. a crash or kill (1/0). An abandoned or failed release is not a crash.",
            s.Crash.PreviousRunUnclean ? 1 : 0);
        Counter(sb, "sextant_service_abandoned_runs_total",
            "Runs that abandoned the writer lease because they could not confirm they had stopped writing (shutdown drain timeout).",
            s.Crash.AbandonedRunsTotal);
        Counter(sb, "sextant_service_lease_release_failures_total",
            "Runs that could not release the writer lease at shutdown (database error).",
            s.Crash.ReleaseFailuresTotal);
        if (s.Crash.PreviousRunLastHeartbeatUnixMs is long lastHeartbeat)
            Gauge(sb, "sextant_service_previous_run_last_heartbeat_timestamp_seconds",
                "Last heartbeat (Unix seconds) of the previous run that did not end cleanly.", lastHeartbeat / 1000.0);
        if (s.CostByRepository.Count > 0)
        {
            sb.Append("# HELP sextant_repository_index_ms Worker index milliseconds attributed to a repository.\n");
            sb.Append("# TYPE sextant_repository_index_ms gauge\n");
            foreach (var cost in s.CostByRepository)
                sb.Append("sextant_repository_index_ms{repository=\"")
                  .Append(Escape(cost.RepositoryScope)).Append("\"} ")
                  .Append(cost.IndexMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }

    private static void Gauge(StringBuilder sb, string name, string help, double value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        sb.Append(name).Append(' ').Append(value.ToString("G", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void Counter(StringBuilder sb, string name, string help, double value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        sb.Append(name).Append(' ').Append(value.ToString("G", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
