using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sextant.Store;

/// <summary>
/// Who a writer-lease holder is, recorded with the lease (issue #320) so a starting process can PROVE a same-host
/// holder is dead. <see cref="InstanceId"/> is a random per-process GUID: a PID alone is not identity (a container
/// restart gives the new process PID 1 again).
/// </summary>
public sealed record ProcessIdentity
{
    public required string InstanceId { get; init; }
    public string? HostId { get; init; }

    /// <summary>
    /// A machine identity independent of the hostname (<c>/etc/machine-id</c>); null when missing or when it cannot
    /// identify the machine (inside a container the file is absent or baked into the image, so it is never read there).
    /// </summary>
    public string? MachineId { get; init; }
    public string? ContainerId { get; init; }

    /// <summary>Start time (clock ticks) of PID 1 of the holder's pid namespace: the container init, which a container restart replaces.</summary>
    public long? InitStartTicks { get; init; }
    public string? BootId { get; init; }
    public string? PidNamespace { get; init; }
    public int Pid { get; init; }

    /// <summary>Kernel start time of the process in clock ticks since boot (<c>/proc/&lt;pid&gt;/stat</c> field 22); null when unknown.</summary>
    public long? StartTicks { get; init; }
}

/// <summary>The state of a process as seen from the current pid namespace.</summary>
public readonly record struct ProcessProbe(bool Known, bool Exists, long? StartTicks)
{
    public static ProcessProbe Unknown => new(false, false, null);
    public static ProcessProbe Absent => new(true, false, null);
    public static ProcessProbe Present(long? startTicks) => new(true, true, startTicks);
}

/// <summary>Seam over /proc so the liveness proof is testable with hand-rolled doubles.</summary>
public interface IProcessInspector
{
    ProcessIdentity Current { get; }
    ProcessProbe Probe(int pid);
}

/// <summary>Reads the identity of this process and probes other pids through /proc (Linux only; elsewhere nothing is provable).</summary>
public sealed class ProcessInspector : IProcessInspector
{
    private static readonly Regex ContainerIdPattern = new(
        "/(?:overlay-)?containers/([0-9a-f]{64})/", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static ProcessInspector Instance { get; } = new();

    public ProcessIdentity Current { get; } = Capture();

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    private const int Esrch = 3;
    private const int Eperm = 1;

    /// <summary>
    /// kill(pid, 0) is the authority on whether a pid exists in this namespace: /proc/&lt;pid&gt; can be hidden
    /// (hidepid) for a live process, so its absence proves nothing. The start time is compared only when
    /// /proc/&lt;pid&gt;/stat is readable; a present pid whose stat cannot be read is Unknown, never "reused".
    /// </summary>
    public ProcessProbe Probe(int pid)
    {
        if (!OperatingSystem.IsLinux() || pid <= 0)
            return ProcessProbe.Unknown;
        try
        {
            // If our own /proc entries are not visible, /proc is not a trustworthy view here.
            if (!File.Exists("/proc/self/stat") || !Directory.Exists("/proc/1"))
                return ProcessProbe.Unknown;

            var rc = Kill(pid, 0);
            var errno = rc == 0 ? 0 : Marshal.GetLastWin32Error();
            return Classify(rc, errno, () => ReadStartTicks(pid));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException
                                      or EntryPointNotFoundException)
        {
            return ProcessProbe.Unknown;
        }
    }

    internal static ProcessProbe Classify(int killResult, int errno, Func<long?> readStartTicks)
    {
        if (killResult != 0 && errno == Esrch) return ProcessProbe.Absent;
        if (killResult != 0 && errno != Eperm) return ProcessProbe.Unknown;
        return readStartTicks() is { } ticks ? ProcessProbe.Present(ticks) : ProcessProbe.Unknown;
    }

    private static ProcessIdentity Capture()
    {
        var pid = Environment.ProcessId;
        var containerId = OperatingSystem.IsLinux() ? ReadContainerId() : null;
        return new ProcessIdentity
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            HostId = Environment.MachineName,
            MachineId = OperatingSystem.IsLinux() && containerId is null ? ReadMachineId() : null,
            ContainerId = containerId,
            InitStartTicks = OperatingSystem.IsLinux() ? ReadStartTicks(1) : null,
            BootId = OperatingSystem.IsLinux() ? ReadFirstLine("/proc/sys/kernel/random/boot_id") : null,
            PidNamespace = OperatingSystem.IsLinux() ? ReadLink("/proc/self/ns/pid") : null,
            Pid = pid,
            StartTicks = OperatingSystem.IsLinux() ? ReadStartTicks(pid) : null
        };
    }

