using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Service.Grants;
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
    TimeProvider? timeProvider = null,
    WriteLockTracker? writeLocks = null,
    DefaultBranchFreshnessTracker? freshness = null,
    DirectorySizeCache? sizeCache = null)
{
    // Bound the latency window so a very long-lived service never scans an unbounded job history.
    private const int LatencyWindow = 1000;

    // Per-phase latency parses each job's stored timings document, so it reads a smaller window (issue #267).
    private const int PhaseLatencyWindow = 200;
    private static readonly long RecentWindowMs = (long)TimeSpan.FromHours(24).TotalMilliseconds;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <param name="visibleRepositoryKeys">
    /// The <see cref="Sextant.Service.Grants.RepositoryGrantKey"/>s the per-repository series (freshness and cost) may name; null
    /// = every repository (the operator's own access).
    /// </param>
    public MetricsSnapshot Collect(AlertThresholds? thresholds = null, IReadOnlySet<string>? visibleRepositoryKeys = null)
    {
        var collectedAt = _time.GetUtcNow();
        var windowEnd = collectedAt.ToUnixTimeMilliseconds();
        var windowStart = windowEnd - RecentWindowMs;
        var jobs = CollectJobs(windowEnd);
        var recentJobs = CollectRecentJobs(windowStart, windowEnd, thresholds?.SuccessRateMinSamples ??
            AlertThresholds.Default.SuccessRateMinSamples);
        var (indexing, queue) = CollectLatencies();
        var storage = CollectStorage();
        var cost = CollectCost(visibleRepositoryKeys);

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
            Maintenance = metrics.Maintenance,
            Crash = CollectCrash(),
            Freshness = freshness?.Evaluate(connection, visibleRepositoryKeys) ?? [],
            WriteLock = writeLocks?.Snapshot() ?? new WriteLockMetrics(),
            CostByRepository = cost
        };

        return snapshot with { Alerts = AlertEvaluator.Evaluate(snapshot, thresholds) };
    }

    // The crash counters are a bonus signal: a missing table (a pre-032 catalog being read) or a database error must
    // never fail /control/metrics.
    private CrashMetrics CollectCrash()
    {
        try
        {
            return Sextant.Store.WriterLease.GetRunState(connection) is { } state
                ? new CrashMetrics
                {
                    UncleanShutdownsTotal = state.UncleanShutdownsTotal,
                    PreviousRunUnclean = state.PreviousRunUnclean
                        && (state.PreviousRunEndReason ?? Sextant.Store.WriterEndReason.Unclean) == Sextant.Store.WriterEndReason.Unclean,
                    PreviousRunLastHeartbeatUnixMs = state.PreviousRunHeartbeatAt,
                    PreviousRunEndReason = state.PreviousRunEndReason,
                    AbandonedRunsTotal = state.AbandonedRunsTotal,
                    ReleaseFailuresTotal = state.ReleaseFailuresTotal,
                    StaleLeaseTakeoversTotal = state.StaleLeaseTakeoversTotal
                }
                : new CrashMetrics();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return new CrashMetrics();
        }
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
              AND
            """ + SnapshotJobStore.NotStartupSettledSql + """

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

    private JobMetrics CollectJobs(long nowMs)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            // Rows settled at startup (issue #327) were never produced: they are counted only in
            // sextant_startup_jobs_settled_total, not as outcomes.
            cmd.CommandText =
                "SELECT status, COUNT(*) FROM snapshot_jobs WHERE " + SnapshotJobStore.NotStartupSettledSql +
                " GROUP BY status;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                counts[reader.GetString(0)] = reader.GetInt64(1);
        }
        var (oldestQueued, longestRunning) = CollectJobAges(nowMs);
        return new JobMetrics
        {
            OldestQueuedAgeSeconds = oldestQueued,
            LongestRunningAgeSeconds = longestRunning,
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

    // Age of the longest-queued job and of the longest-running one (since started_at, else updated_at). A requeue keeps
    // created_at, so queued age runs from updated_at, which every transition into queued stamps. A repeated ensure of a
    // still-queued job also stamps it, so the age restarts then: it can under-report a job that is re-requested while
    // it waits, never over-report a requeued one. Both read the status index, so they cost one seek per status however
    // large the job history is.
    private (double oldestQueued, double longestRunning) CollectJobAges(long nowMs)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MIN(updated_at) FROM snapshot_jobs WHERE status = @queued AND " +
            SnapshotJobStore.NotStartupSettledSql + ";" +
            "SELECT MIN(COALESCE(started_at, updated_at)) FROM snapshot_jobs WHERE status = @running AND " +
            SnapshotJobStore.NotStartupSettledSql + ";";
        cmd.Parameters.AddWithValue("@queued", SnapshotJobStatus.Queued);
        cmd.Parameters.AddWithValue("@running", SnapshotJobStatus.Running);
        using var reader = cmd.ExecuteReader();
        var oldestQueued = reader.Read() && !reader.IsDBNull(0) ? Math.Max(0, (nowMs - reader.GetInt64(0)) / 1000.0) : 0;
        reader.NextResult();
        var longestRunning = reader.Read() && !reader.IsDBNull(0) ? Math.Max(0, (nowMs - reader.GetInt64(0)) / 1000.0) : 0;
        return (oldestQueued, longestRunning);
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
            WHERE started_at IS NOT NULL AND
            """ + SnapshotJobStore.NotStartupSettledSql + """

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

    private StorageMetrics CollectStorage()
    {
        var (pages, free) = CatalogPages();
        var artifacts = Volume(paths.ArtifactRoot);
        var cache = Volume(paths.CacheRoot);
        var checkouts = Volume(paths.CheckoutRoot);
        return new StorageMetrics
        {
            CatalogBytes = CatalogSize(catalogDbPath),
            CatalogPageCount = pages,
            CatalogFreePages = free,
            ArtifactBytes = artifacts ?? 0,
            CacheBytes = cache ?? 0,
            CheckoutBytes = checkouts ?? 0,
            VolumesMeasured = artifacts is not null && cache is not null && checkouts is not null
        };
    }

    // Null while a volume has never been measured (the first walk is pending or failed).
    private long? Volume(string root)
    {
        if (sizeCache is not null)
            return sizeCache.TryGet(root, _time, out var bytes) ? bytes : null;
        try
        {
            return DirectorySizeCache.Measure(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Both pragmas read the database header, so they cost nothing however large the catalog is (issue #354).
    private (long pages, long free) CatalogPages()
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA page_count;";
            var pages = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            cmd.CommandText = "PRAGMA freelist_count;";
            return (pages, Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (SqliteException)
        {
            return (0, 0);
        }
    }

    private IReadOnlyList<AuditCostAttribution> CollectCost(IReadOnlySet<string>? visibleRepositoryKeys)
    {
        try
        {
            return new AuditLogStore(connection).CostByRepository()
                .Where(c => RepositoryGrantKey.IsVisible(visibleRepositoryKeys, c.RepositoryScope))
                .ToList();
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

    internal static long FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}

/// <summary>
/// Remembers each volume's recursive size (issue #355). Walking a checkout or cache volume of hundreds of thousands of
/// files is the one slow step of a metrics collection, so a walk never runs on the caller's thread: <see cref="Get"/>
/// returns the last measured value at once (0 only until the first walk of that root finishes) and, when the value is
/// older than <see cref="Ttl"/>, starts ONE background walk for that root; concurrent callers never start another
/// while it runs.
/// </summary>
public sealed class DirectorySizeCache(Func<string, long>? measure = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public long Bytes;
        public bool Measured;
        public DateTimeOffset? At;
        public bool Walking;
    }

    private readonly Func<string, long> _measure = measure ?? Measure;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public long Get(string root, TimeProvider time)
    {
        TryGet(root, time, out var bytes);
        return bytes;
    }

    /// <summary>
    /// As <see cref="Get"/>, but false (with 0) while no walk of <paramref name="root"/> has completed, so a caller can
    /// tell "never measured" from an empty volume.
    /// </summary>
    public bool TryGet(string root, TimeProvider time, out long bytes)
    {
        var start = false;
        bool measured;
        lock (_gate)
        {
            if (!_entries.TryGetValue(root, out var entry))
                _entries[root] = entry = new Entry();
            bytes = entry.Bytes;
            measured = entry.Measured;
            if (!entry.Walking && (entry.At is not { } at || time.GetUtcNow() - at >= Ttl))
                start = entry.Walking = true;
        }
        if (start)
            _ = Task.Run(() => Walk(root, time));
        return measured;
    }

    private void Walk(string root, TimeProvider time)
    {
        long bytes = 0;
        var ok = false;
        try
        {
            bytes = _measure(root);
            ok = true;
        }
        catch (Exception)
        {
            // Keep the last value; the next collection past the TTL tries again.
        }
        lock (_gate)
        {
            var entry = _entries[root];
            if (ok)
            {
                entry.Bytes = bytes;
                entry.Measured = true;
            }
            entry.At = time.GetUtcNow();
            entry.Walking = false;
        }
    }

    internal static long Measure(string root)
    {
        if (!Directory.Exists(root))
            return 0;
        // An unreadable directory or file throws: a partial total must never be stored as a measurement. Only a file
        // that vanished mid-walk (normal churn) counts 0.
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return total;
    }
}
