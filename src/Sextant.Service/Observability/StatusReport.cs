using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Service.Grants;
using Sextant.Store;

namespace Sextant.Service.Observability;

/// <summary>The verdicts of <see cref="ReportRepository.Verdict"/>.</summary>
public static class ReportVerdict
{
    public const string Complete = "complete";
    public const string Partial = "partial";

    /// <summary>A complete snapshot with no stored coverage row: whether it covers the whole checkout was never recorded.</summary>
    public const string Unknown = "unknown";

    /// <summary>The default branch points at a snapshot a newer one replaced.</summary>
    public const string Superseded = "superseded";

    /// <summary>The default branch points at a snapshot still being staged.</summary>
    public const string Pending = "pending";

    public const string NoSupportedCode = "no_supported_code";
    public const string None = "none";

    /// <summary>The verdicts of a repository with a complete snapshot, whose counts are measured.</summary>
    public static bool IsCounted(string verdict) => verdict is Complete or Partial or Unknown;
}

/// <summary>
/// The fixed gap categories of <see cref="ReportRepository.GapReasons"/>. A report never carries coverage reason text:
/// that text can name projects, paths and submodule URLs.
/// </summary>
public static class ReportGapReason
{
    public const string ProjectsNotLoaded = "projects_not_loaded";
    public const string ProjectsPartiallyLoaded = "projects_partially_loaded";
    public const string ProjectsNotIndexed = "projects_not_indexed";
    public const string ProjectsNotFullyExtracted = "projects_not_fully_extracted";
    public const string ProjectsDegraded = "projects_degraded";
    public const string SolutionsUnusable = "solutions_unusable";
    public const string SubmodulesUnpopulated = "submodules_unpopulated";
    public const string ScanErrors = "scan_errors";
    public const string LoadFailuresUnattributed = "load_failures_unattributed";
    public const string TimeBudgetExhausted = "time_budget_exhausted";
    public const string Other = "other";

    /// <summary>The most categories one repository lists.</summary>
    public const int Max = 5;
}

/// <summary>The operator status report (<c>GET /control/report</c>). Serialized snake_case; unmeasured counts are left out.</summary>
public sealed record StatusReport
{
    public required string GeneratedAt { get; init; }

    /// <summary>
    /// True when more repositories exist than are listed (<see cref="ServiceOptions.ReportMaxRepositories"/>, or the
    /// no-supported-code lookup limit); the listed ones are the first by repository label.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>False when the time budget ended before every count was measured (the missing ones are left out, never guessed).</summary>
    public bool CountsComplete { get; init; }

    public required IReadOnlyList<ReportRepository> Repositories { get; init; }
    public required ReportTotals Totals { get; init; }
    [JsonPropertyName("last_24h")]
    public required ReportLast24h Last24h { get; init; }

    /// <summary>Catalog size and free space; the operator only, left out for an application caller.</summary>
    public ReportStorage? Storage { get; init; }
}

