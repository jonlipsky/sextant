using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindCrossRepositoryUsagesTool
{
    [McpServerTool(Name = "find_cross_repository_usages"),
     Description("Find where a symbol from a shared submodule (provider) repository is used across OTHER repositories, " +
                 "without opening them. Answers \"which apps use this method\" for code shared via a git submodule that " +
                 "Sextant indexed once. Results come from each authorized consumer repository's default-branch head " +
                 "(or an explicit branch/commit), and are bound to the provider's stable symbol identity, never an FQN " +
                 "string match.")]
    public static string FindCrossRepositoryUsages(
        DatabaseProvider dbProvider,
        [Description("Git remote URL of the shared submodule (provider) repository, e.g. 'https://github.com/org/MixAndMatch.git'")]
        string provider_repository_url,
        [Description("Fully qualified name of the provider symbol to find usages of")]
        string symbol_fqn,
        [Description("Optional: restrict consumers to this branch head (by name). Omit for each repository's default branch.")]
        string? branch = null,
        [Description("Optional: restrict to consumers at this exact commit (historical scope). Omit for branch-head scope.")]
        string? consumer_commit = null)
    {
        var db = dbProvider.GetReadyDatabase(out var notReady);
        if (db == null)
            return ResponseBuilder.BuildEmpty(notReady);

        // Honour the Phase-11 fail-closed top-level read gate before touching any cross-repo data: a
        // denied read must surface a structured error, never an empty successful result.
        if (!ReadContextGate.TryResolve(db, out var readContext, out var authError, authorizer: dbProvider.Authorizer))
            return authError;

        using var conn = db.OpenReadConnection();
        var scope = new CrossRepoUsageScope { Branch = branch, ConsumerCommitSha = consumer_commit };

        var outcome = CrossRepositoryUsageResolver.Resolve(
            conn, provider_repository_url, symbol_fqn, scope, dbProvider.Authorizer);

        // FQN did not resolve to a stable provider symbol identity: an FQN-only cross-repo match is
        // prohibited, so say so explicitly instead of implying the symbol has zero usages.
        if (!outcome.StableIdentityResolved)
            return ResponseBuilder.BuildEmpty(
                $"No stable symbol identity for '{symbol_fqn}' in provider repository '{provider_repository_url}'. " +
                "A cross-repository usage query requires the provider symbol to be indexed (stable identity); " +
                "an FQN-only match is not performed.",
                readContext.Provenance);

        var results = outcome.Usages.Select(u => (object)new
        {
            consumer_repository = u.ConsumerRepositoryUrl,
            branch = u.ConsumerBranch,
            commit = u.ConsumerCommitSha,
            project = u.ConsumerProjectCanonicalId,
            file_path = u.FilePath,
            line = u.Line,
            column = u.Column,
            usage_kind = u.OccurrenceKind,
            provider_commit = u.ProviderCommitSha,
            submodule_dirty = u.SubmoduleDirty
        }).ToList();

        // A display FQN is not a unique identity: overloads and same-named members of the provider collapse
        // to one string, so it can resolve to SEVERAL distinct stable keys. The query deliberately unions
        // every resolved key's usages (the caller asked for "this method" by name), but that must not be
        // silent — surface the ambiguity through the standard meta channel (ambiguous + candidate keys) so
        // the caller can tell the FQN was ambiguous and narrow by stable key, instead of reading a merged,
        // symbol-agnostic result set as if the FQN were unique.
        SymbolAmbiguity? ambiguity = null;
        if (outcome.ResolvedSymbolKeys.Count > 1)
        {
            var candidates = outcome.ResolvedSymbolKeys
                .Select(key => new SymbolCandidate(
                    ProjectId: provider_repository_url,
                    SymbolKey: key,
                    FullyQualifiedName: symbol_fqn,
                    Kind: "",
                    FilePath: "",
                    LineStart: 0))
                .ToList();
            ambiguity = new SymbolAmbiguity
            {
                Candidates = candidates,
                SelectedProjectId = provider_repository_url,
                SelectedSymbolKey = outcome.ResolvedSymbolKeys[0]
            };
        }

        return ResponseBuilder.Build(results, readContext.Provenance?.Freshness, ambiguity, readContext.Provenance);
    }
}
