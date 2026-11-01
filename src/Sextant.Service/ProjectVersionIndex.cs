using Sextant.Indexer;

namespace Sextant.Service;

/// <summary>
/// Every target-framework version of each project of a load (issue #351), keyed by the repository-relative path
/// coverage names the project by. A restore failure is excluded only when every version of the project is
/// excluded, so a version the load never reported (a declared project with none) is a null-framework version
/// that no exclusion covers unless one names it.
/// </summary>
public sealed class ProjectVersionIndex
{
    private readonly Dictionary<string, HashSet<string?>> _versions = new(StringComparer.Ordinal);

    public ProjectVersionIndex(string checkoutDir, MultiSolutionLoadResult load)
    {
        string Key(string path) => SnapshotCoverageBuilder.ReasonPath(checkoutDir, path);
        void Add(string path, string? tfm)
        {
            var key = Key(path);
            if (!_versions.TryGetValue(key, out var set))
                _versions[key] = set = [];
            set.Add(string.IsNullOrWhiteSpace(tfm) ? null : tfm);
        }

        foreach (var project in load.Solution.Projects.Where(p => !string.IsNullOrEmpty(p.FilePath)))
            Add(project.FilePath!, ProjectIdentityFactory.ResolveEvaluatedTargetFramework(project));
        foreach (var skipped in load.SkippedProjects)
            Add(skipped.ProjectPath, skipped.TargetFramework);
        foreach (var degraded in load.DegradedProjects)
            Add(degraded.ProjectPath, degraded.TargetFramework);
        foreach (var declared in load.DeclaredProjects)
            if (!_versions.ContainsKey(Key(declared)))
                Add(declared, null);
    }

    /// <summary>
    /// Records <paramref name="project"/> (checkout-relative) as a project the load never saw, with its one null-framework
    /// version, so that a restore failure naming it can be explained by an exclusion of that version. A known project is unchanged.
    /// </summary>
    public void AddUnloaded(string project)
    {
        if (!_versions.ContainsKey(project))
            _versions[project] = [null];
    }

    /// <summary>The known versions of <paramref name="project"/>; empty when the load never saw it.</summary>
    public IReadOnlyCollection<string?> VersionsOf(string project)
        => _versions.TryGetValue(project, out var set) ? set : [];
}