public sealed record ReportRepository
{
    public required string Repository { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public string? IndexedAt { get; init; }
    public required string Verdict { get; init; }
    public long? ProjectsIndexed { get; init; }
    public long? ProjectsExcluded { get; init; }
    public long? Documents { get; init; }
    public long? Symbols { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? LagSeconds { get; init; }

    /// <summary>
    /// Whether the indexed commit differs from the last remote head the freshness tracker read: true, false, or null
    /// (no recent remote head). <see cref="LagSeconds"/> 0 alone cannot tell "current" from "just noticed".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? BehindRemote { get; init; }

    public bool LagUnknown { get; init; }
    public required IReadOnlyList<string> GapReasons { get; init; }
}

public sealed record ReportTotals
{
    public int Repositories { get; init; }
    public required IReadOnlyDictionary<string, int> ByVerdict { get; init; }
    public long ProjectsIndexed { get; init; }
    public long Documents { get; init; }
    public long Symbols { get; init; }

    /// <summary>False when any listed repository's counts were not measured; the sums then undercount.</summary>
    public bool CountsComplete { get; init; }
}

public sealed record ReportLast24h
{
    /// <summary>True when the report lists fewer repositories than exist: these figures cover the listed ones only.</summary>
    public bool Truncated { get; init; }

    public required IReadOnlyDictionary<string, long> JobsByOutcome { get; init; }
    public int RepositoriesReindexed { get; init; }
}

public sealed record ReportStorage
{
    public long CatalogBytes { get; init; }
    public double CatalogFreeRatio { get; init; }
}

/// <summary>
/// Immutable per-snapshot counts, kept for the life of the process. A snapshot never changes once published, so a
/// remembered count stays exact; the cache only saves the next report from scanning index entries again.
/// </summary>
public sealed class ReportCountCache
{
    private const int MaxEntries = 20_000;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Identity, string Kind), long> _counts = [];
    private readonly Dictionary<string, long> _attempts = [];
    private long _sequence;

    /// <summary>The order in which a snapshot's counts were last attempted (0 = never); lower is staler.</summary>
    public long LastAttempt(string identity)
    {
        lock (_gate)
            return _attempts.GetValueOrDefault(identity);
    }

    public void MarkAttempted(string identity)
    {
        lock (_gate)
        {
            if (_attempts.Count >= MaxEntries)
                _attempts.Clear();
            _attempts[identity] = ++_sequence;
        }
    }

    public bool TryGet(string identity, string kind, out long value)
    {
        lock (_gate)
            return _counts.TryGetValue((identity, kind), out value);
    }

    public void Set(string identity, string kind, long value)
    {
        lock (_gate)
        {
            if (_counts.Count >= MaxEntries)
                _counts.Clear();
            _counts[(identity, kind)] = value;
        }
    }
}

/// <summary>
/// Builds the <see cref="StatusReport"/> from read-only connections, with no write lock and no read transaction held
/// across statements: the catalog reads share one short-lived connection, and each count opens its own. Every statement
/// is bounded by the caller's <see cref="ReadInterrupt"/> scope and by <paramref name="budget"/>. The repositories are
/// read whole (one row each), ordered and cut by label at <paramref name="maxRepositories"/>; the verdict and gap
/// categories come from the stored <c>snapshot_coverage</c> row; documents and symbols are index-only counts over the
/// snapshot's project versions, remembered per immutable snapshot in <paramref name="cache"/> the moment each finishes,
/// and measured least recently attempted snapshot first so one that never finishes cannot starve the rest.
/// </summary>
public static class StatusReportBuilder
{
    private const string Documents = "documents";
    private const string Symbols = "symbols";
    private const string Projects = "projects";
    private const int JobRowLimit = 100_000;

    private sealed record CatalogRow(
        string Url, string? Branch, long? SnapshotId, string? Status, string? Identity, long? PublishedAt, string? Commit);

    public static StatusReport Build(
        Func<SqliteConnection> openRead, DefaultBranchFreshnessTracker freshness, ReportCountCache cache, TimeProvider time,
        string catalogDbPath, IReadOnlySet<string>? visibleKeys, IReadOnlyList<string> grantedUrls,
        int maxRepositories, int noCodeJobLimit, bool includeStorage, CancellationToken budget)
    {
        try
        {
            return BuildCore(openRead, freshness, cache, time, catalogDbPath, visibleKeys, grantedUrls, maxRepositories,
                noCodeJobLimit, includeStorage, budget);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == ReadInterrupt.InterruptedErrorCode)
        {
            throw new TimeoutException("The status report did not finish within its time budget.", ex);
        }
    }

    private sealed record Entry(string Label, string Url, CatalogRow? Source, ReportRepository Row);

