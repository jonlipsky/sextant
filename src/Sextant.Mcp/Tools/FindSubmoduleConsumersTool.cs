using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindSubmoduleConsumersTool
{
    [McpServerTool(Name = "find_submodule_consumers"),
     Description("Reverse-dependency query for a shared submodule: list the repositories/projects that pin a given " +
                 "provider (submodule) repository, and at what commit. Answers \"who consumes this submodule\" from the " +
                 "dependency catalog without opening the consumer repositories. Results are limited to each authorized " +
                 "consumer's default-branch head (or an explicit branch/commit).")]
    public static string FindSubmoduleConsumers(
        DatabaseProvider dbProvider,
        [Description("Git remote URL of the shared submodule (provider) repository")]
        string provider_repository_url,
        [Description("Optional: restrict to consumers pinning this exact provider commit. Omit for all pins.")]
        string? provider_commit = null,
        [Description("Optional: restrict consumers to this branch head (by name). Omit for each repository's default branch.")]
        string? branch = null,
        [Description("Optional: restrict to consumers at this exact commit (historical scope). Omit for branch-head scope.")]
        string? consumer_commit = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var scope = new CrossRepoUsageScope { Branch = branch, ConsumerCommitSha = consumer_commit };

        var consumers = CrossRepositoryUsageResolver.ResolveConsumers(
            conn, provider_repository_url, provider_commit, scope, dbProvider.Authorizer);

        var results = consumers.Select(c => (object)new
        {
            consumer_repository = c.ConsumerRepositoryUrl,
            branch = c.ConsumerBranch,
            commit = c.ConsumerCommitSha,
            project = c.ConsumerProjectCanonicalId,
            provider_commit = c.ProviderCommitSha,
            submodule_dirty = c.SubmoduleDirty
        }).ToList();

        return ResponseBuilder.Build(results, readContext.Provenance?.Freshness, ambiguity: null, readContext.Provenance);
    }
}
