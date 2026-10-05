using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// Restores the transitive <c>ProjectReference</c> flow the .NET SDK gives a RESTORED project but an unrestored
/// one lacks. The SDK adds every project reachable through a project's references (its
/// <c>IncludeTransitiveProjectReferences</c> step) from <c>obj/project.assets.json</c>; with no assets file the
/// design-time build passes only the DIRECT references to the compiler. Code that names a type from a project it
/// reaches only transitively then fails to bind (CS0012), and so does every call whose signature mentions such a
/// type, so references and call edges at those sites would be lost. MSBuildWorkspace reports no load failure in
/// that case, so the gap is invisible unless the loader closes it.
/// </summary>
/// <remarks>
/// Only a project with no assets file at the conventional <c>obj/project.assets.json</c> is changed: a restored
/// project already has the SDK's own closure (which honours <c>PrivateAssets</c> and
/// <c>DisableTransitiveProjectReferences</c>), so a healthy, restored solution is returned unchanged. For an
/// unrestored project (or one using a non-default intermediate path) every project reachable through the
/// ORIGINAL reference graph is added, which over-includes a reference the SDK would have kept private; that can
/// only make more names bind. A target the project already references directly (any target-framework variant)
/// or by a metadata reference to an assembly of the same name is skipped, so no duplicate assembly identity is
/// introduced. Traversal is breadth-first over the original graph in reference order, so the result is
/// deterministic, and an added edge follows existing reachability, so it cannot create a cycle.
/// </remarks>
public static class TransitiveProjectReferences
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Adds the missing transitive project references of every unrestored project in <paramref name="solution"/>.
    /// Returns the solution unchanged when nothing is missing. <paramref name="onDiagnostic"/> receives one line
    /// naming how many references were added, when any were.
    /// </summary>
    public static Solution Close(Solution solution, Action<string>? onDiagnostic = null)
        => Close(solution, project => !HasRestoreAssets(project), onDiagnostic);

    /// <summary>
    /// Adds the missing transitive project references of every project <paramref name="needsClosure"/> selects.
    /// Internal so tests can close an in-memory solution regardless of what is on disk.
    /// </summary>
    internal static Solution Close(Solution solution, Func<Project, bool> needsClosure, Action<string>? onDiagnostic)
    {
        var original = solution;
        var added = 0;
        var projectsChanged = 0;
        foreach (var projectId in original.ProjectIds)
        {
            var project = original.GetProject(projectId);
            if (project == null || !project.ProjectReferences.Any() || !needsClosure(project))
                continue;

            var missing = MissingTransitiveReferences(original, project);
            if (missing.Count == 0)
                continue;

            try
            {
                // Not AddProjectReferences: Roslyn's submission check there resolves every EXISTING reference with
                // GetRequiredProjectState, so a project that keeps a reference to a project the workspace never
                // loaded fails with "Unexpected null" (Roslyn 5.0.0, Solution.CheckSubmissionProjectReferences).
                // WithProjectReferences skips existing references; AllProjectReferences keeps the dangling ones.
                var current = solution.GetProject(projectId)!;
                solution = solution.WithProjectReferences(projectId, current.AllProjectReferences.Concat(missing));
                added += missing.Count;
                projectsChanged++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Roslyn refuses a reference it considers a cycle or a duplicate; keep the project as loaded.
                onDiagnostic?.Invoke(
                    $"Could not add transitive project references to '{project.Name}': {ex.Message}");
            }
        }

        if (added > 0)
            onDiagnostic?.Invoke(
                $"Added {added} transitive project reference(s) to {projectsChanged} unrestored project(s) " +
                "so types reached through their project references bind.");
        return solution;
    }

    /// <summary>
    /// True when the project has a NuGet restore assets file at the conventional
    /// <c>obj/project.assets.json</c> next to its project file.
    /// </summary>
    public static bool HasRestoreAssets(Project project)
    {
        if (project.FilePath is not { Length: > 0 } path)
            return false;
        var directory = Path.GetDirectoryName(path);
        return directory != null && File.Exists(Path.Combine(directory, "obj", "project.assets.json"));
    }

    private static List<ProjectReference> MissingTransitiveReferences(Solution solution, Project project)
    {
        var directIds = new HashSet<ProjectId>(project.ProjectReferences.Select(r => r.ProjectId));
        var directPaths = new HashSet<string>(PathComparer);
        foreach (var id in directIds)
        {
            if (solution.GetProject(id)?.FilePath is { } directPath)
                directPaths.Add(directPath);
        }
        var metadataNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
        {
            if (reference.FilePath is { } file)
                metadataNames.Add(Path.GetFileNameWithoutExtension(file));
        }

        var missing = new List<ProjectReference>();
        var addedPaths = new HashSet<string>(PathComparer);
        var visited = new HashSet<ProjectId> { project.Id };
        var queue = new Queue<ProjectId>(project.ProjectReferences.Select(r => r.ProjectId));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id) || solution.GetProject(id) is not { } target)
                continue;

            foreach (var next in target.ProjectReferences)
                queue.Enqueue(next.ProjectId);

            if (directIds.Contains(id) || metadataNames.Contains(target.AssemblyName))
                continue;
            if (target.FilePath is { } targetPath
                && (directPaths.Contains(targetPath) || !addedPaths.Add(targetPath)))
                continue;
            missing.Add(new ProjectReference(id));
        }
        return missing;
    }
}
