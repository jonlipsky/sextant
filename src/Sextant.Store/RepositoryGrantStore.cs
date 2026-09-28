using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>The <c>repository_grants.source</c> vocabulary (migration 024).</summary>
public static class RepositoryGrantSource
{
    /// <summary>A user's own grant (<c>PUT /control/grants/self</c>).</summary>
    public const string Self = "self";

    /// <summary>A tenant-wide enrollment (<c>PUT /control/grants/tenant</c>).</summary>
    public const string Tenant = "tenant";

    /// <summary>A grant imported from another system (reserved for a migration of existing watches).</summary>
    public const string Import = "import";
}

/// <summary>One row of the <c>repository_grants</c> table (migration 024).</summary>
public sealed record RepositoryGrantRow
{
    public required long Id { get; init; }
    public required string TenantId { get; init; }

    /// <summary>The full verified subject of a user grant, or <see cref="RepositoryGrantStore.TenantWide"/>.</summary>
    public required string Principal { get; init; }

    /// <summary>The canonical repository key (the match key for uniqueness and visibility).</summary>
    public required string RepositoryKey { get; init; }

    /// <summary>The first-submitted repository URL spelling, kept on a re-grant.</summary>
    public required string RemoteUrl { get; init; }

    /// <summary>The granted branch, or <see cref="RepositoryGrantStore.DefaultBranch"/> for the default branch.</summary>
    public required string Branch { get; init; }

    public required string Source { get; init; }
    public required long CreatedAt { get; init; }
    public required long UpdatedAt { get; init; }
}

/// <summary>
/// One distinct (repository, branch) reconcile target of a tenant (<c>GET /control/grants?scope=tenant</c>). It
/// carries counts only, never a principal.
/// </summary>
public sealed record RepositoryGrantTarget
{
    /// <summary>The URL to ensure: the oldest tenant-wide grant's spelling for the repository, else the oldest grant's.</summary>
    public required string RemoteUrl { get; init; }

    public required string RepositoryKey { get; init; }
    public required string Branch { get; init; }

    /// <summary>The distinct grant sources for the target, sorted.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>How many grant rows name the target (a tenant-wide grant counts once).</summary>
    public required int Watchers { get; init; }
}

/// <summary>
/// Reads and writes <c>repository_grants</c> (migration 024, SVC-4): which repositories a verified caller may read
/// through the index service. A thin adapter over parameterized SQL on the caller's connection, so a write enrols
/// in the caller's transaction (the limit check, the upsert and the audit row commit together).
/// </summary>
public sealed class RepositoryGrantStore(SqliteConnection connection)
{
    /// <summary>The principal of a tenant-wide grant.</summary>
    public const string TenantWide = "*";

    /// <summary>The branch of a grant on the repository's default branch.</summary>
    public const string DefaultBranch = "";

    /// <summary>The <c>branch</c> filter of <see cref="Delete"/> that removes every branch of the repository.</summary>
    public const string AllBranches = "*";

