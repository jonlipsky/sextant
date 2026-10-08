using System.Diagnostics;
using System.Globalization;

namespace Sextant.Core;

/// <summary>
/// CPU time consumed so far by this process and by its exited child processes (issue #267). Phase timings subtract two
/// readings. Child CPU comes from <c>/proc/self/stat</c> (<c>cutime</c> + <c>cstime</c>: children that have exited and
/// been reaped, such as restore runs and MSBuild BuildHosts), so it is null where <c>/proc</c> is unavailable.
/// </summary>
public readonly record struct ProcessCpuClock(long SelfMs, long? ChildrenMs)
{
    // USER_HZ: the unit of /proc/<pid>/stat times. Linux fixes it at 100 for user space regardless of the kernel HZ.
    private const long ClockTicksPerSecond = 100;

    /// <summary>Reads the current totals.</summary>
    public static ProcessCpuClock Now()
    {
        long self;
        using (var process = Process.GetCurrentProcess())
            self = (long)process.TotalProcessorTime.TotalMilliseconds;
        return new ProcessCpuClock(self, ReadChildrenMs());
    }

    /// <summary>CPU time between <paramref name="start"/> and this reading.</summary>
    public (long SelfMs, long? ChildrenMs) Since(ProcessCpuClock start) =>
        (Math.Max(0, SelfMs - start.SelfMs),
         ChildrenMs is { } now && start.ChildrenMs is { } then ? Math.Max(0, now - then) : null);

    private static long? ReadChildrenMs()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            return ParseChildrenMs(File.ReadAllText("/proc/self/stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses <c>cutime</c> + <c>cstime</c> (fields 16 and 17) from a <c>/proc/&lt;pid&gt;/stat</c> line, in
    /// milliseconds. The command name (field 2) is parenthesized and may contain spaces, so fields are counted after
    /// its closing parenthesis. Null when the line is malformed.
    /// </summary>
    internal static long? ParseChildrenMs(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0)
            return null;
        // After ")" come fields 3.. (state, ppid, …); cutime and cstime are fields 16 and 17, so indices 13 and 14 here.
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 15
            || !long.TryParse(fields[13], NumberStyles.None, CultureInfo.InvariantCulture, out var cutime)
            || !long.TryParse(fields[14], NumberStyles.None, CultureInfo.InvariantCulture, out var cstime))
            return null;
        return (cutime + cstime) * 1000 / ClockTicksPerSecond;
    }
}
