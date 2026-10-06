using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store;

/// <summary>One generation (an <c>index_runs</c> row) as classified by a retention pass.</summary>
public sealed record RetentionGeneration
{
    public required long Id { get; init; }
    public required string Status { get; init; }
    public string? Profile { get; init; }
    public long? CompletedAt { get; init; }

    /// <summary>Why this generation was protected/retained/deleted (human-readable).</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// The outcome of a retention pass (Phase 8, criteria 4 &amp; 5) — identical shape whether planned
/// (dry-run) or executed. Reports which generations were protected (never eligible), retained (kept
/// within the keep window), and deleted (or would be), plus the API-history and source-blob rows
/// reclaimed. Only execution measures reusable page bytes; planning never simulates writes.
/// </summary>
public sealed record RetentionReport
{
    public required bool DryRun { get; init; }
    public IReadOnlyList<RetentionGeneration> Protected { get; init; } = [];
    public IReadOnlyList<RetentionGeneration> Retained { get; init; } = [];
    public IReadOnlyList<RetentionGeneration> Deleted { get; init; } = [];
    public int ApiSnapshotsDeleted { get; init; }
    public int FileVersionsDeleted { get; init; }

    /// <summary>Immutable snapshot rows reclaimed by snapshot-data GC (issue #46), with their catalog metadata.</summary>
    public int SnapshotsDeleted { get; init; }

    /// <summary>Project-version rows (and their cascaded symbols/occurrences/comments/api rows) reclaimed by snapshot-data GC (issue #46).</summary>
    public int SnapshotProjectVersionsDeleted { get; init; }

    public long ReclaimedBytes { get; init; }
    /// <summary>Space estimates are unavailable on a read-only plan; execution measures reusable pages, not disk shrinkage.</summary>
    public bool ReclaimedBytesKnown { get; init; }
    public bool MoreRemaining { get; init; }
    public string? StopReason { get; init; }

    /// <summary>
    /// Stored source-text blobs (issue #244) the pass deleted because no file version references them any more.
    /// Null on a dry run and where no source-text store is kept.
    /// </summary>
    public int? SourceTextsDeleted { get; init; }
}

/// <summary>
/// Applies retention limits (<see cref="RetentionPolicy"/>) to a single index database while honoring a
/// protected set assembled from <see cref="IRetentionProtectionProvider"/>s (Phase 8, criteria 4 &amp; 5).
/// It garbage-collects superseded local generations (the <c>index_runs</c> ledger), trims API-surface
/// history beyond the keep window (<c>api_surface_snapshots</c>), and prunes source blobs no longer
/// referenced by any live symbol/occurrence/comment (<c>file_versions</c>).
///
/// Two invariants are absolute: the currently-servable last-complete generation is NEVER deleted (a
/// hard guard independent of the provider set, so retention can never trip the Phase-7 rebuild gate),
/// and anything a provider protects — branch/PR snapshots, submodule pins, active overlays once those
/// land — is spared even if it falls outside a keep window. Plans use a consistent read transaction.
/// Each execution batch reclassifies eligibility under BEGIN IMMEDIATE before deleting any data.
/// </summary>
public sealed class RetentionService
{
    private readonly SqliteConnection _connection;
    private readonly RetentionPolicy _policy;
    private readonly IReadOnlyList<IRetentionProtectionProvider> _providers;
    private RetentionWorkBudget? _budget;

