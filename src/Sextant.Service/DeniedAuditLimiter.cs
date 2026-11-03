namespace Sextant.Service;

/// <summary>
/// Bounds the durable <c>denied</c> audit rows a flood of refusals can write (issue #198, item 3). Refusals are
/// counted per (caller, action and refusal reason) in a fixed window: the first <see cref="MaxRowsPerWindow"/> in a window are recorded,
/// the rest are only counted, and the count is reported on the next row that key records (after the window ends), so
/// the audit trail still shows how many refusals were left out. The first refusal of a key is therefore recorded,
/// unless the key cap below forces it into the shared overflow entry. The number of tracked keys is capped (<see cref="MaxKeys"/>): expired entries are dropped first, and a
/// key that still does not fit shares one overflow entry, so memory and the audit table stay bounded whatever the
/// caller identities.
/// </summary>
internal sealed class DeniedAuditLimiter(TimeProvider time)
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    internal const int MaxRowsPerWindow = 10;
    internal const int MaxKeys = 4096;
    private const string OverflowKey = "\0overflow";

    private sealed class Entry
    {
        public long WindowStart;
        public int Recorded;
        public long Suppressed;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// True when the refusal should be recorded; <paramref name="suppressed"/> is then the number of refusals of the
    /// previous window that were left out (0 when none). False when the window's budget is spent (it was counted).
    /// </summary>
    public bool TryAdmit(string? actor, string route, out long suppressed)
    {
        var now = time.GetUtcNow().UtcTicks;
        var key = $"{route}\n{actor}";
        lock (_lock)
        {
            if (!_entries.ContainsKey(key) && _entries.Count >= MaxKeys)
            {
                foreach (var expired in _entries.Where(e => Expired(e.Value, now)).Select(e => e.Key).ToList())
                    _entries.Remove(expired);
                if (_entries.Count >= MaxKeys)
                    key = OverflowKey;
            }
            if (!_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = new Entry { WindowStart = now, Recorded = 1 };
                suppressed = 0;
                return true;
            }
            if (Expired(entry, now))
            {
                suppressed = entry.Suppressed;
                entry.WindowStart = now;
                entry.Recorded = 1;
                entry.Suppressed = 0;
                return true;
            }
            suppressed = 0;
            if (entry.Recorded < MaxRowsPerWindow)
            {
                entry.Recorded++;
                return true;
            }
            entry.Suppressed++;
            return false;
        }
    }

    /// <summary><paramref name="detail"/> with <c>;suppressed=N</c> appended when refusals were left out.</summary>
    internal static string WithSuppressed(string detail, long suppressed) =>
        suppressed > 0 ? $"{detail};suppressed={suppressed}" : detail;

    private static bool Expired(Entry entry, long now) => TimeSpan.FromTicks(now - entry.WindowStart) >= Window;
}
