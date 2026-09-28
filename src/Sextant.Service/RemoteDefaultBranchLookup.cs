using Sextant.Service.Grants;

namespace Sextant.Service;

/// <summary>
/// Bounds the issue #199 remote-default lookups the service makes for restricted ensures, so a caller cannot turn
/// ensures into unbounded outbound git calls:
/// <list type="bullet">
/// <item>Concurrent lookups of one repository (keyed by <see cref="RepositoryGrantKey.Of"/>, so URL spellings of it
/// share a key) share ONE resolver call.</item>
/// <item>A finished answer, <c>null</c> included, is reused for <see cref="ResultLifetime"/>.</item>
/// <item>At most <see cref="MaxConcurrentLookups"/> resolver calls run at once. A lookup of another repository while
/// they are all busy answers <c>null</c> immediately, without calling the resolver or remembering anything, which
/// fails closed.</item>
/// </list>
/// The remembered table is pruned so it never holds more than about <see cref="MaxRememberedRepositories"/> entries.
/// Never faults: a resolver that throws answers <c>null</c>.
/// </summary>
internal sealed class RemoteDefaultBranchLookup(IRemoteDefaultBranchResolver resolver, TimeProvider? time = null)
{
    internal const int MaxConcurrentLookups = 4;
    internal const int MaxRememberedRepositories = 1024;
    internal static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _running;

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
    public Task<string?> ResolveAsync(string repositoryRemoteUrl)
    {
        var key = RepositoryGrantKey.Of(repositoryRemoteUrl);
        Entry entry;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_entries.TryGetValue(key, out var existing) && (existing.ExpiresAt is not { } expires || now < expires))
                return existing.Answer.Task;
            if (_running >= MaxConcurrentLookups)
                return Task.FromResult<string?>(null);
            Prune(now);
            entry = new Entry();
            _entries[key] = entry;
            _running++;
        }
        _ = Task.Run(() => Run(repositoryRemoteUrl, entry));
        return entry.Answer.Task;
    }

    private void Run(string repositoryRemoteUrl, Entry entry)
    {
        string? branch;
        try
        {
            branch = resolver.ResolveDefaultBranch(repositoryRemoteUrl);
        }
        catch (Exception)
        {
            // IRemoteDefaultBranchResolver answers null for a remote it cannot read; a throw means the same.
            branch = null;
        }
        lock (_lock)
        {
            entry.ExpiresAt = _time.GetUtcNow() + ResultLifetime;
            _running--;
        }
        entry.Answer.TrySetResult(branch);
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
        public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When the answer stops being reused; <c>null</c> while the lookup is still running.</summary>
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