    private static StatusReport BuildCore(
        Func<SqliteConnection> openRead, DefaultBranchFreshnessTracker freshness, ReportCountCache cache, TimeProvider time,
        string catalogDbPath, IReadOnlySet<string>? visibleKeys, IReadOnlyList<string> grantedUrls,
        int maxRepositories, int noCodeJobLimit, bool includeStorage, CancellationToken budget)
    {
        var now = time.GetUtcNow();
        List<Entry> entries;
        bool truncated;
        ReportStorage? storage = null;
        using (var conn = openRead())
        {
            var rows = ReadCatalogRows(conn, visibleKeys);
            var (noCode, noCodeTruncated) = ReadNoSupportedCode(conn, noCodeJobLimit);
            var lags = freshness.Evaluate(conn, visibleKeys).ToDictionary(f => f.Repository, StringComparer.Ordinal);
            var coverages = new SnapshotCoverageStore(conn);

            // One candidate per label, ordered by label (never by catalog id) before the cut; a label several
            // spellings share keeps the first spelling in ordinal order.
            var candidates = new SortedDictionary<string, (string Url, CatalogRow? Row)>(StringComparer.Ordinal);
            foreach (var row in rows.OrderBy(r => DefaultBranchFreshnessTracker.LabelOf(r.Url), StringComparer.Ordinal)
                         .ThenBy(r => r.Url, StringComparer.Ordinal).ThenBy(r => r.Branch, StringComparer.Ordinal))
                candidates.TryAdd(DefaultBranchFreshnessTracker.LabelOf(row.Url), (row.Url, row));
            // Repositories the catalog has no row for: a no-code job, or (an application caller) a tenant-wide grant.
            foreach (var url in noCode.Values.Select(j => j.Url).Concat(grantedUrls))
                if (RepositoryGrantKey.IsVisible(visibleKeys, url))
                    candidates.TryAdd(DefaultBranchFreshnessTracker.LabelOf(url), (url, null));

            truncated = noCodeTruncated || candidates.Count > maxRepositories;
            entries = [];
            foreach (var (label, (url, row)) in candidates.Take(maxRepositories))
            {
                SnapshotCoverage? coverage = null;
                var verdict = ReportVerdict.None;
                var snapshotVerdict = true;
                switch (row)
                {
                    case { Status: SnapshotStatus.Complete, SnapshotId: not null }:
                        coverage = coverages.Get(row.SnapshotId!.Value);
                        verdict = coverage is null ? ReportVerdict.Unknown
                            : coverage.IsPartial ? ReportVerdict.Partial : ReportVerdict.Complete;
                        break;
                    case { Status: SnapshotStatus.Superseded, SnapshotId: not null }:
                        verdict = ReportVerdict.Superseded;
                        break;
                    case { Status: SnapshotStatus.Pending, SnapshotId: not null }:
                        verdict = ReportVerdict.Pending;
                        break;
                    default:
                        snapshotVerdict = false;
                        break;
                }
                var job = noCode.GetValueOrDefault(label);
                if (!snapshotVerdict && job is not null)
                    verdict = ReportVerdict.NoSupportedCode;
                entries.Add(new Entry(label, url, ReportVerdict.IsCounted(verdict) ? row : null,
                    NewRow(label, row, verdict, coverage, job, lags)));
            }

            if (includeStorage)
                storage = ReadStorage(conn, catalogDbPath);
        }

        // Jobs are limited to the repositories the list holds, so a cut list reports the same repositories everywhere.
        var listed = entries.Select(e => RepositoryGrantKey.Of(e.Url)).ToHashSet(StringComparer.Ordinal);
        ReportLast24h last24h;
        using (var conn = openRead())
            last24h = ReadLast24h(conn, listed, now) with { Truncated = truncated };

        // Counts last: they are the only part that can be slow, and they stop at the budget. Least recently attempted
        // first, so a snapshot whose count never finishes in budget cannot starve the ones after it.
        var counted = entries.Where(e => e.Source is not null)
            .OrderBy(e => cache.LastAttempt(e.Source!.Identity!)).ThenBy(e => e.Label, StringComparer.Ordinal).ToList();
        var measured = new Dictionary<string, ReportRepository>(StringComparer.Ordinal);
        var countsComplete = new[] { true };
        foreach (var entry in counted)
            measured[entry.Label] = WithCounts(openRead, cache, entry.Row, entry.Source!, budget, countsComplete);

        var finalRows = entries.Select(e => measured.GetValueOrDefault(e.Label) ?? e.Row).ToList();
        var byVerdict = new[]
            {
                ReportVerdict.Complete, ReportVerdict.Partial, ReportVerdict.Unknown, ReportVerdict.Superseded,
                ReportVerdict.Pending, ReportVerdict.NoSupportedCode, ReportVerdict.None
            }
            .ToDictionary(v => v, v => finalRows.Count(r => r.Verdict == v));
        var totalsComplete = finalRows.All(r => !ReportVerdict.IsCounted(r.Verdict)
            || (r.ProjectsIndexed is not null && r.Documents is not null && r.Symbols is not null));
        return new StatusReport
        {
            GeneratedAt = now.ToString("o", CultureInfo.InvariantCulture),
            Truncated = truncated,
            CountsComplete = countsComplete[0],
            Repositories = finalRows,
            Totals = new ReportTotals
            {
                Repositories = finalRows.Count,
                ByVerdict = byVerdict,
                ProjectsIndexed = finalRows.Sum(r => r.ProjectsIndexed ?? 0),
                Documents = finalRows.Sum(r => r.Documents ?? 0),
                Symbols = finalRows.Sum(r => r.Symbols ?? 0),
                CountsComplete = totalsComplete
            },
            Last24h = last24h,
            Storage = storage
        };
    }

