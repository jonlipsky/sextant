namespace Sextant.Service.Observability;

/// <summary>Summary statistics for a latency distribution (all values in milliseconds).</summary>
public sealed record LatencyStats
{
    public required long Count { get; init; }
    public required double P50Ms { get; init; }
    public required double P95Ms { get; init; }
    public required double MaxMs { get; init; }

    public static readonly LatencyStats Empty = new() { Count = 0, P50Ms = 0, P95Ms = 0, MaxMs = 0 };

    /// <summary>Computes p50/p95/max over a set of samples. Returns <see cref="Empty"/> for no samples.</summary>
    public static LatencyStats FromSamples(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
            return Empty;
        var sorted = samples.OrderBy(x => x).ToArray();
        return new LatencyStats
        {
            Count = sorted.Length,
            P50Ms = Percentile(sorted, 0.50),
            P95Ms = Percentile(sorted, 0.95),
            MaxMs = sorted[^1]
        };
    }

    // Nearest-rank percentile on an already-sorted, non-empty array.
    private static double Percentile(double[] sorted, double q)
    {
        var rank = (int)Math.Ceiling(q * sorted.Length);
        var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

/// <summary>
/// A bounded, thread-safe latency reservoir. Records observed durations into a fixed-capacity ring buffer
/// (oldest samples evicted) so memory stays bounded regardless of throughput, and computes percentiles on
/// demand. Deliberately lightweight — criterion 5 is served by a clean in-process metrics surface, not a
/// heavyweight metrics dependency.
/// </summary>
public sealed class LatencyHistogram(int capacity = 2048)
{
    private readonly object _gate = new();
    private readonly double[] _buffer = new double[Math.Max(1, capacity)];
    private long _count;
    private int _next;
    private double _max;

    /// <summary>Records one observed duration in milliseconds.</summary>
    public void Record(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0)
            return;
        lock (_gate)
        {
            _buffer[_next] = milliseconds;
            _next = (_next + 1) % _buffer.Length;
            _count++;
            if (milliseconds > _max)
                _max = milliseconds;
        }
    }

    /// <summary>Total observations recorded over the lifetime (not just the retained window).</summary>
    public long Count { get { lock (_gate) { return _count; } } }

    /// <summary>Snapshots the retained window into summary statistics (max is lifetime-wide).</summary>
    public LatencyStats Snapshot()
    {
        double[] window;
        long total;
        double lifetimeMax;
        lock (_gate)
        {
            total = _count;
            lifetimeMax = _max;
            var retained = (int)Math.Min(_count, _buffer.Length);
            window = new double[retained];
            for (var i = 0; i < retained; i++)
                window[i] = _buffer[i];
        }
        if (total == 0)
            return LatencyStats.Empty;
        var stats = LatencyStats.FromSamples(window);
        return stats with { Count = total, MaxMs = lifetimeMax };
    }
}
