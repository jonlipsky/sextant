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

    public double DurationMs { get; init; }
}

/// <summary>What one maintenance tick did (issue #299).</summary>
public sealed record MaintenanceTickReport
{
    public required IdleBranchRetireReport IdleBranches { get; init; }

    /// <summary>
    /// What happened to scheduled retention this tick: <c>disabled</c>, <c>not_due</c> (before the hour, or already run
    /// today), <c>busy</c> (due, but indexing holds the writer: retried next tick), or <c>ran</c>.
    /// </summary>
    public required string Retention { get; init; }

    /// <summary>The scheduled retention pass's report when <see cref="Retention"/> is <c>ran</c>.</summary>
    public RetentionReport? RetentionReport { get; init; }

    public double RetentionDurationMs { get; init; }
}

public sealed partial class SnapshotService
{
    /// <summary>The audit caller of the idle-branch rule's retirements (issue #299).</summary>
    public static readonly AuditCaller IdlePolicyCaller = new("idle_policy", ";source=idle_policy");

    /// <summary>The audit caller of the scheduled retention pass (issue #299).</summary>
    public static readonly AuditCaller RetentionScheduleCaller = new("retention_schedule", ";source=retention_schedule");

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
                try
                {
                    LastMaintenanceTick = await RunMaintenanceTickAsync(lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A failed tick never ends the loop: the next tick retries.
                    _metrics.RecordMaintenanceFailure();
                    Log($"maintenance: tick failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _maintenanceTicked.Release();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// Waits until the maintenance loop has finished another tick (successful or failed). Test support: the loop ticks on
    /// the service's <see cref="TimeProvider"/>, so a test advances its clock and then waits here.
    /// </summary>
    internal async Task<bool> WaitForMaintenanceTickAsync(TimeSpan timeout) =>
        await _maintenanceTicked.WaitAsync(timeout).ConfigureAwait(false);

    /// <summary>
    /// Runs one maintenance tick (issue #299): the idle-branch retire pass, then the scheduled retention pass when it is
    /// due and the writer is free. The loop calls it every <see cref="MaintenancePolicy.Interval"/>; ticks never overlap
    /// (a call made while one runs waits for it).
    /// </summary>
    public async Task<MaintenanceTickReport> RunMaintenanceTickAsync(CancellationToken cancellationToken = default)
    {
        await _maintenanceTick.WaitAsync(cancellationToken).ConfigureAwait(false);
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

            return await RunScheduledRetentionIfDueAsync(idle, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
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
                Enabled = false, RetiredHeadsPruned = await PruneRetiredHeadsAsync(cancellationToken).ConfigureAwait(false)
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
        // An admitted branch-advance ensure that has not finished is pending work too (issue #273's registry).
        HashSet<string> advancing;
        lock (_inFlightLock)
            advancing = [.. _latestAdvance.Keys];

        var eligible = new List<IdleBranchCandidate>();
        var busy = 0;
        foreach (var candidate in idle)
        {
            var repositoryKey = RemoteUrlIdentity.Normalize(candidate.RepositoryRemoteUrl);
            if (pending.Contains((repositoryKey, candidate.Name)) || advancing.Contains(repositoryKey + "\n" + candidate.Name))
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
        // Each retirement is service-owned: this only waits for them (cancellation ends the wait, not the retirements).
        var results = await Task.WhenAll(retirements).WaitAsync(cancellationToken).ConfigureAwait(false);
        var retired = results.Count(r => r.Retired);
        var pruned = await PruneRetiredHeadsAsync(cancellationToken).ConfigureAwait(false);
        return new IdleBranchRetireReport
        {
            Enabled = true,
            Idle = idle.Count,
            Busy = busy,
            Attempted = retirements.Count,
            Retired = retired,
            Skipped = retirements.Count - retired,
            RetiredHeadsPruned = pruned,
            DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
    }

    // Deletes the retired-head records older than MaintenancePolicy.RetiredHeadRetention: a branch idle-retired that long
    // ago and never pushed since is no longer re-created by a push expecting its old head (it is re-created like a new
    // branch, or by any unguarded ensure). Takes the writer only when there is something to prune.
    private async Task<int> PruneRetiredHeadsAsync(CancellationToken cancellationToken)
    {
        var cutoff = (_timeProvider.GetUtcNow() - MaintenancePolicy.RetiredHeadRetention).ToUnixTimeMilliseconds();
        using (var reader = OpenReadConnection())
        {
            using var probe = reader.CreateCommand();
            probe.CommandText = "SELECT EXISTS (SELECT 1 FROM retired_branch_heads WHERE retired_at < @cutoff);";
            probe.Parameters.AddWithValue("@cutoff", cutoff);
            if (probe.ExecuteScalar() is not long found || found == 0)
                return 0;
        }
        return await WithWriteAsync(
            () => Task.FromResult(new SnapshotStore(_conn).PruneRetiredHeads(cutoff)), cancellationToken).ConfigureAwait(false);
    }

    // The tick's scheduled retention (issue #299): once per UTC day, on the first tick at or after the configured hour,
    // the same bounded pass as POST /control/retention?execute=true, audited as retention_schedule. While a production
    // holds (or waits for) the writer the pass does not start and is retried next tick. Once started, a production
    // admitted meanwhile waits at most one bounded batch (RetentionService.BatchTimeLimit) for its turn.
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

        if (IndexingHoldsWriter())
        {
            _metrics.RecordScheduledRetentionSkippedBusy();
            Log("maintenance: scheduled retention is due but indexing holds the writer; retrying next tick.");
            return new MaintenanceTickReport { IdleBranches = idle, Retention = "busy" };
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var report = await RunRetentionAsync(execute: true, RetentionScheduleCaller, cancellationToken).ConfigureAwait(false);
        var durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _lastScheduledRetentionDay = today;
        _metrics.RecordScheduledRetention(report, now.ToUnixTimeMilliseconds(), durationMs);
        Log($"maintenance: scheduled retention deleted {report.SnapshotsDeleted} snapshot(s), " +
            $"{report.FileVersionsDeleted} file version(s), {report.SourceTextsDeleted ?? 0} source text(s) in {durationMs:F0} ms" +
            (report.MoreRemaining ? $"; more remaining ({report.StopReason ?? "unfinished"})." : "."));
        return new MaintenanceTickReport
        {
            IdleBranches = idle, Retention = "ran", RetentionReport = report, RetentionDurationMs = durationMs
        };
    }

    // A production holds the writer for its whole run, and a write waiting for its turn would queue behind it: either
    // way a retention pass now would only spend its budget waiting, or delay that write (issue #299).
    private bool IndexingHoldsWriter()
    {
        lock (_inFlightLock)
        {
            if (_inFlight.Count > 0)
                return true;
        }
        return _writeGate.CurrentCount == 0;
    }

    // The UTC day of today's scheduled retention when one already ran today (a restart after it ran), from the audit
    // log's retention rows attributed to retention_schedule; else null.
    private DateOnly? LastScheduledRetentionDay(DateOnly today)
    {
        var dayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
        var dayEnd = dayStart + (long)TimeSpan.FromDays(1).TotalMilliseconds;
        using var reader = OpenReadConnection();
        return new AuditLogStore(reader).HasActionByActor(AuditAction.Retention, RetentionScheduleCaller.Actor!, dayStart, dayEnd)
            ? today
            : null;
    }

    private static void Log(string message) => Console.Error.WriteLine(message);
}
