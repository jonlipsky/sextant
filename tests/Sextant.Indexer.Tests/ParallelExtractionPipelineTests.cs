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
