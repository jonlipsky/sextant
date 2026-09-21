using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// The outcome of resolving cross-repository usages of a provider (submodule) symbol. Distinguishes the
/// three cases the caller must present differently: the input FQN could not be resolved to a stable
/// provider symbol identity (<see cref="StableIdentityResolved"/> false — an FQN-only cross-repo match
/// is PROHIBITED, so this is NOT reported as "no usages"); the FQN resolved but no authorized consumer
/// uses it (resolved, empty <see cref="Usages"/>); or authorized usages were found.
/// </summary>
public sealed record CrossRepositoryUsageResult(
    bool StableIdentityResolved,
    IReadOnlyList<string> ResolvedSymbolKeys,
    IReadOnlyList<CrossRepositoryUsage> Usages)
{
    /// <summary>
    /// The number of DISTINCT provider stable keys the input display FQN resolved to. A display FQN is not
    /// a unique identity (overloads and same-named members collapse to one string), so this is >1 when the
    /// FQN is ambiguous within the provider; the usage query then unions every resolved key's occurrences
    /// and the tool surfaces the ambiguity in <c>meta</c> so the caller can narrow by stable key.
    /// </summary>
    public int ResolvedSymbolKeyCount => ResolvedSymbolKeys.Count;
}

/// <summary>
/// Orchestrates a Phase-12 cross-repository usage query on top of the <see cref="SnapshotDependencyStore"/>,
/// applying the fail-closed per-repository authorization seam (criterion 4). It (1) resolves the human
/// input FQN to the provider's stable <c>symbol_key</c>(s) — refusing an FQN-only match when no stable
/// identity exists (#32 / spec prohibition), (2) enumerates the candidate consumer repositories in scope,
/// (3) runs each through <see cref="IReadAuthorizer.AuthorizeRepository"/>, and (4) returns only the
/// occurrences owned by authorized repositories — so an inaccessible repository contributes neither a
/// result nor any existence/count metadata. Kept separate from the MCP tool so it is directly testable
/// with an injected authorizer (mirroring how Phase-11's authorizer seam is tested through the context).
/// </summary>
public static class CrossRepositoryUsageResolver
{
    public static CrossRepositoryUsageResult Resolve(
        SqliteConnection conn,
        string providerRepositoryUrl,
        string symbolFqn,
        CrossRepoUsageScope scope,
        IReadAuthorizer authorizer)
    {
        var store = new SnapshotDependencyStore(conn);

        // Bind the input FQN to the provider's STABLE symbol key(s). No key ⇒ no stable identity /
        // assembly lineage for this name in the provider repo, so a cross-repo match is prohibited: report
        // that explicitly rather than silently searching by FQN string (which could conflate two repos'
        // same-named-but-distinct symbols — #32).
        var providerKeys = store.ResolveProviderSymbolKeysByFqn(providerRepositoryUrl, symbolFqn);
        if (providerKeys.Count == 0)
            return new CrossRepositoryUsageResult(StableIdentityResolved: false, ResolvedSymbolKeys: [], Usages: []);

        // Authorize each candidate consumer repository ONCE (decision is per-repo, independent of which
        // provider key it uses) and fail closed: only authorized ids flow into the usage query.
        var authorizationMemo = new Dictionary<long, bool>();
        var usages = new List<CrossRepositoryUsage>();

        foreach (var providerKey in providerKeys)
        {
            var candidates = store.GetCandidateConsumerRepositories(providerRepositoryUrl, providerKey, scope);
            var authorizedIds = new List<long>();
            foreach (var (repositoryId, remoteUrl) in candidates)
            {
                if (!authorizationMemo.TryGetValue(repositoryId, out var allowed))
                {
                    allowed = authorizer.AuthorizeRepository(repositoryId, remoteUrl).Allowed;
                    authorizationMemo[repositoryId] = allowed;
                }
                if (allowed)
                    authorizedIds.Add(repositoryId);
            }

            usages.AddRange(store.FindCrossRepositoryUsages(
                providerRepositoryUrl, providerKey, scope, authorizedIds));
        }

        return new CrossRepositoryUsageResult(
            StableIdentityResolved: true, ResolvedSymbolKeys: providerKeys, Usages: usages);
    }

    /// <summary>
    /// Reverse-dependency query: the authorized consumer repositories that pin a shared submodule
    /// (provider) repository (optionally at a specific commit), under the given scope. Each candidate
    /// consumer repository is run through the fail-closed authorizer, so an inaccessible repository
    /// contributes neither a row nor any count/existence metadata (criterion 4).
    /// </summary>
    public static IReadOnlyList<SubmoduleConsumer> ResolveConsumers(
        SqliteConnection conn,
        string providerRepositoryUrl,
        string? providerCommitSha,
        CrossRepoUsageScope scope,
        IReadAuthorizer authorizer)
    {
        var store = new SnapshotDependencyStore(conn);
        var rows = store.GetConsumersByProviderRepository(providerRepositoryUrl, providerCommitSha, scope);

        var authorizationMemo = new Dictionary<long, bool>();
        var authorized = new List<SubmoduleConsumer>();
        foreach (var row in rows)
        {
            if (!authorizationMemo.TryGetValue(row.ConsumerRepositoryId, out var allowed))
            {
                allowed = authorizer.AuthorizeRepository(row.ConsumerRepositoryId, row.ConsumerRepositoryUrl).Allowed;
                authorizationMemo[row.ConsumerRepositoryId] = allowed;
            }
            if (allowed)
                authorized.Add(row);
        }
        return authorized;
    }
}