    /// <summary>The grant for exactly (tenant, principal, key, branch), or null.</summary>
    public RepositoryGrantRow? Get(string tenantId, string principal, string repositoryKey, string branch)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM repository_grants
            WHERE tenant_id = @tenant AND principal = @principal AND repository_key = @key AND branch = @branch;
            """;
        Bind(cmd, tenantId, principal);
        cmd.Parameters.AddWithValue("@key", repositoryKey);
        cmd.Parameters.AddWithValue("@branch", branch);
        return ReadAll(cmd).FirstOrDefault();
    }

    /// <summary>
    /// Inserts the grant, or refreshes <c>updated_at</c> when it already exists (the first-submitted URL spelling
    /// and the source are kept). Returns the stored row and whether it was created.
    /// </summary>
    public (RepositoryGrantRow Row, bool Created) Upsert(
        string tenantId, string principal, string repositoryKey, string remoteUrl, string branch, string source, long now)
    {
        if (Get(tenantId, principal, repositoryKey, branch) is { } existing)
        {
            using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE repository_grants SET updated_at = @now WHERE id = @id;";
            touch.Parameters.AddWithValue("@now", now);
            touch.Parameters.AddWithValue("@id", existing.Id);
            touch.ExecuteNonQuery();
            return (existing with { UpdatedAt = now }, false);
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO repository_grants
                (tenant_id, principal, repository_key, remote_url, branch, source, created_at, updated_at)
            VALUES (@tenant, @principal, @key, @url, @branch, @source, @now, @now)
            RETURNING id;
            """;
        Bind(cmd, tenantId, principal);
        cmd.Parameters.AddWithValue("@key", repositoryKey);
        cmd.Parameters.AddWithValue("@url", remoteUrl);
        cmd.Parameters.AddWithValue("@branch", branch);
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@now", now);
        var id = Convert.ToInt64(cmd.ExecuteScalar()!);
        return (new RepositoryGrantRow
        {
            Id = id,
            TenantId = tenantId,
            Principal = principal,
            RepositoryKey = repositoryKey,
            RemoteUrl = remoteUrl,
            Branch = branch,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now
        }, true);
    }

    /// <summary>
    /// Deletes the principal's grants on a repository: one branch, or every branch when <paramref name="branch"/> is
    /// <see cref="AllBranches"/>. Returns the number of rows deleted.
    /// </summary>
    public int Delete(string tenantId, string principal, string repositoryKey, string branch)
    {
        using var cmd = connection.CreateCommand();
        var allBranches = branch == AllBranches;
        cmd.CommandText = $"""
            DELETE FROM repository_grants
            WHERE tenant_id = @tenant AND principal = @principal AND repository_key = @key
              {(allBranches ? string.Empty : "AND branch = @branch")};
            """;
        Bind(cmd, tenantId, principal);
        cmd.Parameters.AddWithValue("@key", repositoryKey);
        if (!allBranches)
            cmd.Parameters.AddWithValue("@branch", branch);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>How many grants the principal holds in the tenant.</summary>
    public int CountForPrincipal(string tenantId, string principal)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM repository_grants WHERE tenant_id = @tenant AND principal = @principal;";
        Bind(cmd, tenantId, principal);
        return Convert.ToInt32(cmd.ExecuteScalar()!);
    }

    /// <summary>How many grants the tenant holds, across every principal.</summary>
    public int CountForTenant(string tenantId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM repository_grants WHERE tenant_id = @tenant;";
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        return Convert.ToInt32(cmd.ExecuteScalar()!);
    }

    /// <summary>The grants held by exactly <paramref name="principal"/> in the tenant, ordered by key then branch.</summary>
    public IReadOnlyList<RepositoryGrantRow> ListForPrincipal(string tenantId, string principal)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM repository_grants
            WHERE tenant_id = @tenant AND principal = @principal
            ORDER BY repository_key, branch;
            """;
        Bind(cmd, tenantId, principal);
        return ReadAll(cmd);
    }

    /// <summary>
    /// The grants that make repositories visible to a caller: the tenant-wide grants plus, for a user
    /// (<paramref name="userSubject"/> non-null), that exact subject's own grants. Ordered by key, branch, then
    /// tenant-wide first.
    /// </summary>
    public IReadOnlyList<RepositoryGrantRow> ListVisible(string tenantId, string? userSubject)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM repository_grants
            WHERE tenant_id = @tenant AND (principal = @wide {(userSubject is null ? string.Empty : "OR principal = @principal")})
            ORDER BY repository_key, branch, principal <> @wide, created_at, id;
            """;
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@wide", TenantWide);
        if (userSubject is not null)
            cmd.Parameters.AddWithValue("@principal", userSubject);
        return ReadAll(cmd);
    }

    /// <summary>The distinct repository keys visible to a caller (see <see cref="ListVisible"/>).</summary>
    public IReadOnlySet<string> VisibleKeys(string tenantId, string? userSubject)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT repository_key FROM repository_grants
            WHERE tenant_id = @tenant AND (principal = @wide {(userSubject is null ? string.Empty : "OR principal = @principal")});
            """;
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@wide", TenantWide);
        if (userSubject is not null)
            cmd.Parameters.AddWithValue("@principal", userSubject);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>
    /// The tenant's distinct (repository, branch) reconcile targets, ordered by key then branch. Only counts and
    /// sources are returned, never a principal.
    /// </summary>
    public IReadOnlyList<RepositoryGrantTarget> TenantTargets(string tenantId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT g.repository_key, g.branch, COUNT(*) AS watchers, GROUP_CONCAT(DISTINCT g.source) AS sources,
                   (SELECT o.remote_url FROM repository_grants o
                    WHERE o.tenant_id = g.tenant_id AND o.repository_key = g.repository_key
                    ORDER BY o.principal <> @wide, o.created_at, o.id
                    LIMIT 1) AS remote_url
            FROM repository_grants g
            WHERE g.tenant_id = @tenant
            GROUP BY g.repository_key, g.branch
            ORDER BY g.repository_key, g.branch;
            """;
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@wide", TenantWide);
        var targets = new List<RepositoryGrantTarget>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            targets.Add(new RepositoryGrantTarget
            {
                RepositoryKey = reader.GetString(0),
                Branch = reader.GetString(1),
                Watchers = reader.GetInt32(2),
                Sources = reader.GetString(3).Split(',').Order(StringComparer.Ordinal).ToList(),
                RemoteUrl = reader.GetString(4)
            });
        }
        return targets;
    }

    private const string Columns =
        "id, tenant_id, principal, repository_key, remote_url, branch, source, created_at, updated_at";

    private static void Bind(SqliteCommand cmd, string tenantId, string principal)
    {
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@principal", principal);
    }

    private static List<RepositoryGrantRow> ReadAll(SqliteCommand cmd)
    {
        var rows = new List<RepositoryGrantRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RepositoryGrantRow
            {
                Id = reader.GetInt64(0),
                TenantId = reader.GetString(1),
                Principal = reader.GetString(2),
                RepositoryKey = reader.GetString(3),
                RemoteUrl = reader.GetString(4),
                Branch = reader.GetString(5),
                Source = reader.GetString(6),
                CreatedAt = reader.GetInt64(7),
                UpdatedAt = reader.GetInt64(8)
            });
        }
        return rows;
    }
}
