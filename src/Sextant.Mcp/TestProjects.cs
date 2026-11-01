using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Which project versions of the pinned scope are test projects, so the agent tools can list production
/// code before test code (issue #241). Read once per call from the scope's project rows: no scan of the
/// occurrence, call-graph or relationship tables.
/// </summary>
internal static class TestProjects
{
    internal static HashSet<long> IdsIn(ProjectStore projectStore)
    {
        var ids = new HashSet<long>();
        foreach (var (id, project) in projectStore.GetAll())
            if (project.IsTestProject)
                ids.Add(id);
        return ids;
    }
}
