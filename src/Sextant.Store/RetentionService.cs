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
/// reclaimed and the estimated bytes freed.
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
/// land — is spared even if it falls outside a keep window. Dry-run and execution share one code path;
/// dry-run performs the deletes inside a transaction that is rolled back, so its reported counts and
/// reclaimed bytes match what execution would do.
/// </summary>
public sealed class RetentionService
{
    private readonly SqliteConnection _connection;
    private readonly RetentionPolicy _policy;
    private readonly IReadOnlyList<IRetentionProtectionProvider> _providers;

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
    public RetentionReport Plan() => Run(execute: false);

    /// <summary>Applies retention and returns what was deleted.</summary>
    public RetentionReport Execute() => Run(execute: true);

    private RetentionReport Run(bool execute)
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

        long pageSize = 0;
        long freelistBefore = 0;
        var apiDeleted = 0;
        var fileVersionsDeleted = 0;
        var snapshotProjectsDeleted = 0;
        var snapshotsDeleted = 0;
        long reclaimed = 0;

        Exec("BEGIN IMMEDIATE;");
        try
        {
            // Measure the reclaimable-space baseline under the write lock, so the freelist delta
            // reflects only this pass's deletions. page_size is fixed; freelist_count is read right
            // after BEGIN IMMEDIATE and before any delete. Dry-run and execute share this measurement.
            pageSize = ScalarLong("PRAGMA page_size;");
            freelistBefore = ScalarLong("PRAGMA freelist_count;");

            foreach (var runId in deletableRunIds)
                runStore.DeleteRun(runId);

            (snapshotProjectsDeleted, snapshotsDeleted) = DeleteSnapshotData(orphanedSnapshotIds);

            apiDeleted = DeleteApiCommits(deletableCommits);

            // Blob prune runs LAST and computes orphans inside the transaction so it reclaims blobs newly
            // orphaned by the snapshot-data GC above too (bounded + protected-set-aware — issue #37).
            if (_policy.PruneSupersededSourceBlobs)
                fileVersionsDeleted = PruneOrphanFileVersions(protections);

            long freelistAfter = ScalarLong("PRAGMA freelist_count;");
            reclaimed = Math.Max(0, freelistAfter - freelistBefore) * pageSize;

            Exec(execute ? "COMMIT;" : "ROLLBACK;");
        }
        catch
        {
            TryExec("ROLLBACK;");
            throw;
        }

        if (execute && reclaimed > 0)
        {
            // Realize the freed pages on disk and keep the WAL bounded.
            TryExec("VACUUM;");
            TryExec("PRAGMA wal_checkpoint(TRUNCATE);");
        }

