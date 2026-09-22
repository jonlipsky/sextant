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

        // Authorize the PROVIDER repository BEFORE resolving its symbol identity (Phase 17, criterion 1):
        // otherwise StableIdentityResolved is a provider-symbol existence oracle for a caller with no
        // access to the provider repo. Gated on IsEnforcing so the zero-policy local path is byte-identical
        // (AllowAll resolves nothing here). An unauthorized OR unknown provider collapses to the SAME
        // "no stable identity" outcome as a symbol that genuinely does not exist — no existence leak.
        if (authorizer.IsEnforcing)
        {
            // Authorize on the provider repository URL, INDEPENDENT of whether the provider exists (the
            // policy decision is URL-based; the id is not consulted). An unknown provider (id null → 0) and
            // an existing-but-denied provider therefore run the identical authorize-and-return path, so
            // neither the result nor the work performed distinguishes "denied" from "does not exist"
            // (criterion 1 — no timing/existence oracle).
            var providerRepoId = new SnapshotStore(conn).GetRepositoryId(providerRepositoryUrl);
            if (!authorizer.AuthorizeRepository(providerRepoId ?? 0, providerRepositoryUrl).Allowed)
                return new CrossRepositoryUsageResult(StableIdentityResolved: false, ResolvedSymbolKeys: [], Usages: []);
        }

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

        // Provider-repository authorization (Phase 17, criterion 1) FIRST, before enumerating any consumer
        // rows: a caller with no access to the provider must learn nothing about it — not that it exists,
        // and not (via query latency) how many consumers it has. IsEnforcing-gated so the local path is
        // byte-identical. An unknown/denied provider returns empty, indistinguishable from "no consumers".
        if (authorizer.IsEnforcing)
        {
            // URL-based authorization, INDEPENDENT of provider existence (see Resolve): unknown-and-denied
            // is indistinguishable from unknown-and-authorized-with-no-consumers — both return empty after
            // the same work, so query latency reveals neither existence nor consumer cardinality.
            var providerRepoId = new SnapshotStore(conn).GetRepositoryId(providerRepositoryUrl);
            if (!authorizer.AuthorizeRepository(providerRepoId ?? 0, providerRepositoryUrl).Allowed)
                return [];
        }

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
