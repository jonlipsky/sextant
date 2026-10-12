using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #269: the prefetcher compiles at most its window of projects ahead of the phase, in order, starts nothing
/// once stopped, prefetches nothing below a window of 2, and always tears down promptly, even when a compile fails.
/// </summary>
[TestClass]
public sealed class CompilationPrefetcherTests
{
    private readonly ConcurrentQueue<string> _started = new();

    [TestMethod]
    public async Task CompilesAtMostTheWindowAheadOfThePhase_InOrder()
    {
        var projects = Projects(5);
        await using var prefetch = new CompilationPrefetcher(projects, 2, CancellationToken.None, Record);

        // Each compile starts on its own thread, so they may record in either order within a step; which projects have
        // started after each step is what the window guarantees.
        await WaitUntil(() => _started.Count == 2);
        await Task.Delay(50);
        CollectionAssert.AreEquivalent(new[] { "P0", "P1" }, _started.ToArray(), "two ahead before the phase reaches any");

        prefetch.Reached();
        await WaitUntil(() => _started.Count == 3);
        await Task.Delay(50);
        CollectionAssert.AreEquivalent(new[] { "P0", "P1", "P2" }, _started.ToArray(), "one more, the next in order");

        prefetch.Reached();
        prefetch.Reached();
        await WaitUntil(() => _started.Count == 5);
        CollectionAssert.AreEquivalent(new[] { "P0", "P1", "P2", "P3", "P4" }, _started.ToArray());
    }

    [TestMethod]
    public async Task StartsNothingOnceStopped()
    {
        var projects = Projects(5);
        await using var prefetch = new CompilationPrefetcher(projects, 2, CancellationToken.None, Record);
        await WaitUntil(() => _started.Count == 2);

        prefetch.Stop();
        prefetch.Reached();
        prefetch.Reached();
        await Task.Delay(100);

        Assert.HasCount(2, _started, "the time budget skips the rest: none of it is compiled");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task PrefetchesNothingBelowAWindowOfTwo(int window)
    {
        await using (var prefetch = new CompilationPrefetcher(Projects(3), window, CancellationToken.None, Record))
        {
            prefetch.Reached();
            await Task.Delay(50);
        }

        Assert.IsEmpty(_started);
    }

    [TestMethod]
    public async Task DisposesPromptly_WhileWaitingForThePhase_AndWhenACompileFails()
    {
        var gate = new TaskCompletionSource();
        var prefetch = new CompilationPrefetcher(Projects(4), 2, CancellationToken.None, async (project, token) =>
        {
            _started.Enqueue(project.Name);
            if (project.Name == "P0")
                throw new InvalidOperationException("broken project");
            await gate.Task.WaitAsync(token);
        });
        await WaitUntil(() => _started.Count == 2);

        // P1 is still compiling and the producer waits for the phase: disposing cancels both.
        await prefetch.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.HasCount(2, _started);
    }

    [TestMethod]
    public async Task TheRunsCancellationStopsIt()
    {
        using var run = new CancellationTokenSource();
        await using var prefetch = new CompilationPrefetcher(Projects(4), 2, run.Token, Record);
        await WaitUntil(() => _started.Count == 2);

        await run.CancelAsync();
        prefetch.Reached();
        await Task.Delay(100);

        Assert.HasCount(2, _started);
    }

    [TestMethod]
    public async Task APrefetchThatFinishedFirst_IsAHit_WithThePrefetchsOwnTimings()
    {
        var projects = Projects(2);
        await using var prefetch = new CompilationPrefetcher(projects, 2, CancellationToken.None, Record, recordTimings: true);
        await WaitUntil(() => prefetch.IsHit(projects[0].Id));

        var timing = prefetch.TimingOf(projects[0].Id)!;
        var window = prefetch.CompileWindow(projects[0].Id, walkRequestedMs: 900, walkStartedMs: 901, walkEndedMs: 902);
        Assert.AreEqual((timing.RequestedMs, timing.StartedMs, timing.EndedMs), window);
    }

    [TestMethod]
    public async Task AProjectTheWalkCompilesItself_IsAMiss_WithTheWalksTimings()
    {
        var projects = Projects(2);
        await using var prefetch = new CompilationPrefetcher(projects, 0, CancellationToken.None, Record, recordTimings: true);

        Assert.IsFalse(prefetch.IsHit(projects[0].Id));
        Assert.IsNull(prefetch.TimingOf(projects[0].Id));
        Assert.AreEqual((10L, 12L, 40L), prefetch.CompileWindow(projects[0].Id, 10, 12, 40));
    }

    [TestMethod]
    public async Task AFailedPrefetch_IsAMiss_WithTheWalksTimings()
    {
        var projects = Projects(2);
        await using var prefetch = new CompilationPrefetcher(projects, 2, CancellationToken.None,
            (_, _) => throw new InvalidOperationException("broken project"), recordTimings: true);
        await WaitUntil(() => prefetch.TimingOf(projects[0].Id) is { EndedMs: >= 0 });

        Assert.IsFalse(prefetch.TimingOf(projects[0].Id)!.Succeeded);
        Assert.IsFalse(prefetch.IsHit(projects[0].Id), "a failed prefetch is not a hit");
        Assert.AreEqual((10L, 12L, 40L), prefetch.CompileWindow(projects[0].Id, 10, 12, 40));
    }

    [TestMethod]
    public async Task RecordedTimingsAreOrdered_AndNothingIsRecordedWhenOff()
    {
        var projects = Projects(3);
        await using var prefetch = new CompilationPrefetcher(projects, 3, CancellationToken.None, Record, recordTimings: true);
        await WaitUntil(() => projects.All(p => prefetch.IsHit(p.Id)));
        foreach (var project in projects)
        {
            var timing = prefetch.TimingOf(project.Id)!;
            Assert.IsTrue(timing.RequestedMs <= timing.StartedMs && timing.StartedMs <= timing.EndedMs);
        }

        // A walk window given out of order is clamped.
        var (requested, started, ended) = prefetch.CompileWindow(ProjectId.CreateNewId(), 50, 20, 10);
        Assert.IsTrue(requested <= started && started <= ended);

        await using var off = new CompilationPrefetcher(projects, 3, CancellationToken.None, Record);
        await WaitUntil(() => _started.Count >= 6);
        Assert.IsNull(off.TimingOf(projects[0].Id));
        Assert.IsFalse(off.IsHit(projects[0].Id));
    }

    private static readonly string?[] Keys = ["a", "b", "c", "d", "e", "f"];

    private static IReadOnlyList<int> Order(string?[] keys, Dictionary<string, long>? cost, int window) =>
        CompilePrefetchPolicy.PrefetchOrder(keys, cost, window).Order;

    [TestMethod]
    public void PrefetchOrder_WithoutHints_IsTheWalkOrder()
    {
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, Order(Keys, null, 2).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, Order(Keys, new(), 2).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, Order(Keys, new() { ["zzz"] = 90_000 }, 2).ToArray(), "unknown keys are ignored");
        Assert.IsEmpty(Order([], new() { ["a"] = 5000 }, 2));
    }

