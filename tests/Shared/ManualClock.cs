namespace Sextant.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> whose time moves only when a test advances it, so a time-budget test can make
/// "time pass" exactly where it wants (for example, on a progress line) with no real waiting and no flakiness.
/// Thread-safe: indexing reports progress from worker threads.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
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
        lock (_gate)
            _now += by;
    }
}
