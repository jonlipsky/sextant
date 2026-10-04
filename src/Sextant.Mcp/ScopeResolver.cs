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
            // An unknown project must not degrade to an unfiltered whole-repository query: the caller asked for a
            // narrower answer than that.
            if (proj == null)
                return ScopeFilter.Invalid(
                    $"Unknown project '{scope[8..]}' in scope '{scope}'. Use a project canonical ID as listed by get_index_status.");
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
            return solutionStore.Exists(solutionPath)
                ? new ScopeFilter { ProjectIds = [] }
                : ScopeFilter.Invalid($"Unknown solution '{solutionPath}' in scope '{scope}'.");
        }

        return ScopeFilter.Invalid(
            $"Unrecognized scope '{scope}'. Use 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'.");
    }
}

internal sealed class ScopeFilter
{
    public static readonly ScopeFilter None = new();
    public string? FilePath { get; init; }
    public HashSet<long>? ProjectIds { get; init; }

    /// <summary>Why the scope argument names nothing (an unknown project or solution, or no scope form), or null.</summary>
    public string? Error { get; init; }

    public bool IsEmpty => FilePath == null && ProjectIds == null;

    public static ScopeFilter Invalid(string error) => new() { Error = error };

    /// <summary>The <c>invalid_argument</c> response for <see cref="Error"/>.</summary>
    public string ErrorResponse(SnapshotProvenance? provenance) =>
        ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, Error!, provenance);
}
