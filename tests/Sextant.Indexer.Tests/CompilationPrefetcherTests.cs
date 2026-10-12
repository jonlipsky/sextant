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