    [TestMethod]
    public void PrefetchOrder_PromotesTheExpensive_MostExpensiveFirst_ThenWalkOrder()
    {
        var cost = new Dictionary<string, long> { ["d"] = 5000, ["b"] = 31_700, ["e"] = 2000, ["c"] = 1999 };

        CollectionAssert.AreEqual(new[] { 1, 3, 4, 0, 2, 5 }, Order(Keys, cost, 3).ToArray(),
            "b, d, e (2 s is the threshold; c at 1999 ms is not promoted), then the rest in walk order");
    }

    [TestMethod]
    public void PrefetchOrder_PromotesAtMostTheWindow_AndEachProjectOnce()
    {
        var cost = new Dictionary<string, long> { ["a"] = 3000, ["b"] = 4000, ["c"] = 5000, ["d"] = 6000 };

        var order = Order(Keys, cost, 2);

        CollectionAssert.AreEqual(new[] { 3, 2, 0, 1, 4, 5 }, order.ToArray());
        Assert.HasCount(Keys.Length, order.Distinct().ToList());
    }

    [TestMethod]
    public void PrefetchOrder_TiesKeepWalkOrder_AndAProjectWithoutAKeyIsNeverPromoted()
    {
        string?[] keys = ["a", null, "c"];
        var cost = new Dictionary<string, long> { ["a"] = 4000, ["c"] = 4000 };

        CollectionAssert.AreEqual(new[] { 0, 2, 1 }, Order(keys, cost, 5).ToArray());
    }

