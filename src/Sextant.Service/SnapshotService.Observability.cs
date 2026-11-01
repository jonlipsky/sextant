using Sextant.Service.Observability;
using Sextant.Store;

namespace Sextant.Service;

public sealed partial class SnapshotService
{
    // Issue #355: the optional background loop behind the freshness signal. It is inert unless configured (clone mode
    // with a freshness interval), never touches the write gate, never overlaps itself (one sequential loop), and stops
    // with the service lifetime like the maintenance loop.
    private const int RemoteHeadParallelism = 3;
    private static readonly TimeSpan RemoteHeadTimeout = TimeSpan.FromSeconds(30);

    private DefaultBranchFreshnessTracker _freshness = null!;
    private readonly DirectorySizeCache _sizeCache = new();
    private IRemoteBranchHeadResolver? _headResolver;
    private PeriodicTimer? _freshnessTimer;

    private void StartObservabilityLoops()
    {
        if (_headResolver is not null && _options.FreshnessCheckInterval > TimeSpan.Zero)
        {
            _freshnessTimer = new PeriodicTimer(_options.FreshnessCheckInterval, _timeProvider);
            var timer = _freshnessTimer;
            lock (_inFlightLock)
                _operations.Add(Task.Run(() => RunFreshnessLoopAsync(timer), CancellationToken.None));
        }
    }

    private async Task RunFreshnessLoopAsync(PeriodicTimer timer)
    {
        var lifetime = _lifetime.Token;
        try
        {
            do
            {
                await RunFreshnessPassAsync(lifetime).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(lifetime).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// Reads each tracked default branch's head from its remote (a hardened <c>git ls-remote</c> through
    /// <see cref="IRemoteBranchHeadResolver"/>) and records it. Runs outside the write gate with a few parallel
    /// calls, each bounded by <see cref="RemoteHeadTimeout"/>; the whole pass by the interval. An unreadable remote is
    /// recorded as such.
    /// </summary>
    internal async Task RunFreshnessPassAsync(CancellationToken lifetime)
    {
        if (_headResolver is not { } resolver)
            return;
        try
        {
            IReadOnlyList<DefaultBranchHead> heads;
            using (var conn = OpenReadConnection())
                heads = new SnapshotStore(conn).ListDefaultBranchHeads();
            if (heads.Count == 0)
                return;
            using var pass = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            pass.CancelAfter(_options.FreshnessCheckInterval);
            using var gate = new SemaphoreSlim(RemoteHeadParallelism);
            var noCode = new NoSupportedCodeLookup(this);
            await Task.WhenAll(heads.Select(async head =>
            {
                await gate.WaitAsync(pass.Token).ConfigureAwait(false);
                try
                {
                    var remote = await Task.Run(() => ResolveHead(resolver, head, pass.Token), CancellationToken.None).ConfigureAwait(false);
                    // A pass the interval cut short records nothing for what it did not finish: the earlier observation
                    // stays, and goes stale by the usual rule, so `unknown` does not flap.
                    if (pass.IsCancellationRequested)
                        return;
                    var holdsNoCode = remote is not null
                        && !string.Equals(head.CommitSha, remote, StringComparison.OrdinalIgnoreCase)
                        && noCode.Holds(remote, head.RepositoryRemoteUrl);
                    _freshness.Record(head.RepositoryRemoteUrl, head.CommitSha, remote, holdsNoCode);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A pass that ran out of time or hit a database error leaves the older observations to go stale (unknown).
            Log($"freshness: observation pass did not finish ({ex.GetType().Name}).");
        }
    }

    private static string? ResolveHead(IRemoteBranchHeadResolver resolver, DefaultBranchHead head, CancellationToken ct)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        call.CancelAfter(RemoteHeadTimeout);
        try
        {
            return resolver.ResolveBranchHead(head.RepositoryRemoteUrl, head.Branch, call.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // Which repositories an unsupported job marked as holding no supported code at a commit, read once per commit per
    // pass (the observation), so a metrics collection never scans the job table.
    private sealed class NoSupportedCodeLookup(SnapshotService service)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, HashSet<string>> _byCommit = new(StringComparer.OrdinalIgnoreCase);

        public bool Holds(string commit, string repositoryUrl)
        {
            lock (_gate)
            {
                if (!_byCommit.TryGetValue(commit, out var urls))
                {
                    using var conn = service.OpenReadConnection();
                    _byCommit[commit] = urls = new SnapshotJobStore(conn).RepositoriesWithNoSupportedCodeAt(commit)
                        .Select(DefaultBranchFreshnessTracker.LabelOf).ToHashSet(StringComparer.Ordinal);
                }
                return urls.Contains(DefaultBranchFreshnessTracker.LabelOf(repositoryUrl));
            }
        }
    }

    private void DisposeObservability()
    {
        _freshnessTimer?.Dispose();
    }
}
