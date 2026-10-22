namespace Sextant.Store;

/// <summary>What a <see cref="WriteLockTracker"/> found worth reporting.</summary>
public enum WriteLockEventKind
{
    /// <summary>A write transaction held the lock longer than the threshold.</summary>
    LongHold,

    /// <summary>Acquiring the lock (the <c>BEGIN IMMEDIATE</c>) waited longer than the threshold.</summary>
    LongWait
}

/// <summary>A hold or wait over the threshold, handed to the tracker's sink (issue #342).</summary>
public sealed record WriteLockEvent(
    WriteLockEventKind Kind, string Owner, string? Detail, TimeSpan Duration, TimeSpan Threshold, DateTimeOffset At)
{
    /// <summary>The warning line the service logs for this event.</summary>
    public string Describe() =>
        Kind == WriteLockEventKind.LongHold
            ? $"Write lock held for {Duration.TotalSeconds:0.0} s by '{Owner}'{Suffix()} (threshold {Threshold.TotalSeconds:0.#} s)."
            : $"'{Owner}'{Suffix()} waited {Duration.TotalSeconds:0.0} s to acquire the write lock (threshold {Threshold.TotalSeconds:0.#} s).";

    private string Suffix() => string.IsNullOrEmpty(Detail) ? "" : $" ({Detail})";
}

/// <summary>An open hold: returned by <see cref="WriteLockTracker.Acquired"/>, closed by <see cref="WriteLockTracker.Released"/>.</summary>
public readonly record struct WriteLockHold(string Owner, string? Detail, long StartedAt);

/// <summary>Point-in-time counters of a <see cref="WriteLockTracker"/>.</summary>
public sealed record WriteLockMetrics
{
    /// <summary>Write transactions that ended (committed or rolled back) since start.</summary>
    public long HoldsTotal { get; init; }

    /// <summary>Holds longer than the threshold.</summary>
    public long HoldsOverThreshold { get; init; }

    public double LongestHoldMs { get; init; }
    public string? LongestHoldOwner { get; init; }

    /// <summary>When the longest hold ended, unix milliseconds (null until one ended).</summary>
    public long? LongestHoldAtUnixMs { get; init; }

    /// <summary>Lock acquisitions that waited longer than the threshold.</summary>
    public long WaitsOverThreshold { get; init; }

    public double LongestWaitMs { get; init; }
    public string? LongestWaitOwner { get; init; }

    /// <summary>The threshold the over-threshold counters use, in milliseconds.</summary>
    public double ThresholdMs { get; init; }

    /// <summary>In-process writers waiting to acquire the lock right now.</summary>
    public int WaitingWriters { get; init; }

    /// <summary>Times an index write session held off its next <c>BEGIN IMMEDIATE</c> so a waiting writer could take the lock (issue #344).</summary>
    public long YieldsToWaiters { get; init; }

    /// <summary>Yields that gave up at the cap with a writer still waiting.</summary>
    public long YieldTimeouts { get; init; }
}

/// <summary>
/// Measures, per named owner, how long a write transaction holds SQLite's write lock (from a successful
/// <c>BEGIN IMMEDIATE</c> to the matching COMMIT/ROLLBACK) and how long acquiring it waited (issue #342). A hold or
/// wait over the threshold is handed to <c>sink</c> (the service logs a warning); the longest of each is kept for
/// <c>/control/metrics</c>. Store takes no logging dependency, so reporting is a callback; the default is none.
/// Cheap: one lock and a few fields per transaction, no allocation beyond the returned struct.
/// </summary>
public sealed class WriteLockTracker
{
    /// <summary>The default threshold for a long hold or wait.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly Action<WriteLockEvent>? _sink;
    private readonly object _gate = new();
    private long _holds;
    private long _holdsOver;
    private double _longestHoldMs;
    private string? _longestHoldOwner;
    private long? _longestHoldAt;
    private long _waitsOver;
    private double _longestWaitMs;
    private string? _longestWaitOwner;
    private int _waiting;
    private long _acquisitions;
    private long _yields;
    private long _yieldTimeouts;

