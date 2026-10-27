using Sextant.Service.Grants;

namespace Sextant.Service;

/// <summary>
/// Bounds the issue #199 remote-default lookups the service makes for restricted ensures, so a caller cannot turn
/// ensures into unbounded outbound git calls:
/// <list type="bullet">
/// <item>Concurrent lookups of one repository (keyed by <see cref="RepositoryGrantKey.Of"/>, so URL spellings of it
/// share a key) share ONE resolver call.</item>
/// <item>A finished answer, <c>null</c> included, is reused for <see cref="ResultLifetime"/>.</item>
/// <item>At most <see cref="MaxConcurrentLookups"/> resolver calls run at once PER TENANT (and
/// <see cref="MaxConcurrentLookupsTotal"/> in all, issue #203), so one tenant's slow remotes never hold the slots of
/// another. A lookup of another repository while its tenant's slots (or all of them) are busy answers <c>null</c>
/// immediately, without calling the resolver or remembering anything, which fails closed. The head and reachability
/// lookups (issues #203/#209) instead throw <see cref="RemoteLookupBusyException"/>, so a full slot is never read as
/// "the commit is not on the branch".</item>
/// <item>A head or reachability call whose every waiter was cancelled is cancelled itself (its git process is
/// killed), so an abandoned request never keeps a slot for the lookup's full timeout.</item>
/// </list>
/// The remembered table is pruned so it never holds more than about <see cref="MaxRememberedRepositories"/> entries.
/// Never faults: a resolver that throws answers <c>null</c>.
/// </summary>
internal sealed class RemoteDefaultBranchLookup(
    IRemoteDefaultBranchResolver resolver, TimeProvider? time = null, IRemoteBranchHeadResolver? heads = null,
    IRemoteCommitBranchChecker? reachability = null)
{
    internal const int MaxConcurrentLookups = 4;
    internal const int MaxConcurrentLookupsTotal = 32;
    internal const int MaxRememberedRepositories = 1024;
    internal static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _runningByTenant = new(StringComparer.Ordinal);
    private int _running;

    /// <summary>Whether a branch-head resolver is configured (clone mode).</summary>
    internal bool HasBranchHeadResolver => heads is not null;

    /// <summary>Whether a commit-on-branch checker is configured (clone mode).</summary>
    internal bool HasCommitBranchChecker => reachability is not null;

    /// <summary>
    /// Whether the commit is reachable from a branch of the remote (issue #209); <c>false</c> when it is not, cannot be
    /// determined or no checker is configured; throws <see cref="RemoteLookupBusyException"/> when no lookup slot is free.
    /// Shares in-flight calls, never remembers an answer.
    /// </summary>
    public async Task<bool> IsCommitOnBranchAsync(
        string repositoryRemoteUrl, string commit, string? tenant = null, CancellationToken cancellationToken = default) =>
        reachability is not null
        && await Start($"\u0001reach\n{RepositoryGrantKey.Of(repositoryRemoteUrl)}\n{commit.ToLowerInvariant()}", tenant, TimeSpan.Zero,
            ct => reachability.IsCommitOnBranch(repositoryRemoteUrl, commit, ct) ? "yes" : null, cancellationToken, throwWhenBusy: true)
            .ConfigureAwait(false) is not null;

    /// <summary>How many repositories the table currently holds (for tests).</summary>
    internal int RememberedCount
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    /// <summary>The remote's default branch, or <c>null</c> when it is unknown or no lookup slot is free.</summary>
    public Task<string?> ResolveAsync(string repositoryRemoteUrl, string? tenant = null) =>
        Start(RepositoryGrantKey.Of(repositoryRemoteUrl), tenant, ResultLifetime, _ => resolver.ResolveDefaultBranch(repositoryRemoteUrl));

    /// <summary>
    /// The commit <c>refs/heads/&lt;branch&gt;</c> names on the remote (issue #203), or <c>null</c> when it is unknown,
    /// no head resolver is configured; throws <see cref="RemoteLookupBusyException"/> when no lookup slot is free.
    /// Concurrent lookups share one call but a finished answer is never reused, because a head moves with every push.
    /// </summary>
    public Task<string?> ResolveBranchHeadAsync(
        string repositoryRemoteUrl, string branch, string? tenant = null, CancellationToken cancellationToken = default) =>
        heads is null
            ? Task.FromResult<string?>(null)
            : Start($"{RepositoryGrantKey.Of(repositoryRemoteUrl)}\n{branch}", tenant, TimeSpan.Zero,
                ct => heads.ResolveBranchHead(repositoryRemoteUrl, branch, ct), cancellationToken, throwWhenBusy: true);

    private Task<string?> Start(
        string key, string? tenant, TimeSpan lifetime, Func<CancellationToken, string?> lookup,
        CancellationToken caller = default, bool throwWhenBusy = false)
    {
        tenant ??= string.Empty;
        Entry entry;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_entries.TryGetValue(key, out var existing) && !existing.Abandoned
                && (existing.ExpiresAt is not { } expires || now < expires))
            {
                if (lifetime == TimeSpan.Zero)
                    Watch(existing, caller);
                return existing.Answer.Task;
            }
            if (_running >= MaxConcurrentLookupsTotal || _runningByTenant.GetValueOrDefault(tenant) >= MaxConcurrentLookups)
                return throwWhenBusy ? Task.FromException<string?>(new RemoteLookupBusyException()) : Task.FromResult<string?>(null);
            Prune(now);
            entry = new Entry();
            _entries[key] = entry;
            _running++;
            _runningByTenant[tenant] = _runningByTenant.GetValueOrDefault(tenant) + 1;
            if (lifetime == TimeSpan.Zero)
                Watch(entry, caller);
        }
        entry.Worker = Task.Run(() => Run(lookup, tenant, lifetime, entry));
        return entry.Answer.Task;
    }

    // Counts a waiter of an unremembered lookup (caller holds _lock). When every waiter has been cancelled the lookup
    // is cancelled too and no later caller joins it (its answer would be the cancelled one). A remembered lookup
    // (the #199 default branch) is never cancelled: its answer serves later ensures.
    private void Watch(Entry entry, CancellationToken caller)
    {
        entry.Waiters++;
        if (!caller.CanBeCanceled)
            return;
        var registration = caller.Register(() =>
        {
            bool cancel;
            lock (_lock)
            {
                cancel = --entry.Waiters <= 0 && entry.ExpiresAt is null;
                if (cancel)
                    entry.Abandoned = true;
            }
            if (cancel)
                entry.Cancellation.Cancel();
        });
        entry.Unregister = entry.Answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
    }

    private void Run(Func<CancellationToken, string?> lookup, string tenant, TimeSpan lifetime, Entry entry)
    {
        string? answer;
        try
        {
            answer = lookup(entry.Cancellation.Token);
        }
        catch (Exception)
        {
            // The resolvers answer null for a remote they cannot read; a throw means the same.
            answer = null;
        }
        lock (_lock)
        {
            entry.ExpiresAt = _time.GetUtcNow() + lifetime;
            _running--;
            if (_runningByTenant[tenant] <= 1)
                _runningByTenant.Remove(tenant);
            else
                _runningByTenant[tenant]--;
        }
        entry.Answer.TrySetResult(answer);
    }

    // Drops expired answers; if the table is still full, drops every finished one. In-flight entries are kept (at
    // most MaxConcurrentLookups of them), so the table stays bounded and a running lookup is always shared.
    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count < MaxRememberedRepositories)
            return;
        RemoveWhere(e => e.ExpiresAt is { } expires && now >= expires);
        if (_entries.Count >= MaxRememberedRepositories)
            RemoveWhere(e => e.ExpiresAt is not null);
    }

    private void RemoveWhere(Func<Entry, bool> predicate)
    {
        foreach (var key in _entries.Where(pair => predicate(pair.Value)).Select(pair => pair.Key).ToList())
            _entries.Remove(key);
    }

    private sealed class Entry
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public int Waiters { get; set; }

        /// <summary>The task running the lookup; <see cref="Run"/> catches everything, so it never faults.</summary>
        public Task? Worker { get; set; }

        /// <summary>The continuation that disposes the last registered waiter's cancellation registration.</summary>
        public Task? Unregister { get; set; }

        /// <summary>Every waiter was cancelled; no new caller may join the (cancelled) lookup.</summary>
        public bool Abandoned { get; set; }

        public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When the answer stops being reused; <c>null</c> while the lookup is still running.</summary>
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}

/// <summary>No remote-lookup slot was free for a head (#203) or reachability (#209) check; the caller may retry.</summary>
public sealed class RemoteLookupBusyException() : Exception("no remote lookup slot is free");
