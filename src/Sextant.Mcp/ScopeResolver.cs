using Sextant.Core;
using Sextant.Store;
using Microsoft.Data.Sqlite;

namespace Sextant.Mcp;

internal static class ScopeResolver
{
    /// <summary>
    /// Parses a tool's <c>scope</c> argument. <c>file:</c> and <c>solution:</c> take a repository-relative path
    /// (issue #145); the local surface also keeps accepting the absolute path, and the remote surface refuses it
    /// (<see cref="ScopeFilter.Error"/>, an <c>invalid_argument</c>). <paramref name="paths"/> is the request's
    /// <see cref="FederatedReadContext.Paths"/>.
    /// </summary>
    public static ScopeFilter Resolve(
        string? scope, SqliteConnection conn, SnapshotReadScope? pinnedScope = null, PathPresenter? paths = null)
    {
        paths ??= PathPresenter.Local;
        if (string.IsNullOrEmpty(scope) || scope == "all")
            return ScopeFilter.None;

        if (scope.StartsWith("file:"))
        {
            var file = scope[5..];
            if (paths.Refuses(file))
                return ScopeFilter.Invalid(PathPresenter.AbsolutePathMessage("scope"));
            return new ScopeFilter { FilePath = file, Paths = paths };
        }

        if (scope.StartsWith("project:"))
        {
            var projectStore = new ProjectStore(conn) { Scope = pinnedScope };
            var proj = projectStore.GetByCanonicalId(scope[8..]);
            // An unknown project must not degrade to an unfiltered whole-repository query: the caller asked for a
            // narrower answer than that.
            if (proj == null)
                return ScopeFilter.Invalid(
                    $"Unknown project '{scope[8..]}' in scope '{scope}'. Use a project_id from a find_symbol result.");
            return new ScopeFilter { ProjectIds = new HashSet<long> { proj.Value.id } };
        }

        if (scope.StartsWith("solution:"))
        {
            var solutionPath = scope[9..];
            if (paths.Refuses(solutionPath))
                return ScopeFilter.Invalid(PathPresenter.AbsolutePathMessage("scope"));

            var solutionStore = new SolutionStore(conn);
            var projectIds = solutionStore.GetProjectIdsForSolution(solutionPath);
            var known = projectIds.Count > 0 || solutionStore.Exists(solutionPath);

            // Solutions are recorded by their absolute checkout path. A repository-relative path is resolved
            // against the checkout roots of the PINNED read's own projects only, so it can never name another
            // repository's solution.
            if (!known && !Path.IsPathRooted(solutionPath))
            {
                foreach (var candidate in SolutionCandidates(conn, pinnedScope, solutionPath))
                {
                    var ids = solutionStore.GetProjectIdsForSolution(candidate);
                    projectIds.UnionWith(ids);
                    known |= ids.Count > 0 || solutionStore.Exists(candidate);
                }
            }

            if (projectIds.Count > 0)
                return new ScopeFilter { ProjectIds = projectIds };
            // A KNOWN solution with no mapped project (e.g. a selected multi-solution head none of whose
            // projects loaded, issue #124) fails CLOSED: an empty, non-null project set matches nothing,
            // rather than degrading to an unfiltered whole-repository query.
            return known
                ? new ScopeFilter { ProjectIds = [] }
                : ScopeFilter.Invalid($"Unknown solution '{solutionPath}' in scope '{scope}'.");
        }

        return ScopeFilter.Invalid(
            $"Unrecognized scope '{scope}'. Use 'file:<path>', 'project:<project_id>', 'solution:<path>' (repository-relative paths), or 'all'.");
    }

    private static IEnumerable<string> SolutionCandidates(SqliteConnection conn, SnapshotReadScope? pinnedScope, string relative)
    {
        var normalized = relative.Trim().Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        var roots = new ProjectStore(conn) { Scope = pinnedScope }.GetAll()
            .Select(p => SourcePaths.DeriveRepoRoot(p.project.DiskPath, p.project.RepoRelativePath))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            // A '..' that climbs out of the checkout names no solution of this read.
            if (candidate.StartsWith(root.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                yield return candidate;
        }
    }
}

internal sealed class ScopeFilter
{
    public static readonly ScopeFilter None = new();
    public string? FilePath { get; init; }
    public HashSet<long>? ProjectIds { get; init; }

    /// <summary>Why the scope argument names nothing (an unknown project or solution, no scope form, or a refused
    /// absolute path), or null.</summary>
    public string? Error { get; init; }

    /// <summary>How <see cref="FilePath"/> is matched against stored paths.</summary>
    public PathPresenter Paths { get; init; } = PathPresenter.Local;

    public bool IsEmpty => FilePath == null && ProjectIds == null;

    public static ScopeFilter Invalid(string error) => new() { Error = error };

    /// <summary>The <c>invalid_argument</c> response for <see cref="Error"/>.</summary>
    public string ErrorResponse(SnapshotProvenance? provenance) =>
        ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, Error!, provenance);

    /// <summary>True when <paramref name="storedPath"/> is the file a <c>file:</c> scope names.</summary>
    public bool MatchesFile(string? storedPath) => FilePath != null && Paths.Matches(storedPath, FilePath);
}