        return new RetentionReport
        {
            DryRun = !execute,
            Protected = protectedGenerations,
            Retained = retainedGenerations,
            Deleted = deletedGenerations,
            ApiSnapshotsDeleted = apiDeleted,
            FileVersionsDeleted = fileVersionsDeleted,
            SnapshotsDeleted = snapshotsDeleted,
            SnapshotProjectVersionsDeleted = snapshotProjectsDeleted,
            ReclaimedBytes = reclaimed
        };
    }

    private RetentionProtectionSet BuildProtections()
    {
        var builder = new RetentionProtectionBuilder();
        foreach (var provider in _providers)
            provider.Contribute(_connection, builder);
        return builder.Build();
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
                commits.Add(reader.GetString(0));
        }

        var deletable = new List<string>();
        for (var i = 0; i < commits.Count; i++)
        {
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
            cmd.CommandText = "DELETE FROM api_surface_snapshots WHERE git_commit = @c;";
            cmd.Parameters.AddWithValue("@c", commit);
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
            if (s.baseSnapshotId is long b)
                baseOf[s.id] = b;

        var providersOf = new Dictionary<long, List<long>>();
        foreach (var (consumer, provider) in edges)
            (providersOf.TryGetValue(consumer, out var list) ? list : providersOf[consumer] = []).Add(provider);

        // Expands a seed set across base + provider edges to a fixpoint (a retained snapshot pins the
        // base it overlays and every provider it consumes — issues #44/#54).
        HashSet<long> ExpandClosure(IEnumerable<long> seed)
        {
            var set = new HashSet<long>(seed);
            var frontier = new Queue<long>(set);
            while (frontier.Count > 0)
            {
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
        var hardProtected = ExpandClosure(pending.Concat(rootPinned));

        // Seed the retained set with everything the generation keep-window keeps (a snapshot on a
        // generation this pass is NOT deleting), plus the hard-protected set, then expand to a fixpoint.
        var windowSeed = new HashSet<long>(hardProtected);
        foreach (var s in snapshots)
            if (s.runId is long rid && !deletableRuns.Contains(rid))
                windowSeed.Add(s.id);
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
            if (s.status != SnapshotStatus.Pending && !retained.Contains(s.id))
                orphaned.Add(s.id);
        return orphaned;
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
        var cap = _policy.MaxSnapshotsPerRepository;
        var evict = new HashSet<long>();

        foreach (var repoGroup in snapshots
                     .Where(s => s.status == SnapshotStatus.Complete && retained.Contains(s.id) && !hardProtected.Contains(s.id))
                     .GroupBy(s => s.repositoryId))
        {
            // Newest first (created_at desc, id desc as a stable tiebreak); keep the cap newest, evict the rest.
            var ranked = repoGroup.OrderByDescending(s => s.createdAt).ThenByDescending(s => s.id).ToList();
            for (var i = cap; i < ranked.Count; i++)
                evict.Add(ranked[i].id);
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
    /// Reclaims the project-version rows owned by the orphaned snapshots (and their cascaded
    /// symbols/occurrences/comments/files/api rows via the Phase-7 <c>projects</c> cascade), then deletes
    /// the now-empty snapshot catalog rows (cascading <c>snapshot_projects</c>/<c>snapshot_dependencies</c>).
    /// Bounded: set-based deletes in fixed-size id chunks rather than one statement per row (issue #46/#37).
    /// Returns (project-versions deleted, snapshot rows deleted).
    /// </summary>
    private (int projectVersions, int snapshots) DeleteSnapshotData(List<long> orphanedSnapshotIds)
    {
        if (orphanedSnapshotIds.Count == 0) return (0, 0);

        var projectsDeleted = 0;
        var snapshotsDeleted = 0;
        foreach (var chunk in Chunk(orphanedSnapshotIds, 256))
        {
            var inList = string.Join(",", chunk);

            using (var projCmd = _connection.CreateCommand())
            {
                projCmd.CommandText = $"DELETE FROM projects WHERE snapshot_id IN ({inList});";
                projectsDeleted += projCmd.ExecuteNonQuery();
            }

            using var snapCmd = _connection.CreateCommand();
            snapCmd.CommandText = $"DELETE FROM snapshots WHERE id IN ({inList});";
            snapshotsDeleted += snapCmd.ExecuteNonQuery();
        }
        return (projectsDeleted, snapshotsDeleted);
    }

    /// <summary>
    /// Prunes source blobs referenced by no live symbol/occurrence/comment (bounded, protected-set-aware —
    /// issue #37). When no blob is protected (the common case) this is a single set-based DELETE; when a
    /// provider protects specific blobs it materializes the orphan set, removes the protected ids, and
    /// deletes the rest in fixed-size chunks. Returns the number of blobs pruned.
    /// </summary>
    private int PruneOrphanFileVersions(RetentionProtectionSet protections)
    {
        const string orphanPredicate = """
            id NOT IN (SELECT file_version_id FROM symbols WHERE file_version_id IS NOT NULL)
              AND id NOT IN (SELECT file_version_id FROM occurrences)
              AND id NOT IN (SELECT file_version_id FROM comments WHERE file_version_id IS NOT NULL)
            """;

        if (protections.FileVersions.Count == 0)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM file_versions WHERE {orphanPredicate};";
            return cmd.ExecuteNonQuery();
        }

        var ids = new List<long>();
        using (var select = _connection.CreateCommand())
        {
            select.CommandText = $"SELECT id FROM file_versions WHERE {orphanPredicate};";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!protections.IsFileVersionProtected(id)) ids.Add(id);
            }
        }

        var deleted = 0;
        foreach (var chunk in Chunk(ids, 256))
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM file_versions WHERE id IN ({string.Join(",", chunk)});";
            deleted += cmd.ExecuteNonQuery();
        }
        return deleted;
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

    private void TryExec(string sql)
    {
        try
        {
            Exec(sql);
        }
        catch (SqliteException)
        {
            // Best-effort cleanup step (rollback/vacuum/checkpoint); never mask the primary outcome.
        }
    }
}