    private static ReportRepository NewRow(
        string label, CatalogRow? row, string verdict, SnapshotCoverage? coverage, NoCodeJob? noCodeJob,
        Dictionary<string, DefaultBranchFreshness> lags)
    {
        var counted = ReportVerdict.IsCounted(verdict);
        var showsSnapshot = counted || verdict is ReportVerdict.Superseded or ReportVerdict.Pending;
        var commit = showsSnapshot ? row?.Commit : noCodeJob?.Commit;
        double? lag = null;
        bool? behind = null;
        var unknown = false;
        if (verdict != ReportVerdict.NoSupportedCode)
        {
            if (lags.TryGetValue(label, out var f))
            {
                unknown = f.Unknown;
                lag = f.Unknown ? null : f.LagSeconds;
                behind = f.BehindRemote;
            }
            else
            {
                unknown = true;
            }
        }
        return new ReportRepository
        {
            Repository = label,
            Branch = verdict == ReportVerdict.NoSupportedCode ? null : row?.Branch,
            Commit = commit is { Length: > 0 } c ? c[..Math.Min(12, c.Length)] : null,
            IndexedAt = counted && row?.PublishedAt is long at
                ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToString("o", CultureInfo.InvariantCulture) : null,
            Verdict = verdict,
            ProjectsExcluded = counted ? coverage?.ProjectVersionsExcluded : null,
            LagSeconds = lag,
            BehindRemote = behind,
            LagUnknown = unknown,
            GapReasons = verdict == ReportVerdict.Partial ? GapCategories(coverage!) : []
        };
    }

    /// <summary>The fixed categories a partial coverage record falls in, most important first, at most <see cref="ReportGapReason.Max"/>.</summary>
    internal static IReadOnlyList<string> GapCategories(SnapshotCoverage coverage)
    {
        var found = new List<string>();
        void Add(bool when, string category)
        {
            if (when)
                found.Add(category);
        }
        Add(coverage.ProjectsSkipped > 0 || coverage.TimeBudget is { ProjectsNotLoaded: > 0 }, ReportGapReason.ProjectsNotLoaded);
        Add(coverage.ProjectsPartiallyLoaded > 0, ReportGapReason.ProjectsPartiallyLoaded);
        Add(coverage.TimeBudget is { ProjectsNotIndexed: > 0 }, ReportGapReason.ProjectsNotIndexed);
        Add(coverage.TimeBudget is { ProjectsNotFullyExtracted: > 0 }, ReportGapReason.ProjectsNotFullyExtracted);
        Add(coverage.ProjectsDegraded > 0 || coverage.Binding is { ProjectsDegraded: > 0 }, ReportGapReason.ProjectsDegraded);
        Add(coverage.SolutionsSkipped > 0 || coverage.SolutionsUnreadable > 0 || coverage.SolutionsNotSelected > 0,
            ReportGapReason.SolutionsUnusable);
        Add(coverage.SubmodulesUnpopulated > 0, ReportGapReason.SubmodulesUnpopulated);
        Add(coverage.ScanErrors > 0, ReportGapReason.ScanErrors);
        Add(coverage.UnattributedLoadFailures > 0, ReportGapReason.LoadFailuresUnattributed);
        Add(coverage.TimeBudget is { Exhausted: true }, ReportGapReason.TimeBudgetExhausted);
        if (found.Count == 0)
            found.Add(ReportGapReason.Other);
        return found.Take(ReportGapReason.Max).ToList();
    }