    [TestMethod]
    public void PrefetchOrder_ReportsHowManyArePromoted()
    {
        var cost = new Dictionary<string, long> { ["a"] = 3000, ["b"] = 4000, ["c"] = 5000 };

        Assert.AreEqual(2, CompilePrefetchPolicy.PrefetchOrder(Keys, cost, 2).Promoted);
        Assert.AreEqual(3, CompilePrefetchPolicy.PrefetchOrder(Keys, cost, 5).Promoted);
        Assert.AreEqual(0, CompilePrefetchPolicy.PrefetchOrder(Keys, null, 5).Promoted);
    }

    [TestMethod]
    public async Task WithHints_TheExpensiveProjectsCompileFirst_AndTheirPermitsReturnWhenTheyEnd()
    {
        var projects = Projects(8);
        var hints = new CompileCostHints(new Dictionary<string, long> { ["P6"] = 9000, ["P5"] = 3000 });
        var gates = projects.ToDictionary(p => p.Name, _ => new TaskCompletionSource());
        await using var prefetch = new CompilationPrefetcher(
            projects, 3, CancellationToken.None, Gated(gates), hints: hints, keyOf: p => p.Name);

        CollectionAssert.AreEqual(new[] { "P6", "P5" }, prefetch.Promoted.Select(p => p.Key).ToArray());
        await WaitUntil(() => _started.Count == 3);
        await Task.Delay(50);
        CollectionAssert.AreEquivalent(new[] { "P6", "P5", "P0" }, _started.ToArray(),
            "two promoted and, with the one permit left, the first project in walk order");

        // The walk is on P0 (reached, compiling): its permit is returned, the promoted ones still hold theirs.
        prefetch.Reached();
        await WaitUntil(() => _started.Count == 4);
        await Task.Delay(50);
        Assert.AreEqual("P1", _started.ToArray()[3]);

        // The promoted compiles end: their permits return at once, though the walk is far from them, and the
        // next requests are the projects just ahead of the walk, not P0 again.
        gates["P6"].SetResult();
        gates["P5"].SetResult();
        await WaitUntil(() => _started.Count == 6);
        await Task.Delay(50);
        CollectionAssert.AreEquivalent(new[] { "P2", "P3" }, _started.ToArray()[4..]);
        Assert.HasCount(6, _started.Distinct().ToList());
    }

    [TestMethod]
    public async Task ReachingAPromotedProject_ReleasesNoExtraPermit()
    {
        var projects = Projects(6);
        var hints = new CompileCostHints(new Dictionary<string, long> { ["P1"] = 9000 });
        var gates = projects.ToDictionary(p => p.Name, _ => new TaskCompletionSource());
        await using var prefetch = new CompilationPrefetcher(
            projects, 2, CancellationToken.None, Gated(gates), hints: hints, keyOf: p => p.Name);
        await WaitUntil(() => _started.Count == 2);
        gates["P1"].SetResult();
        await WaitUntil(() => _started.Count == 3);
        await Task.Delay(50);
        CollectionAssert.AreEquivalent(new[] { "P1", "P0" }, _started.ToArray()[..2]);
        Assert.AreEqual("P2", _started.ToArray()[2], "P0 holds a permit, P1's was returned and went to P2");

        prefetch.Reached(); // P0: returns its permit, P3 starts
        await WaitUntil(() => _started.Count == 4);
        prefetch.Reached(); // P1 is promoted: no permit to return
        await Task.Delay(100);
        Assert.HasCount(4, _started, "reaching P1 must not let another project start");

        prefetch.Reached(); // P2
        await WaitUntil(() => _started.Count == 5);
    }

