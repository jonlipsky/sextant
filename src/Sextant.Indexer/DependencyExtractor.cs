using Sextant.Core;
using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

public static class DependencyExtractor
{
    /// <summary>
    /// Extracts project dependencies from a solution, classifying each as project_ref, submodule_ref, or nuget_ref.
    /// </summary>
    public static List<ProjectDependency> ExtractDependencies(
        Solution solution,
        IReadOnlyDictionary<string, long> projectPathToId,
        IReadOnlyList<SubmoduleInfo> submodules,
        string repoRoot)
    {
        var results = new List<ProjectDependency>();

        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null || !projectPathToId.TryGetValue(project.FilePath, out var consumerId))
                continue;

            // Process ProjectReference items
            foreach (var projectRef in project.ProjectReferences)
            {
                var referencedProject = solution.GetProject(projectRef.ProjectId);
                if (referencedProject?.FilePath == null)
                    continue;

                if (!projectPathToId.TryGetValue(referencedProject.FilePath, out var dependencyId))
                    continue;

                var submodule = SubmoduleDiscovery.FindContainingSubmodule(
                    referencedProject.FilePath, submodules, repoRoot);

                if (submodule != null)
                {
                    results.Add(new ProjectDependency
                    {
                        ConsumerProjectId = consumerId,
                        DependencyProjectId = dependencyId,
                        ReferenceKind = "submodule_ref",
                        SubmodulePinnedCommit = submodule.CommitSha
                    });
                }
                else
                {
                    results.Add(new ProjectDependency
                    {
                        ConsumerProjectId = consumerId,
                        DependencyProjectId = dependencyId,
                        ReferenceKind = "project_ref"
                    });
                }
            }

            // Process NuGet MetadataReferences — record as nuget_ref
            foreach (var metadataRef in project.MetadataReferences)
            {
                if (metadataRef is not PortableExecutableReference peRef)
                    continue;

                var displayPath = peRef.Display;
                if (string.IsNullOrEmpty(displayPath))
                    continue;

                // Heuristic: NuGet packages live under a "packages" or ".nuget" directory
                var lowerPath = displayPath.ToLowerInvariant();
                if (!lowerPath.Contains(".nuget") && !lowerPath.Contains("packages"))
                    continue;

                // Extract package name from path (e.g., .../microsoft.data.sqlite/10.0.3/...)
                var packageName = ExtractNuGetPackageName(displayPath);
                if (packageName == null) continue;

                results.Add(new ProjectDependency
                {
                    ConsumerProjectId = consumerId,
                    DependencyProjectId = 0, // No indexed project for NuGet packages
                    ReferenceKind = "nuget_ref",
                    SubmodulePinnedCommit = packageName // Store the package name for identification
                });
            }
        }

        return results;
    }

    private static string? ExtractNuGetPackageName(string path)
    {
        // Path typically looks like: .../.nuget/packages/<name>/<version>/lib/<tfm>/<assembly>.dll
        var parts = path.Replace('\\', '/').Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("packages", StringComparison.OrdinalIgnoreCase) && i + 2 < parts.Length)
                return parts[i + 1];
        }
        return null;
    }
}
