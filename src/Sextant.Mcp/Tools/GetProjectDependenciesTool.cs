using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetProjectDependenciesTool
{
    [McpServerTool(Name = "get_project_dependencies"), Description("Project references, direct or transitive. Use instead of reading .csproj files.")]
    public static string GetProjectDependencies(
        DatabaseProvider dbProvider,
        [Description("canonical_id from get_index_status.")] string project_id,
        bool transitive = false)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };
        var dependencyStore = new ProjectDependencyStore(conn);

        var project = projectStore.GetByCanonicalId(project_id);
        if (project == null)
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"Unknown project_id '{project_id}'. Use a project canonical ID as listed by get_index_status.",
                readContext.Provenance);

        var visited = new HashSet<long>();
        var results = new List<object>();

        CollectDependencies(project.Value.id, 0, transitive, visited, results, projectStore, dependencyStore);

        return ResponseBuilder.Build(results, project.Value.lastIndexedAt, provenance: readContext.Provenance,
            message: results.Count == 0 ? $"Project '{project_id}' has no indexed project dependencies." : null);
    }

    private static void CollectDependencies(
        long projectId, int depth, bool transitive,
        HashSet<long> visited, List<object> results,
        ProjectStore projectStore, ProjectDependencyStore dependencyStore)
    {
        if (!visited.Add(projectId))
            return;

        var deps = dependencyStore.GetByConsumer(projectId);
        foreach (var dep in deps)
        {
            var depProject = projectStore.GetById(dep.DependencyProjectId);
            if (depProject == null) continue;

            results.Add(new
            {
                consumer_canonical_id = projectStore.GetById(dep.ConsumerProjectId)?.project.CanonicalId,
                dependency = new
                {
                    canonical_id = depProject.Value.project.CanonicalId,
                    git_remote_url = depProject.Value.project.GitRemoteUrl,
                    repo_relative_path = depProject.Value.project.RepoRelativePath,
                    assembly_name = depProject.Value.project.AssemblyName,
                    is_test_project = depProject.Value.project.IsTestProject
                },
                reference_kind = dep.ReferenceKind,
                submodule_pinned_commit = dep.SubmodulePinnedCommit,
                depth
            });

            if (transitive)
            {
                CollectDependencies(dep.DependencyProjectId, depth + 1, true, visited, results, projectStore, dependencyStore);
            }
        }
    }
}
