using System.Diagnostics;
using Sextant.Store;

namespace Sextant.Benchmarks;

/// <summary>
/// Periodically samples on-disk index size (main database plus write-ahead log) and process
/// memory on a background timer, tracking the peak of each. This is how the transient WAL peak
/// — which is checkpointed away before the run ends — is captured and distinguished from the
/// final database size.
/// </summary>
public sealed class ResourceSampler : IDisposable
{
    private readonly IndexDatabase _db;
    private readonly Process _process;
    private readonly Timer _timer;

    private long _peakDbPlusWal;
    private long _peakWal;
    private long _peakShm;
    private long _peakStaged;
    private long _peakManaged;
    private long _peakWorkingSet;

    public ResourceSampler(IndexDatabase db, TimeSpan interval)
    {
        _db = db;
        _process = Process.GetCurrentProcess();
        Sample();
        _timer = new Timer(_ => Sample(), null, interval, interval);
    }

    public long PeakDbPlusWalBytes => Interlocked.Read(ref _peakDbPlusWal);
    public long PeakWalBytes => Interlocked.Read(ref _peakWal);
    public long PeakShmBytes => Interlocked.Read(ref _peakShm);

    /// <summary>
    /// Peak transient footprint of the staging generation: main database plus WAL plus SHM. With the
    /// in-file generation model this is the not-yet-published on-disk cost that later phases reduce.
    /// </summary>
    public long PeakStagedArtifactBytes => Interlocked.Read(ref _peakStaged);
    public long PeakManagedBytes => Interlocked.Read(ref _peakManaged);
    public long PeakWorkingSetBytes => Interlocked.Read(ref _peakWorkingSet);

    /// <summary>Takes an immediate sample. Called on the timer and can be forced at phase boundaries.</summary>
    public void Sample()
    {
        try
        {
            var dbBytes = _db.MainDbBytes;
            var walBytes = _db.WalBytes;
            var shmBytes = _db.ShmBytes;
            UpdatePeak(ref _peakDbPlusWal, dbBytes + walBytes);
            UpdatePeak(ref _peakWal, walBytes);
            UpdatePeak(ref _peakShm, shmBytes);
            UpdatePeak(ref _peakStaged, dbBytes + walBytes + shmBytes);
            UpdatePeak(ref _peakManaged, GC.GetTotalMemory(forceFullCollection: false));
            _process.Refresh();
            UpdatePeak(ref _peakWorkingSet, _process.WorkingSet64);
        }
        catch
        {
            // Sampling is best-effort; a transient file-access error must not fail the run.
        }
    }

    private static void UpdatePeak(ref long field, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref field)))
        {
            if (Interlocked.CompareExchange(ref field, value, current) == current)
                return;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _process.Dispose();
    }
}
