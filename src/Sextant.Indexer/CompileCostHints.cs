namespace Sextant.Indexer;

/// <summary>
/// Historical compile costs of a repository's projects (issue #288), keyed by <see cref="KeyOf"/>, that let the
/// symbol phase's <see cref="CompilationPrefetcher"/> start its few expensive compilations first instead of leaving
/// them to the walk. Only the request order changes: never how many compile at once, and never any output.
/// </summary>
public sealed class CompileCostHints
{
    /// <summary>A project whose hinted compile took at least this long is compiled ahead of its walk position.</summary>
    public const long ExpensiveThresholdMs = 2000;

    public CompileCostHints(IReadOnlyDictionary<string, long> costMs)
    {
        ArgumentNullException.ThrowIfNull(costMs);
        CostMs = costMs;
    }

    /// <summary>Compile time in milliseconds by <see cref="KeyOf"/>.</summary>
    public IReadOnlyDictionary<string, long> CostMs { get; }

    public int Count => CostMs.Count;

    /// <summary>
    /// The stable key of a project across jobs: its path relative to the repository root and its evaluated target
    /// framework (each is a separate Roslyn project), with '/' separators.
    /// </summary>
    public static string KeyOf(string repoRelativePath, string? targetFramework) =>
        repoRelativePath.Replace('\\', '/').TrimStart('/') + "|" + (targetFramework ?? "");
}

/// <summary>The order in which the prefetcher requests compilations.</summary>
internal static class CompilePrefetchPolicy
{
    /// <summary>
    /// The indices into the walk, in the order to request them: first the expensive projects (hinted cost at least
    /// <see cref="CompileCostHints.ExpensiveThresholdMs"/>), most expensive first (walk order on a tie) and at most
    /// <paramref name="window"/> of them, then every other project in walk order. Each index appears once. Without
    /// hints, or with none that qualify, it is the walk order. <c>Promoted</c> is how many leading indices are the
    /// expensive ones.
    /// </summary>
    public static (IReadOnlyList<int> Order, int Promoted) PrefetchOrder(
        IReadOnlyList<string?> walkKeys, IReadOnlyDictionary<string, long>? costMs, int window)
    {
        var order = new List<int>(walkKeys.Count);
        if (costMs is { Count: > 0 } && window > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var promoted = Enumerable.Range(0, walkKeys.Count)
                .Where(i => walkKeys[i] is { } key && costMs.TryGetValue(key, out var ms)
                            && ms >= CompileCostHints.ExpensiveThresholdMs && seen.Add(key))
                .OrderByDescending(i => costMs[walkKeys[i]!])
                .ThenBy(i => i)
                .Take(window)
                .ToList();
            order.AddRange(promoted);
        }

        var promotedCount = order.Count;

        var taken = new HashSet<int>(order);
        for (var i = 0; i < walkKeys.Count; i++)
        {
            if (!taken.Contains(i))
                order.Add(i);
        }
        return (order, promotedCount);
    }
}
