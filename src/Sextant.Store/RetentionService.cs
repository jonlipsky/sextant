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
        [new LastCompleteRunProtection(), new BranchPointerProtection(), new OverlayBaseProtection()];

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
        var deletableFileVersionIds = _policy.PruneSupersededSourceBlobs
            ? OrphanFileVersionIds(protections)
            : [];

        long pageSize = 0;
        long freelistBefore = 0;
        var apiDeleted = 0;
        var fileVersionsDeleted = 0;
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

            apiDeleted = DeleteApiCommits(deletableCommits);
            fileVersionsDeleted = DeleteFileVersions(deletableFileVersionIds);

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

    /// <summary>Source blobs referenced by no live symbol/occurrence/comment, minus protected blobs.</summary>
    private List<long> OrphanFileVersionIds(RetentionProtectionSet protections)
    {
        var ids = new List<long>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id FROM file_versions
                WHERE id NOT IN (SELECT file_version_id FROM symbols WHERE file_version_id IS NOT NULL)
                  AND id NOT IN (SELECT file_version_id FROM occurrences)
                  AND id NOT IN (SELECT file_version_id FROM comments WHERE file_version_id IS NOT NULL);
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        }

        ids.RemoveAll(protections.IsFileVersionProtected);
        return ids;
    }

    private int DeleteFileVersions(List<long> ids)
    {
        var deleted = 0;
        foreach (var id in ids)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM file_versions WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            deleted += cmd.ExecuteNonQuery();
        }
        return deleted;
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
