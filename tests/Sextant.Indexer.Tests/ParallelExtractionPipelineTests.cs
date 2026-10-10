using System.Collections.Concurrent;
using Sextant.Core;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Fast, MSBuild-free tests of the Phase 6 bounded producer/consumer pipeline. They drive
/// <see cref="ParallelExtractionPipeline"/> with synthetic project/document work so the concurrency
/// contracts — deterministic ordering independent of completion order (criterion 1), clean teardown
/// on cancellation and worker failure (criterion 4), and bounded run-ahead under backpressure
/// (criterion 2) — can be asserted deterministically and in milliseconds.
/// </summary>
[TestClass]
public sealed class ParallelExtractionPipelineTests
{
    /// <param name="Throw">When set, the extract worker throws to simulate a failing analysis worker.</param>
    private sealed record TestDoc(int Project, int Doc, int DelayMs, bool Throw);

    private static Func<CancellationToken, Task<ProjectExtraction<TestDoc>>> Project(
        int project, int docCount, Func<int, int> delayForDoc, int throwOnDoc = -1,
        Action? onMaterialize = null)
        => _ =>
        {
            onMaterialize?.Invoke();
            var docs = Enumerable.Range(0, docCount)
                .Select(d => new TestDoc(project, d, delayForDoc(d), d == throwOnDoc))
                .ToList();
            return Task.FromResult(new ProjectExtraction<TestDoc>(project, $"P{project}", docs));
        };

    private static DocumentContributionSet Extract(TestDoc doc, CancellationToken _)
    {
        if (doc.DelayMs > 0)
            Thread.Sleep(doc.DelayMs);
        if (doc.Throw)
            throw new InvalidOperationException($"worker fault {doc.Project}:{doc.Doc}");
        var set = new DocumentContributionSet();
        // A unique key per document so nothing dedups away — the persisted order is fully observable.
        set.AddRelationship(new RelationshipContribution($"{doc.Project}:{doc.Doc}", "to", RelationshipKind.Inherits));
        return set;
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(8)]
    public async Task PersistedOrder_IsDeterministic_IndependentOfCompletionOrder(int maxParallelism)
    {
        const int projectCount = 4;
        const int docCount = 6;
        var recorded = new List<string>();

        var projects = Enumerable.Range(0, projectCount)
            // Later documents finish first (delay decreases with ordinal), so completion order is the
            // reverse of document ordinal — the merge must still restore ascending-ordinal order.
            .Select(p => Project(p, docCount, d => (docCount - d) * 4))
            .ToList();

        Task Persist(ProjectContributions project, CancellationToken _)
        {
            foreach (var rel in project.Contributions.Relationships)
                recorded.Add(rel.FromKey);
            return Task.CompletedTask;
        }

        await ParallelExtractionPipeline.RunAsync(
            projects, Extract, Persist,
            new ExtractionParallelismOptions { MaxParallelism = maxParallelism, QueueCapacity = 3 },
            CancellationToken.None);

        var expected = (from p in Enumerable.Range(0, projectCount)
                        from d in Enumerable.Range(0, docCount)
                        select $"{p}:{d}").ToList();
        CollectionAssert.AreEqual(expected, recorded,
            "the merged, persisted order must follow (project ordinal, document ordinal) regardless of " +
            "which parallel workers finished first");
    }