    private static long? ReadStartTicks(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            // The command name (field 2) may contain spaces and parentheses; fields resume after the LAST ')'.
            var close = stat.LastIndexOf(')');
            if (close < 0) return null;
            var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // fields[0] is field 3 (state), so field 22 (starttime) is fields[19].
            return fields.Length > 19 && long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                ? ticks
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadContainerId()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var match = ContainerIdPattern.Match(line);
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
        }
        return null;
    }

    private static string? ReadMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            var id = ReadFirstLine(path);
            if (!string.IsNullOrWhiteSpace(id) && !id.Equals("uninitialized", StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
    }

    private static string? ReadFirstLine(string path)
    {
        try { return File.ReadLines(path).FirstOrDefault()?.Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadLink(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// The safety-critical decision of issue #320: is the recorded holder of a LIVE (unexpired) lease provably dead?
/// Pure over two identities and an inspector. Returns a short proof description, or null when the holder is alive,
/// on another host, or cannot be proven dead (the caller then keeps waiting for the lease to expire).
/// </summary>
public static class HolderLiveness
{
    public static string? ProveDead(ProcessIdentity holder, IProcessInspector inspector)
    {
        var me = inspector.Current;

        // Never the same process; and a holder on another (or unknown) host cannot be probed from here.
        if (holder.InstanceId == me.InstanceId) return null;
        if (string.IsNullOrEmpty(holder.HostId) || holder.HostId != me.HostId) return null;

        if (string.IsNullOrEmpty(holder.BootId) || string.IsNullOrEmpty(me.BootId)) return null;

        // A different boot: every process of the earlier boot is gone. Only trusted when both sides carry the SAME
        // known machine identity: the hostname alone can be shared by two machines (or containers), and a boot id
        // from another machine would then look like a reboot. Without it the shortcut is dropped (wait for the TTL),
        // except for the container-restart proof below.
        if (holder.BootId != me.BootId)
        {
            if (!string.IsNullOrEmpty(holder.MachineId) && holder.MachineId == me.MachineId)
                return "the host rebooted since the holder acquired the lease";
            return ContainerRestarted(holder, me);
        }

        // Same pid namespace: /proc describes the holder's pid exactly. A pid that is gone, or that now has another
        // start time (pid reuse), proves the holder dead; a matching start time is the live holder itself.
        if (!string.IsNullOrEmpty(holder.PidNamespace) && holder.PidNamespace == me.PidNamespace)
        {
            if (holder.Pid <= 0 || holder.StartTicks is not { } recorded) return null;
            var probe = inspector.Probe(holder.Pid);
            if (!probe.Known) return null;
            if (!probe.Exists) return $"pid {holder.Pid} no longer exists";
            if (probe.StartTicks is { } now && now != recorded)
                return $"pid {holder.Pid} was reused (start time {recorded} -> {now})";
            return null;
        }

        return ContainerRestarted(holder, me);
    }

    // Different pid namespace but the SAME container id, AND the container's PID 1 started at a different time (a
    // docker restart replaces the init and gives the process a fresh namespace): the old namespace died with the
    // container's previous run, so none of its processes survive. A nested namespace inside one container run has
    // the same container id and a different namespace but the same init start, so it is NOT proof. Anything equal,
    // missing or unknown blocks.
    private static string? ContainerRestarted(ProcessIdentity holder, ProcessIdentity me)
    {
        if (string.IsNullOrEmpty(holder.ContainerId) || holder.ContainerId != me.ContainerId) return null;
        if (string.IsNullOrEmpty(holder.PidNamespace) || string.IsNullOrEmpty(me.PidNamespace)
            || holder.PidNamespace == me.PidNamespace) return null;
        // Both sides must be the init of their own namespace: a real container restart restarts the init, a nested
        // successor (or holder) is some other process in a namespace whose PID 1 says nothing about the holder.
        if (holder.Pid != 1 || me.Pid != 1) return null;
        if (holder.InitStartTicks is not { } before || me.InitStartTicks is not { } after || before == after) return null;
        return "the container restarted (its init process and pid namespace were replaced)";
    }
}
