using Sextant.Core;
using Sextant.Service.Observability;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>The outcome of one idle-branch retire pass (issue #299).</summary>
public sealed record IdleBranchRetireReport
{
    /// <summary>False when the rule is disabled (<see cref="MaintenancePolicy.BranchIdleRetireDays"/> is 0).</summary>
    public required bool Enabled { get; init; }

    /// <summary>Idle non-default branches found, before the pending-work filter and the per-pass cap.</summary>
    public int Idle { get; init; }

    /// <summary>Idle branches left alone because a job or a handover is pending against them.</summary>
    public int Busy { get; init; }

    /// <summary>Retirements submitted this pass (at most the per-pass cap).</summary>
    public int Attempted { get; init; }

    /// <summary>Branch pointers this pass deleted.</summary>
    public int Retired { get; init; }

    /// <summary>Retired-head records past <see cref="MaintenancePolicy.RetiredHeadRetention"/> this pass deleted.</summary>
    public int RetiredHeadsPruned { get; init; }

    /// <summary>Submitted retirements that did not apply (the pointer moved, work appeared, or the branch went away).</summary>
    public int Skipped { get; init; }

    /// <summary>Submitted retirements still waiting for the writer when the step's bound passed (issue #333); they apply when their turn comes.</summary>
    public int Deferred { get; init; }

    public double DurationMs { get; init; }
}

/// <summary>The outcome of one inactive-repository reclaim pass (issue #291).</summary>
public sealed record RepositoryReclaimReport
{
    /// <summary>False when the rule is disabled (<see cref="MaintenancePolicy.RepositoryReclaimDays"/> is 0).</summary>
    public required bool Enabled { get; init; }

    /// <summary>True when the pass could not get the writer within <see cref="MaintenanceStepLimit"/>, so it stopped (retried next tick).</summary>
    public bool Busy { get; init; }

    /// <summary>Inactive repositories found, before the on-disk filter and the per-pass cap.</summary>
    public int Inactive { get; init; }

    /// <summary>Repositories whose checkout and caches were (at least partly) deleted this pass.</summary>
    public int Reclaimed { get; init; }

    /// <summary>Bytes freed, measured before deleting.</summary>
    public long BytesFreed { get; init; }

    /// <summary>Candidates left alone because work or activity appeared before their turn at the writer.</summary>
    public int Skipped { get; init; }

    /// <summary>Repositories whose reclaim failed (logged; a later pass retries).</summary>
    public int Failed { get; init; }

    public double DurationMs { get; init; }
}

/// <summary>What one maintenance tick did (issue #299).</summary>
public sealed record MaintenanceTickReport
{
    public required IdleBranchRetireReport IdleBranches { get; init; }

    /// <summary>
    /// What happened to scheduled retention this tick: <c>disabled</c>, <c>not_due</c> (before the hour, or already run
    /// today), or <c>ran</c>. A due pass no longer skips while indexing holds the writer (issue #333): it waits for its
    /// priority turn inside its pass budget and continues next tick if the budget ran out.
    /// </summary>
    public required string Retention { get; init; }

    /// <summary>The scheduled retention pass's report when <see cref="Retention"/> is <c>ran</c>.</summary>
    public RetentionReport? RetentionReport { get; init; }

    public double RetentionDurationMs { get; init; }

    /// <summary>
    /// True when the scheduled pass ended with work left after making progress (issue #333): the loop then ticks again
    /// after <see cref="MaintenancePolicy.RetentionContinueMinutes"/> instead of at the next interval tick.
    /// </summary>
    public bool ContinueSoon { get; init; }

    /// <summary>The tick's inactive-repository reclaim pass (issue #291).</summary>
    public RepositoryReclaimReport? RepositoryReclaim { get; init; }

    /// <summary>The tick's upstream-branch reconcile pass (issue #338).</summary>
    public UpstreamBranchReport? UpstreamBranches { get; init; }
}

public sealed partial class SnapshotService
{
    /// <summary>The audit caller of the idle-branch rule's retirements (issue #299).</summary>
    public static readonly AuditCaller IdlePolicyCaller = new("idle_policy", ";source=idle_policy");

    /// <summary>The audit caller of the scheduled retention pass (issue #299).</summary>
    public static readonly AuditCaller RetentionScheduleCaller = new("retention_schedule", ";source=retention_schedule");

    /// <summary>The audit caller of the inactive-repository reclaim (issue #291).</summary>
    public static readonly AuditCaller ReclaimPolicyCaller = new("reclaim_policy", ";source=reclaim_policy");

    // Issue #299: one maintenance tick at a time (the loop's own, or a direct call).
    private readonly SemaphoreSlim _maintenanceTick = new(1, 1);
    private readonly SemaphoreSlim _maintenanceTicked = new(0, int.MaxValue);

    // The loop's timer, created in Start on the service's clock (so the first tick is exactly one interval later) and
    // disposed by Dispose once the loop has drained. Null when the loop is off.
    private PeriodicTimer? _maintenanceTimer;

    // The UTC day the scheduled retention last ran on, so it runs once a day; read from the audit log on the first due
    // tick so a restart does not run it twice the same day. Only the (serialized) tick reads and writes it.
    private DateOnly? _lastScheduledRetentionDay;
    private bool _lastScheduledRetentionDayLoaded;

    /// <summary>The report of the loop's latest successful tick, or null before the first.</summary>
    public MaintenanceTickReport? LastMaintenanceTick { get; private set; }

    /// <summary>Test hook: runs at the start of every maintenance tick (a throwing hook fails that tick).</summary>
    internal Func<Task>? BeforeMaintenanceTick { get; set; }

    // Issue #333: ONE deadline per maintenance tick, shared by every step. Read on the service's clock, so tests drive it
    // with a ManualClock. Each step waits and works only for what is left of it (BeginStep).
    internal TimeSpan MaintenanceStepLimit { get; set; } = TimeSpan.Zero;

    internal TimeSpan MaintenanceStepLimitForTesting => StepLimit();

    private TimeSpan StepLimit() => MaintenanceStepLimit > TimeSpan.Zero
        ? MaintenanceStepLimit
        : TimeSpan.FromSeconds(Math.Clamp(_options.Maintenance.TickDeadlineSeconds, 1, MaintenancePolicy.MaxTickDeadlineSeconds));

    // Set under the serialized tick (_maintenanceTick) to the moment the running tick's deadline passes; null outside a tick.
    private DateTimeOffset? _tickDeadlineAt;

    private TimeSpan StepRemaining()
    {
        if (_tickDeadlineAt is not { } at)
            return StepLimit();
        var left = at - _timeProvider.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    // A token cancelled when the caller's is, or when the step's bound passes; TimedOut tells the two apart.
    private sealed class StepBound : IDisposable
    {
        private readonly CancellationTokenSource _bound;
        private readonly CancellationTokenSource _linked;
        private readonly CancellationToken _outer;

        public StepBound(TimeSpan limit, TimeProvider time, CancellationToken outer)
        {
            _outer = outer;
            _bound = new CancellationTokenSource(limit, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _bound.Token);
        }

        public CancellationToken Token => _linked.Token;

        public bool TimedOut => _bound.IsCancellationRequested && !_outer.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _bound.Dispose();
        }
    }

    private StepBound BeginStep(CancellationToken cancellationToken) => new(StepRemaining(), _timeProvider, cancellationToken);

    private void DeferStep(string step, string detail)
    {
        _metrics.RecordMaintenanceDeferral(step);
        Log($"maintenance: {step} could not finish within the tick deadline of {StepLimit().TotalSeconds:F0} s ({detail}); deferred to the next tick.");
    }

    // Starts the loop (issue #299) when the policy enables it. Tracked as an operation, so Dispose drains it after
    // StopProduction cancelled the lifetime it waits on.
    private void StartMaintenanceLoop()
    {
        var interval = _options.Maintenance.Interval;
        if (interval <= TimeSpan.Zero)
            return;
        var longest = TimeSpan.FromMinutes(MaintenancePolicy.MaxIntervalMinutes);
        if (interval > longest)
            interval = longest;
        _maintenanceTimer = new PeriodicTimer(interval, _timeProvider);
        lock (_inFlightLock)
            _operations.Add(Task.Run(() => RunMaintenanceLoopAsync(_maintenanceTimer), CancellationToken.None));
    }

    private async Task RunMaintenanceLoopAsync(PeriodicTimer timer)
    {
        var lifetime = _lifetime.Token;
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime).ConfigureAwait(false))
            {
                // Issue #333: while a scheduled pass keeps making progress with work left, tick again after the short
                // continuation interval instead of waiting for the next interval tick.
                var soon = TimeSpan.FromMinutes(Math.Clamp(
                    _options.Maintenance.RetentionContinueMinutes, 0, MaintenancePolicy.MaxRetentionContinueMinutes));
                while (await RunLoopTickAsync(lifetime).ConfigureAwait(false)
                    && soon > TimeSpan.Zero && LastMaintenanceTick is { } last && ShouldTickSoon(last)
                    && TryCountContinue())
                    // Never re-plan faster than the last pass took to plan and run.
                    await Task.Delay(MinReTickDelay(soon, last), _timeProvider, lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    // One loop tick: false when it failed (the next interval tick retries), never throws except the shutdown cancellation.
    /// <summary>At most this many short-interval continuation ticks run per UTC day; later continues wait for the interval tick.</summary>
    internal const int MaxContinuesPerDay = 100;

    private DateOnly _continueDay;
    private int _continuesToday;

    private bool TryCountContinue()
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        if (_continueDay != today)
        {
            _continueDay = today;
            _continuesToday = 0;
        }
        return ++_continuesToday <= MaxContinuesPerDay;
    }

    /// <summary>The delay before a continuation tick: the continue interval, or the previous pass's duration if longer.</summary>
    internal static TimeSpan MinReTickDelay(TimeSpan interval, MaintenanceTickReport last)
    {
        var previous = TimeSpan.FromMilliseconds(Math.Max(0, last.RetentionDurationMs));
        return previous > interval ? previous : interval;
    }

    // One loop tick: false when it failed (the next interval tick retries), never throws except the shutdown cancellation.
    private async Task<bool> RunLoopTickAsync(CancellationToken lifetime)
    {
        try
        {
            LastMaintenanceTick = await RunMaintenanceTickAsync(lifetime).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed tick never ends the loop: the next tick retries.
            _metrics.RecordMaintenanceFailure();
            Log($"maintenance: tick failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            _maintenanceTicked.Release();
        }
    }

    /// <summary>
    /// Waits until the maintenance loop has finished another tick (successful or failed). Test support: the loop ticks on
    /// the service's <see cref="TimeProvider"/>, so a test advances its clock and then waits here.
    /// </summary>
    internal async Task<bool> WaitForMaintenanceTickAsync(TimeSpan timeout) =>
        // A test's bounded wait: not tied to the service lifetime, so a tick completing during shutdown is still seen.
        await _maintenanceTicked.WaitAsync(timeout, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Runs one maintenance tick (issue #299): the idle-branch retire pass, the upstream-branch reconcile (issue #338),
    /// the inactive-repository reclaim, then the scheduled retention pass when it is
    /// due and the writer is free. The loop calls it every <see cref="MaintenancePolicy.Interval"/>; ticks never overlap
    /// (a call made while one runs waits for it).
    /// </summary>
    public async Task<MaintenanceTickReport> RunMaintenanceTickAsync(CancellationToken cancellationToken = default)
    {
        await _maintenanceTick.WaitAsync(cancellationToken).ConfigureAwait(false);
        _tickDeadlineAt = _timeProvider.GetUtcNow() + StepLimit();
        try
        {
            if (BeforeMaintenanceTick is { } hook)
                await hook().ConfigureAwait(false);

            var idle = await RetireIdleBranchesAsync(cancellationToken).ConfigureAwait(false);
            _metrics.RecordIdleBranchRetirePass(idle.Retired, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), idle.DurationMs);
            if (idle.Enabled)
                Log($"maintenance: idle-branch pass retired {idle.Retired} branch pointer(s) " +
                    $"(idle {idle.Idle}, busy {idle.Busy}, attempted {idle.Attempted}, skipped {idle.Skipped}, " +
                    $"retired heads pruned {idle.RetiredHeadsPruned}) " +
                    $"in {idle.DurationMs:F0} ms.");

            var upstream = await ReconcileUpstreamBranchesAsync(cancellationToken).ConfigureAwait(false);
            if (upstream.Enabled)
            {
                _metrics.RecordUpstreamBranchPass(
                    upstream.Checked, upstream.ListingFailures, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), upstream.DurationMs);
                Log($"maintenance: upstream-branch pass retired {upstream.Retired} branch pointer(s) " +
                    $"(repositories due {upstream.Due}, checked {upstream.Checked}, listing failures {upstream.ListingFailures}, " +
                    $"busy {upstream.Busy}, awaiting confirmation {upstream.AwaitingConfirmation}, " +
                    $"attempted {upstream.Attempted}, skipped {upstream.Skipped}) in {upstream.DurationMs:F0} ms.");
            }
            var reclaim = await ReclaimInactiveRepositoriesAsync(cancellationToken).ConfigureAwait(false);
            if (reclaim.Enabled && (!reclaim.Busy || reclaim.Reclaimed + reclaim.Failed > 0))
            {
                _metrics.RecordRepositoryReclaimPass(
                    reclaim.Reclaimed, reclaim.BytesFreed, reclaim.Failed, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), reclaim.DurationMs);
                Log($"maintenance: repository reclaim freed {reclaim.BytesFreed / (1024 * 1024)} MiB ({reclaim.BytesFreed} bytes) " +
                    $"from {reclaim.Reclaimed} repository(ies) (inactive {reclaim.Inactive}, skipped {reclaim.Skipped}, " +
                    $"failed {reclaim.Failed}) in {reclaim.DurationMs:F0} ms.");
            }

            var report = await RunScheduledRetentionIfDueAsync(idle, cancellationToken).ConfigureAwait(false);
            return report with { RepositoryReclaim = reclaim, UpstreamBranches = upstream };
        }
        finally
        {
            _tickDeadlineAt = null;
            _maintenanceTick.Release();
        }
    }

    /// <summary>
    /// Retires the non-default branch pointers that have not moved for more than
    /// <see cref="MaintenancePolicy.BranchIdleRetireDays"/> days (issue #299), least recently moved first, at most
    /// <see cref="MaintenancePolicy.BranchIdleRetireMaxPerPass"/> of them. Candidates are read on an independent reader;
    /// each retirement then goes through the audited retire path (issue #158 write turn, one transaction with its
    /// <c>retire</c> audit row, actor <c>idle_policy</c>) and applies only while the branch is still non-default, its
    /// pointer is exactly the one read (snapshot and <c>updated_at</c>), and no queued/running job or handover is pending
    /// against it. Only the pointer row goes: the snapshots stay for retention, whose protected set keeps any snapshot
    /// another branch still reaches.
    /// </summary>
    public async Task<IdleBranchRetireReport> RetireIdleBranchesAsync(CancellationToken cancellationToken = default)
    {
        var policy = _options.Maintenance;
        if (policy.BranchIdleRetireDays <= 0)
        {
            // Records the rule wrote while it was on still expire on schedule.
            return new IdleBranchRetireReport
            {
                Enabled = false, RetiredHeadsPruned = await PruneRetiredHeadsBoundedAsync(cancellationToken).ConfigureAwait(false)
            };
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var idleDays = Math.Min(policy.BranchIdleRetireDays, MaintenancePolicy.MaxBranchIdleRetireDays);
        var cutoff = _timeProvider.GetUtcNow().AddDays(-idleDays).ToUnixTimeMilliseconds();
        IReadOnlyList<IdleBranchCandidate> idle;
        HashSet<(string RepositoryKey, string Branch)> pending;
        using (var reader = OpenReadConnection())
        {
            idle = new SnapshotStore(reader).ListIdleNonDefaultBranches(cutoff);
            pending = new SnapshotJobStore(reader).BranchesWithPendingWork();
        }
        // An admitted branch-advance ensure that has not finished is pending work too (issue #273's registry), and so is
        // an idle retirement an earlier tick queued that has not applied yet (issue #333: never queue a duplicate).
        HashSet<string> advancing;
        HashSet<string> retiring;
        lock (_inFlightLock)
        {
            advancing = [.. _latestAdvance.Keys];
            retiring = [.. _pendingIdleRetires];
        }

        var eligible = new List<IdleBranchCandidate>();
        var busy = 0;
        foreach (var candidate in idle)
        {
            var repositoryKey = RemoteUrlIdentity.Normalize(candidate.RepositoryRemoteUrl);
            if (pending.Contains((repositoryKey, candidate.Name)) || advancing.Contains(repositoryKey + "\n" + candidate.Name)
                || retiring.Contains(IdleRetireKey(candidate.RepositoryRemoteUrl, candidate.Name)))
                busy++;
            else
                eligible.Add(candidate);
        }

        var retirements = new List<Task<RetireBranchResult>>();
        foreach (var candidate in eligible.Take(policy.BranchIdleRetireMaxPerPass))
        {
            cancellationToken.ThrowIfCancellationRequested();
            retirements.Add(StartRetire(
                new RetireBranchRequest { Repository = candidate.RepositoryRemoteUrl, Branch = candidate.Name },
                IdlePolicyCaller,
                new IdleRetireGuard(candidate.SnapshotId, candidate.UpdatedAt)));
        }
        // Each retirement is service-owned: this only waits for them (cancellation or the step bound ends the wait, not the
        // retirements, which apply when their priority turn comes).
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
                DeferStep("idle_branch_retire", $"{deferred} of {retirements.Count} retirement(s) still queued");
            }
        }
        var retired = results.Count(r => r.Retired);
        var pruned = await PruneRetiredHeadsBoundedAsync(cancellationToken).ConfigureAwait(false);
        return new IdleBranchRetireReport
        {
            Enabled = true,
            Idle = idle.Count,
            Busy = busy,
            Attempted = retirements.Count,
            Retired = retired,
            Skipped = retirements.Count - retired - deferred,
            Deferred = deferred,
            RetiredHeadsPruned = pruned,
            DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
    }

    /// <summary>
    /// Deletes the checkout, git object store (submodule stores included) and package folder of repositories the service
    /// no longer indexes (issue #291), least recently active first, at most
    /// <see cref="MaintenancePolicy.RepositoryReclaimMaxPerPass"/> per pass. A repository is inactive when it has no
    /// branch pointer, no queued/running job and no pending handover (or admitted branch advance), and its latest
    /// catalog activity is at least <see cref="MaintenancePolicy.RepositoryReclaimDays"/> days old. Activity is the most
    /// recent of its row's creation, branch <c>updated_at</c>, snapshot <c>created_at</c>/<c>published_at</c>, job
    /// <c>created_at</c>/<c>updated_at</c>/<c>started_at</c>/<c>completed_at</c>, handover <c>recorded_at</c> and
    /// retired-head <c>retired_at</c>, so a repository whose last branch was just retired waits the full period. The
    /// candidate is re-checked and its directories renamed aside inside the write gate, so it never races an ensure or
    /// an index of that repository; the delete runs after the gate is released. Catalog rows stay (retention owns them):
    /// a later ensure re-clones cold. While indexing holds the writer the pass does not run (retried next tick).
    /// </summary>
    public async Task<RepositoryReclaimReport> ReclaimInactiveRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        var policy = _options.Maintenance;
        if (policy.RepositoryReclaimDays <= 0)
            return new RepositoryReclaimReport { Enabled = false };

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var cutoff = _timeProvider.GetUtcNow()
            .AddDays(-Math.Min(policy.RepositoryReclaimDays, MaintenancePolicy.MaxRepositoryReclaimDays)).ToUnixTimeMilliseconds();
        var reclaimer = new RepositoryReclaimer(_paths, Log);
        reclaimer.SweepInterrupted();

        IReadOnlyList<RepositoryActivity> activity;
        using (var reader = OpenReadConnection())
            activity = new RepositoryActivityStore(reader).List();
        var inactive = activity.Where(a => IsInactive(a, cutoff)).ToList();
        var activeNames = ActiveDirectoryNames(activity, cutoff);

        int reclaimed = 0, skipped = 0, failed = 0;
        var deferredPass = false;
        long bytes = 0;
        foreach (var candidate in inactive)
        {
            if (reclaimed >= policy.RepositoryReclaimMaxPerPass)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            if (activeNames.Contains(ServicePaths.RepoDirectoryName(candidate.RemoteUrl)))
            {
                skipped++;
                continue;
            }
            var plan = reclaimer.Find(candidate.RemoteUrl);
            if (plan.Refused.Count > 0)
                LogRefusedOncePerDay(candidate.RepositoryId, plan.Refused);
            if (plan.Targets.Count == 0)
                continue;
            try
            {
                if (BeforeReclaimWriterTurn is { } hook)
                    await hook().ConfigureAwait(false);
                bool turnTimedOut;
                ReclaimOutcome? outcome;
                // The wait for the writer is bounded per candidate (issue #333): a pass that cannot get a turn stops
                // here and the next tick retries, instead of blocking the loop.
                using (var step = BeginStep(cancellationToken))
                {
                    try
                    {
                        outcome = await WithWriteAsync(
                            () => Task.FromResult(ReclaimUnderWriter(reclaimer, candidate, cutoff)), step.Token, priority: true)
                            .ConfigureAwait(false);
                        turnTimedOut = false;
                    }
                    catch (OperationCanceledException) when (step.TimedOut)
                    {
                        outcome = null;
                        turnTimedOut = true;
                    }
                }
                if (turnTimedOut)
                {
                    deferredPass = true;
                    DeferStep("repository_reclaim", $"repository {candidate.RepositoryId}");
                    break;
                }
                if (outcome is null)
                {
                    skipped++;
                    continue;
                }
                // Measured and deleted outside the write gate; only a directory that is really gone counts as freed.
                long freed = 0;
                var deleted = new List<string>();
                foreach (var aside in outcome.Asides)
                {
                    var size = RepositoryReclaimer.Measure(aside.Path);
                    if (!reclaimer.Delete(aside))
                        continue;
                    freed += size;
                    deleted.Add(aside.Target.Kind.Replace(' ', '_'));
                }
                var clean = outcome.MoveFailures == 0 && deleted.Count == outcome.Asides.Count;
                if (BeforeReclaimAudit is { } beforeAudit)
                    await beforeAudit().ConfigureAwait(false);
                await AuditReclaimAsync(outcome.RemoteUrl, clean, deleted, freed, cancellationToken).ConfigureAwait(false);
                bytes += freed;
                if (clean)
                    reclaimed++;
                else
                    failed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                Log($"maintenance: repository reclaim failed for repository {candidate.RepositoryId}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return new RepositoryReclaimReport
        {
            Enabled = true, Busy = deferredPass, Inactive = inactive.Count, Reclaimed = reclaimed, BytesFreed = bytes, Skipped = skipped, Failed = failed,
            DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
    }

    private static bool IsInactive(RepositoryActivity a, long cutoffMs) =>
        !a.HasBranches && !a.HasPendingWork && a.LastActivityMs < cutoffMs;

    private bool HasAdmittedAdvance(string remoteUrl)
    {
        var prefix = RemoteUrlIdentity.Normalize(remoteUrl) + "\n";
        lock (_inFlightLock)
            return _latestAdvance.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    // Test seam: runs after a reclaim's directories are deleted and before its audit row is written.
    internal Func<Task>? BeforeReclaimAudit { get; set; }

    // Test seam: runs after a candidate is selected and before it takes its writer turn.
    internal Func<Task>? BeforeReclaimWriterTurn { get; set; }

    private readonly Dictionary<long, DateOnly> _reclaimRefusedLogged = [];

    private void LogRefusedOncePerDay(long repositoryId, IReadOnlyList<string> refused)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        lock (_reclaimRefusedLogged)
        {
            if (_reclaimRefusedLogged.TryGetValue(repositoryId, out var day) && day == today)
                return;
            _reclaimRefusedLogged[repositoryId] = today;
        }
        Log($"maintenance: repository reclaim refused for repository {repositoryId}: {string.Join("; ", refused)}.");
    }

    // Directory names of repositories that are NOT inactive: an inactive repository sharing one is never reclaimed.
    private static HashSet<string> ActiveDirectoryNames(IEnumerable<RepositoryActivity> activity, long cutoff) =>
        activity.Where(a => !IsInactive(a, cutoff)).Select(a => ServicePaths.RepoDirectoryName(a.RemoteUrl)).ToHashSet(StringComparer.Ordinal);

    // The audit row of a completed reclaim has its own small bound, independent of the tick deadline (which may be spent
    // by then): the directories are already gone, so the row should not be lost to an exhausted tick.
    internal TimeSpan ReclaimAuditWait { get; set; } = TimeSpan.FromSeconds(5);

    private async Task AuditReclaimAsync(string remoteUrl, bool clean, List<string> kinds, long bytes, CancellationToken cancellationToken)
    {
        using var step = new StepBound(ReclaimAuditWait, _timeProvider, cancellationToken);
        try
        {
            await WithWriteAsync(() =>
            {
                EnsureLeaseHeld();
                new AuditLogStore(_conn).Append(
                    AuditAction.Reclaim, clean ? AuditOutcome.Complete : AuditOutcome.Failed, actor: ReclaimPolicyCaller.Actor,
                    repositoryScope: remoteUrl,
                    detail: ReclaimPolicyCaller.Detail((clean ? "reclaimed;" : "reclaim_incomplete;") + string.Join(',', kinds)),
                    costBytes: bytes);
                return Task.FromResult(0);
            }, step.Token, priority: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (step.TimedOut)
        {
            DeferStep("reclaim_audit", $"the audit row is skipped after {ReclaimAuditWait.TotalSeconds:F0} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"maintenance: repository reclaim audit failed: {ex.GetType().Name}.");
        }
    }

    private sealed record ReclaimOutcome(List<RepositoryReclaimer.Aside> Asides, string RemoteUrl, int MoveFailures);

    // Runs inside the write gate: re-checks that the repository is still inactive on the writer's own view of the
    // catalog, renames its directories aside. Null when it is no longer inactive.
    private ReclaimOutcome? ReclaimUnderWriter(RepositoryReclaimer reclaimer, RepositoryActivity candidate, long cutoff)
    {
        var all = new RepositoryActivityStore(_conn).List();
        var current = all.FirstOrDefault(a => a.RepositoryId == candidate.RepositoryId);
        if (current is null || !IsInactive(current, cutoff) || HasAdmittedAdvance(current.RemoteUrl)
            || ActiveDirectoryNames(all, cutoff).Contains(ServicePaths.RepoDirectoryName(current.RemoteUrl)))
            return null;
        var plan = reclaimer.Find(current.RemoteUrl);
        var asides = new List<RepositoryReclaimer.Aside>();
        var moveFailures = 0;
        foreach (var target in plan.Targets)
        {
            if (reclaimer.MoveAside(target) is { } aside)
                asides.Add(aside);
            else
                moveFailures++;
        }
        foreach (var aside in asides.Where(a => a.Target.Kind == "package folder"))
            RepositoryReclaimer.DeleteUsageRecord(aside.Target);
        return new ReclaimOutcome(asides, current.RemoteUrl, moveFailures);
    }

    // Deletes the retired-head records older than MaintenancePolicy.RetiredHeadRetention: a branch idle-retired that long
    // ago and never pushed since is no longer re-created by a push expecting its old head (it is re-created like a new
    // branch, or by any unguarded ensure). Takes the writer only when there is something to prune.
    private async Task<int> PruneRetiredHeadsBoundedAsync(CancellationToken cancellationToken)
    {
        using var step = BeginStep(cancellationToken);
        try
        {
            return await PruneRetiredHeadsAsync(step.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (step.TimedOut)
        {
            DeferStep("retired_head_prune", "pruning retired heads");
            return 0;
        }
    }

    private async Task<int> PruneRetiredHeadsAsync(CancellationToken cancellationToken)
    {
        var cutoff = (_timeProvider.GetUtcNow() - MaintenancePolicy.RetiredHeadRetention).ToUnixTimeMilliseconds();
        using (var reader = OpenReadConnection())
        {
            using var probe = reader.CreateCommand();
            probe.CommandText = "SELECT EXISTS (SELECT 1 FROM retired_branch_heads WHERE retired_at < @cutoff);";
            probe.Parameters.AddWithValue("@cutoff", cutoff);
            if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long found || found == 0)
                return 0;
        }
        return await WithWriteAsync(
            () => Task.FromResult(new SnapshotStore(_conn).PruneRetiredHeads(cutoff)), cancellationToken, priority: true).ConfigureAwait(false);
    }

    // The tick's scheduled retention (issue #299): once per UTC day, on the first tick at or after the configured hour,
    // the same bounded pass as POST /control/retention?execute=true, audited as retention_schedule. The pass takes priority
    // turns on the write gate and runs its batches in held bursts (issue #333); a production admitted meanwhile waits at
    // most one burst (SEXTANT_SERVICE_RETENTION_BURST_SECONDS) plus one batch for its turn.
    private async Task<MaintenanceTickReport> RunScheduledRetentionIfDueAsync(
        IdleBranchRetireReport idle, CancellationToken cancellationToken)
    {
        var policy = _options.Maintenance;
        if (!policy.ScheduledRetention)
            return new MaintenanceTickReport { IdleBranches = idle, Retention = "disabled" };

        var now = _timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (!_lastScheduledRetentionDayLoaded)
        {
            _lastScheduledRetentionDay = LastScheduledRetentionDay(today);
            _lastScheduledRetentionDayLoaded = true;
        }
        if (now.UtcDateTime.Hour < policy.RetentionHourUtc || _lastScheduledRetentionDay == today)
            return new MaintenanceTickReport { IdleBranches = idle, Retention = "not_due" };

        // Issue #333: a busy writer no longer skips the tick. The pass takes priority turns on the gate, runs its batches
        // in held bursts and spends its budget only while holding; the tick's shared deadline bounds the whole wait, so a
        // pass that never got a turn ends with no batches and stays due (the day stays open below).
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        RetentionReport report;
        // A chain day other than today or the still-open previous day is stale (a chain only continues while the last
        // scheduled day is before today), so it is replaced.
        lock (_sweepDayGate)
        {
            if (_scheduledChainDay is not { } chain || (chain != today && chain != today.AddDays(-1)))
                _scheduledChainDay = today;
        }
        var passCompleted = false;
        try
        {
            using (var step = BeginStep(cancellationToken))
            {
                ScheduledRetentionFault?.Invoke();
                report = await RunRetentionAsync(
                    execute: true, RetentionScheduleCaller, cancellationToken,
                    TimeSpan.FromSeconds(Math.Clamp(policy.RetentionPassSeconds, 1, MaintenancePolicy.MaxRetentionPassSeconds)),
                    ScheduledRetentionAuditWait, maintenancePriority: true, passBound: step.Token).ConfigureAwait(false);
            }
            passCompleted = true;
        }
        finally
        {
            // A pass that throws or is cancelled ends the chain.
            if (!passCompleted) lock (_sweepDayGate) _scheduledChainDay = null;
        }
        var durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (report.Batches == 0 && report.MoreRemaining)
        {
            if (report.StopReason == StarvedStopReason)
                DeferStep("scheduled_retention", "the writer was never free");
            else
                Log($"maintenance: scheduled retention ran no batch (stop reason {report.StopReason ?? "unfinished"}); it stays due.");
        }
        var continues = ScheduledPassContinues(report);
        _lastScheduledRetentionDay = continues ? _lastScheduledRetentionDay : today;
        // The chain ended: the next scheduled day's first pass is the only thing that resets the sweep cycles.
        if (!continues) lock (_sweepDayGate) _scheduledChainDay = null;
        _metrics.RecordScheduledRetention(report, now.ToUnixTimeMilliseconds(), durationMs);
        Log(ScheduledRetentionLogLine(report, durationMs));
        return new MaintenanceTickReport
        {
            IdleBranches = idle, Retention = "ran", RetentionReport = report, RetentionDurationMs = durationMs,
            ContinueSoon = continues && MadeStopReasonProgress(report)
        };
    }

    internal static string ScheduledRetentionLogLine(RetentionReport report, double durationMs) =>
        $"maintenance: scheduled retention deleted {report.SnapshotsDeleted} snapshot(s), " +
        $"{report.FileVersionsDeleted} file version(s), {report.SourceTextsDeleted ?? 0} source text(s) in {durationMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} ms" +
        (report.MoreRemaining ? $"; more remaining ({report.StopReason ?? "unfinished"})" : "") +
        $"; skipped={report.SnapshotsSkipped}" +
        (report.LeftWork is { } left ? $"; left: {left}" : "") + "." +
        (report.Timings is { } t ? $" Steps: {t.ToLogText()}." : "");

    // A scheduled pass that ran out of budget with work left stays due: the next tick continues it (issue #317) instead
    // of leaving the rest for tomorrow. A pass that left work but neither deleted anything nor stopped for a budget or a
    // busy writer (a stuck catalog) is done for the day, so it is not re-planned every tick. Any zero-progress stop that
    // does continue (issue #321, #329) does so at most MaxScheduledNoProgressContinues times in a row.
    internal const int MaxScheduledNoProgressContinues = 3;
    private int _scheduledNoProgressContinues;

    // Whether the scheduled day stays open after this pass. Bounded: a pass that only ever runs out of budget must not
    // re-plan every tick for the rest of the day.
    /// <summary>Test seam: invoked at the start of a scheduled retention pass (may throw).</summary>
    internal Action? ScheduledRetentionFault { get; set; }

    internal bool ScheduledPassContinues(RetentionReport report)
    {
        var continues = ScheduledRetentionContinues(report);
        // A pass that never got the writer did nothing, so it says nothing about whether the catalog can move: it neither
        // counts toward the cap nor resets it. Only the UTC day bounds it (the day rolls over and the pass is due again).
        if (continues && report.StopReason == StarvedStopReason)
            return true;
        if (continues && !MadeStopReasonProgress(report))
        {
            if (++_scheduledNoProgressContinues > MaxScheduledNoProgressContinues)
                continues = false;
        }
        else
            _scheduledNoProgressContinues = 0;
        if (!continues) _scheduledNoProgressContinues = 0;
        return continues;
    }

    // Real movement on the BACKLOG the pass started with (issue #336): planned snapshots, generations, API history or chunk
    // rows deleted, a blob sweep's cursor advancing within its one cycle of the day (files, source texts), or pages
    // vacuumed when vacuum is what is left. File versions and source texts freed by ongoing indexing are deliberately not
    // counted by themselves: a trickle of them never ends. The cursor is what shows blob work moved, and each sweep runs
    // at most one cycle per scheduled day (BeginScheduledSweepDay), so the trickle is bounded too.
    internal static bool MadeStopReasonProgress(RetentionReport report) =>
        (report.SnapshotsDeleted + report.SnapshotProjectVersionsDeleted + report.ApiSnapshotsDeleted +
            report.Deleted.Count + report.ProjectChunkRowsDeleted) > 0 ||
        report.SourceTextSweepAdvanced || report.FileSweepAdvanced || (report.PagesVacuumed > 0 && report.VacuumLeft);

    // Stops that are about a budget or a busy writer, not a catalog that cannot move: they may continue without progress,
    // but only a bounded number of times in a row (ScheduledPassContinues).
    private static bool IsRetryableStop(string? reason) =>
        reason is "unit_budget" or "batch_budget" or "sweep_budget" or "api_deferred" or "sqlite_busy" or StarvedStopReason
            or RetentionService.KeysBudgetStopReason;

    // A pass that never got the writer (starved by productions until the tick deadline) did not run at all: the day stays
    // open without counting toward the no-progress cap (ScheduledPassContinues), bounded only by the UTC day (issue #333).
    internal static bool ScheduledRetentionContinues(RetentionReport report) =>
        report.MoreRemaining && (MadeStopReasonProgress(report) || IsRetryableStop(report.StopReason) || report.Batches == 0);

    /// <summary>
    /// Whether the maintenance loop should tick again after the short continuation interval instead of waiting for the next
    /// interval tick: a scheduled pass ended with work left AND made progress (issue #333).
    /// </summary>
    internal static bool ShouldTickSoon(MaintenanceTickReport report) => report.ContinueSoon;

    // Whether a production is running or the gate is taken: only the job-memory reclaim (issue #325) still asks.
    private bool IndexingHoldsWriter()
    {
        lock (_inFlightLock)
        {
            if (_inFlight.Count > 0)
                return true;
        }
        return _writeGate.CurrentCount == 0;
    }

    // The UTC day of today's scheduled retention when a pass that asked for no continuation already ended today (a
    // restart after it ran), from the audit log's pass rows attributed to retention_schedule; else null. A pass that
    // asked to continue, or per-batch rows with no pass row, leave the day open.
    private DateOnly? LastScheduledRetentionDay(DateOnly today)
    {
        var dayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
        var dayEnd = dayStart + (long)TimeSpan.FromDays(1).TotalMilliseconds;
        using var reader = OpenReadConnection();
        return new AuditLogStore(reader).HasActionByActor(AuditAction.Retention, RetentionScheduleCaller.Actor!, dayStart, dayEnd, RetentionPassDoneDetail)
            ? today
            : null;
    }

    private static void Log(string message) => Console.Error.WriteLine(message);

    // Issue #320: a job's heap is unreachable once the worker returns, but the runtime keeps the freed regions
    // committed until a later GC decommits them, which an idle service never runs. Return them after the job, but only
    // when it is worth the ~2 s all-threads pause. It runs AFTER the write gate was released, on a fresh pool work item
    // (so the job's frames have yielded and its locals are unreachable), and never holds the gate or fails anything.
    private int _reclaiming;
    private Task _pendingReclaim = Task.CompletedTask;
    private long _reclaimGeneration;

    internal WriteGate WriteGateForTesting => _writeGate;

    internal Lock InFlightLockForTesting => _inFlightLock;

    private enum JobReclaim { Ran, Busy, Skipped, Residual }

    // Issue #325: 1 while the last production's memory was returned (or was never worth returning); 0 from the moment
    // a production reaches its worker until a post-job reclaim ran or found the working set below the threshold. A
    // production that starts while it is 0 inherits the previous job's memory and reclaims it before its own work.
    // _productionGeneration counts the productions that took the flag: a reclaim captures it when it starts and may mark
    // memory clean only if no production started meanwhile (it would otherwise mark a running job's memory clean).
    private readonly object _memoryDebtLock = new();
    private long _productionGeneration;
    private int _jobMemoryClean = 1;

    private bool TakeCarriedJobMemory()
    {
        lock (_memoryDebtLock)
        {
            _productionGeneration++;
            var carried = _jobMemoryClean == 0;
            _jobMemoryClean = 0;
            return carried;
        }
    }

    private long ProductionGeneration()
    {
        lock (_memoryDebtLock)
            return _productionGeneration;
    }

    private void MarkJobMemoryClean(long generation)
    {
        lock (_memoryDebtLock)
        {
            if (_productionGeneration == generation)
                _jobMemoryClean = 1;
        }
    }

    private void RecordReclaimSkipped(long jobId, string reason, string detail, string phase)
    {
        _metrics.RecordMemoryReclaimSkipped(reason, phase);
        try
        {
            MemoryReclaimLog($"Job {jobId}: {PhaseLabel(phase)}memory reclaim skipped ({reason}): {detail}.");
        }
        catch
        {
            // Logging is best effort.
        }
    }

    private static string PhaseLabel(string phase) => phase == MemoryReclaimPhases.PreJob ? "start-of-job " : "";

    // The start-of-job reclaim: runs on the production's own thread, under the write gate, so no other production is
    // in flight (never two at once is still enforced through _reclaiming). Never throws. The writer lease keeps
    // renewing meanwhile: WriterLease renews from its own System.Threading.Timer on its own connection.
    private void ReclaimBeforeJob(long jobId)
    {
        const string phase = MemoryReclaimPhases.PreJob;
        try
        {
            if (_lifetime.IsCancellationRequested)
            {
                RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.ShuttingDown, "the service is stopping", phase);
                return;
            }
            if (WorkingSetProbe() < MemoryReclaimMinWorkingSetBytes)
            {
                RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.BelowThreshold, "working set below the threshold at job start", phase);
                return;
            }
            if (Interlocked.CompareExchange(ref _reclaiming, 1, 0) != 0)
            {
                RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.Busy, "another reclaim is running", phase);
                return;
            }
            try
            {
                // Shutdown may have begun since the first look (taking the flag/probing can take a moment).
                if (_lifetime.IsCancellationRequested)
                {
                    RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.ShuttingDown, "the service is stopping", phase);
                    return;
                }
                var r = MemoryReclaimAction();
                _metrics.RecordMemoryReclaimRun(phase);
                MemoryReclaimLog($"Job {jobId}: start-of-job memory reclaimed in {r.Elapsed.TotalMilliseconds:F0} ms " +
                    $"(working set {r.WorkingSetBefore / (1024 * 1024)} -> {r.WorkingSetAfter / (1024 * 1024)} MiB, " +
                    $"managed heap {r.HeapAfter / (1024 * 1024)} MiB; {r.PhaseSummary}).");
            }
            finally
            {
                Volatile.Write(ref _reclaiming, 0);
            }
        }
        catch
        {
            // Best effort: never fails the job.
        }
    }

    internal TimeSpan MemoryReclaimBusyRetryDelay { get; set; } = TimeSpan.FromSeconds(15);
    internal int MemoryReclaimBusyRetries { get; set; } = 8;

    // Issue #330: a reclaim whose collections still leave this much live managed heap ABOVE the baseline (the smallest
    // heap a reclaim has left so far: the service's own steady state, which may legitimately exceed any fixed size) ran
    // while something still held the job's state (a transient root); the freed regions would stay committed, so it
    // runs again later, a bounded number of times. The first reclaim has no baseline yet and uses the margin alone;
    // the baseline is the minimum (never an average) so a job-sized excess never raises it, and it is rebased to
    // the observed heap when the retries are exhausted, so a legitimately grown heap stops triggering retries.
    internal long MemoryReclaimResidualHeapBytes { get; set; } = 256L * 1024 * 1024;
    private long _reclaimHeapBaseline = -1;
    private long _lastReclaimHeapAfter;

    private bool IsResidualHeap(long heapAfter)
    {
        lock (_memoryDebtLock)
        {
            var baseline = Math.Max(_reclaimHeapBaseline, 0);
            var residual = heapAfter > baseline + MemoryReclaimResidualHeapBytes;
            if (heapAfter > 0 && (_reclaimHeapBaseline < 0 || heapAfter < _reclaimHeapBaseline))
                _reclaimHeapBaseline = heapAfter;
            return residual;
        }
    }

    private void RebaseReclaimHeap(long heapAfter)
    {
        lock (_memoryDebtLock)
            _reclaimHeapBaseline = heapAfter;
    }

    internal long ReclaimHeapBaselineForTesting
    {
        get { lock (_memoryDebtLock) return _reclaimHeapBaseline; }
    }
    internal TimeSpan MemoryReclaimResidualRetryDelay { get; set; } = TimeSpan.FromSeconds(15);
    internal int MemoryReclaimResidualRetries { get; set; } = 4;

    internal long MemoryReclaimMinWorkingSetBytes { get; set; } = JobMemoryReclaimer.MinWorkingSetBytes;
    internal Func<long> WorkingSetProbe { get; set; } = static () => Environment.WorkingSet;
    internal Func<JobMemoryReclaimer.Result> MemoryReclaimAction { get; set; } = JobMemoryReclaimer.Reclaim;
    internal Action<string> MemoryReclaimLog { get; set; } = Log;

    /// <summary>The latest scheduled end-of-job reclaim (completed when none is pending); never faults.</summary>
    internal Task PendingMemoryReclaim => Volatile.Read(ref _pendingReclaim);

    internal void ScheduleJobMemoryReclaim(Task production, long jobId)
    {
        try
        {
            var generation = Interlocked.Increment(ref _reclaimGeneration);
            // Waits for the production Task to complete (its frames have unwound by the time the yield resumes),
            // on a pool thread, so nothing of the job is on a stack when the collection runs.
            Volatile.Write(ref _pendingReclaim, Task.Run(async () =>
            {
                try
                {
                    await production.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await Task.Yield();
                    var residualRetries = 0;
                    for (var attempt = 0; ; attempt++)
                    {
                        // A newer production's reclaim supersedes this one, so a burst never queues two.
                        if (Interlocked.Read(ref _reclaimGeneration) != generation)
                        {
                            RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.Superseded, "a newer job's reclaim replaces it", MemoryReclaimPhases.PostJob);
                            return;
                        }
                        var outcome = TryReclaimOnce(jobId);
                        if (outcome == JobReclaim.Residual)
                        {
                            if (residualRetries++ >= MemoryReclaimResidualRetries)
                            {
                                RebaseReclaimHeap(Interlocked.Read(ref _lastReclaimHeapAfter));
                                RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.Deferred,
                                    "the managed heap is still large after the reclaim; the next job reclaims before it starts", MemoryReclaimPhases.PostJob);
                                return;
                            }
                            attempt = -1;
                            await Task.Delay(MemoryReclaimResidualRetryDelay, _timeProvider, _lifetime.Token).ConfigureAwait(false);
                            continue;
                        }
                        if (outcome != JobReclaim.Busy)
                            return;
                        if (attempt >= MemoryReclaimBusyRetries)
                        {
                            RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.Deferred,
                                "still busy (another job queued or running); the next job reclaims before it starts", MemoryReclaimPhases.PostJob);
                            return;
                        }
                        // Busy only because the gate is held or a production is running (a queued retire, an attached
                        // ensure, ...): nothing will schedule another reclaim, so look again shortly, a bounded number of times.
                        await Task.Delay(MemoryReclaimBusyRetryDelay, _timeProvider, _lifetime.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The service is stopping: a quiet exit.
                }
                catch
                {
                    // Best effort: never faults.
                }
            }, _lifetime.Token).ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default));
        }
        catch
        {
            // Best effort: scheduling a reclaim must never fail the job or leave the gate held.
        }
    }

    // True when the reclaim ran. Never throws.
    internal bool TryReclaimJobMemory(long jobId) => TryReclaimOnce(jobId) is JobReclaim.Ran or JobReclaim.Residual;

    private JobReclaim TryReclaimOnce(long jobId)
    {
        try
        {
            // A production that starts while this runs (the gate is not held) re-arms the debt; clean is only
            // recorded if none did.
            var generation = ProductionGeneration();
            var state = ReclaimState(jobId, generation);
            if (state != JobReclaim.Ran)
                return state;
            if (Interlocked.CompareExchange(ref _reclaiming, 1, 0) != 0)
            {
                RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.Busy, "another reclaim is running", MemoryReclaimPhases.PostJob);
                return JobReclaim.Skipped;
            }
            try
            {
                // Re-checked right before collecting: another job may have been admitted since the first look.
                state = ReclaimState(jobId, generation);
                if (state != JobReclaim.Ran)
                    return state;
                var r = MemoryReclaimAction();
                var residual = IsResidualHeap(r.HeapAfter);
                Interlocked.Exchange(ref _lastReclaimHeapAfter, r.HeapAfter);
                // A residual heap means the memory was not returned: the debt stays, so the next job reclaims first.
                if (!residual)
                    MarkJobMemoryClean(generation);
                _metrics.RecordMemoryReclaimRun(MemoryReclaimPhases.PostJob);
                MemoryReclaimLog($"Job {jobId}: memory reclaimed in {r.Elapsed.TotalMilliseconds:F0} ms " +
                    $"(working set {r.WorkingSetBefore / (1024 * 1024)} -> {r.WorkingSetAfter / (1024 * 1024)} MiB, " +
                    $"managed heap {r.HeapAfter / (1024 * 1024)} MiB; {r.PhaseSummary})" +
                    (residual ? "; the heap is still large, retrying later." : "."));
                return residual ? JobReclaim.Residual : JobReclaim.Ran;
            }
            finally
            {
                Volatile.Write(ref _reclaiming, 0);
            }
        }
        catch
        {
            return JobReclaim.Skipped;
        }
    }

    // Only the last job of a burst reclaims: not while shutting down, not while another production is running or
    // queued for the gate (a released gate with a queued waiter is handed over, so CurrentCount stays 0), and not
    // when the working set is too small for the pause to buy anything. Busy (gate/production) is the only skip a
    // later look can change; shutdown and the threshold are final. Ran means "worthwhile now".
    // A busy look is not recorded here: the caller retries and records the final outcome. A final skip is recorded.
    private JobReclaim ReclaimState(long jobId, long generation)
    {
        if (_lifetime.IsCancellationRequested)
        {
            RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.ShuttingDown, "the service is stopping", MemoryReclaimPhases.PostJob);
            return JobReclaim.Skipped;
        }
        lock (_inFlightLock)
        {
            if (_inFlight.Count > 0)
                return JobReclaim.Busy;
        }
        if (_writeGate.CurrentCount == 0)
            return JobReclaim.Busy;
        if (WorkingSetProbe() >= MemoryReclaimMinWorkingSetBytes)
            return JobReclaim.Ran;
        // Nothing worth returning: the next job inherits nothing (unless a production started since the generation was read).
        MarkJobMemoryClean(generation);
        RecordReclaimSkipped(jobId, MemoryReclaimSkipReasons.BelowThreshold, "working set below the threshold", MemoryReclaimPhases.PostJob);
        return JobReclaim.Skipped;
    }
}
