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
    public static string Render(MetricsSnapshot s)
    {
        var sb = new StringBuilder();

        Gauge(sb, "sextant_indexing_latency_p50_ms", "Indexing latency p50 (ms).", s.IndexingLatency.P50Ms);
        Gauge(sb, "sextant_indexing_latency_p95_ms", "Indexing latency p95 (ms).", s.IndexingLatency.P95Ms);
        Gauge(sb, "sextant_queue_delay_p50_ms", "Queue delay p50 (ms).", s.QueueDelay.P50Ms);
        Gauge(sb, "sextant_queue_delay_p95_ms", "Queue delay p95 (ms).", s.QueueDelay.P95Ms);
        Gauge(sb, "sextant_query_latency_p50_ms", "Query latency p50 (ms).", s.QueryLatency.P50Ms);
        Gauge(sb, "sextant_query_latency_p95_ms", "Query latency p95 (ms).", s.QueryLatency.P95Ms);

        Gauge(sb, "sextant_jobs_queued", "Jobs queued.", s.Jobs.Queued);
        Gauge(sb, "sextant_jobs_running", "Jobs running.", s.Jobs.Running);
        Gauge(sb, "sextant_jobs_complete", "Jobs complete.", s.Jobs.Complete);
        Gauge(sb, "sextant_jobs_partial", "Jobs partial.", s.Jobs.Partial);
        Gauge(sb, "sextant_jobs_failed", "Jobs failed.", s.Jobs.Failed);
        Gauge(sb, "sextant_jobs_unsupported", "Jobs unsupported.", s.Jobs.Unsupported);
        Gauge(sb, "sextant_job_success_rate", "Terminal-job success rate.", s.Jobs.SuccessRate);
        Gauge(sb, "sextant_job_completeness_rate", "Terminal-job completeness rate.", s.Jobs.CompletenessRate);

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
        Gauge(sb, "sextant_alerts_critical", "Active critical alerts.",
            s.Alerts.Count(a => a.Level == AlertLevel.Critical));

        // Per-repository cost attribution (operator-only; control-plane gated).
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

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
