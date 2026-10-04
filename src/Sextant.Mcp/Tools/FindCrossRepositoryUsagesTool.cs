using System.ComponentModel;
using Sextant.Core;
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
        // Honour the Phase-17 fail-closed top-level read gate before touching any cross-repo data: a
        // denied read surfaces the uniform not-found, never an empty successful result.
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var scope = new CrossRepoUsageScope { Branch = branch, ConsumerCommitSha = consumer_commit };

        var outcome = CrossRepositoryUsageResolver.Resolve(
            conn, provider_repository_url, symbol_fqn, scope, dbProvider.Authorizer);

        // The name matches several different provider symbols: never union their usages as if they were one.
        if (outcome.Ambiguous)
        {
            var namer = new SymbolNamer(new SymbolStore(conn));
            var candidates = outcome.AmbiguousMatches
                .Take(Math.Min(outcome.AmbiguousMatchCount, SymbolResolver.MaxAmbiguousCandidates))
                .Select(s => namer.Candidate(s, new ProjectStore(conn).GetById(s.ProjectId)?.project.CanonicalId ?? ""))
                .ToList();
            return SymbolResolver.AmbiguousResponse(
                symbol_fqn.Trim(), candidates, outcome.AmbiguousMatchCount,
                $"in provider repository '{provider_repository_url}'", readContext.Provenance);
        }

        // The name did not resolve to a stable provider symbol identity: an FQN-only cross-repo match is prohibited,
        // so say so explicitly (as an error) instead of implying the symbol has zero usages. The message is the same
        // for a provider the caller may not read, so it reveals nothing about one.
        if (!outcome.StableIdentityResolved)
            return ResponseBuilder.BuildError(
                ResponseBuilder.SymbolNotFoundCode,
                $"No symbol '{symbol_fqn.Trim()}' is indexed in provider repository '{provider_repository_url}' with a " +
                "stable identity. Pass the symbol as a qualified name (Ns.Type, Ns.Type.Member or " +
                "Ns.Type.Method(int, string)) or a documentation ID, and the provider's repository URL; " +
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

        var resolved = Describe(conn, outcome.Symbol!);
        var message = results.Count == 0
            ? $"No authorized consumer repository uses {resolved} in the selected scope."
            : $"Usages of {resolved}.";
        return ResponseBuilder.Build(results, readContext.Provenance?.Freshness, provenance: readContext.Provenance, message: message);
    }

    private static string Describe(Microsoft.Data.Sqlite.SqliteConnection conn, SymbolInfo symbol) =>
        SymbolResolver.Describe(new SymbolNamer(new SymbolStore(conn)), symbol);
}