    public static TimeSpan PassTimeLimit { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan BatchTimeLimit { get; } = TimeSpan.FromSeconds(2);
    public const int BatchRowLimit = 256;

    public RetentionService(
        SqliteConnection connection,
        RetentionPolicy policy,
        IEnumerable<IRetentionProtectionProvider>? providers = null)
    {
        _connection = connection;
        _policy = policy.Normalized();
        _providers = (providers ?? DefaultProviders).ToList();
    }

    /// <summary>The providers active today. Phase 9/10/12 append branch/PR/overlay/pin providers here.</summary>
    public static IReadOnlyList<IRetentionProtectionProvider> DefaultProviders { get; } =
        [new LastCompleteRunProtection(), new BranchPointerProtection(), new OverlayBaseProtection(), new SubmoduleProviderProtection(), new PullRequestSnapshotProtection()];

    /// <summary>Reports what retention would do without modifying the database.</summary>
    public RetentionReport Plan(CancellationToken cancellationToken = default)
    {
        using var budget = new RetentionWorkBudget(_connection, PassTimeLimit, cancellationToken);
        _budget = budget;
        try
        {
            budget.Check();
            Exec("BEGIN;");
            var (report, snapshots, commits, protections) = Classify();
            var projects = snapshots.Sum(id => CountRows(RetentionCount.ProjectVersions, id));
            var apiRows = commits.Sum(commit => CountRows(RetentionCount.ApiRows, commit));
            var files = _policy.PruneSupersededSourceBlobs ? OrphanFileVersionIds(protections).Count : 0;
            return report with
            {
                DryRun = true,
                SnapshotsDeleted = snapshots.Count,
                SnapshotProjectVersionsDeleted = projects,
                ApiSnapshotsDeleted = apiRows,
                FileVersionsDeleted = files
            };
        }
        catch (Exception ex) when (IsBudgetOrBusy(ex, budget))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new RetentionReport { DryRun = true, MoreRemaining = true, StopReason = StopReason(ex) };
        }
        finally
        {
            budget.Dispose();
            _budget = null;
            if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) Exec("ROLLBACK;");
        }
    }

