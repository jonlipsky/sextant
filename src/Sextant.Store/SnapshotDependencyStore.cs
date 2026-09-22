using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store;

/// <summary>
/// Scope of a Phase-12 cross-repository usage query. The default is the current default-branch head of
/// each consumer repository (criterion 3) — historical retained snapshots are excluded so a symbol used
/// once is not reported many times. An explicit branch or consumer commit selects a historical scope
/// (criterion 5); results are still resolved by the provider's stable <c>symbol_key</c>, so distinct
/// definitions that share an FQN are never conflated (#32).
/// </summary>
public sealed record CrossRepoUsageScope
{
    /// <summary>Restrict to a specific consumer branch head (by name). Null with no commit = the default branch.</summary>
    public string? Branch { get; init; }

    /// <summary>Restrict to a specific consumer commit (historical, criterion 5). Null = branch-head scope.</summary>
    public string? ConsumerCommitSha { get; init; }

    /// <summary>The default scope: each authorized consumer repository's current default-branch head.</summary>
    public static CrossRepoUsageScope DefaultHeads { get; } = new();
}

/// <summary>
/// The Phase-12 reverse-dependency catalog (<c>snapshot_dependencies</c>): one immutable edge per
/// (consumer project version -> deduplicated submodule PROVIDER project version), carrying the parent's
/// exact pin and dirty state. It is the narrowing index for cross-repository usage queries — an
/// authorized-consumer filter is applied through this catalog BEFORE the provider symbol's occurrences
/// are searched — and the retention anchor that keeps a shared provider alive while any parent pins it.
/// A thin adapter over parameterized SQL on the caller's connection.
/// </summary>
public sealed class SnapshotDependencyStore(SqliteConnection connection)
{
    /// <summary>
    /// Inserts (or idempotently refreshes) a dependency edge. Keyed by (consumer project version ->
    /// provider project version) so a re-index of the same consumer generation re-records the same edge
    /// without duplicating it.
    /// </summary>
    public long Insert(SnapshotDependencyEdge edge)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshot_dependencies
                (consumer_snapshot_id, consumer_project_id, provider_snapshot_id, provider_project_id,
                 provider_repository_id, provider_commit_sha, reference_kind, submodule_dirty, created_at)
            VALUES (@consumer_snapshot_id, @consumer_project_id, @provider_snapshot_id, @provider_project_id,
                 @provider_repository_id, @provider_commit_sha, @reference_kind, @submodule_dirty, @created_at)
            ON CONFLICT(consumer_project_id, provider_project_id) DO UPDATE SET
                consumer_snapshot_id = excluded.consumer_snapshot_id,
                provider_snapshot_id = excluded.provider_snapshot_id,
                provider_repository_id = excluded.provider_repository_id,
                provider_commit_sha = excluded.provider_commit_sha,
                reference_kind = excluded.reference_kind,
                submodule_dirty = excluded.submodule_dirty
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@consumer_snapshot_id", edge.ConsumerSnapshotId);
        cmd.Parameters.AddWithValue("@consumer_project_id", edge.ConsumerProjectId);
        cmd.Parameters.AddWithValue("@provider_snapshot_id", edge.ProviderSnapshotId);
        cmd.Parameters.AddWithValue("@provider_project_id", edge.ProviderProjectId);
        cmd.Parameters.AddWithValue("@provider_repository_id", edge.ProviderRepositoryId);
        cmd.Parameters.AddWithValue("@provider_commit_sha", edge.ProviderCommitSha);
        cmd.Parameters.AddWithValue("@submodule_dirty", edge.SubmoduleDirty ? 1 : 0);
        cmd.Parameters.AddWithValue("@reference_kind", edge.ReferenceKind);
        cmd.Parameters.AddWithValue("@created_at", edge.CreatedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Every dependency edge recorded for a consumer snapshot (criterion 2: per-parent pins).</summary>
    public List<SnapshotDependencyEdge> GetByConsumerSnapshot(long consumerSnapshotId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectEdge + " WHERE consumer_snapshot_id = @id ORDER BY id;";
        cmd.Parameters.AddWithValue("@id", consumerSnapshotId);
        return ReadEdges(cmd);
    }

    /// <summary>Every consumer edge pinning a given provider project version (reverse dependency).</summary>
    public List<SnapshotDependencyEdge> GetByProviderProject(long providerProjectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectEdge + " WHERE provider_project_id = @id ORDER BY id;";
        cmd.Parameters.AddWithValue("@id", providerProjectId);
        return ReadEdges(cmd);
    }

    /// <summary>
    /// Distinct provider project versions in a repository that expose a symbol with the given stable
    /// <c>symbol_key</c> AND are actually pinned by at least one consumer edge. Used to reject
    /// FQN-only cross-repo queries: an input FQN must first resolve to exactly one stable key here (via
    /// <see cref="ResolveProviderSymbolKeysByFqn"/>); the usage query then binds to that key, never a
    /// display string, so two repos that share an FQN are never conflated (#32).
    /// </summary>
    public List<string> ResolveProviderSymbolKeysByFqn(string providerRepositoryUrl, string fullyQualifiedName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT s.symbol_key
            FROM snapshot_dependencies d
            JOIN repositories r ON r.id = d.provider_repository_id
            JOIN symbols s ON s.project_id = d.provider_project_id
            WHERE r.remote_url = @url AND s.fully_qualified_name = @fqn
            ORDER BY s.symbol_key;
            """;
        cmd.Parameters.AddWithValue("@url", providerRepositoryUrl);
        cmd.Parameters.AddWithValue("@fqn", fullyQualifiedName);
        var keys = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>
    /// The distinct consumer repositories (id + remote url) that reference a provider symbol under the
    /// given scope, BEFORE authorization. The MCP layer runs each through the fail-closed authorizer and
    /// passes the surviving ids to <see cref="FindCrossRepositoryUsages"/>, so an inaccessible repository
    /// contributes neither results nor existence/count metadata (criterion 4).
    /// </summary>
    public List<(long repositoryId, string remoteUrl)> GetCandidateConsumerRepositories(
        string providerRepositoryUrl, string providerSymbolKey, CrossRepoUsageScope scope)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT crepo.id, crepo.remote_url
            {UsageFromWhere(scope)};
            """;
        BindUsageParameters(cmd, providerRepositoryUrl, providerSymbolKey, scope);
        var rows = new List<(long, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        return rows;
    }

    /// <summary>
    /// Reverse-dependency listing: the distinct consumer repository/project pins of a shared submodule
    /// (provider) repository under the given scope, BEFORE authorization (the caller filters by the
    /// fail-closed authorizer, criterion 4). Independent of any particular provider symbol — it answers
    /// "which repositories consume this submodule and at what commit" straight from the dependency
    /// catalog. An optional <paramref name="providerCommitSha"/> narrows to one pin.
    /// </summary>
    public List<SubmoduleConsumer> GetConsumersByProviderRepository(
        string providerRepositoryUrl, string? providerCommitSha, CrossRepoUsageScope scope)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT crepo.id, crepo.remote_url, cb.name, cc.commit_sha, clp.canonical_id,
                   d.provider_commit_sha, d.submodule_dirty
            {ConsumerFromWhere(scope)}{(providerCommitSha != null ? " AND d.provider_commit_sha = @provider_commit" : "")}
            ORDER BY crepo.remote_url, clp.canonical_id;
            """;
        cmd.Parameters.AddWithValue("@provider_url", providerRepositoryUrl);
        cmd.Parameters.AddWithValue("@published_complete", SnapshotStatus.Complete);
        cmd.Parameters.AddWithValue("@published_superseded", SnapshotStatus.Superseded);
        if (scope.ConsumerCommitSha != null)
            cmd.Parameters.AddWithValue("@consumer_commit", scope.ConsumerCommitSha);
        else if (scope.Branch != null)
            cmd.Parameters.AddWithValue("@branch", scope.Branch);
        if (providerCommitSha != null)
            cmd.Parameters.AddWithValue("@provider_commit", providerCommitSha);

        var rows = new List<SubmoduleConsumer>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SubmoduleConsumer
            {
                ConsumerRepositoryId = reader.GetInt64(0),
                ConsumerRepositoryUrl = reader.GetString(1),
                ConsumerBranch = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ConsumerCommitSha = reader.IsDBNull(3) ? null : reader.GetString(3),
                ConsumerProjectCanonicalId = reader.GetString(4),
                ProviderCommitSha = reader.GetString(5),
                SubmoduleDirty = !reader.IsDBNull(6) && reader.GetInt64(6) != 0
            });
        }
        return rows;
    }

    /// <summary>
    /// Authorized cross-repository usages of a provider (submodule) symbol identified by its stable
    /// <c>symbol_key</c>. Narrows to consumer snapshots in the scope (default = default-branch heads),
    /// restricts to <paramref name="authorizedConsumerRepositoryIds"/> when provided (fail-closed:
    /// null = allow-all, empty = deny-all), then returns the target symbol's occurrences in those
    /// consumers with the consumer location and the exact pinned provider version.
    /// </summary>
    public List<CrossRepositoryUsage> FindCrossRepositoryUsages(
        string providerRepositoryUrl, string providerSymbolKey, CrossRepoUsageScope scope,
        IReadOnlyCollection<long>? authorizedConsumerRepositoryIds)
    {
        // Fail closed: an explicit empty authorization set contributes nothing.
        if (authorizedConsumerRepositoryIds is { Count: 0 })
            return [];

        var authFilter = "";
        if (authorizedConsumerRepositoryIds is { Count: > 0 })
        {
            var ids = string.Join(",", authorizedConsumerRepositoryIds);
            authFilter = $" AND crepo.id IN ({ids})";
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT crepo.remote_url, cb.name, cc.commit_sha, clp.canonical_id,
                   cf.repo_relative_path, o.line, o.col, o.kind, d.provider_commit_sha, d.submodule_dirty
            {UsageFromWhere(scope)}{authFilter}
            ORDER BY crepo.remote_url, clp.canonical_id, cf.repo_relative_path, o.line, o.col;
            """;
        BindUsageParameters(cmd, providerRepositoryUrl, providerSymbolKey, scope);

        var results = new List<CrossRepositoryUsage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var kind = (ReferenceKind)reader.GetInt32(7);
            results.Add(new CrossRepositoryUsage
            {
                ConsumerRepositoryUrl = reader.GetString(0),
                ConsumerBranch = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ConsumerCommitSha = reader.IsDBNull(2) ? null : reader.GetString(2),
                ConsumerProjectCanonicalId = reader.GetString(3),
                FilePath = reader.GetString(4),
                Line = reader.GetInt32(5),
                Column = reader.GetInt32(6),
                OccurrenceKind = kind.ToString(),
                ProviderCommitSha = reader.GetString(8),
                SubmoduleDirty = !reader.IsDBNull(9) && reader.GetInt64(9) != 0
            });
        }
        return results;
    }

