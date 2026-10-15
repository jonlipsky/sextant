using System.Runtime;
using System.Runtime.InteropServices;

namespace Sextant.Service;

/// <summary>
/// Gives a job's memory back at the end of the job (issue #320). A job's managed heap peaks at several GiB;
/// once the job's objects are unreachable a plain collection frees them but the runtime keeps the freed regions
/// committed (and, with transparent hugepages, resident) until a later GC decides to decommit, which an idle
/// service never triggers. Measured on the ProcessStack repository: 3.4 GiB RSS stayed after a forced compacting
/// GC with a 50 MiB live heap; an aggressive blocking GC returned it to ~0.5 GiB. Glibc's malloc arenas (SQLite,
/// MSBuild host, brotli) are trimmed too (glibc only: <c>malloc_trim</c> is skipped elsewhere). Runs after a job,
/// on a pool thread with the write gate released (the caller decides whether it is worth the ~2 s pause), never
/// throws, and changes nothing a snapshot contains.
/// </summary>
public static class JobMemoryReclaimer
{
    public readonly record struct Result(long WorkingSetBefore, long WorkingSetAfter, long HeapAfter, TimeSpan Elapsed);

    /// <summary>
    /// Below this working set the pause buys nothing: an idle service sits around 0.5 GiB (measured) and a job that
    /// stayed under 1 GiB left little committed memory to give back.
    /// </summary>
    public const long MinWorkingSetBytes = 1L << 30;

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    public static Result Reclaim()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var before = 0L;
        try
        {
            before = Environment.WorkingSet;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // Both collections are needed: the first finds finalizable objects dead and queues their finalizers
            // (workspace/SQLite/stream handles), which free memory only once they have run; those objects survive the
            // first GC and only a collection after WaitForPendingFinalizers can free them (and decommit, aggressively).
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            TrimNativeHeap();
        }
        catch
        {
            // Best effort: a failed reclaim must never fail or requeue a job.
        }
        try
        {
            return new Result(before, Environment.WorkingSet, GC.GetTotalMemory(false), watch.Elapsed);
        }
        catch
        {
            return new Result(before, before, 0, watch.Elapsed);
        }
    }

    private static void TrimNativeHeap()
    {
        if (!OperatingSystem.IsLinux())
            return;
        try
        {
            MallocTrim(0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // musl or an unusual libc: nothing to trim.
        }
    }
}
