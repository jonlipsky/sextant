using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// The optional <c>project_id</c> argument of the symbol tools (issue #224): when one symbol key is declared in several
/// project versions (a multi-targeted project, or the same source compiled into two projects), it picks the version
/// the tool answers for. Without it the resolver keeps its default (the first version, the others listed in
/// <c>meta</c>).
/// </summary>
internal static class ProjectArgument
{
    /// <summary>
    /// <paramref name="options"/> restricted to the project version <paramref name="projectId"/> names (a canonical id
    /// from <c>find_symbol</c>), so the lookup binds that version's row. A null or blank id leaves the options as they
    /// are. An unknown id is an <c>invalid_argument</c> error response, never an unrestricted lookup.
    /// </summary>
    public static bool TryRestrict(
        string? projectId, ProjectStore projects, SymbolLookupOptions options, SnapshotProvenance? provenance,
        out SymbolLookupOptions restricted, out string? errorResponse)
    {
        restricted = options;
        errorResponse = null;
        if (string.IsNullOrWhiteSpace(projectId))
            return true;
        var id = projectId.Trim();
        if (projects.GetByCanonicalId(id) is not { } project)
        {
            errorResponse = UnknownProject(id, provenance);
            return false;
        }
        restricted = options with { ProjectIds = new HashSet<long> { project.id }, ScopeDescription = $"in project '{id}'" };
        return true;
    }

    /// <summary>The <c>invalid_argument</c> error for a <c>project_id</c> no indexed project version has.</summary>
    public static string UnknownProject(string projectId, SnapshotProvenance? provenance) =>
        ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
            $"Unknown project_id '{projectId}'. Use a project_id from a find_symbol result.", provenance);
}