    [TestMethod]
    public async Task ConcurrentCompiles_NeverExceedTheWindow_AndAReachedProjectIsNeverRequested()
    {
        var projects = Projects(10);
        var hints = new CompileCostHints(new Dictionary<string, long> { ["P9"] = 9000, ["P8"] = 8000, ["P7"] = 7000 });
        var gates = projects.ToDictionary(p => p.Name, _ => new TaskCompletionSource());
        var running = 0;
        var maxRunning = 0;
        await using var prefetch = new CompilationPrefetcher(projects, 3, CancellationToken.None, async (project, token) =>
        {
            _started.Enqueue(project.Name);
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref maxRunning)) < now && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen) { }
            try { await gates[project.Name].Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref running); }
        }, hints: hints, keyOf: p => p.Name);

        await WaitUntil(() => _started.Count == 3);
        // The walk reaches P0..P2 before any of them was requested: they must never be.
        prefetch.Reached();
        prefetch.Reached();
        prefetch.Reached();
        foreach (var name in new[] { "P9", "P8", "P7" })
        {
            gates[name].SetResult();
            await Task.Delay(30);
        }
        await WaitUntil(() => _started.Count >= 6);
        foreach (var gate in gates.Values)
            gate.TrySetResult();
        prefetch.Reached(); // P3
        prefetch.Reached(); // P4
        prefetch.Reached(); // P5
        await WaitUntil(() => _started.Count == 7);
        await Task.Delay(50);
        Assert.HasCount(7, _started, "P6 is the only one left to request");

        CollectionAssert.DoesNotContain(_started.ToArray(), "P0");
        CollectionAssert.DoesNotContain(_started.ToArray(), "P1");
        CollectionAssert.DoesNotContain(_started.ToArray(), "P2");
        Assert.HasCount(_started.Count, _started.Distinct().ToList());
        Assert.IsLessThanOrEqualTo(3, maxRunning, "never more than the window compile at once");
    }

    [TestMethod]
    public async Task WithoutHints_TheRequestOrderIsTheWalkOrder()
    {
        var projects = Projects(6);
        var gates = projects.ToDictionary(p => p.Name, _ => new TaskCompletionSource());
        await using var prefetch = new CompilationPrefetcher(projects, 2, CancellationToken.None, Gated(gates));
        await WaitUntil(() => _started.Count == 2);

        prefetch.Reached(); // P0
        await WaitUntil(() => _started.Count == 3);
        prefetch.Reached(); // P1
        await WaitUntil(() => _started.Count == 4);
        prefetch.Reached(); // P2
        await WaitUntil(() => _started.Count == 5);
        await Task.Delay(50);

        CollectionAssert.AreEquivalent(new[] { "P0", "P1" }, _started.ToArray()[..2]);
        CollectionAssert.AreEqual(new[] { "P2", "P3", "P4" }, _started.ToArray()[2..]);
    }

    [TestMethod]
    public async Task WithoutHints_AProjectTheWalkAlreadyReached_IsNeverRequested()
    {
        var projects = Projects(6);
        var gates = projects.ToDictionary(p => p.Name, _ => new TaskCompletionSource());
        var paused = new TaskCompletionSource();
        var pausedAtThird = new TaskCompletionSource();
        Task Hold(int position)
        {
            if (position < 2)
                return Task.CompletedTask;
            pausedAtThird.TrySetResult();
            return paused.Task;
        }
        await using var prefetch = new CompilationPrefetcher(
            projects, 2, CancellationToken.None, Gated(gates), beforePermit: Hold);
        await WaitUntil(() => _started.Count == 2);
        await pausedAtThird.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The walk runs ahead of the paused producer, so it cannot request a project the walk has already reached.
        for (var i = 0; i < 5; i++)
            prefetch.Reached();
        paused.SetResult();
        await WaitUntil(() => _started.Contains("P5"));

        CollectionAssert.DoesNotContain(_started.ToArray()[2..], "P3");
        CollectionAssert.DoesNotContain(_started.ToArray()[2..], "P4");
        Assert.HasCount(_started.Count, _started.Distinct().ToList());
    }

    [TestMethod]
    public async Task StopAndDispose_WhilePromotedCompilesRun_CompleteWithoutException()
    {
        foreach (var stopFirst in new[] { true, false })
        {
            while (_started.TryDequeue(out _)) { }
            var projects = Projects(6);
            var hints = new CompileCostHints(new Dictionary<string, long> { ["P4"] = 9000, ["P5"] = 8000 });
            var gates = projects.ToDictionary(p => p.Name, _ => new TaskCompletionSource());
            var prefetch = new CompilationPrefetcher(
                projects, 3, CancellationToken.None, Gated(gates), recordTimings: true, hints: hints, keyOf: p => p.Name);
            await WaitUntil(() => _started.Count == 3);
            Assert.IsTrue(_started.Contains("P4") && _started.Contains("P5"), "the promoted compiles are running");

            if (stopFirst)
                prefetch.Stop();
            await prefetch.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod]
    public async Task AWalkThatReachesAProjectMidCompile_RecordsTheFullCompileAsItsCost()
    {
        var projects = Projects(3);
        var hints = new CompileCostHints(new Dictionary<string, long> { ["P2"] = 9000 });
        var gates = projects.ToDictionary(p => p.Name, _ => new TaskCompletionSource());
        await using var prefetch = new CompilationPrefetcher(
            projects, 2, CancellationToken.None, Gated(gates), recordTimings: true, hints: hints, keyOf: p => p.Name);
        await WaitUntil(() => prefetch.TimingOf(projects[2].Id) is { StartedMs: >= 0 });

        await Task.Delay(200);
        var walkStarted = prefetch.NowMs;
        await Task.Delay(10);
        var walkEnded = prefetch.NowMs;

        var window = prefetch.CompileWindow(projects[2].Id, walkStarted, walkStarted, walkEnded);
        var cost = prefetch.CompileCostMs(projects[2].Id, walkStarted, walkStarted, walkEnded);
        Assert.IsLessThanOrEqualTo(100, window.Ended - window.Started, "the walk's window is only its short wait");
        Assert.IsGreaterThanOrEqualTo(200, cost, "the cost is the compile from the prefetch's start");
        Assert.AreEqual(walkEnded - prefetch.TimingOf(projects[2].Id)!.StartedMs, cost);
    }

    [TestMethod]
    public async Task TheCost_OfAFinishedPrefetch_IsItsOwnDuration_AndOfAMiss_TheWalksWindow()
    {
        var projects = Projects(2);
        await using var prefetch = new CompilationPrefetcher(projects, 2, CancellationToken.None, Record, recordTimings: true);
        await WaitUntil(() => prefetch.IsHit(projects[0].Id));
        var timing = prefetch.TimingOf(projects[0].Id)!;

        Assert.AreEqual(timing.EndedMs - timing.StartedMs, prefetch.CompileCostMs(projects[0].Id, 900, 901, 902));
        Assert.AreEqual(28L, prefetch.CompileCostMs(ProjectId.CreateNewId(), 10, 12, 40));
    }

    [TestMethod]
    public async Task WithoutQualifyingHints_TheOrderIsTodays()
    {
        var hints = new CompileCostHints(new Dictionary<string, long> { ["P3"] = 1500, ["nope"] = 99_000 });
        await using var prefetch = new CompilationPrefetcher(
            Projects(4), 2, CancellationToken.None, Record, hints: hints, keyOf: p => p.Name);

        Assert.IsEmpty(prefetch.Promoted);
        await WaitUntil(() => _started.Count == 2);
        await Task.Delay(50);
        CollectionAssert.AreEquivalent(new[] { "P0", "P1" }, _started.ToArray());
    }

    [TestMethod]
    public void HintKeys_AreRepoRelativeWithForwardSlashes_AndTheTargetFramework()
    {
        Assert.AreEqual("src/Api/Api.csproj|net10.0", CompileCostHints.KeyOf("src\\Api\\Api.csproj", "net10.0"));
        Assert.AreEqual("src/Api/Api.csproj|", CompileCostHints.KeyOf("/src/Api/Api.csproj", null));
        Assert.AreNotEqual(CompileCostHints.KeyOf("a.csproj", "net8.0"), CompileCostHints.KeyOf("a.csproj", "net10.0"));
    }

    private Func<Project, CancellationToken, Task> Gated(Dictionary<string, TaskCompletionSource> gates) =>
        (project, token) =>
        {
            _started.Enqueue(project.Name);
            return gates[project.Name].Task.WaitAsync(token);
        };

    private Task Record(Project project, CancellationToken token)
    {
        _started.Enqueue(project.Name);
        return Task.CompletedTask;
    }

    private static List<Project> Projects(int count, params (int From, int To)[] references)
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        var ids = new List<ProjectId>();
        for (var i = 0; i < count; i++)
        {
            var id = ProjectId.CreateNewId();
            solution = solution.AddProject(id, $"P{i}", $"P{i}", LanguageNames.CSharp);
            ids.Add(id);
        }
        foreach (var (from, to) in references)
            solution = solution.AddProjectReference(ids[from], new ProjectReference(ids[to]));
        return ids.Select(solution.GetProject).Select(p => p!).ToList();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("timed out waiting for the prefetcher");
            await Task.Delay(5);
        }
    }
}
