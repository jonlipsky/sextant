using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Observability;

/// <summary>
/// Builds a <see cref="MetricsSnapshot"/> from the durable catalog + on-disk volumes + the live in-process
/// <see cref="ServiceMetrics"/> counters (criterion 5). The catalog-derived signals (indexing latency,
/// queue delay, success/completeness) are computed from the <c>snapshot_jobs</c> ledger, storage from the
/// volume + catalog file sizes, cost attribution from the audit log, and query latency + cache reuse from
/// the live counters. It reads through the supplied connection ONLY — a caller passes an independent read
/// connection so collecting metrics never blocks the writer or a running index.
/// </summary>
public sealed class MetricsCollector(
    SqliteConnection connection,
    ServicePaths paths,
    string catalogDbPath,
    ServiceMetrics metrics,
    bool hasWorkerCapacity,
    TimeProvider? timeProvider = null)
{
    // Bound the latency window so a very long-lived service never scans an unbounded job history.
    private const int LatencyWindow = 1000;

    // Per-phase latency parses each job's stored timings document, so it reads a smaller window (issue #267).
    private const int PhaseLatencyWindow = 200;
    private static readonly long RecentWindowMs = (long)TimeSpan.FromHours(24).TotalMilliseconds;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public MetricsSnapshot Collect(AlertThresholds? thresholds = null)
    {
        var collectedAt = _time.GetUtcNow();
        var windowEnd = collectedAt.ToUnixTimeMilliseconds();
        var windowStart = windowEnd - RecentWindowMs;
        var jobs = CollectJobs();
        var recentJobs = CollectRecentJobs(windowStart, windowEnd, thresholds?.SuccessRateMinSamples ??
            AlertThresholds.Default.SuccessRateMinSamples);
        var (indexing, queue) = CollectLatencies();
        var storage = CollectStorage();
        var cost = CollectCost();

        var snapshot = new MetricsSnapshot
        {
            CollectedAt = windowEnd,
            IndexingLatency = indexing,
            QueueDelay = queue,
            PhaseLatency = CollectPhaseLatency(),
            QueryLatency = metrics.QueryLatency.Snapshot(),
            Jobs = jobs,
            RecentJobs = recentJobs,
            WorkerCapacity = new WorkerCapacityMetrics
            {
                HasCapacity = hasWorkerCapacity,
                InFlight = jobs.Running,
                Queued = jobs.Queued
            },
            Storage = storage,
            CacheReuse = new CacheReuseMetrics
            {
                EnsureTotal = metrics.EnsureTotal,
                EnsureAttached = metrics.EnsureAttached,
                PageCacheHits = metrics.PageCacheHits,
                PageCacheMisses = metrics.PageCacheMisses
            },
            QueryErrors = metrics.QueryErrors,
            CostByRepository = cost
        };

        return snapshot with { Alerts = AlertEvaluator.Evaluate(snapshot, thresholds) };
    }

    private IReadOnlyDictionary<string, LatencyStats> CollectPhaseLatency()
    {
        var samples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var json in new SnapshotJobStore(connection).RecentTimings(PhaseLatencyWindow))
        {
            if (JobTimingsJson.Parse(json) is not { } timings)
                continue;
            foreach (var phase in timings.Phases)
            {
                if (!samples.TryGetValue(phase.Name, out var list))
                    samples[phase.Name] = list = [];
                list.Add(phase.WallMs);
            }
        }
        // Report in the order a job runs its phases (IndexPhaseNames.ExecutionOrder); an unknown phase goes last.
        var result = new Dictionary<string, LatencyStats>(StringComparer.Ordinal);
        foreach (var name in samples.Keys
                     .OrderBy(n => IndexPhaseNames.OrderOf(n) is var i and >= 0 ? i : int.MaxValue)
                     .ThenBy(n => n, StringComparer.Ordinal))
            result[name] = LatencyStats.FromSamples(samples[name]);
        return result;
    }

    private RecentJobMetrics CollectRecentJobs(long windowStart, long windowEnd, long minimumSamples)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT status, COUNT(*)
            FROM snapshot_jobs INDEXED BY ix_snapshot_jobs_completed_at
            WHERE completed_at >= @window_start AND completed_at < @window_end
              AND status IN (@complete, @partial, @failed, @unsupported, @cancelled)
            GROUP BY status;
            """;
        cmd.Parameters.AddWithValue("@window_start", windowStart);
        cmd.Parameters.AddWithValue("@window_end", windowEnd);
        cmd.Parameters.AddWithValue("@complete", SnapshotJobStatus.Complete);
        cmd.Parameters.AddWithValue("@partial", SnapshotJobStatus.Partial);
        cmd.Parameters.AddWithValue("@failed", SnapshotJobStatus.Failed);
        cmd.Parameters.AddWithValue("@unsupported", SnapshotJobStatus.Unsupported);
        cmd.Parameters.AddWithValue("@cancelled", SnapshotJobStatus.Cancelled);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt64(1);

        return new RecentJobMetrics
        {
            WindowStartUnixMs = windowStart,
            WindowEndUnixMs = windowEnd,
            MinimumSamples = minimumSamples,
            Complete = counts.GetValueOrDefault(SnapshotJobStatus.Complete),
            Partial = counts.GetValueOrDefault(SnapshotJobStatus.Partial),
            Failed = counts.GetValueOrDefault(SnapshotJobStatus.Failed),
            Unsupported = counts.GetValueOrDefault(SnapshotJobStatus.Unsupported),
            Cancelled = counts.GetValueOrDefault(SnapshotJobStatus.Cancelled)
        };
    }

    private JobMetrics CollectJobs()
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT status, COUNT(*) FROM snapshot_jobs GROUP BY status;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                counts[reader.GetString(0)] = reader.GetInt64(1);
        }
        return new JobMetrics
        {
            Queued = counts.GetValueOrDefault(SnapshotJobStatus.Queued),
            Running = counts.GetValueOrDefault(SnapshotJobStatus.Running),
            Complete = counts.GetValueOrDefault(SnapshotJobStatus.Complete),
            Partial = counts.GetValueOrDefault(SnapshotJobStatus.Partial),
            Failed = counts.GetValueOrDefault(SnapshotJobStatus.Failed),
            Unsupported = counts.GetValueOrDefault(SnapshotJobStatus.Unsupported),
            Cancelled = counts.GetValueOrDefault(SnapshotJobStatus.Cancelled),
            Coalesced = counts.GetValueOrDefault(SnapshotJobStatus.Coalesced)
        };
    }

    // Indexing latency = completed_at - started_at (worker run time); queue delay = started_at - created_at
    // (wait before a worker picked it up). Sampled over the most-recent completed jobs.
    private (LatencyStats indexing, LatencyStats queue) CollectLatencies()
    {
        var indexingSamples = new List<double>();
        var queueSamples = new List<double>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT created_at, started_at, completed_at
            FROM snapshot_jobs
            WHERE started_at IS NOT NULL
            ORDER BY id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", LatencyWindow);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var createdAt = reader.GetInt64(0);
            var startedAt = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var completedAt = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            if (startedAt is long s)
            {
                var delay = s - createdAt;
                if (delay >= 0)
                    queueSamples.Add(delay);
                if (completedAt is long c)
                {
                    var run = c - s;
                    if (run >= 0)
                        indexingSamples.Add(run);
                }
            }
        }
        return (LatencyStats.FromSamples(indexingSamples), LatencyStats.FromSamples(queueSamples));
    }

    private StorageMetrics CollectStorage() => new()
    {
        CatalogBytes = CatalogSize(catalogDbPath),
        ArtifactBytes = DirectorySize(paths.ArtifactRoot),
        CacheBytes = DirectorySize(paths.CacheRoot),
        CheckoutBytes = DirectorySize(paths.CheckoutRoot)
    };

    private IReadOnlyList<AuditCostAttribution> CollectCost()
    {
        try
        {
            return new AuditLogStore(connection).CostByRepository();
        }
        catch (SqliteException)
        {
            // A pre-migration-020 catalog has no audit_log table; cost attribution is simply empty then.
            return [];
        }
    }

    // The catalog SQLite file plus its WAL/SHM sidecars (the live on-disk footprint).
    private static long CatalogSize(string dbPath)
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            total += FileSize(dbPath + suffix);
        return total;
    }

    private static long FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static long DirectorySize(string root)
    {
        if (!Directory.Exists(root))
            return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                total += FileSize(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }
}
