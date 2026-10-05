using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindCrossRepositoryUsagesTool
{
    [McpServerTool(Name = "find_cross_repository_usages"),
     Description("Uses of a shared submodule symbol in OTHER repositories. Use instead of searching each consumer.")]
    public static string FindCrossRepositoryUsages(
        DatabaseProvider dbProvider,
        [Description("Submodule repository URL.")]
        string provider_repository_url,
        [Description(ToolText.SymbolFqn)]
        string symbol_fqn,
        [Description("Branch of the consumers.")]
        string? branch = null,
        string? consumer_commit = null,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
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

        // The rows come from each consumer's own branch head, not the selected snapshot, so the cursor is also bound
        // to the consumer commits it paged: a consumer that moved between pages makes it invalid_cursor.
        if (!Paging.TryBegin("find_cross_repository_usages", limit, cursor, readContext, out var page, out var cursorError,
                provider_repository_url, symbol_fqn, branch, consumer_commit, ConsumerHeads(outcome.Usages)))
            return cursorError;

        object? Summary() => new { ByRepository = Paging.CountBy(outcome.Usages, u => u.ConsumerRepositoryUrl) };

        var results = page.Slice(outcome.Usages).Select(u => (object)new
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
        var message = outcome.Usages.Count == 0
            ? $"No authorized consumer repository uses {resolved} in the selected scope."
            : $"Usages of {resolved}.";
        return ResponseBuilder.BuildPage(results, outcome.Usages.Count, page, readContext.Provenance?.Freshness,
            provenance: readContext.Provenance, summary: Summary, message: message);
    }

    private static string Describe(Microsoft.Data.Sqlite.SqliteConnection conn, SymbolInfo symbol) =>
        SymbolResolver.Describe(new SymbolNamer(new SymbolStore(conn)), symbol);

    private static string ConsumerHeads(IEnumerable<CrossRepositoryUsage> usages) =>
        string.Join("\n", usages
            .Select(u => $"{u.ConsumerRepositoryUrl}\t{u.ConsumerBranch}\t{u.ConsumerCommitSha}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
}
