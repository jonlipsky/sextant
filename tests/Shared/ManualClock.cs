namespace Sextant.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> whose time moves only when a test advances it, so a time-budget test can make
/// "time pass" exactly where it wants (for example, on a progress line) with no real waiting and no flakiness.
/// Its timers run on the same clock: a timer (and so a <see cref="CancellationTokenSource"/> or a
/// <c>Task.Delay</c> created on this provider) fires inside the <see cref="Advance"/> that reaches its due time,
/// never on its own. Thread-safe: indexing reports progress from worker threads.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualClock(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt is { } at && at <= _now).ToList();
            foreach (var timer in due)
                timer.Reschedule(_now);
        }
        // Fire outside the lock: a callback (a cancellation, a continuation) may read the clock or create timers.
        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        // Guarded by the clock's gate.
        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                if (!clock._timers.Contains(this))
                    clock._timers.Add(this);
            }
            return true;
        }

        // Called under the clock's gate once the timer is due: re-arm a periodic timer, retire a one-shot one.
        public void Reschedule(DateTimeOffset now)
        {
            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
                DueAt = now + _period;
            else
            {
                DueAt = null;
                clock._timers.Remove(this);
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                DueAt = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