    public WriteLockTracker(TimeSpan? threshold = null, Action<WriteLockEvent>? sink = null, TimeProvider? timeProvider = null)
    {
        Threshold = threshold is { } t && t > TimeSpan.Zero ? t : DefaultThreshold;
        _sink = sink;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A tracker that reports nothing; the default of every site that was given none.</summary>
    public static WriteLockTracker Untracked { get; } = new(waiters: false);

    // The shared untracked instance serves unrelated databases, so it must not turn one's wait into another's yield.
    private readonly bool _countWaiters = true;

    private WriteLockTracker(bool waiters) : this() => _countWaiters = waiters;

    public TimeSpan Threshold { get; }

    /// <summary>The clock this tracker measures with.</summary>
    public TimeProvider TimeProvider => _time;

    /// <summary>
    /// Call right before <c>BEGIN IMMEDIATE</c>; pass the result to <see cref="Acquired"/> or <see cref="AcquireFailed"/>
    /// (exactly one of them, always). The caller counts as a waiting writer until then.
    /// </summary>
    public long BeginAcquire()
    {
        if (_countWaiters)
            lock (_gate)
                _waiting++;
        return _time.GetTimestamp();
    }

    /// <summary>
    /// A bare timestamp for a hold that is not a lock wait to other writers (a PRAGMA that waits and holds in one
    /// call): pass it to <see cref="Released"/> via a <see cref="WriteLockHold"/>. Counts no waiting writer.
    /// </summary>
    public long Timestamp() => _time.GetTimestamp();

    /// <summary>Time elapsed since a <see cref="Timestamp"/>/<see cref="BeginAcquire"/>/hold start.</summary>
    public TimeSpan ElapsedSince(long timestamp) => _time.GetElapsedTime(timestamp);

    /// <summary>Writers (other than the caller, who is not waiting) currently between <see cref="BeginAcquire"/> and its outcome.</summary>
    public int WaitingWriters
    {
        get
        {
            lock (_gate)
                return _waiting;
        }
    }

    /// <summary>
    /// Lets a writer that is about to start its next transaction give way: while writers are waiting, blocks until none
    /// is left (each acquires the lock or gives up) or <paramref name="cap"/> passes, whichever is first, so a second
    /// waiter is not starved by the first. Returns false when nobody was waiting (nothing was done); true when it
    /// yielded. The cap runs on this tracker's <see cref="TimeProvider"/> (a fake clock drives it); wakeups are a
    /// monitor signal, not a spin.
    /// </summary>
    public bool YieldToWaiters(TimeSpan cap)
    {
        if (cap <= TimeSpan.Zero) return false;
        lock (_gate)
            if (_waiting == 0) return false;

        var expired = false;
        using var timer = _time.CreateTimer(_ =>
        {
            lock (_gate)
            {
                expired = true;
                Monitor.PulseAll(_gate);
            }
        }, null, cap, Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            if (_waiting == 0) return false;
            _yields++;
            while (_waiting > 0 && !expired)
                Monitor.Wait(_gate);
            if (_waiting > 0) _yieldTimeouts++;
            return true;
        }
    }

    /// <summary>The <c>BEGIN IMMEDIATE</c> succeeded: records the wait and starts the hold.</summary>
    public WriteLockHold Acquired(string owner, string? detail, long waitStartedAt)
    {
        var now = _time.GetTimestamp();
        EndWait(acquired: true);
        RecordWait(owner, detail, _time.GetElapsedTime(waitStartedAt, now));
        return new WriteLockHold(owner, detail, now);
    }

    /// <summary>The <c>BEGIN IMMEDIATE</c> failed (busy/locked): the wait still counts, there is no hold.</summary>
    public void AcquireFailed(string owner, string? detail, long waitStartedAt)
    {
        EndWait(acquired: false);
        RecordWait(owner, detail, _time.GetElapsedTime(waitStartedAt));
    }

    /// <summary>A lock failure of a hold started with <see cref="Timestamp"/> (no waiter was counted): records the wait only.</summary>
    public void AcquireFailedAt(string owner, string? detail, long timestamp) =>
        RecordWait(owner, detail, _time.GetElapsedTime(timestamp));

    private void EndWait(bool acquired)
    {
        if (!_countWaiters) return;
        lock (_gate)
        {
            if (_waiting > 0) _waiting--;
            if (acquired) _acquisitions++;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>The transaction ended (COMMIT or ROLLBACK): records the hold.</summary>
    public void Released(WriteLockHold hold)
    {
        var held = _time.GetElapsedTime(hold.StartedAt);
        var at = _time.GetUtcNow();
        var over = held > Threshold;
        lock (_gate)
        {
            _holds++;
            if (over) _holdsOver++;
            if (held.TotalMilliseconds > _longestHoldMs || _longestHoldOwner is null)
            {
                _longestHoldMs = held.TotalMilliseconds;
                _longestHoldOwner = hold.Owner;
                _longestHoldAt = at.ToUnixTimeMilliseconds();
            }
        }
        if (over)
            Report(new WriteLockEvent(WriteLockEventKind.LongHold, hold.Owner, hold.Detail, held, Threshold, at));
    }

    public WriteLockMetrics Snapshot()
    {
        lock (_gate)
            return new WriteLockMetrics
            {
                HoldsTotal = _holds,
                HoldsOverThreshold = _holdsOver,
                LongestHoldMs = _longestHoldMs,
                LongestHoldOwner = _longestHoldOwner,
                LongestHoldAtUnixMs = _longestHoldAt,
                WaitsOverThreshold = _waitsOver,
                LongestWaitMs = _longestWaitMs,
                LongestWaitOwner = _longestWaitOwner,
                ThresholdMs = Threshold.TotalMilliseconds,
                WaitingWriters = _waiting,
                YieldsToWaiters = _yields,
                YieldTimeouts = _yieldTimeouts
            };
    }

    private void RecordWait(string owner, string? detail, TimeSpan waited)
    {
        var over = waited > Threshold;
        lock (_gate)
        {
            if (over) _waitsOver++;
            if (waited.TotalMilliseconds > _longestWaitMs)
            {
                _longestWaitMs = waited.TotalMilliseconds;
                _longestWaitOwner = owner;
            }
        }
        if (over)
            Report(new WriteLockEvent(WriteLockEventKind.LongWait, owner, detail, waited, Threshold, _time.GetUtcNow()));
    }

    // A failing sink must never break the transaction it observes.
    private void Report(WriteLockEvent e)
    {
        try
        {
            _sink?.Invoke(e);
        }
        catch (Exception)
        {
        }
    }
}
