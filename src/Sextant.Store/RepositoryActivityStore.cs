using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store;

/// <summary>One repository's catalog activity, as the service's reclaim rule reads it (issue #291).</summary>
/// <param name="RepositoryId">The catalog repository row.</param>
/// <param name="RemoteUrl">The repository's remote URL as stored.</param>
/// <param name="HasBranches">True while any branch pointer row exists.</param>
/// <param name="HasPendingWork">True while a queued or running job, or an unconsumed branch-advance handover, names it.</param>
/// <param name="LastActivityMs">
/// The most recent of: the repository row's creation, every branch's <c>updated_at</c>, every snapshot's
/// <c>created_at</c>/<c>published_at</c>, every job's <c>created_at</c>/<c>updated_at</c>/<c>started_at</c>/<c>completed_at</c>,
/// every handover's <c>recorded_at</c> and every retired-head record's <c>retired_at</c> (Unix milliseconds).
/// </param>
public sealed record RepositoryActivity(
    long RepositoryId, string RemoteUrl, bool HasBranches, bool HasPendingWork, long LastActivityMs);

/// <summary>Read-only per-repository activity for the maintenance loop's reclaim pass (issue #291).</summary>
public sealed class RepositoryActivityStore(SqliteConnection connection)
{
    /// <summary>
    /// Every repository with its activity, least recently active first. Jobs and handovers name a repository by URL, so
    /// they are matched to the catalog row through <see cref="RemoteUrlIdentity.Normalize"/>.
    /// </summary>
    public IReadOnlyList<RepositoryActivity> List()
    {
        var jobLast = new Dictionary<string, long>(StringComparer.Ordinal);
        var jobPending = new HashSet<string>(StringComparer.Ordinal);
        using (var jobs = connection.CreateCommand())
        {
            jobs.CommandText = """
                SELECT repository_url, status,
                       MAX(created_at, updated_at, COALESCE(started_at, 0), COALESCE(completed_at, 0))
                FROM snapshot_jobs;
                """;
            using var reader = jobs.ExecuteReader();
            while (reader.Read())
            {
                var key = RemoteUrlIdentity.Normalize(reader.GetString(0));
                var at = reader.GetInt64(2);
                jobLast[key] = jobLast.TryGetValue(key, out var seen) ? Math.Max(seen, at) : at;
                if (reader.GetString(1) is SnapshotJobStatus.Queued or SnapshotJobStatus.Running)
                    jobPending.Add(key);
            }
        }
        using (var handovers = connection.CreateCommand())
        {
            handovers.CommandText = "SELECT repository_key, recorded_at FROM branch_advance_handovers;";
            using var reader = handovers.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var at = reader.GetInt64(1);
                jobLast[key] = jobLast.TryGetValue(key, out var seen) ? Math.Max(seen, at) : at;
                jobPending.Add(key);
            }
        }

        var result = new List<RepositoryActivity>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.remote_url,
                   EXISTS (SELECT 1 FROM branches b WHERE b.repository_id = r.id),
                   MAX(r.created_at,
                       COALESCE((SELECT MAX(b.updated_at) FROM branches b WHERE b.repository_id = r.id), 0),
                       COALESCE((SELECT MAX(MAX(s.created_at), MAX(COALESCE(s.published_at, 0))) FROM snapshots s
                                 WHERE s.repository_id = r.id), 0),
                       COALESCE((SELECT MAX(h.retired_at) FROM retired_branch_heads h WHERE h.repository_id = r.id), 0))
            FROM repositories r;
            """;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = RemoteUrlIdentity.Normalize(reader.GetString(1));
                var last = reader.GetInt64(3);
                if (jobLast.TryGetValue(key, out var jobAt))
                    last = Math.Max(last, jobAt);
                result.Add(new RepositoryActivity(
                    reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0, jobPending.Contains(key), last));
            }
        }
        result.Sort((a, b) => a.LastActivityMs != b.LastActivityMs
            ? a.LastActivityMs.CompareTo(b.LastActivityMs)
            : a.RepositoryId.CompareTo(b.RepositoryId));
        return result;
    }
}
