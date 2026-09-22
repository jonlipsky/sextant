namespace Sextant.Indexer;

/// <summary>
/// Pure, dependency-free expansion of a seed set of projects to its connected closure over an
/// undirected project-dependency graph. Extracted from <see cref="IncrementalIndexer"/> so the
/// invalidation-closure logic (Phase 4, acceptance criterion 6) is unit-testable without Roslyn.
///
/// Correctness rationale: a semantic edge (reference, relationship, call) can only cross a project
/// boundary when one project's compilation references the other, which requires a ProjectReference,
/// which is an edge in this graph. Expanding a changed project to its undirected connected component
/// therefore pulls in every project that could own an edge touching the changed project, so rebuilding
/// exactly the closure reproduces the same cross-project edges a full index would — no edge is left
/// dangling across the closure boundary.
/// </summary>
public static class ProjectClosure
{
    /// <summary>
    /// Returns every node reachable from <paramref name="seeds"/> through the undirected
    /// <paramref name="adjacency"/> (inclusive of the seeds themselves).
    /// </summary>
    public static HashSet<T> Expand<T>(
        IEnumerable<T> seeds,
        IReadOnlyDictionary<T, IReadOnlyCollection<T>> adjacency,
        IEqualityComparer<T>? comparer = null)
    {
        var result = new HashSet<T>(comparer);
        var queue = new Queue<T>();
        foreach (var seed in seeds)
            if (result.Add(seed))
                queue.Enqueue(seed);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!adjacency.TryGetValue(node, out var neighbors))
                continue;
            foreach (var neighbor in neighbors)
                if (result.Add(neighbor))
                    queue.Enqueue(neighbor);
        }

        return result;
    }

    /// <summary>
    /// Builds an undirected adjacency map from a directed dependency edge list
    /// (consumer → dependency), materialising both directions so a change to a dependency invalidates
    /// its consumers and vice versa.
    /// </summary>
    public static Dictionary<T, IReadOnlyCollection<T>> BuildUndirectedAdjacency<T>(
        IEnumerable<(T Consumer, T Dependency)> edges,
        IEqualityComparer<T>? comparer = null)
    {
        var mutable = new Dictionary<T, HashSet<T>>(comparer);

        HashSet<T> Bucket(T key)
        {
            if (!mutable.TryGetValue(key, out var set))
            {
                set = new HashSet<T>(comparer);
                mutable[key] = set;
            }
            return set;
        }

        foreach (var (consumer, dependency) in edges)
        {
            if (EqualityComparer<T>.Default.Equals(consumer, dependency))
                continue;
            Bucket(consumer).Add(dependency);
            Bucket(dependency).Add(consumer);
        }

        return mutable.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<T>)kv.Value, comparer);
    }
}