    // The shared FROM/WHERE for both the candidate-repo pre-query and the usage query. Joins the
    // dependency catalog to the provider symbol (by stable key) and to the consumer occurrences that
    // target it, then to the consumer project's logical identity / repository / file / branch|commit for
    // the scope filter. A default-heads scope joins the consumer's default branch pointer; a branch scope
    // its named branch pointer; a commit scope filters the consumer snapshot's commit directly. The
    // occurrence join is restricted to PURE references (source_symbol_id IS NULL): a cross-project call
    // site emits BOTH a pure-reference row and an additional call-graph row at the same (file, line, col),
    // so counting both would double-count one usage. The pure-reference rows are the complete set of
    // usage locations (every call/inheritance also emits one), and they carry the reference kind, so this
    // filter yields exactly one row per usage without losing any. The consumer snapshot is also restricted
    // to a PUBLISHED status (complete or a retained superseded generation): a default-heads/branch scope is
    // already limited to live branch pointers (only ever set on a complete snapshot), but an explicit
    // commit scope must not match a still-staging or abandoned snapshot sharing that commit.
    private static string UsageFromWhere(CrossRepoUsageScope scope)
    {
        var scopeJoin = scope.ConsumerCommitSha != null
            // Historical commit scope: bind the consumer snapshot's own commit; branch pointer optional.
            ? """
              JOIN commits cc ON cc.id = cs.commit_id AND cc.commit_sha = @consumer_commit
              LEFT JOIN branches cb ON cb.snapshot_id = d.consumer_snapshot_id
              """
            // Branch-head scope (default): the consumer snapshot must be a live branch head.
            : $"""
              JOIN branches cb ON cb.snapshot_id = d.consumer_snapshot_id {(scope.Branch != null ? "AND cb.name = @branch" : "AND cb.is_default = 1")}
              LEFT JOIN commits cc ON cc.id = cs.commit_id
              """;

        return $"""
            FROM snapshot_dependencies d
            JOIN repositories prepo ON prepo.id = d.provider_repository_id AND prepo.remote_url = @provider_url
            JOIN symbols psym ON psym.project_id = d.provider_project_id AND psym.symbol_key = @symbol_key
            JOIN occurrences o ON o.target_symbol_id = psym.id AND o.in_project_id = d.consumer_project_id
                AND o.source_symbol_id IS NULL
            JOIN snapshots cs ON cs.id = d.consumer_snapshot_id
            JOIN projects cp ON cp.id = d.consumer_project_id
            JOIN logical_projects clp ON clp.id = cp.logical_project_id
            JOIN repositories crepo ON crepo.id = clp.repository_id
            JOIN file_versions cfv ON cfv.id = o.file_version_id
            JOIN files cf ON cf.id = cfv.file_id
            {scopeJoin}
            WHERE cs.status IN (@published_complete, @published_superseded)
            """;
    }