    [TestMethod]
    public async Task ParallelResult_MatchesSequentialResult()
    {
        const int projectCount = 3;
        const int docCount = 5;

        async Task<List<string>> Run(int parallelism)
        {
            var recorded = new List<string>();
            var projects = Enumerable.Range(0, projectCount)
                .Select(p => Project(p, docCount, d => (d % 3) * 3))
                .ToList();
            await ParallelExtractionPipeline.RunAsync(
                projects, Extract,
                (project, _) =>
                {
                    foreach (var rel in project.Contributions.Relationships) recorded.Add(rel.FromKey);
                    return Task.CompletedTask;
                },
                new ExtractionParallelismOptions { MaxParallelism = parallelism, QueueCapacity = 2 },
                CancellationToken.None);
            return recorded;
        }

        CollectionAssert.AreEqual(await Run(1), await Run(8),
            "parallel extraction must produce the identical contribution order as the sequential path");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(8)]
    public async Task CrossDocumentRelationship_DedupsFirstWins_InDocumentOrdinalOrder(int maxParallelism)
    {
        // A relationship is the only contribution kind that can dedup ACROSS documents (reference and
        // call keys include the file), so this exercises the cross-document first-wins fold directly.
        // Docs 0 and 2 emit the SAME relationship; doc 1 a distinct one. Doc 2 finishes first (no
        // delay) while doc 0 is slow, so completion order != ordinal order. The merged output must
        // still collapse the later duplicate and keep ascending-document-ordinal order: [shared, mid].
        var docs = new List<TestDoc>
        {
            new(0, 0, DelayMs: 30, Throw: false), // shared -> base (slow)
            new(0, 1, DelayMs: 15, Throw: false), // mid -> base
            new(0, 2, DelayMs: 0, Throw: false),  // shared -> base (duplicate, finishes first)
        };
        var projects = new List<Func<CancellationToken, Task<ProjectExtraction<TestDoc>>>>
        {
            _ => Task.FromResult(new ProjectExtraction<TestDoc>(0, "P0", docs)),
        };

        static DocumentContributionSet ExtractRel(TestDoc doc, CancellationToken _)
        {
            if (doc.DelayMs > 0) Thread.Sleep(doc.DelayMs);
            var set = new DocumentContributionSet();
            var from = doc.Doc == 1 ? "mid" : "shared";
            set.AddRelationship(new RelationshipContribution(from, "base", RelationshipKind.Inherits));
            return set;
        }

        var recorded = new List<string>();
        await ParallelExtractionPipeline.RunAsync(
            projects, ExtractRel,
            (project, _) =>
            {
                foreach (var rel in project.Contributions.Relationships) recorded.Add(rel.FromKey);
                return Task.CompletedTask;
            },
            new ExtractionParallelismOptions { MaxParallelism = maxParallelism, QueueCapacity = 2 },
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "shared", "mid" }, recorded,
            "the duplicate relationship from the later document must collapse (first-wins) and the " +
            "merged order must follow ascending document ordinal, not worker completion order");
    }

    [TestMethod]
    public async Task Cancellation_MidRun_TearsDownCleanly_WithoutDeadlock()
    {
        using var cts = new CancellationTokenSource();
        var persistedProjects = 0;

        // Many projects, each with slow documents, so the run is still in flight when we cancel.
        var projects = Enumerable.Range(0, 50)
            .Select(p => Project(p, 4, _ => 20))
            .ToList();

        Task Persist(ProjectContributions _, CancellationToken __)
        {
            if (Interlocked.Increment(ref persistedProjects) == 1)
                cts.Cancel();
            return Task.CompletedTask;
        }

        var run = ParallelExtractionPipeline.RunAsync(
            projects, Extract, Persist,
            new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = 2 },
            cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => WithTimeout(run));
        Assert.IsTrue(persistedProjects < projects.Count,
            "cancellation should stop the pipeline before every project is persisted");
    }

    [TestMethod]
    public async Task WorkerFailure_SingleDocProject_PropagatesAndTearsDown()
    {
        var persisted = new ConcurrentBag<long>();
        var projects = new List<Func<CancellationToken, Task<ProjectExtraction<TestDoc>>>>
        {
            Project(0, 1, _ => 0),
            Project(1, 1, _ => 0, throwOnDoc: 0), // single-doc project → the fault surfaces unwrapped
            Project(2, 1, _ => 0),
        };

        Task Persist(ProjectContributions project, CancellationToken _)
        {
            persisted.Add(project.OwnerProjectId);
            return Task.CompletedTask;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WithTimeout(ParallelExtractionPipeline.RunAsync(
                projects, Extract, Persist,
                new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = 2 },
                CancellationToken.None)));
        StringAssert.Contains(ex.Message, "worker fault 1:0");
    }

    [TestMethod]
    public async Task WorkerFailure_UnderParallelism_TerminatesAllStages()
    {
        // A multi-document project with one throwing document, extracted under parallelism: the fault
        // must terminate the run (no hang) and carry the marker somewhere in the surfaced exception.
        var projects = new List<Func<CancellationToken, Task<ProjectExtraction<TestDoc>>>>
        {
            Project(0, 8, _ => 5, throwOnDoc: 3),
        };

        var thrown = await Assert.ThrowsAsync<Exception>(
            () => WithTimeout(ParallelExtractionPipeline.RunAsync(
                projects, Extract, (_, __) => Task.CompletedTask,
                new ExtractionParallelismOptions { MaxParallelism = 8, QueueCapacity = 2 },
                CancellationToken.None)));

        var flattened = thrown is AggregateException agg
            ? string.Join(" | ", agg.Flatten().InnerExceptions.Select(e => e.Message))
            : thrown.Message;
        StringAssert.Contains(flattened, "worker fault 0:3");
    }

    [TestMethod]
    public async Task Backpressure_BoundsProducerRunAhead()
    {
        const int projectCount = 24;
        const int capacity = 2;
        var extractStarted = 0;
        var persistStarted = 0;
        var maxLead = 0;

        // One document per project, instant to extract, so the producer would race far ahead of a slow
        // consumer if the bounded channel did not apply backpressure.
        var projects = Enumerable.Range(0, projectCount)
            .Select(p => Project(p, 1, _ => 0,
                onMaterialize: () => Interlocked.Increment(ref extractStarted)))
            .ToList();

        Task Persist(ProjectContributions _, CancellationToken __)
        {
            var started = Interlocked.Increment(ref persistStarted);
            var lead = Volatile.Read(ref extractStarted) - (started - 1);
            InterlockedMax(ref maxLead, lead);
            Thread.Sleep(15);
            return Task.CompletedTask;
        }

        await ParallelExtractionPipeline.RunAsync(
            projects, Extract, Persist,
            new ExtractionParallelismOptions { MaxParallelism = 1, QueueCapacity = capacity },
            CancellationToken.None);

        Assert.AreEqual(projectCount, persistStarted);
        // Outstanding "extracted but not yet persisted" work is bounded by: QueueCapacity buffered in
        // the channel + one item in the consumer's hand + one the producer is blocked mid-write on.
        Assert.IsTrue(maxLead <= capacity + 2,
            $"producer ran {maxLead} projects ahead of the consumer; backpressure should bound it to " +
            $"at most capacity+2 ({capacity + 2}), far below the {projectCount} total projects");
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(4)]
    public async Task SeveralProjectsInFlight_PersistInProjectOrder_WhenLaterProjectsFinishFirst(int projectsInFlight)
    {
        // Issue #270: project 0 is slow and the rest are fast, so they finish first; they are still persisted in order,
        // and their analysis overlapped project 0's (they were materialized before project 0 was persisted).
        const int projectCount = 6;
        var materialized = 0;
        var materializedWhenFirstPersisted = -1;
        var recorded = new List<string>();
        var projects = Enumerable.Range(0, projectCount)
            .Select(p => Project(p, 3, _ => p == 0 ? 120 : 5, onMaterialize: () => Interlocked.Increment(ref materialized)))
            .ToList();

        Task Persist(ProjectContributions project, CancellationToken _)
        {
            if (materializedWhenFirstPersisted < 0)
                materializedWhenFirstPersisted = Volatile.Read(ref materialized);
            recorded.AddRange(project.Contributions.Relationships.Select(r => r.FromKey));
            return Task.CompletedTask;
        }

        await WithTimeout(ParallelExtractionPipeline.RunAsync(projects, Extract, Persist,
            new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = 2, ProjectsInFlight = projectsInFlight },
            CancellationToken.None));

        CollectionAssert.AreEqual(
            (from p in Enumerable.Range(0, projectCount) from d in Enumerable.Range(0, 3) select $"{p}:{d}").ToList(),
            recorded, "projects are persisted in project order, each in document order");
        Assert.IsGreaterThanOrEqualTo(projectsInFlight, materializedWhenFirstPersisted,
            "the projects behind the slow head were analyzed while it ran");
    }

    [TestMethod]
    public async Task ProjectsAnalyzing_NeverExceedProjectsInFlight_AndDocumentsNeverExceedTheWorkers()
    {
        const int projectsInFlight = 3;
        const int workers = 4;
        var activeByProject = new ConcurrentDictionary<int, int>();
        var activeDocuments = 0;
        var maxProjects = 0;
        var maxDocuments = 0;

        DocumentContributionSet Tracked(TestDoc doc, CancellationToken token)
        {
            activeByProject.AddOrUpdate(doc.Project, 1, (_, n) => n + 1);
            InterlockedMax(ref maxProjects, activeByProject.Count(e => e.Value > 0));
            InterlockedMax(ref maxDocuments, Interlocked.Increment(ref activeDocuments));
            try
            {
                return Extract(doc, token);
            }
            finally
            {
                Interlocked.Decrement(ref activeDocuments);
                activeByProject.AddOrUpdate(doc.Project, 0, (_, n) => n - 1);
            }
        }

        var projects = Enumerable.Range(0, 10).Select(p => Project(p, 4, d => 3 + (p * 7 + d * 5) % 11)).ToList();
        await WithTimeout(ParallelExtractionPipeline.RunAsync(projects, Tracked, (_, _) => Task.CompletedTask,
            new ExtractionParallelismOptions { MaxParallelism = workers, QueueCapacity = 2, ProjectsInFlight = projectsInFlight },
            CancellationToken.None));

        Assert.IsLessThanOrEqualTo(projectsInFlight, maxProjects);
        Assert.IsLessThanOrEqualTo(workers, maxDocuments, "the projects in flight share one set of workers");
    }

    [TestMethod]
    public async Task AFaultBehindASlowHead_FailsTheRunWithoutStartingMoreProjects()
    {
        // Project 0 is slow; project 1 faults at once. The run fails, and no further project is started while the
        // head is still running (before #270's fail-fast, freed slots kept admitting projects until the head ended).
        var materialized = 0;
        var projects = Enumerable.Range(0, 12)
            .Select(p => Project(p, 1, _ => p == 0 ? 400 : 0, throwOnDoc: p == 1 ? 0 : -1,
                onMaterialize: () => Interlocked.Increment(ref materialized)))
            .ToList();

        var run = ParallelExtractionPipeline.RunAsync(projects, Extract, (_, _) => Task.CompletedTask,
            new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = 6, ProjectsInFlight = 2 },
            CancellationToken.None);

        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WithTimeout(run));
        StringAssert.Contains(fault.Message, "worker fault 1:0");
        Assert.IsLessThanOrEqualTo(2, Volatile.Read(ref materialized), "only the two projects in flight were started");
    }

    [TestMethod]
    public async Task ManyInstantProjectsInFlight_NeverWaitOnAnEmptySet()
    {
        // The head can finish between the check that it is unfinished and the wait on the unfinished projects;
        // waiting on an empty set threw ("The tasks argument contains no tasks") and failed the run.
        for (var round = 0; round < 30; round++)
        {
            var persisted = 0;
            var projects = Enumerable.Range(0, 200).Select(p => Project(p, 1, _ => 0)).ToList();
            await WithTimeout(ParallelExtractionPipeline.RunAsync(projects, Extract,
                (_, _) => { Interlocked.Increment(ref persisted); return Task.CompletedTask; },
                new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = 2, ProjectsInFlight = 4 },
                CancellationToken.None));
            Assert.AreEqual(200, persisted);
        }
    }

    // ---- RunOrderedAsync: the ordered core the symbol pass uses directly (issue #282) ----------------------------

    // A factory whose synchronous part records its start (in the order the producer invokes it) and whose
    // asynchronous part takes delayMs, as the symbol pass's admission (synchronous) and analysis (after the yield) do.
    private static Func<CancellationToken, Task<int>> Item(
        int index, int delayMs, List<int> started, Action? onAnalyze = null, bool fail = false)
        => token =>
        {
            lock (started)
                started.Add(index);
            return Analyze();

            async Task<int> Analyze()
            {
                await Task.Yield();
                onAnalyze?.Invoke();
                if (delayMs > 0)
                    await Task.Delay(delayMs, token);
                if (fail)
                    throw new InvalidOperationException($"analysis fault {index}");
                return index;
            }
        };

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(2, 1)]
    [DataRow(4, 2)]
    [DataRow(8, 4)]
    public async Task Ordered_StartsAndPersistsInProjectOrder_WhateverFinishesFirst(int projectsInFlight, int queueCapacity)
    {
        // Earlier items are slower, so with several in flight the later ones finish first.
        const int count = 12;
        var started = new List<int>();
        var persisted = new List<int>();
        var produce = Enumerable.Range(0, count).Select(i => Item(i, (count - i) * 3, started)).ToList();

        await WithTimeout(ParallelExtractionPipeline.RunOrderedAsync(produce,
            (item, _) => { persisted.Add(item); return Task.CompletedTask; },
            new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = queueCapacity, ProjectsInFlight = projectsInFlight },
            CancellationToken.None));

        CollectionAssert.AreEqual(Enumerable.Range(0, count).ToList(), started, "factories are invoked in project order");
        CollectionAssert.AreEqual(Enumerable.Range(0, count).ToList(), persisted, "items are persisted in project order");
    }

    [TestMethod]
    public async Task Ordered_PersistingOneProjectOverlapsAnalysisOfTheNext()
    {
        // The persist of item 0 waits until item 1's analysis has begun: it completes only if the producer keeps
        // analyzing while the writer persists, even with one project in flight and a one-slot queue.
        var started = new List<int>();
        var nextAnalyzing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var produce = new List<Func<CancellationToken, Task<int>>>
        {
            Item(0, 0, started),
            Item(1, 0, started, onAnalyze: () => nextAnalyzing.TrySetResult()),
            Item(2, 0, started),
        };
        var persisted = new List<int>();

        async Task Persist(int item, CancellationToken token)
        {
            if (item == 0)
                await nextAnalyzing.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            persisted.Add(item);
        }

        await WithTimeout(ParallelExtractionPipeline.RunOrderedAsync(produce, Persist,
            ExtractionParallelismOptions.Sequential, CancellationToken.None));

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, persisted);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task Ordered_ProjectsInFlightAndHeldResults_StayBounded(int projectsInFlight)
    {
        const int capacity = 2;
        var started = new List<int>();
        var analyzing = 0;
        var maxAnalyzing = 0;
        var persistedCount = 0;
        var maxLead = 0;
        var produce = Enumerable.Range(0, 30).Select(i => (Func<CancellationToken, Task<int>>)(async token =>
        {
            lock (started)
                started.Add(i);
            await Task.Yield();
            InterlockedMax(ref maxAnalyzing, Interlocked.Increment(ref analyzing));
            try
            {
                await Task.Delay(2, token);
                return i;
            }
            finally
            {
                Interlocked.Decrement(ref analyzing);
            }
        })).ToList();

        Task Persist(int item, CancellationToken _)
        {
            int startedCount;
            lock (started)
                startedCount = started.Count;
            InterlockedMax(ref maxLead, startedCount - persistedCount);
            Thread.Sleep(10);
            persistedCount++;
            return Task.CompletedTask;
        }

        await WithTimeout(ParallelExtractionPipeline.RunOrderedAsync(produce, Persist,
            new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = capacity, ProjectsInFlight = projectsInFlight },
            CancellationToken.None));

        Assert.AreEqual(30, persistedCount);
        Assert.IsLessThanOrEqualTo(projectsInFlight, maxAnalyzing, "at most ProjectsInFlight items analyze at once");
        // Started but not yet persisted: held by the producer (in flight + finished behind the head), the channel, and
        // the item in the writer's hand.
        Assert.IsLessThanOrEqualTo(projectsInFlight + 2 * capacity + 2, maxLead,
            "the producer's run-ahead of the writer is bounded");
    }

    [TestMethod]
    public async Task Ordered_AnalysisFault_FailsTheRun_WithoutPersistingLaterProjects()
    {
        var started = new List<int>();
        var persisted = new List<int>();
        // Item 0 (the head) is slow and item 1 behind it faults: the run fails at once, nothing is persisted, and no
        // further project is started.
        var produce = Enumerable.Range(0, 8).Select(i => Item(i, i == 0 ? 400 : 0, started, fail: i == 1)).ToList();

        var run = ParallelExtractionPipeline.RunOrderedAsync(produce,
            (item, _) => { persisted.Add(item); return Task.CompletedTask; },
            new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = 2, ProjectsInFlight = 2 },
            CancellationToken.None);

        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WithTimeout(run));
        StringAssert.Contains(fault.Message, "analysis fault 1");
        Assert.AreEqual(0, persisted.Count, "nothing is persisted: the head was still analyzing when the run failed");
        Assert.IsLessThanOrEqualTo(2, started.Count, "no project is started after the fault");
    }

    [TestMethod]
    public async Task Ordered_SynchronousFactoryFault_FailsTheRun()
    {
        // A factory that throws in its synchronous part (as the symbol pass's admission could) fails the run cleanly.
        var started = new List<int>();
        var produce = new List<Func<CancellationToken, Task<int>>>
        {
            Item(0, 0, started),
            _ => throw new InvalidOperationException("admission fault"),
            Item(2, 0, started),
        };

        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WithTimeout(
            ParallelExtractionPipeline.RunOrderedAsync(produce, (_, _) => Task.CompletedTask,
                ExtractionParallelismOptions.Sequential, CancellationToken.None)));
        Assert.AreEqual("admission fault", fault.Message);
        CollectionAssert.AreEqual(new[] { 0 }, started);
    }

    [TestMethod]
    public async Task Ordered_PersistFault_StopsTheProducer()
    {
        var started = new List<int>();
        var produce = Enumerable.Range(0, 50).Select(i => Item(i, 5, started)).ToList();

        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WithTimeout(
            ParallelExtractionPipeline.RunOrderedAsync(produce,
                (item, _) => item == 1 ? throw new InvalidOperationException("writer fault") : Task.CompletedTask,
                new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = 2, ProjectsInFlight = 2 },
                CancellationToken.None)));
        Assert.AreEqual("writer fault", fault.Message);
        Assert.IsLessThan(50, started.Count, "the producer stops once the writer fails");
    }

    [TestMethod]
    public async Task Ordered_Cancellation_TearsDownWithoutDeadlock()
    {
        using var cts = new CancellationTokenSource();
        var started = new List<int>();
        var persisted = 0;
        var produce = Enumerable.Range(0, 50).Select(i => Item(i, 10, started)).ToList();

        var run = ParallelExtractionPipeline.RunOrderedAsync(produce,
            (_, _) =>
            {
                if (++persisted == 2)
                    cts.Cancel();
                return Task.CompletedTask;
            },
            new ExtractionParallelismOptions { MaxParallelism = 4, QueueCapacity = 1, ProjectsInFlight = 2 },
            cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => WithTimeout(run));
        Assert.IsLessThan(50, persisted);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
    }

    private static async Task WithTimeout(Task task, int timeoutMs = 15_000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
        if (completed != task)
            Assert.Fail("pipeline did not complete within the timeout — a stage deadlocked");
        await task; // surface the pipeline's own exception
    }
}
