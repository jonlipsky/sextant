using Sextant.Store;
using Microsoft.Data.Sqlite;

namespace Sextant.Mcp;

internal static class ScopeResolver
{
    public static ScopeFilter Resolve(string? scope, SqliteConnection conn)
    {
        if (string.IsNullOrEmpty(scope) || scope == "all")
            return ScopeFilter.None;

        if (scope.StartsWith("file:"))
            return new ScopeFilter { FilePath = scope[5..] };

        if (scope.StartsWith("project:"))
        {
            var projectStore = new ProjectStore(conn);
            var proj = projectStore.GetByCanonicalId(scope[8..]);
            if (proj == null) return ScopeFilter.None;
            return new ScopeFilter { ProjectIds = new HashSet<long> { proj.Value.id } };
        }

        if (scope.StartsWith("solution:"))
        {
            var solutionStore = new SolutionStore(conn);
            var projectIds = solutionStore.GetProjectIdsForSolution(scope[9..]);
            if (projectIds.Count == 0) return ScopeFilter.None;
            return new ScopeFilter { ProjectIds = projectIds };
        }

        return ScopeFilter.None;
    }
}

internal sealed class ScopeFilter
{
    public static readonly ScopeFilter None = new();
    public string? FilePath { get; init; }
    public HashSet<long>? ProjectIds { get; init; }

    public bool IsEmpty => FilePath == null && ProjectIds == null;
}
