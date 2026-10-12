using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>
/// Loads the compile-cost hints (issue #288) of a repository from the timings its earlier jobs recorded: the latest
/// completed job on the same branch that recorded any, else the latest on any branch. Hints are an optimization only:
/// a malformed entry is skipped, and any failure to read them yields none (logged once), never a failed job.
/// </summary>
public static class CompileCostHintLoader
{
    private const int HistoryRows = 20;

    public static CompileCostHints? Load(
        SnapshotJobStore jobs, string repositoryUrl, string? branchName, Action<string>? log = null)
    {
        try
        {
            var history = new List<(string? BranchName, IReadOnlyList<ProjectCompileCost> Costs)>();
            foreach (var row in jobs.RecentCompletedTimingsOfRepository(repositoryUrl, HistoryRows))
            {
                try
                {
                    var costs = JobTimingsJson.Parse(row.TimingsJson)?.CompileCosts ?? [];
                    if (costs.Count > 0)
                        history.Add((row.BranchName, costs));
                }
                catch (Exception)
                {
                    // One unreadable row never costs the others' hints.
                }
            }
            var chosen = history.FirstOrDefault(row => branchName != null && row.BranchName == branchName);
            if (chosen.Costs is null)
                chosen = history.FirstOrDefault();
            if (chosen.Costs is not { Count: > 0 })
                return null;
            var cost = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var c in chosen.Costs)
                if (c is { Key: { } key })
                    cost[key] = c.Ms;
            return new CompileCostHints(cost);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  Compile cost hints unavailable ({ex.GetType().Name}); indexing without them.");
            return null;
        }
    }
}
