using System.Diagnostics;
using Sextant.Core;

namespace Sextant.Core.Tests;

/// <summary>
/// Issue #267: the building blocks of per-phase timings — CPU readings from <c>/proc/self/stat</c>, the child-process
/// sampler that counts BuildHost processes, and thread-safe per-project timing records.
/// </summary>
[TestClass]
public class IndexingTimingTests
{
    [TestMethod]
    public void ParseChildrenMs_ReadsCutimeAndCstime_AfterACommandNameWithSpacesAndParentheses()
    {
        // Fields after "(comm)": state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime cutime cstime …
        const string stat = "4242 (my (odd) proc) S 1 4242 4242 0 -1 4194560 10 20 0 0 300 40 250 150 20 0 1";

        Assert.AreEqual((250 + 150) * 10, ProcessCpuClock.ParseChildrenMs(stat), "100 ticks per second → 10 ms per tick");
        Assert.IsNull(ProcessCpuClock.ParseChildrenMs("no parenthesis here"));
        Assert.IsNull(ProcessCpuClock.ParseChildrenMs("1 (x) S 1 2"), "too few fields");
    }

    [TestMethod]
    public void ParseParent_ReadsField4_AfterTheCommandName()
    {
        Assert.AreEqual(77, ChildProcessSampler.ParseParent("123 (a b) R 77 123 123 0 -1"));
        Assert.IsNull(ChildProcessSampler.ParseParent("garbage"));
    }

    [TestMethod]
    public void Since_IsNonNegative_AndNullWhenEitherReadingLacksChildren()
    {
        var start = new ProcessCpuClock(1_000, 50);
        Assert.AreEqual((500L, (long?)25), new ProcessCpuClock(1_500, 75).Since(start));
        Assert.AreEqual((0L, (long?)null), new ProcessCpuClock(900, null).Since(start));
    }

    [TestMethod]
    public void Now_OnLinux_ReportsChildCpu_ThatGrowsWhenAChildExits()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Child CPU comes from /proc.");

        var before = ProcessCpuClock.Now();
        Assert.IsNotNull(before.ChildrenMs);
        // A child that burns CPU and is reaped: its time lands in cutime/cstime.
        using (var child = Process.Start(new ProcessStartInfo("sh", ["-c", "i=0; while [ $i -lt 300000 ]; do i=$((i+1)); done"])
               {
                   RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
               })!)
        {
            Assert.IsTrue(child.WaitForExit(60_000));
        }
        var after = ProcessCpuClock.Now();
        Assert.IsTrue(after.ChildrenMs > before.ChildrenMs, $"{before.ChildrenMs} → {after.ChildrenMs}");
    }

    [TestMethod]
    public void Sampler_CountsMatchingDescendants_AndTheirMemory()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("The sampler reads /proc.");

        var marker = "sextant-sampler-" + Guid.NewGuid().ToString("N")[..8];
        using var sampler = new ChildProcessSampler(marker, TimeSpan.FromMilliseconds(20));
        Assert.IsTrue(sampler.Supported);
        var children = new List<Process>();
        try
        {
            // Two descendants whose command line carries the marker ($0 of the shell). Two commands, so a shell that
            // execs a lone last command cannot replace itself and drop the marker.
            for (var i = 0; i < 2; i++)
            {
                var child = Process.Start(new ProcessStartInfo("sh", ["-c", "sleep 1; true", marker])
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                })!;
                children.Add(child);
            }
            Thread.Sleep(300);
            var usage = sampler.Stop();

            Assert.IsNotNull(usage);
            // At least the two shells; a shell's fork for `sleep` briefly carries its command line before it execs.
            Assert.IsGreaterThanOrEqualTo(2, usage.Launches);
            Assert.IsGreaterThanOrEqualTo(2, usage.PeakConcurrent);
            Assert.IsGreaterThan(0L, usage.PeakResidentBytes);
        }
        finally
        {
            foreach (var child in children)
            {
                try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                child.Dispose();
            }
        }
    }

    [TestMethod]
    public void IsDescendant_WalksTheParentChain_AndStopsAtInitOrEightGenerations()
    {
        // pid → parent: 10 is the service; 11 and 12 its child and grandchild; 20 another tree under init.
        var parents = new Dictionary<int, int> { [11] = 10, [12] = 11, [20] = 1, [21] = 20 };
        Assert.IsTrue(ChildProcessSampler.IsDescendant(11, 10, parents));
        Assert.IsTrue(ChildProcessSampler.IsDescendant(12, 10, parents));
        Assert.IsFalse(ChildProcessSampler.IsDescendant(21, 10, parents), "another process tree");
        Assert.IsFalse(ChildProcessSampler.IsDescendant(10, 10, parents), "not its own descendant");
        Assert.IsFalse(ChildProcessSampler.IsDescendant(99, 10, parents), "unknown pid");

        var chain = new Dictionary<int, int>();
        for (var pid = 101; pid <= 110; pid++)
            chain[pid] = pid - 1;
        Assert.IsTrue(ChildProcessSampler.IsDescendant(108, 100, chain), "eight generations");
        Assert.IsFalse(ChildProcessSampler.IsDescendant(110, 100, chain), "ten generations is beyond the bound");
    }

    [TestMethod]
    public void Sampler_CountsNothing_WhenNoDescendantCarriesTheMarker()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("The sampler reads /proc.");

        using var sampler = new ChildProcessSampler("sextant-no-such-process-" + Guid.NewGuid().ToString("N"),
            TimeSpan.FromMilliseconds(20));
        Thread.Sleep(100);
        Assert.AreEqual(new ChildProcessUsage(0, 0, 0), sampler.Stop());
    }

    [TestMethod]
    public void RecordProject_IsThreadSafe_AndReplaceProjectsSwapsTheSet()
    {
        var metrics = new IndexingMetrics { Mode = "full" };
        Parallel.For(0, 1000, i => metrics.RecordProject(new ProjectTiming { Phase = "p", Project = $"x{i}", WallMs = i }));
        Assert.HasCount(1000, metrics.Projects);

        metrics.ReplaceProjects([new ProjectTiming { Phase = "p", Project = "only" }]);
        Assert.AreEqual("only", metrics.Projects.Single().Project);
    }
}