    /// <summary>Applies bounded batches. Service callers acquire/release their writer gate around ExecuteBatch instead.</summary>
    public RetentionReport Execute(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var report = new RetentionReport { DryRun = false };
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = PassTimeLimit - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return report with { MoreRemaining = true, StopReason = "time_budget" };
            var batch = ExecuteBatch(cancellationToken, remaining < BatchTimeLimit ? remaining : BatchTimeLimit);
            report = Combine(report, batch);
            if (batch.StopReason != null) return report;
        } while (report.MoreRemaining);
        return report;
    }

    /// <summary>One atomic batch, including fresh eligibility checks. Interrupted cascades roll back the entire batch.</summary>
    public RetentionReport ExecuteBatch(CancellationToken cancellationToken = default, TimeSpan? timeLimit = null,
        Action? beforeCommit = null)
    {
        using var budget = new RetentionWorkBudget(_connection, timeLimit ?? BatchTimeLimit, cancellationToken);
        _budget = budget;
        try
        {
            budget.Check();
            Exec("BEGIN IMMEDIATE;");
            var (report, snapshots, commits, protections) = Classify();
            var pageSize = ScalarLong("PRAGMA page_size;");
            var freeBefore = ScalarLong("PRAGMA freelist_count;");
            var projectsDeleted = 0;
            var snapshotsDeleted = 0;
            // One project cascade at a time, never a whole multi-project snapshot in one transaction.
            if (snapshots.Count > 0)
            {
                var id = snapshots[0];
                WithdrawAffectedPublication(id, snapshots.ToHashSet());
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM projects WHERE id IN (SELECT id FROM projects WHERE snapshot_id = @id ORDER BY id LIMIT 1);";
                cmd.Parameters.AddWithValue("@id", id);
                projectsDeleted = cmd.ExecuteNonQuery();
                if (CountRows(RetentionCount.ProjectVersions, id) == 0)
                    snapshotsDeleted = DeleteEmptySnapshot(id);
            }
            // Do not remove ledger rows ahead of snapshot GC's classification.
            var deletedRuns = snapshots.Count == 0 ? report.Deleted.Take(BatchRowLimit).ToList() : [];
            var runStore = new IndexRunStore(_connection);
            foreach (var run in deletedRuns)
            {
                budget.Check();
                runStore.DeleteRun(run.Id);
            }
            var apiDeleted = DeleteApiCommits(commits.Take(1).ToList());
            var fileDeleted = _policy.PruneSupersededSourceBlobs ? PruneOrphanFileVersions(protections) : 0;
            var reclaimed = Math.Max(0, ScalarLong("PRAGMA freelist_count;") - freeBefore) * pageSize;
            budget.Check();
            beforeCommit?.Invoke();
            budget.Check();
            Exec("COMMIT;");
            return report with
            {
                DryRun = false,
                Deleted = deletedRuns,
                SnapshotsDeleted = snapshotsDeleted,
                SnapshotProjectVersionsDeleted = projectsDeleted,
                ApiSnapshotsDeleted = apiDeleted,
                FileVersionsDeleted = fileDeleted,
                ReclaimedBytes = reclaimed,
                ReclaimedBytesKnown = true,
                MoreRemaining = snapshots.Count > 0 || report.Deleted.Count > deletedRuns.Count ||
                    apiDeleted > 0 || fileDeleted > 0
            };
        }
        catch (Exception ex) when (IsBudgetOrBusy(ex, budget))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = StopReason(ex) };
        }
        finally
        {
            budget.Dispose();
            _budget = null;
            // SQLITE_INTERRUPT can roll back a write transaction itself.
            if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) Exec("ROLLBACK;");
        }
    }

    /// <summary>Run only under the service writer gate: blobs can precede their file_versions rows.</summary>
    public RetentionReport SweepSourceTexts(SourceTextStore texts, CancellationToken cancellationToken,
        TimeSpan timeLimit)
    {
        using var budget = new RetentionWorkBudget(_connection, timeLimit, cancellationToken);
        try
        {
            budget.Check();
            var keys = new FileStore(_connection).ReferencedSourceTextKeys();
            budget.Check();
            var deleted = texts.DeleteUnreferenced(keys, BatchRowLimit, () => budget.ShouldStop, out var remaining);
            cancellationToken.ThrowIfCancellationRequested();
            return new RetentionReport
            {
                DryRun = false, SourceTextsDeleted = deleted, MoreRemaining = remaining,
                StopReason = budget.ShouldStop ? "time_budget" : null
            };
        }
        catch (Exception ex) when (IsBudgetOrBusy(ex, budget))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = StopReason(ex) };
        }
    }

    private static bool IsBudgetOrBusy(Exception ex, RetentionWorkBudget budget) =>
        ex is TimeoutException || ex is SqliteException sql &&
        (sql.SqliteErrorCode is 5 or 6 || sql.SqliteErrorCode == 9 && budget.ShouldStop);

    private static string StopReason(Exception ex) =>
        ex is SqliteException { SqliteErrorCode: 5 or 6 } ? "sqlite_busy" : "time_budget";

    public static RetentionReport Combine(RetentionReport total, RetentionReport batch) => batch with
    {
        Deleted = total.Deleted.Concat(batch.Deleted).ToList(),
        SnapshotsDeleted = total.SnapshotsDeleted + batch.SnapshotsDeleted,
        SnapshotProjectVersionsDeleted = total.SnapshotProjectVersionsDeleted + batch.SnapshotProjectVersionsDeleted,
        ApiSnapshotsDeleted = total.ApiSnapshotsDeleted + batch.ApiSnapshotsDeleted,
        FileVersionsDeleted = total.FileVersionsDeleted + batch.FileVersionsDeleted,
        ReclaimedBytes = total.ReclaimedBytes + batch.ReclaimedBytes,
        ReclaimedBytesKnown = total.ReclaimedBytesKnown || batch.ReclaimedBytesKnown
    };

    private (RetentionReport report, List<long> snapshots, List<string> commits, RetentionProtectionSet protections) Classify()
    {
        var runStore = new IndexRunStore(_connection);
        var servable = runStore.GetLastCompleteRun();
        var protections = BuildProtections();

        var allRuns = runStore.GetAllRuns();
        var protectedGenerations = new List<RetentionGeneration>();
        var retainedGenerations = new List<RetentionGeneration>();
        var deletableRunIds = new List<long>();
        var deletedGenerations = new List<RetentionGeneration>();

        // Rank complete runs newest-first; keep the first KeepCompleteGenerations, GC the rest.
        var completeRank = 0;
        foreach (var run in allRuns)
        {
            _budget?.Check();
            var isServable = servable != null && run.Id == servable.Id;

            // Hard guard + provider protections: never delete the servable generation or a protected one.
            if (isServable || protections.IsGenerationProtected(run.Id))
            {
                var reason = isServable
                    ? "currently-servable last-complete generation"
                    : protections.Generations.GetValueOrDefault(run.Id, "protected");
                protectedGenerations.Add(ToGeneration(run, reason));
                if (run.Status == IndexRunState.Complete) completeRank++;
                continue;
            }

            if (run.Status == IndexRunState.Complete)
            {
                completeRank++;
                if (completeRank <= _policy.KeepCompleteGenerations)
                {
                    retainedGenerations.Add(ToGeneration(run, $"within keep window (newest {_policy.KeepCompleteGenerations})"));
                }
                else
                {
                    deletableRunIds.Add(run.Id);
                    deletedGenerations.Add(ToGeneration(run, "superseded generation beyond keep window"));
                }
            }
            else if (run.Status == IndexRunState.Abandoned)
            {
                deletableRunIds.Add(run.Id);
                deletedGenerations.Add(ToGeneration(run, "abandoned generation"));
            }
            else
            {
                // Staging (in-progress) runs are left untouched — a live writer owns them.
                retainedGenerations.Add(ToGeneration(run, "in-progress generation"));
            }
        }

        var deletableCommits = DeletableApiCommits(protections);

        // Snapshot-data GC (issue #46): reclaim the semantic rows owned by snapshots that no retained
        // generation, branch pointer, overlay base, or retained consumer still references (issue #54).
        var orphanedSnapshotIds = OrphanedSnapshotIds(deletableRunIds);
        // A retained provider/base may own an expired run. Keep that ledger row until its last
        // surviving snapshot is collected; otherwise ON DELETE SET NULL manufactures legacy-unknown
        // ownership and makes healthy data permanently protected on the following pass.
        var orphaned = orphanedSnapshotIds.ToHashSet();
        var retainedRunIds = new SnapshotStore(_connection).GetSnapshotsForRetention()
            .Where(s => !orphaned.Contains(s.id) && s.runId.HasValue)
            .Select(s => s.runId!.Value).ToHashSet();
        foreach (var generation in deletedGenerations.Where(g => retainedRunIds.Contains(g.Id)))
            protectedGenerations.Add(generation with { Reason = "generation owns retained snapshot data" });
        deletedGenerations.RemoveAll(g => retainedRunIds.Contains(g.Id));

        return (new RetentionReport
        {
            DryRun = true,
            Protected = protectedGenerations,
            Retained = retainedGenerations,
            Deleted = deletedGenerations
        }, orphanedSnapshotIds, deletableCommits, protections);
    }

    private RetentionProtectionSet BuildProtections()
    {
        var builder = new RetentionProtectionBuilder();
        foreach (var provider in _providers)
            provider.Contribute(_connection, builder);
        return builder.Build();
    }

    private void WithdrawAffectedPublication(long snapshotId, HashSet<long> eligible)
    {
        var store = new SnapshotStore(_connection);
        var dependents = new Dictionary<long, List<long>>();
        foreach (var (consumer, provider) in store.GetSnapshotDependencyEdges())
        {
            _budget?.Check();
            (dependents.TryGetValue(provider, out var list) ? list : dependents[provider] = []).Add(consumer);
        }
        foreach (var snapshot in store.GetSnapshotsForRetention())
        {
            _budget?.Check();
            if (snapshot.baseSnapshotId is long baseId)
                (dependents.TryGetValue(baseId, out var list) ? list : dependents[baseId] = []).Add(snapshot.id);
        }

        // A provider cascade removes consumers' dependency edges AND target occurrences. Withdraw
        // every affected consumer before yielding, transitively and cycle-safely, not just the owner.
        // NULL ownership keeps these tombstones GC-eligible even inside a retained run/quota window.
        var visited = new HashSet<long>();
        var frontier = new Queue<long>();
        frontier.Enqueue(snapshotId);
        using var invalidate = _connection.CreateCommand();
        invalidate.CommandText = "UPDATE snapshots SET status = @failed, run_id = NULL, published_at = NULL WHERE id = @id;";
        invalidate.Parameters.AddWithValue("@failed", SnapshotStatus.Failed);
        var id = invalidate.Parameters.Add("@id", SqliteType.Integer);
        while (frontier.Count > 0)
        {
            _budget?.Check();
            var current = frontier.Dequeue();
            if (!visited.Add(current)) continue;
            if (!eligible.Contains(current))
                throw new InvalidOperationException("Retention cannot collect data required by an ineligible snapshot.");
            id.Value = current;
            invalidate.ExecuteNonQuery();
            if (dependents.TryGetValue(current, out var consumers))
                foreach (var consumer in consumers) frontier.Enqueue(consumer);
        }
    }

    private static RetentionGeneration ToGeneration(IndexRun run, string reason) => new()
    {
        Id = run.Id,
        Status = run.Status,
        Profile = run.IndexingProfile,
        CompletedAt = run.CompletedAt,
        Reason = reason
    };

    /// <summary>API-history commits outside the keep window and not protected, oldest windowing first.</summary>
    private List<string> DeletableApiCommits(RetentionProtectionSet protections)
    {
        var commits = new List<string>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT git_commit, MAX(captured_at) AS m
                FROM api_surface_snapshots
                GROUP BY git_commit
                ORDER BY m DESC, git_commit DESC;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _budget?.Check();
                commits.Add(reader.GetString(0));
            }
        }

        var deletable = new List<string>();
        for (var i = 0; i < commits.Count; i++)
        {
            _budget?.Check();
            if (i < _policy.ApiSnapshotKeepCommits) continue;
            if (protections.IsCommitProtected(commits[i])) continue;
            deletable.Add(commits[i]);
        }
        return deletable;
    }

    private int DeleteApiCommits(List<string> commits)
    {
        var deleted = 0;
        foreach (var commit in commits)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM api_surface_snapshots WHERE id IN (SELECT id FROM api_surface_snapshots WHERE git_commit = @c LIMIT @limit);";
            cmd.Parameters.AddWithValue("@c", commit);
            cmd.Parameters.AddWithValue("@limit", BatchRowLimit);
            deleted += cmd.ExecuteNonQuery();
        }
        return deleted;
    }

    /// <summary>
    /// The immutable snapshots whose semantic DATA a retention pass may reclaim (issue #46): every
    /// snapshot NOT kept alive by a retained generation, a branch pointer, an overlay base it is layered
    /// on, or a submodule provider a retained consumer still references (issue #54). Computed as the
    /// complement of the RETAINED-snapshot closure so a shared provider/base is never orphaned while any
    /// retained snapshot still reads it. Pending snapshots are excluded (a live writer may own them).
    /// </summary>
    private List<long> OrphanedSnapshotIds(List<long> deletableRunIds)
    {
        var snapshotStore = new SnapshotStore(_connection);
        var snapshots = snapshotStore.GetSnapshotsForRetention();
        if (snapshots.Count == 0) return [];

        var deletableRuns = deletableRunIds.ToHashSet();
        var rootPinned = snapshotStore.GetBranchPointedSnapshotIds()
            .Concat(snapshotStore.GetOpenPullRequestSnapshotIds())
            .ToHashSet();
        var edges = snapshotStore.GetSnapshotDependencyEdges();

        // Dependency maps shared by the retained-closure expansion and the quota safety check.
        var baseOf = new Dictionary<long, long>();
        foreach (var s in snapshots)
        {
            _budget?.Check();
            if (s.baseSnapshotId is long b)
                baseOf[s.id] = b;
        }

        var providersOf = new Dictionary<long, List<long>>();
        foreach (var (consumer, provider) in edges)
        {
            _budget?.Check();
            (providersOf.TryGetValue(consumer, out var list) ? list : providersOf[consumer] = []).Add(provider);
        }

        // Expands a seed set across base + provider edges to a fixpoint (a retained snapshot pins the
        // base it overlays and every provider it consumes — issues #44/#54).
        HashSet<long> ExpandClosure(IEnumerable<long> seed)
        {
            var set = new HashSet<long>(seed);
            var frontier = new Queue<long>(set);
            while (frontier.Count > 0)
            {
                _budget?.Check();
                var id = frontier.Dequeue();
                if (baseOf.TryGetValue(id, out var baseId) && set.Add(baseId))
                    frontier.Enqueue(baseId);
                if (providersOf.TryGetValue(id, out var provs))
                    foreach (var p in provs)
                        if (set.Add(p))
                            frontier.Enqueue(p);
            }
            return set;
        }

        // HARD protected closure (criterion 4): pending snapshots (a live writer may own them) plus every
        // snapshot pinned by a branch pointer or an open pull request, transitively expanded across the
        // base/provider chains. Quota eviction may NEVER cross this set.
        var pending = snapshots.Where(s => s.status == SnapshotStatus.Pending).Select(s => s.id);
        // Catalogs written before #127 may still bind a published retry to an abandoned run (or NULL
        // after its ledger row was GC'd). No reliable age can be inferred from that pointer: fail closed.
        var runs = new IndexRunStore(_connection).GetAllRuns().ToDictionary(r => r.Id);
        var uncertainOwnership = snapshots.Where(s =>
            s.status is SnapshotStatus.Complete or SnapshotStatus.Superseded &&
            (s.runId == null || !runs.TryGetValue(s.runId.Value, out var run) || run.Status == IndexRunState.Abandoned))
            .Select(s => s.id);
        var hardProtected = ExpandClosure(pending.Concat(rootPinned).Concat(uncertainOwnership));

        // Seed the retained set with everything the generation keep-window keeps (a snapshot on a
        // generation this pass is NOT deleting), plus the hard-protected set, then expand to a fixpoint.
        var windowSeed = new HashSet<long>(hardProtected);
        foreach (var s in snapshots)
        {
            _budget?.Check();
            if (s.runId is long rid && !deletableRuns.Contains(rid))
                windowSeed.Add(s.id);
        }
        var retained = ExpandClosure(windowSeed);

        // Per-repository quota (Phase 17): cap retained COMPLETE snapshots per repository, evicting oldest
        // first — but only snapshots that are neither hard-protected nor depended on by a still-retained
        // snapshot, so quota GC is provably protected-set-aware and never deletes shared base/provider data.
        if (_policy.MaxSnapshotsPerRepository > 0)
        {
            var evictions = ComputeQuotaEvictions(snapshots, retained, hardProtected, baseOf, providersOf);
            retained.ExceptWith(evictions);
        }

        var orphaned = new List<long>();
        foreach (var s in snapshots)
        {
            _budget?.Check();
            if (s.status != SnapshotStatus.Pending && !retained.Contains(s.id))
                orphaned.Add(s.id);
        }
        return orphaned.Order().ToList();
    }

    /// <summary>
    /// Selects the oldest-over-cap complete snapshots per repository that are safe to evict for the quota:
    /// never a hard-protected (branch/open-PR/overlay/provider) snapshot, and never one a still-retained
    /// snapshot depends on (its base or a provider it consumes). The dependency guard runs to a fixpoint so
    /// evicting a snapshot can never strand a kept snapshot's shared rows.
    /// </summary>
    private List<long> ComputeQuotaEvictions(
        IReadOnlyList<(long id, long repositoryId, long? runId, string status, long? baseSnapshotId, bool isProvider, long createdAt)> snapshots,
        HashSet<long> retained,
        HashSet<long> hardProtected,
        Dictionary<long, long> baseOf,
        Dictionary<long, List<long>> providersOf)
    {
        _budget?.Check();
        var cap = _policy.MaxSnapshotsPerRepository;
        var evict = new HashSet<long>();

        foreach (var repoGroup in snapshots
                     .Where(s => s.status == SnapshotStatus.Complete && retained.Contains(s.id) && !hardProtected.Contains(s.id))
                     .GroupBy(s => s.repositoryId))
        {
            _budget?.Check();
            // Newest first (created_at desc, id desc as a stable tiebreak); keep the cap newest, evict the rest.
            var ranked = repoGroup.OrderByDescending(s => s.createdAt).ThenByDescending(s => s.id).ToList();
            for (var i = cap; i < ranked.Count; i++)
            {
                _budget?.Check();
                evict.Add(ranked[i].id);
            }
        }

        if (evict.Count == 0) return [];

        // Safety fixpoint: never evict a snapshot that a KEPT (retained-and-not-evicted) snapshot still
        // depends on. Un-evict any base/provider referenced by a kept snapshot until stable.
        bool changed;
        do
        {
            changed = false;
            foreach (var kept in retained)
            {
                _budget?.Check();
                if (evict.Contains(kept)) continue;
                if (baseOf.TryGetValue(kept, out var baseId) && evict.Remove(baseId))
                    changed = true;
                if (providersOf.TryGetValue(kept, out var provs))
                    foreach (var p in provs)
                        if (evict.Remove(p))
                            changed = true;
            }
        }
        while (changed);

        return evict.ToList();
    }

    /// <summary>
    /// Deletes the catalog row only after all of its owned project versions have been collected.
    /// </summary>
    private int DeleteEmptySnapshot(long snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM snapshots WHERE id = @id
              AND NOT EXISTS (SELECT 1 FROM projects WHERE snapshot_id = @id);
            """;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Prunes source blobs referenced by no live symbol/occurrence/comment (bounded, protected-set-aware —
    /// issue #37). Materializes at most BatchRowLimit unprotected ids before deleting them.
    /// </summary>
    private int PruneOrphanFileVersions(RetentionProtectionSet protections)
    {
        var ids = OrphanFileVersionIds(protections, BatchRowLimit);

        var deleted = 0;
        foreach (var chunk in Chunk(ids, 256))
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM file_versions WHERE id IN ({string.Join(",", chunk)});";
            deleted += cmd.ExecuteNonQuery();
        }
        return deleted;
    }

    private List<long> OrphanFileVersionIds(RetentionProtectionSet protections, int? limit = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT fv.id FROM file_versions fv
            WHERE NOT EXISTS (SELECT 1 FROM symbols WHERE file_version_id = fv.id)
              AND NOT EXISTS (SELECT 1 FROM occurrences WHERE file_version_id = fv.id)
              AND NOT EXISTS (SELECT 1 FROM comments WHERE file_version_id = fv.id)
            ORDER BY fv.id;
            """;
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _budget?.Check();
            var id = reader.GetInt64(0);
            if (!protections.IsFileVersionProtected(id)) ids.Add(id);
            if (limit.HasValue && ids.Count >= limit.Value) break;
        }
        return ids;
    }

    private enum RetentionCount { ProjectVersions, ApiRows }

    private int CountRows(RetentionCount count, object value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = count switch
        {
            RetentionCount.ProjectVersions => "SELECT COUNT(*) FROM projects WHERE snapshot_id = @value;",
            RetentionCount.ApiRows => "SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = @value;",
            _ => throw new ArgumentOutOfRangeException(nameof(count))
        };
        cmd.Parameters.AddWithValue("@value", value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static IEnumerable<IReadOnlyList<long>> Chunk(List<long> ids, int size)
    {
        for (var i = 0; i < ids.Count; i += size)
            yield return ids.GetRange(i, Math.Min(size, ids.Count - i));
    }

    private long ScalarLong(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

}
