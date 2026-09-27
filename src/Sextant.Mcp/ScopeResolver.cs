using Sextant.Store;
using Microsoft.Data.Sqlite;

namespace Sextant.Mcp;

internal static class ScopeResolver
{
    public static ScopeFilter Resolve(string? scope, SqliteConnection conn, SnapshotReadScope? pinnedScope = null)
    {
        if (string.IsNullOrEmpty(scope) || scope == "all")
            return ScopeFilter.None;

        if (scope.StartsWith("file:"))
            return new ScopeFilter { FilePath = scope[5..] };

        if (scope.StartsWith("project:"))
        {
            var projectStore = new ProjectStore(conn) { Scope = pinnedScope };
            var proj = projectStore.GetByCanonicalId(scope[8..]);
            if (proj == null) return ScopeFilter.None;
            return new ScopeFilter { ProjectIds = new HashSet<long> { proj.Value.id } };
        }

        if (scope.StartsWith("solution:"))
        {
            var solutionStore = new SolutionStore(conn);
            var solutionPath = scope[9..];
            var projectIds = solutionStore.GetProjectIdsForSolution(solutionPath);
            if (projectIds.Count > 0)
                return new ScopeFilter { ProjectIds = projectIds };
            // A KNOWN solution with no mapped project (e.g. a selected multi-solution head none of whose
            // projects loaded, issue #124) fails CLOSED: an empty, non-null project set matches nothing,
            // rather than degrading to an unfiltered whole-repository query.
            return solutionStore.Exists(solutionPath) ? new ScopeFilter { ProjectIds = [] } : ScopeFilter.None;
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