    private static void BindUsageParameters(
        SqliteCommand cmd, string providerRepositoryUrl, string providerSymbolKey, CrossRepoUsageScope scope)
    {
        cmd.Parameters.AddWithValue("@provider_url", providerRepositoryUrl);
        cmd.Parameters.AddWithValue("@symbol_key", providerSymbolKey);
        cmd.Parameters.AddWithValue("@published_complete", SnapshotStatus.Complete);
        cmd.Parameters.AddWithValue("@published_superseded", SnapshotStatus.Superseded);
        if (scope.ConsumerCommitSha != null)
            cmd.Parameters.AddWithValue("@consumer_commit", scope.ConsumerCommitSha);
        else if (scope.Branch != null)
            cmd.Parameters.AddWithValue("@branch", scope.Branch);
    }

    // The FROM/WHERE for the symbol-independent reverse-dependency listing: the dependency catalog joined
    // to the consumer project's logical identity / repository and the scope's branch|commit pointer, with
    // NO symbol/occurrence join. Mirrors the scope semantics of <see cref="UsageFromWhere"/>.
    private static string ConsumerFromWhere(CrossRepoUsageScope scope)
    {
        var scopeJoin = scope.ConsumerCommitSha != null
            ? """
              JOIN commits cc ON cc.id = cs.commit_id AND cc.commit_sha = @consumer_commit
              LEFT JOIN branches cb ON cb.snapshot_id = d.consumer_snapshot_id
              """
            : $"""
              JOIN branches cb ON cb.snapshot_id = d.consumer_snapshot_id {(scope.Branch != null ? "AND cb.name = @branch" : "AND cb.is_default = 1")}
              LEFT JOIN commits cc ON cc.id = cs.commit_id
              """;

        return $"""
            FROM snapshot_dependencies d
            JOIN repositories prepo ON prepo.id = d.provider_repository_id AND prepo.remote_url = @provider_url
            JOIN snapshots cs ON cs.id = d.consumer_snapshot_id
            JOIN projects cp ON cp.id = d.consumer_project_id
            JOIN logical_projects clp ON clp.id = cp.logical_project_id
            JOIN repositories crepo ON crepo.id = clp.repository_id
            {scopeJoin}
            WHERE cs.status IN (@published_complete, @published_superseded)
            """;
    }

    private const string SelectEdge = """
        SELECT id, consumer_snapshot_id, consumer_project_id, provider_snapshot_id, provider_project_id,
               provider_repository_id, provider_commit_sha, reference_kind, submodule_dirty, created_at
        FROM snapshot_dependencies
        """;

    private static List<SnapshotDependencyEdge> ReadEdges(SqliteCommand cmd)
    {
        var results = new List<SnapshotDependencyEdge>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SnapshotDependencyEdge
            {
                Id = reader.GetInt64(0),
                ConsumerSnapshotId = reader.GetInt64(1),
                ConsumerProjectId = reader.GetInt64(2),
                ProviderSnapshotId = reader.GetInt64(3),
                ProviderProjectId = reader.GetInt64(4),
                ProviderRepositoryId = reader.GetInt64(5),
                ProviderCommitSha = reader.GetString(6),
                ReferenceKind = reader.GetString(7),
                SubmoduleDirty = !reader.IsDBNull(8) && reader.GetInt64(8) != 0,
                CreatedAt = reader.GetInt64(9)
            });
        }
        return results;
    }
}
