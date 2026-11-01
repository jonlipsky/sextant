using Sextant.Service.Observability;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>Issue #355: a volume walk never runs on the caller's thread and never runs twice at once for a root.</summary>
[TestClass]
public class DirectorySizeCacheTests
{
    [TestMethod]
    public async Task ColdWalk_DoesNotBlockTheCaller_IsNotDuplicated_AndServesZeroUntilItFinishes()
    {
        using var release = new ManualResetEventSlim();
        var walks = 0;
        var cache = new DirectorySizeCache(_ =>
        {
            Interlocked.Increment(ref walks);
            release.Wait(TimeSpan.FromSeconds(60));
            return 42;
        });
        var clock = new ManualClock();

        var calls = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => cache.Get("/vol", clock))));

        CollectionAssert.AreEqual(new long[8], calls);
        release.Set();
        await WaitUntilAsync(() => cache.Get("/vol", clock) == 42);
        Assert.AreEqual(1, Volatile.Read(ref walks));
    }

    [TestMethod]
    public async Task StaleValue_IsServedWhileOneRefreshRuns()
    {
        using var release = new ManualResetEventSlim(true);
        var walks = 0;
        var cache = new DirectorySizeCache(_ =>
        {
            var n = Interlocked.Increment(ref walks);
            if (n == 2)
                release.Wait(TimeSpan.FromSeconds(60));
            return n * 10;
        });
        var clock = new ManualClock();
        cache.Get("/vol", clock);
        await WaitUntilAsync(() => cache.Get("/vol", clock) == 10);

        release.Reset();
        clock.Advance(DirectorySizeCache.Ttl + TimeSpan.FromSeconds(1));
        Assert.AreEqual(10, cache.Get("/vol", clock));
        Assert.AreEqual(10, cache.Get("/vol", clock));
        release.Set();
        await WaitUntilAsync(() => cache.Get("/vol", clock) == 20);
        Assert.AreEqual(2, Volatile.Read(ref walks));
    }

    [TestMethod]
    public async Task ThrowingWalk_KeepsThePreviousMeasurement_AndANeverMeasuredRootStaysUnmeasured()
    {
        var walks = 0;
        var cache = new DirectorySizeCache(_ =>
        {
            var n = Interlocked.Increment(ref walks);
            return n == 2 ? throw new IOException("unreadable") : 77;
        });
        var clock = new ManualClock();
        Assert.IsFalse(cache.TryGet("/vol", clock, out _));
        await WaitUntilAsync(() => cache.TryGet("/vol", clock, out var b) && b == 77);

        clock.Advance(DirectorySizeCache.Ttl + TimeSpan.FromSeconds(1));
        cache.Get("/vol", clock);
        await WaitUntilAsync(() => Volatile.Read(ref walks) == 2);
        await Task.Delay(100);
        Assert.IsTrue(cache.TryGet("/vol", clock, out var kept));
        Assert.AreEqual(77, kept);

        var failing = new DirectorySizeCache(_ => throw new IOException("unreadable"));
        failing.Get("/never", clock);
        await Task.Delay(200);
        Assert.IsFalse(failing.TryGet("/never", clock, out var bytes));
        Assert.AreEqual(0, bytes);
    }

    [TestMethod]
    public void Measure_ThrowsOnAnUnreadableDirectory_InsteadOfReturningAPartialTotal()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            Assert.Inconclusive("permission denial cannot be simulated");
        var root = Path.Combine(Path.GetTempPath(), "sextant-355-" + Guid.NewGuid().ToString("N"));
        var locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(root, "a.txt"), "abc");
        File.WriteAllText(Path.Combine(locked, "b.txt"), "def");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => DirectorySizeCache.Measure(root));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "condition was not met in time");
            await Task.Delay(20);
        }
    }
}