    private static ReportRepository WithCounts(
        Func<SqliteConnection> openRead, ReportCountCache cache, ReportRepository row, CatalogRow source,
        CancellationToken budget, bool[] countsComplete)
    {
        var attempted = false;
        long? Count(string kind, string sql)
        {
            if (cache.TryGet(source.Identity!, kind, out var known))
                return known;
            if (budget.IsCancellationRequested)
            {
                countsComplete[0] = false;
                return null;
            }
            if (!attempted)
            {
                attempted = true;
                cache.MarkAttempted(source.Identity!);
            }
            try
            {
                // Its own connection, so no read transaction stays open between counts (or while the next one waits).
                using var conn = openRead();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@s", source.SnapshotId!.Value);
                var value = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                cache.Set(source.Identity!, kind, value);
                return value;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == ReadInterrupt.InterruptedErrorCode)
            {
                countsComplete[0] = false;
                return null;
            }
        }

        // Index-only counts over the project versions of the snapshot. documents: ix_files_path; symbols:
        // ix_symbols_project_access (both covering, so no table row is read). Reference counts are deliberately absent:
        // they would read every occurrences row of the snapshot.
        return row with
        {
            ProjectsIndexed = Count(Projects, "SELECT COUNT(*) FROM snapshot_projects WHERE snapshot_id = @s;"),
            Documents = Count(Documents,
                "SELECT COUNT(*) FROM snapshot_projects sp CROSS JOIN files f INDEXED BY ix_files_path ON f.project_id = sp.project_id WHERE sp.snapshot_id = @s;"),
            Symbols = Count(Symbols,
                "SELECT COUNT(*) FROM snapshot_projects sp CROSS JOIN symbols y INDEXED BY ix_symbols_project_access ON y.project_id = sp.project_id WHERE sp.snapshot_id = @s;")
        };
    }

