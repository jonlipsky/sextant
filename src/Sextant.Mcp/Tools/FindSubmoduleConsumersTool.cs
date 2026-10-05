using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindSubmoduleConsumersTool
{
    [McpServerTool(Name = "find_submodule_consumers"),
     Description("Repositories pinning a shared submodule, and at which commit. Use instead of reading .gitmodules.")]
    public static string FindSubmoduleConsumers(
        DatabaseProvider dbProvider,
        [Description("Submodule repository URL.")]
        string provider_repository_url,
        [Description("Pinned commit.")]
        string? provider_commit = null,
        [Description("Branch of the consumers.")]
        string? branch = null,
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

        return ResponseBuilder.BuildBounded(results, readContext, readContext.Provenance?.Freshness, ambiguity: null,
            readContext.Provenance);
    }
}
