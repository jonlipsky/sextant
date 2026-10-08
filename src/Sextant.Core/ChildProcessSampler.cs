using System.Globalization;

namespace Sextant.Core;

/// <summary>What <see cref="ChildProcessSampler"/> observed of this process's matching descendants (issue #267).</summary>
/// <param name="Launches">
/// Distinct processes seen. Approximate: a process that starts and exits between two samples is missed, and a child a
/// matching process forks carries its command line until it execs.
/// </param>
/// <param name="PeakConcurrent">Most matching processes alive at one sample.</param>
/// <param name="PeakResidentBytes">Largest combined resident memory of the matching processes at one sample.</param>
public sealed record ChildProcessUsage(int Launches, int PeakConcurrent, long PeakResidentBytes);

/// <summary>
/// Samples <c>/proc</c> on a timer for descendants of this process whose command line contains a marker, such as the
/// MSBuild BuildHost processes Roslyn's <c>MSBuildWorkspace</c> starts (issue #267). The service's memory watchdog only
/// sees its own working set; this makes the evaluation processes' count and memory visible. On a platform without
/// <c>/proc</c> it observes nothing and <see cref="Stop"/> returns null.
/// </summary>
public sealed class ChildProcessSampler : IDisposable
{
    /// <summary>The command-line marker of the MSBuild BuildHost processes Roslyn's <c>MSBuildWorkspace</c> starts.</summary>
    public const string BuildHostMarker = "BuildHost.dll";

    private readonly string _marker;
    private readonly TimeSpan _period;
    private readonly Timer? _timer;
    private readonly int _self = Environment.ProcessId;
    private readonly HashSet<int> _seen = [];
    private readonly object _gate = new();
    private int _peakConcurrent;
    private long _peakResident;
    private bool _stopped;

    /// <summary>Starts sampling every <paramref name="interval"/> (default 250 ms).</summary>
    public ChildProcessSampler(string commandLineMarker, TimeSpan? interval = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandLineMarker);
        _marker = commandLineMarker;
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
            return;
        _period = interval ?? TimeSpan.FromMilliseconds(250);
        // One-shot, re-armed after each sample, so a slow scan never overlaps the next one.
        _timer = new Timer(_ => SampleAndRearm(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>True when this platform can be sampled.</summary>
    public bool Supported => _timer != null;

    /// <summary>Takes a final sample, stops, and returns what was observed (null when unsupported).</summary>
    public ChildProcessUsage? Stop()
    {
        if (_timer == null)
            return null;
        Sample();
        lock (_gate)
        {
            _stopped = true;
            _timer.Dispose();
            return new ChildProcessUsage(_seen.Count, _peakConcurrent, _peakResident);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            _timer?.Dispose();
        }
    }

    private void SampleAndRearm()
    {
        Sample();
        lock (_gate)
        {
            if (_stopped)
                return;
            try { _timer!.Change(_period, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private void Sample()
    {
        try
        {
            // First the process tree (one small read per process), then command lines for this process's
            // descendants only.
            var parents = new Dictionary<int, int>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    && ReadParent(pid) is { } ppid)
                    parents[pid] = ppid;
            }

            var alive = 0;
            long resident = 0;
            var matched = new List<int>();
            foreach (var pid in parents.Keys)
            {
                if (pid == _self || !IsDescendant(pid, _self, parents) || !CommandLineContains(pid, _marker))
                    continue;
                matched.Add(pid);
                alive++;
                resident += ReadResidentBytes(pid);
            }

            lock (_gate)
            {
                if (_stopped)
                    return;
                foreach (var pid in matched)
                    _seen.Add(pid);
                _peakConcurrent = Math.Max(_peakConcurrent, alive);
                _peakResident = Math.Max(_peakResident, resident);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // /proc entries vanish mid-scan; the next sample sees a consistent view again.
        }
    }

    /// <summary>
    /// True when <paramref name="pid"/> descends from <paramref name="ancestor"/> in <paramref name="parents"/> (pid →
    /// parent pid), within eight generations (BuildHosts are children or grandchildren of the service).
    /// </summary>
    internal static bool IsDescendant(int pid, int ancestor, IReadOnlyDictionary<int, int> parents)
    {
        for (var depth = 0; depth < 8 && parents.TryGetValue(pid, out var parent); depth++)
        {
            if (parent == ancestor)
                return true;
            if (parent <= 1)
                return false;
            pid = parent;
        }
        return false;
    }

    private static int? ReadParent(int pid)
    {
        try
        {
            return ParseParent(File.ReadAllText($"/proc/{pid}/stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The parent pid (field 4) of a <c>/proc/&lt;pid&gt;/stat</c> line, counted after the command name.</summary>
    internal static int? ParseParent(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0)
            return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid)
            ? ppid
            : null;
    }

    private static bool CommandLineContains(int pid, string marker)
    {
        try
        {
            // Arguments are NUL-separated; a substring match over the raw bytes is enough for a marker.
            return File.ReadAllText($"/proc/{pid}/cmdline").Contains(marker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long ReadResidentBytes(int pid)
    {
        try
        {
            // statm: size resident shared … in pages.
            var fields = File.ReadAllText($"/proc/{pid}/statm").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pages)
                ? pages * Environment.SystemPageSize
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
