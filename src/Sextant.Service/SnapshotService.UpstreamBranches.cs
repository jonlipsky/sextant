using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>The outcome of one upstream-branch reconcile pass (issue #338).</summary>
public sealed record UpstreamBranchReport
{
    /// <summary>False when the step is off (<see cref="MaintenancePolicy.BranchUpstreamCheckMinutes"/> is 0) or no remote lister is wired (locate mode).</summary>
    public required bool Enabled { get; init; }

    /// <summary>Repositories with a non-default branch pointer that were due for a check, before the per-tick cap.</summary>
    public int Due { get; init; }

    /// <summary>Repositories whose remote was listed this pass (successfully or not).</summary>
    public int Checked { get; init; }

    /// <summary>Listings that failed or were not trustworthy (timeout, error, unparseable, empty for a repository with pointers).</summary>
    public int ListingFailures { get; init; }

    /// <summary>Pointers absent upstream that were left alone because a job, handover or advance is pending against them.</summary>
    public int Busy { get; init; }

    /// <summary>Pointers whose stored name cannot be compared with a listing (a <c>refs/</c> prefix or characters git refs cannot have); never retired.</summary>
    public int Unverifiable { get; init; }

    /// <summary>Pointers absent upstream for the first time: retired only if the next successful listing agrees.</summary>
    public int AwaitingConfirmation { get; init; }

    /// <summary>Retirements submitted this pass.</summary>
    public int Attempted { get; init; }

    /// <summary>Branch pointers this pass deleted.</summary>
    public int Retired { get; init; }

    /// <summary>Submitted retirements that did not apply (the pointer moved, work appeared, or the branch went away).</summary>
    public int Skipped { get; init; }

    /// <summary>Submitted retirements still waiting for the writer when the step's bound passed; they apply when their turn comes.</summary>
    public int Deferred { get; init; }

    public double DurationMs { get; init; }
}

public sealed partial class SnapshotService
{
    /// <summary>The audit caller of the upstream-branch reconcile's retirements (issue #338).</summary>
    public static readonly AuditCaller UpstreamPolicyCaller = new("upstream_policy", ";source=upstream_policy");

    // Issue #338: in-memory state of the upstream-branch step, read and written only by the serialized maintenance tick
    // (a restart simply re-checks and re-confirms).
    private readonly Dictionary<string, long> _upstreamCheckedAt = new(StringComparer.Ordinal);
    // Value: when the listing that first found the pointer absent started.
    private readonly Dictionary<(string Repository, string Branch, long SnapshotId), long> _upstreamAbsentOnce = [];

    // Repositories whose listing is still running (a previous tick abandoned the wait); guarded by its own lock because
    // the listing tasks clear their entry from the thread pool. Everything else above stays on the tick's flow.
    private readonly HashSet<string> _upstreamListing = new(StringComparer.Ordinal);

    private const int UpstreamListingParallelism = 3;
    private const int UpstreamConfirmationWindowIntervals = 3;

    private const long UpstreamDueSlackMaxMs = 5 * 60 * 1000;

    /// <summary>
    /// Timer jitter tolerance of the due test: a repository is due when its last check (recorded as the START of the
    /// maintenance tick that listed it) is at least the interval minus this slack ago, so a tick that fires a little early still
    /// lists it. A tenth of the interval, at most five minutes.
    /// </summary>
    private static long DueSlack(long intervalMs) => Math.Min(intervalMs / 10, UpstreamDueSlackMaxMs);

    private readonly record struct UpstreamListing(string RepositoryKey, bool Listed, IReadOnlySet<string>? Heads, long StartedAt);

    /// <summary>
    /// Retires non-default branch pointers whose branch no longer exists on the remote (issue #338). Per tick it lists
    /// (<c>ls-remote --heads</c>, OUTSIDE the writer gate) at most
    /// <see cref="MaintenancePolicy.BranchUpstreamCheckMaxRepositoriesPerTick"/> repositories that have such pointers and
    /// were last checked at least <see cref="MaintenancePolicy.BranchUpstreamCheckMinutes"/> ago, least recently checked
    /// first. A listing that is not complete (failure, timeout, empty for a repository with pointers) retires nothing. A
    /// pointer is retired only when absent on two consecutive successful listings (same snapshot, so a moved pointer starts
    /// over) and the two listings began at most three check intervals apart. Up to three repositories are listed in
    /// parallel and the whole listing phase gets half of what is left of the tick deadline; repositories not listed in
    /// time stay due. Each retirement goes through the audited retire path (actor <c>upstream_policy</c>, snapshot + updated_at
    /// compare-and-swap, never a default branch, never a branch with pending work) and records no retired head, since the
    /// branch is gone from git too.
    /// </summary>
    internal async Task<UpstreamBranchReport> ReconcileUpstreamBranchesAsync(DateTimeOffset tickStartedAt, CancellationToken cancellationToken = default)
    {
        var policy = _options.Maintenance;
        if (policy.BranchUpstreamCheckMinutes <= 0 || _remoteBranches is not { } lister)
            return new UpstreamBranchReport { Enabled = false };

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var tickStart = tickStartedAt.ToUnixTimeMilliseconds();
        var interval = (long)TimeSpan.FromMinutes(
            Math.Min(policy.BranchUpstreamCheckMinutes, MaintenancePolicy.MaxBranchUpstreamCheckMinutes)).TotalMilliseconds;

        IReadOnlyList<IdleBranchCandidate> pointers;
        HashSet<(string RepositoryKey, string Branch)> pending;
        using (var reader = OpenReadConnection())
        {
            pointers = new SnapshotStore(reader).ListIdleNonDefaultBranches(long.MaxValue);
            pending = new SnapshotJobStore(reader).BranchesWithPendingWork();
        }
        HashSet<string> advancing;
        HashSet<string> retiring;
        lock (_inFlightLock)
        {
            advancing = [.. _latestAdvance.Keys];
            retiring = [.. _pendingIdleRetires];
        }

        var byRepository = pointers
            .GroupBy(p => RemoteUrlIdentity.Normalize(p.RepositoryRemoteUrl), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        // Forget repositories that no longer have a non-default pointer.
        foreach (var gone in _upstreamCheckedAt.Keys.Where(k => !byRepository.ContainsKey(k)).ToList())
            _upstreamCheckedAt.Remove(gone);
        ForgetAbsent(k => !byRepository.ContainsKey(k.Repository));

        var due = byRepository
            .Where(r => !_upstreamCheckedAt.TryGetValue(r.Key, out var last) || tickStart - last >= interval - DueSlack(interval))
            .OrderBy(r => _upstreamCheckedAt.GetValueOrDefault(r.Key))
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

        var checkedCount = 0;
        var failures = 0;
        var busy = 0;
        var awaiting = 0;
        var unverifiable = 0;
        var retirements = new List<Task<RetireBranchResult>>();

        var batch = due.Where(r => { lock (_upstreamListing) return !_upstreamListing.Contains(r.Key); })
            .Take(policy.BranchUpstreamCheckMaxRepositoriesPerTick).ToList();

        // The listings run in parallel, bounded, outside the writer gate; the in-memory state below is touched only by
        // this flow once they return, never from the listing tasks.
        var listings = await ListUpstreamAsync(lister, batch, cancellationToken).ConfigureAwait(false);
        var notListed = batch.Count - listings.Count(l => l.Listed);

        foreach (var listing in listings.Where(l => l.Listed))
        {
            var repositoryKey = listing.RepositoryKey;
            var branches = byRepository[repositoryKey];
            checkedCount++;
            _upstreamCheckedAt[repositoryKey] = tickStart;
            var heads = listing.Heads;
            // The repository has pointers, so an empty listing is as untrustworthy as a failed one. Either one drops the
            // pending confirmations, so two "absent" listings are never separated by a gap nobody saw.
            if (heads is null || heads.Count == 0)
            {
                failures++;
                ForgetAbsent(k => k.Repository == repositoryKey);
                continue;
            }

            var current = branches.Select(b => (repositoryKey, b.Name, b.SnapshotId)).ToHashSet();
            // A pointer that moved or went away drops out of the confirmation set.
            foreach (var stale in _upstreamAbsentOnce.Keys.Where(k => k.Repository == repositoryKey && !current.Contains(k)).ToList())
                _upstreamAbsentOnce.Remove(stale);

            foreach (var candidate in branches)
            {
                var key = (repositoryKey, candidate.Name, candidate.SnapshotId);
                if (!RemoteBranchHeads.IsVerifiableBranchName(candidate.Name))
                {
                    unverifiable++;
                    _upstreamAbsentOnce.Remove(key);
                    continue;
                }
                if (heads.Contains(candidate.Name))
                {
                    _upstreamAbsentOnce.Remove(key);
                    continue;
                }
                if (pending.Contains((repositoryKey, candidate.Name)) || advancing.Contains(repositoryKey + "\n" + candidate.Name)
                    || retiring.Contains(IdleRetireKey(candidate.RepositoryRemoteUrl, candidate.Name)))
                {
                    busy++;
                    continue;
                }
                if (!_upstreamAbsentOnce.TryGetValue(key, out var firstAbsentAt)
                    || listing.StartedAt - firstAbsentAt > UpstreamConfirmationWindowIntervals * interval)
                {
                    _upstreamAbsentOnce[key] = listing.StartedAt;
                    awaiting++;
                    continue;
                }

                _upstreamAbsentOnce.Remove(key);
                retirements.Add(StartRetire(
                    new RetireBranchRequest { Repository = candidate.RepositoryRemoteUrl, Branch = candidate.Name },
                    UpstreamPolicyCaller,
                    new IdleRetireGuard(candidate.SnapshotId, candidate.UpdatedAt, Upstream: true, ListedAtUnixMs: listing.StartedAt)));
            }
        }

        // Each retirement is service-owned: this only waits for them within what is left of the tick deadline.
        int deferred;
        RetireBranchResult[] results;
        using (var step = BeginStep(cancellationToken))
        {
            try
            {
                results = await Task.WhenAll(retirements).WaitAsync(step.Token).ConfigureAwait(false);
                deferred = 0;
            }
            catch (OperationCanceledException) when (step.TimedOut)
            {
                results = [.. retirements.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result)];
                deferred = retirements.Count - results.Length;
                DeferStep("upstream_branch_retire", $"{deferred} of {retirements.Count} retirement(s) still queued");
            }
        }
        if (notListed > 0)
            DeferStep("upstream_branch_check", $"{notListed} repository(ies) not listed");

        var retired = results.Count(r => r.Retired);
        return new UpstreamBranchReport
        {
            Enabled = true,
            Due = due.Count,
            Checked = checkedCount,
            ListingFailures = failures,
            Busy = busy,
            Unverifiable = unverifiable,
            AwaitingConfirmation = awaiting,
            Attempted = retirements.Count,
            Retired = retired,
            Skipped = retirements.Count - retired - deferred,
            Deferred = deferred,
            DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
    }

    // Lists the repositories of one batch, at most UpstreamListingParallelism at a time, inside one phase bound of half of
    // what is left of the tick deadline. A listing that outlasts the bound is abandoned: its git call is bounded by its
    // own timeout and only deletes its throwaway directory, so it is harmless; _upstreamListing keeps the repository from
    // being listed again until it ends. The tasks never touch the step's other state.
    private async Task<List<UpstreamListing>> ListUpstreamAsync(
        IRemoteBranchLister lister, List<KeyValuePair<string, List<IdleBranchCandidate>>> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return [];
        var budget = StepRemaining() / 2;
        if (budget <= TimeSpan.Zero)
            return [.. batch.Select(r => new UpstreamListing(r.Key, false, null, 0))];

        using var phaseBound = new CancellationTokenSource(budget, _timeProvider);
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, phaseBound.Token);
        using var slots = new SemaphoreSlim(UpstreamListingParallelism);

        async Task<UpstreamListing> ListOneAsync(KeyValuePair<string, List<IdleBranchCandidate>> repository)
        {
            var notListed = new UpstreamListing(repository.Key, false, null, 0);
            try
            {
                await slots.WaitAsync(phase.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return notListed;
            }
            try
            {
                var startedAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var url = repository.Value[0].RepositoryRemoteUrl;
                lock (_upstreamListing)
                {
                    if (!_upstreamListing.Add(repository.Key))
                        return notListed;
                }
                var listing = Task.Run(() =>
                {
                    try
                    {
                        return SafeListBranches(lister, url);
                    }
                    finally
                    {
                        lock (_upstreamListing)
                            _upstreamListing.Remove(repository.Key);
                    }
                }, CancellationToken.None);
                try
                {
                    return new UpstreamListing(repository.Key, true, await listing.WaitAsync(phase.Token).ConfigureAwait(false), startedAt);
                }
                catch (OperationCanceledException)
                {
                    return notListed;
                }
            }
            finally
            {
                slots.Release();
            }
        }

        var results = await Task.WhenAll(batch.Select(ListOneAsync)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return [.. results];
    }

    internal bool IsListingUpstreamForTesting(string repositoryUrl)
    {
        lock (_upstreamListing)
            return _upstreamListing.Contains(RemoteUrlIdentity.Normalize(repositoryUrl));
    }

    private void ForgetAbsent(Func<(string Repository, string Branch, long SnapshotId), bool> predicate)
    {
        foreach (var key in _upstreamAbsentOnce.Keys.Where(predicate).ToList())
            _upstreamAbsentOnce.Remove(key);
    }

    // A throwing lister is a failed listing, never a failed tick.
    private static IReadOnlySet<string>? SafeListBranches(IRemoteBranchLister lister, string url)
    {
        try
        {
            return lister.ListBranches(url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