    // One pass over the repositories table (one row per repository, none of it a count). It is read whole so the cut can
    // be made by label; the table holds one row per repository, not per symbol.
    private static List<CatalogRow> ReadCatalogRows(SqliteConnection conn, IReadOnlySet<string>? visibleKeys)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.remote_url, b.name, b.snapshot_id, s.status, s.identity_hash, s.published_at, c.commit_sha
            FROM repositories r
            LEFT JOIN branches b ON b.repository_id = r.id AND b.is_default = 1
            LEFT JOIN snapshots s ON s.id = b.snapshot_id
            LEFT JOIN commits c ON c.id = s.commit_id
            WHERE r.is_provider = 0;
            """;
        var rows = new List<CatalogRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var url = reader.GetString(0);
            if (!RepositoryGrantKey.IsVisible(visibleKeys, url))
                continue;
            rows.Add(new CatalogRow(
                url, reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return rows;
    }

    private sealed record NoCodeJob(string Url, string Commit);

    // Repositories whose most recent finished job ended for no supported code, by label. Only repositories that ever had
    // an unsupported job are read (the status index), ordered by repository URL, at most `limit` of them; one more is
    // read to tell whether the limit cut the answer.
    private static (Dictionary<string, NoCodeJob> Jobs, bool Truncated) ReadNoSupportedCode(SqliteConnection conn, int limit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT repository_url, commit_sha FROM (
                SELECT repository_url, commit_sha, status, last_error,
                       ROW_NUMBER() OVER (PARTITION BY repository_url
                                          ORDER BY COALESCE(completed_at, updated_at) DESC, id DESC) AS rn
                FROM snapshot_jobs
                WHERE status NOT IN (@queued, @running)
                  AND repository_url IN (SELECT repository_url FROM snapshot_jobs WHERE status = @unsupported))
            WHERE rn = 1 AND status = @unsupported
              AND (substr(last_error, 1, @len1) = @prefix1 OR substr(last_error, 1, @len2) = @prefix2)
            ORDER BY repository_url
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@queued", SnapshotJobStatus.Queued);
        cmd.Parameters.AddWithValue("@running", SnapshotJobStatus.Running);
        cmd.Parameters.AddWithValue("@unsupported", SnapshotJobStatus.Unsupported);
        cmd.Parameters.AddWithValue("@len1", SnapshotJobStore.NoSupportedCodePrefix.Length);
        cmd.Parameters.AddWithValue("@prefix1", SnapshotJobStore.NoSupportedCodePrefix);
        cmd.Parameters.AddWithValue("@len2", SnapshotJobStore.NoSupportedCodeDefaultBranchPrefix.Length);
        cmd.Parameters.AddWithValue("@prefix2", SnapshotJobStore.NoSupportedCodeDefaultBranchPrefix);
        cmd.Parameters.AddWithValue("@limit", limit + 1);
        var result = new Dictionary<string, NoCodeJob>(StringComparer.Ordinal);
        var read = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (++read > limit)
                return (result, true);
            result.TryAdd(DefaultBranchFreshnessTracker.LabelOf(reader.GetString(0)), new NoCodeJob(reader.GetString(0), reader.GetString(1)));
        }
        return (result, false);
    }

    // Terminal outcomes completed in the last 24 hours, through the completion-time index (the same window and filter as
    // the rolling job metrics), folded to the repositories the caller may see. Left out when the row limit cuts it.
    private static ReportLast24h ReadLast24h(SqliteConnection conn, IReadOnlySet<string>? visibleKeys, DateTimeOffset now)
    {
        var end = now.ToUnixTimeMilliseconds();
        var start = end - (long)TimeSpan.FromHours(24).TotalMilliseconds;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT repository_url, status, COUNT(*)
            FROM snapshot_jobs INDEXED BY ix_snapshot_jobs_completed_at
            WHERE completed_at >= @start AND completed_at < @end
              AND status IN (@complete, @partial, @failed, @unsupported, @cancelled)
              AND
            """ + SnapshotJobStore.NotStartupSettledSql + """

            GROUP BY repository_url, status
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@start", start);
        cmd.Parameters.AddWithValue("@end", end);
        cmd.Parameters.AddWithValue("@complete", SnapshotJobStatus.Complete);
        cmd.Parameters.AddWithValue("@partial", SnapshotJobStatus.Partial);
        cmd.Parameters.AddWithValue("@failed", SnapshotJobStatus.Failed);
        cmd.Parameters.AddWithValue("@unsupported", SnapshotJobStatus.Unsupported);
        cmd.Parameters.AddWithValue("@cancelled", SnapshotJobStatus.Cancelled);
        cmd.Parameters.AddWithValue("@limit", JobRowLimit);
        var counts = new[]
        {
            SnapshotJobStatus.Complete, SnapshotJobStatus.Partial, SnapshotJobStatus.Failed, SnapshotJobStatus.Unsupported,
            SnapshotJobStatus.Cancelled
        }.ToDictionary(s => s, _ => 0L);
        var reindexed = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var url = reader.GetString(0);
            if (!RepositoryGrantKey.IsVisible(visibleKeys, url))
                continue;
            var status = reader.GetString(1);
            counts[status] += reader.GetInt64(2);
            if (status is SnapshotJobStatus.Complete or SnapshotJobStatus.Partial)
                reindexed.Add(RepositoryGrantKey.Of(url));
        }
        return new ReportLast24h { JobsByOutcome = counts, RepositoriesReindexed = reindexed.Count };
    }

    // Both pragmas read the database header, so they cost nothing however large the catalog is.
    private static ReportStorage ReadStorage(SqliteConnection conn, string catalogDbPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA page_count;";
        var pages = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        cmd.CommandText = "PRAGMA freelist_count;";
        var free = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new ReportStorage
        {
            CatalogBytes = new[] { "", "-wal", "-shm" }.Sum(suffix => MetricsCollector.FileSize(catalogDbPath + suffix)),
            CatalogFreeRatio = pages <= 0 ? 0 : (double)free / pages
        };
    }
}
