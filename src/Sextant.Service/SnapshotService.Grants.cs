using Microsoft.Data.Sqlite;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Grants;
using Sextant.Store;

namespace Sextant.Service;

// SVC-4: repository grants. Grant WRITES run on their own short-lived catalog connection, NOT under the write gate:
// a production holds the gate for a whole index (minutes), and a user watching a repository must not wait behind it.
// SQLite serializes the two writers (BEGIN IMMEDIATE + busy_timeout; the production's write sessions commit at batch
// boundaries). Each write is still a service-owned operation drained by Dispose, runs only while the writer lease is
// held, and commits its limit check, row change and audit row in ONE transaction. Grant READS go through ReadCatalog.
public sealed partial class SnapshotService
{
    // How long a grant write waits for SQLite's write lock (a production commits at batch boundaries well inside it).
    private const int GrantWriteBusyTimeoutMs = 30_000;

    /// <summary>
    /// Creates (or refreshes) a grant for <paramref name="caller"/> on a repository branch (SVC-4,
    /// <c>PUT /control/grants/{self|tenant}</c>). The principal comes only from <paramref name="caller"/>: its exact
    /// subject for <see cref="GrantScope.Self"/>, <c>'*'</c> for <see cref="GrantScope.Tenant"/>. The host has
    /// already applied the actor rule, the repository URL policy (<paramref name="remoteUrl"/> is the accepted
    /// spelling and <paramref name="repositoryKey"/> its canonical key) and the branch rule. A new grant beyond
    /// <see cref="ServiceOptions.MaxGrantsPerPrincipal"/> or <see cref="ServiceOptions.MaxGrantsPerTenant"/> is refused
    /// with <see cref="GrantReason.GrantLimit"/>. The outcome and its <c>grant</c> audit row commit together.
    /// </summary>
    /// <exception cref="ArgumentException">The caller's actor does not match <paramref name="scope"/>.</exception>
    /// <exception cref="OperationCanceledException">The service is shutting down (the host answers 503).</exception>
    /// <exception cref="GrantStoreUnavailableException">The lease is lost or the catalog stayed locked (503).</exception>
    public Task<GrantWriteResult> PutGrantAsync(
        CallerPrincipal caller, GrantScope scope, string remoteUrl, string repositoryKey, string branch,
        AuditCaller auditor = default, CancellationToken cancellationToken = default)
    {
        var principal = GrantPrincipal(caller, scope);
        ArgumentException.ThrowIfNullOrEmpty(remoteUrl);
        ArgumentException.ThrowIfNullOrEmpty(repositoryKey);
        ArgumentNullException.ThrowIfNull(branch);
        var operation = GrantOperation("put", scope);
        var source = scope == GrantScope.Self ? RepositoryGrantSource.Self : RepositoryGrantSource.Tenant;

        return RunGrantWriteAsync(conn =>
        {
            var grants = new RepositoryGrantStore(conn);
            var audit = new AuditLogStore(conn);
            if (grants.Get(caller.TenantId, principal, repositoryKey, branch) is null && OverLimit(grants, caller.TenantId, principal))
            {
                audit.Append(AuditAction.Grant, AuditOutcome.Denied, actor: auditor.Actor,
                    repositoryScope: repositoryKey, detail: auditor.Detail($"{operation};{GrantReason.GrantLimit}"));
                return new GrantWriteResult { Refusal = GrantReason.GrantLimit };
            }

            var (row, created) = grants.Upsert(
                caller.TenantId, principal, repositoryKey, remoteUrl, branch, source, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            audit.Append(AuditAction.Grant, AuditOutcome.Accepted, actor: auditor.Actor,
                repositoryScope: repositoryKey, detail: auditor.Detail($"{operation};{(created ? "created" : "updated")}"));
            return new GrantWriteResult { Grant = row, Created = created };
        }, cancellationToken);
    }

    /// <summary>
    /// Revokes <paramref name="caller"/>'s grants on a repository (SVC-4, <c>DELETE /control/grants/{self|tenant}</c>):
    /// the one branch named, or every branch for <see cref="RepositoryGrantStore.AllBranches"/>. Returns how many
    /// grants were deleted (zero is not an error). The revocation takes effect on the caller's next read.
    /// </summary>
    /// <exception cref="ArgumentException">The caller's actor does not match <paramref name="scope"/>.</exception>
    public Task<int> DeleteGrantsAsync(
        CallerPrincipal caller, GrantScope scope, string repositoryKey, string branch,
        AuditCaller auditor = default, CancellationToken cancellationToken = default)
    {
        var principal = GrantPrincipal(caller, scope);
        ArgumentException.ThrowIfNullOrEmpty(repositoryKey);
        ArgumentNullException.ThrowIfNull(branch);
        var operation = GrantOperation("delete", scope);

        return RunGrantWriteAsync(conn =>
        {
            var deleted = new RepositoryGrantStore(conn).Delete(caller.TenantId, principal, repositoryKey, branch);
            new AuditLogStore(conn).Append(AuditAction.Grant, AuditOutcome.Accepted, actor: auditor.Actor,
                repositoryScope: repositoryKey, detail: auditor.Detail($"{operation};deleted_{deleted}"));
            return deleted;
        }, cancellationToken);
    }

    /// <summary>
    /// Records the <c>grant</c>/<c>denied</c> audit row for a grant write the host refused (its caller, body,
    /// repository URL or branch). <paramref name="operation"/> and <paramref name="reason"/> are codes; the repository
    /// scope is set only for a repository the URL policy accepted (a refused URL is never stored).
    /// </summary>
    public Task RecordGrantDeniedAsync(
        string operation, string reason, string? repositoryKey = null, AuditCaller auditor = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return RunGrantWriteAsync(conn =>
        {
            new AuditLogStore(conn).Append(AuditAction.Grant, AuditOutcome.Denied, actor: auditor.Actor,
                repositoryScope: repositoryKey, detail: auditor.Detail($"{operation};{reason}"));
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Records an <c>ensure</c>/<c>denied</c> row for an ensure a user caller is not granted (SVC-4 <c>not_granted</c>).
    /// Unlike the URL-policy refusal the repository was accepted, so the row is scoped to its key.
    /// </summary>
    public Task RecordEnsureNotGrantedAsync(
        string repositoryKey, AuditCaller auditor = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositoryKey);
        return RunGrantWriteAsync(conn =>
        {
            new AuditLogStore(conn).Append(AuditAction.Ensure, AuditOutcome.Denied, actor: auditor.Actor,
                repositoryScope: repositoryKey, detail: auditor.Detail(GrantReason.NotGranted));
            return true;
        }, cancellationToken);
    }

    /// <summary>The caller's own grants (<see cref="GrantScope.Self"/>: its subject; <see cref="GrantScope.Tenant"/>: '*'), each with its catalog status.</summary>
    public IReadOnlyList<GrantView> ListGrants(CallerPrincipal caller, GrantScope scope)
    {
        var principal = GrantPrincipal(caller, scope);
        return ReadCatalog(conn =>
        {
            var catalog = new GrantCatalogReader(conn);
            return new RepositoryGrantStore(conn).ListForPrincipal(caller.TenantId, principal)
                .Select(g => new GrantView
                {
                    Repository = g.RemoteUrl,
                    Branch = g.Branch,
                    Source = g.Source,
                    CreatedAt = g.CreatedAt,
                    Status = catalog.StatusOf(g.RepositoryKey, g.Branch)
                })
                .ToList();
        });
    }

    /// <summary>
    /// The distinct (repository, branch) reconcile targets of an application caller's tenant (SVC-4,
    /// <c>GET /control/grants?scope=tenant</c>): counts and sources only, never a principal.
    /// </summary>
    /// <exception cref="ArgumentException">The caller is not an application caller.</exception>
    public IReadOnlyList<RepositoryGrantTarget> ListGrantTargets(CallerPrincipal caller)
    {
        GrantPrincipal(caller, GrantScope.Tenant);
        return ReadCatalog(conn => new RepositoryGrantStore(conn).TenantTargets(caller.TenantId));
    }

    /// <summary>
    /// The <see cref="RepositoryGrantKey"/>s the caller may read: its tenant's '*' grants plus, for a user caller, its
    /// exact subject's grants. Read fresh on every call (the host caches it for one request only).
    /// </summary>
    public IReadOnlySet<string> GetVisibleRepositoryKeys(CallerPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return ReadCatalog(conn => new RepositoryGrantStore(conn).VisibleKeys(caller.TenantId, VisibilitySubject(caller)));
    }

    /// <summary>The grant rows that make repositories visible to the caller (see <see cref="GetVisibleRepositoryKeys"/>).</summary>
    public IReadOnlyList<RepositoryGrantRow> GetVisibleGrants(CallerPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return ReadCatalog(conn => new RepositoryGrantStore(conn).ListVisible(caller.TenantId, VisibilitySubject(caller)));
    }

    /// <summary>Whether the caller may read <paramref name="remoteUrl"/> (any spelling of the repository).</summary>
    public bool IsRepositoryVisible(CallerPrincipal caller, string? remoteUrl) =>
        !string.IsNullOrWhiteSpace(remoteUrl) && GetVisibleRepositoryKeys(caller).Contains(RepositoryGrantKey.Of(remoteUrl));

    /// <summary>
    /// The implicit selection of a delegate read that names no repository (SVC-4): the remote URL of the ONE catalog
    /// repository among <paramref name="visibleKeys"/> that has a complete default-branch snapshot, or null when
    /// there is none or more than one (the read then fails with <c>repository_required</c>).
    /// </summary>
    public string? ResolveImplicitRepository(IReadOnlySet<string> visibleKeys)
    {
        ArgumentNullException.ThrowIfNull(visibleKeys);
        if (visibleKeys.Count == 0)
            return null;
        return ReadCatalog(conn =>
        {
            var snapshots = new SnapshotStore(conn);
            string? selectedKey = null;
            string? selectedUrl = null;
            foreach (var (key, url) in new GrantCatalogReader(conn).ConsumerRepositories())
            {
                if (!visibleKeys.Contains(key) || snapshots.GetSelectedSnapshotIdForRepository(url) is null)
                    continue;
                if (selectedKey is not null && selectedKey != key)
                    return null;
                selectedKey ??= key;
                selectedUrl ??= url;
            }
            return selectedUrl;
        });
    }

    /// <summary>
    /// The repositories visible to the caller with their branches (<c>list_repositories</c>): the catalog's branches of
    /// each repository plus any granted branch the catalog does not know yet, ordered by repository key then branch.
    /// </summary>
    public IReadOnlyList<VisibleRepository> ListVisibleRepositories(CallerPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return ReadCatalog(conn =>
        {
            var catalog = new GrantCatalogReader(conn);
            return new RepositoryGrantStore(conn).ListVisible(caller.TenantId, VisibilitySubject(caller))
                .GroupBy(g => g.RepositoryKey, StringComparer.Ordinal)
                .Select(group => catalog.Describe(group.Key, group.ToList()))
                .ToList();
        });
    }

    // The principal a grant route writes for. The caller's actor must match the scope, and a user whose subject is the
    // reserved tenant-wide principal can never write through /self (it would create or revoke tenant-wide grants).
    private static string GrantPrincipal(CallerPrincipal caller, GrantScope scope)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return scope switch
        {
            GrantScope.Self when caller is { Actor: CallerActor.User, UserId: { Length: > 0 } sub }
                && sub != RepositoryGrantStore.TenantWide => sub,
            GrantScope.Tenant when caller.Actor == CallerActor.Application => RepositoryGrantStore.TenantWide,
            _ => throw new ArgumentException("The caller's actor cannot hold grants in this scope.", nameof(caller))
        };
    }

    /// <summary>Whether <paramref name="caller"/> may write grants in <paramref name="scope"/> (the host's actor check).</summary>
    public static bool CanWriteGrants(CallerPrincipal caller, GrantScope scope)
    {
        try
        {
            GrantPrincipal(caller, scope);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // The subject whose own grants add to the tenant-wide ones: a user's exact subject, none for an application.
    private static string? VisibilitySubject(CallerPrincipal caller) =>
        caller.Actor == CallerActor.User ? caller.UserId : null;

    private static string GrantOperation(string verb, GrantScope scope) =>
        $"{verb}_{(scope == GrantScope.Self ? "self" : "tenant")}";

    private bool OverLimit(RepositoryGrantStore grants, string tenantId, string principal) =>
        (principal != RepositoryGrantStore.TenantWide
            && grants.CountForPrincipal(tenantId, principal) >= _options.MaxGrantsPerPrincipal)
        || grants.CountForTenant(tenantId) >= _options.MaxGrantsPerTenant;

    // Runs one grant write as a service-owned operation (drained by Dispose, refused once shutdown began) on its own
    // catalog connection, in ONE BEGIN IMMEDIATE transaction that commits only while the writer lease is held. The
    // caller's token bounds only its wait.
    private async Task<T> RunGrantWriteAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        Task<T> write;
        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfStopping();
            write = Task.Run(() => GrantWrite(work), CancellationToken.None);
            _operations.Add(write);
        }
        _ = write.ContinueWith(
            t =>
            {
                lock (_inFlightLock)
                    _operations.Remove(t);
                _ = t.Exception;
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await WaitForCallerAsync(write, cancellationToken).ConfigureAwait(false);
    }

    private T GrantWrite<T>(Func<SqliteConnection, T> work)
    {
        try
        {
            return GrantWriteCore(work);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            throw new GrantStoreUnavailableException(
                "The catalog stayed locked by another writer past the grant write's wait; retry the request.", ex);
        }
    }

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private T GrantWriteCore<T>(Func<SqliteConnection, T> work)
    {
        ThrowIfGrantLeaseLost();
        using var conn = OpenGrantWriteConnection();
        ExecOn(conn, "BEGIN IMMEDIATE;");
        var committed = false;
        try
        {
            var result = work(conn);
            // The lease may have been lost while this write waited for SQLite's lock: never commit then.
            ThrowIfGrantLeaseLost();
            ExecOn(conn, "COMMIT;");
            committed = true;
            return result;
        }
        finally
        {
            if (!committed)
                TryRollback(conn);
        }
    }

    private static void TryRollback(SqliteConnection conn)
    {
        try
        {
            ExecOn(conn, "ROLLBACK;");
        }
        catch (SqliteException)
        {
            // SQLite already rolled the transaction back (e.g. a failed COMMIT); the original failure propagates.
        }
    }

    private void ThrowIfGrantLeaseLost()
    {
        if (LeaseLost)
            throw new GrantStoreUnavailableException(
                "This service no longer holds the single-writer lease, so it refuses grant writes (issue #38).");
    }

    private SqliteConnection OpenGrantWriteConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.CatalogDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true
        }.ToString();
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        ExecOn(conn, $"PRAGMA busy_timeout = {GrantWriteBusyTimeoutMs};");
        return conn;
    }

    // Catalog lookups for grant status and listing, on one read connection. Repositories and in-flight jobs are
    // loaded once per reader (the repositories table is one row per repository; queued/running jobs are few).
    private sealed class GrantCatalogReader(SqliteConnection conn)
    {
        private readonly SnapshotStore _snapshots = new(conn);
        private List<(string Key, long Id, string Url)>? _repositories;
        private Dictionary<string, long>? _repositoryIds;
        private List<(string Key, string? Branch)>? _activeJobs;

        // Every consumer (non-provider) repository with its key, in id order.
        public IEnumerable<(string Key, string Url)> ConsumerRepositories() =>
            Repositories().Select(r => (r.Key, r.Url));

        public GrantStatus StatusOf(string key, string branch)
        {
            var repositoryId = RepositoryId(key);
            var row = repositoryId is long id ? BranchFor(id, branch) : null;
            var resolved = row?.Name ?? (branch.Length == 0 ? null : branch);
            var (status, snapshot) = BranchState(key, row, resolved, branch.Length == 0 || row?.IsDefault == true);
            return new GrantStatus
            {
                ResolvedBranch = resolved,
                SnapshotStatus = status,
                CommitSha = snapshot is null ? null : _snapshots.GetCommitSha(snapshot.CommitId),
                PublishedAt = snapshot?.PublishedAt,
                IdentityHash = snapshot?.IdentityHash
            };
        }

        public VisibleRepository Describe(string key, IReadOnlyList<RepositoryGrantRow> grants)
        {
            var repository = GrantedSpelling(grants);
            var branches = new List<VisibleBranch>();
            var catalogBranches = RepositoryId(key) is long id ? CatalogBranches(id) : [];
            foreach (var row in catalogBranches)
                branches.Add(Listed(key, row, row.Name, row.IsDefault));

            var hasDefault = catalogBranches.Any(b => b.IsDefault);
            foreach (var granted in grants.Select(g => g.Branch).Distinct(StringComparer.Ordinal))
            {
                if (granted.Length == 0 ? hasDefault : catalogBranches.Any(b => b.Name == granted))
                    continue;
                if (branches.Any(b => b.Branch == granted))
                    continue;
                branches.Add(Listed(key, null, granted, isDefault: granted.Length == 0));
            }
            return new VisibleRepository
            {
                Repository = repository,
                Sources = grants.Select(g => g.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                Branches = branches.OrderBy(b => b.Branch, StringComparer.Ordinal).ToList()
            };
        }

        private VisibleBranch Listed(string key, BranchRow? row, string name, bool isDefault)
        {
            var (status, snapshot) = BranchState(key, row, name.Length == 0 ? null : name, isDefault);
            return new VisibleBranch
            {
                Branch = name,
                IsDefault = isDefault,
                Status = status,
                CommitSha = snapshot is null ? null : _snapshots.GetCommitSha(snapshot.CommitId),
                PublishedAt = snapshot?.PublishedAt
            };
        }

        // complete/partial from the branch's complete snapshot; else pending when an ensure for the branch is queued
        // or running (a default-branch grant also matches an ensure that named no branch); else missing.
        private (string Status, SnapshotRow? Snapshot) BranchState(string key, BranchRow? row, string? name, bool isDefault)
        {
            if (row is { SnapshotId: long snapshotId } && _snapshots.GetById(snapshotId) is { Status: SnapshotStatus.Complete } snapshot)
            {
                var partial = CoverageOn(conn, snapshot.Id) is { IsPartial: true };
                return (partial ? GrantSnapshotStatus.Partial : GrantSnapshotStatus.Complete, snapshot);
            }
            var pending = ActiveJobs().Any(j => j.Key == key
                && (name is not null && j.Branch == name || isDefault && string.IsNullOrEmpty(j.Branch)));
            return (pending ? GrantSnapshotStatus.Pending : GrantSnapshotStatus.Missing, null);
        }

        // How a repository is spelled back to the caller: the tenant-wide grant's spelling first, then the oldest grant's.
        public static string GrantedSpelling(IEnumerable<RepositoryGrantRow> grants) =>
            grants
                .OrderBy(g => g.Principal == RepositoryGrantStore.TenantWide ? 0 : 1)
                .ThenBy(g => g.CreatedAt)
                .ThenBy(g => g.Id)
                .First().RemoteUrl;

        public BranchRow? BranchFor(long repositoryId, string branch) =>
            branch.Length == 0
                ? _snapshots.GetDefaultBranchId(repositoryId) is long id ? _snapshots.GetBranchById(id) : null
                : _snapshots.GetBranch(repositoryId, branch);

        // The lowest-id consumer repository with the key. The map is built once per reader, since a search resolves
        // every grant of the caller.
        public long? RepositoryId(string key)
        {
            if (_repositoryIds is null)
            {
                _repositoryIds = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var (repositoryKey, id, _) in Repositories())
                    _repositoryIds.TryAdd(repositoryKey, id);
            }
            return _repositoryIds.TryGetValue(key, out var found) ? found : null;
        }

        private List<BranchRow> CatalogBranches(long repositoryId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM branches WHERE repository_id = @repo ORDER BY name;";
            cmd.Parameters.AddWithValue("@repo", repositoryId);
            var ids = new List<long>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    ids.Add(reader.GetInt64(0));
            }
            return ids.Select(_snapshots.GetBranchById).OfType<BranchRow>().ToList();
        }

        private List<(string Key, long Id, string Url)> Repositories()
        {
            if (_repositories is not null)
                return _repositories;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, remote_url FROM repositories WHERE is_provider = 0 ORDER BY id;";
            var rows = new List<(string, long, string)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var url = reader.GetString(1);
                rows.Add((RepositoryGrantKey.Of(url), reader.GetInt64(0), url));
            }
            return _repositories = rows;
        }

        private List<(string Key, string? Branch)> ActiveJobs()
        {
            if (_activeJobs is not null)
                return _activeJobs;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT repository_url, branch_name FROM snapshot_jobs WHERE status IN (@queued, @running);";
            cmd.Parameters.AddWithValue("@queued", SnapshotJobStatus.Queued);
            cmd.Parameters.AddWithValue("@running", SnapshotJobStatus.Running);
            var jobs = new List<(string, string?)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                jobs.Add((RepositoryGrantKey.Of(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1)));
            return _activeJobs = jobs;
        }
    }
}
