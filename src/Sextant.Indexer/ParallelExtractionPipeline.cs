using System.Threading.Channels;

namespace Sextant.Indexer;

/// <summary>
/// A project's materialized per-document extraction input in deterministic order. The producer
/// acquires it just-in-time (up to <see cref="ExtractionParallelismOptions.ProjectsInFlight"/> projects at
/// once) so completed projects' semantic models can be released and peak memory stays bounded.
/// </summary>
public sealed record ProjectExtraction<TDoc>(long OwnerProjectId, string Name, IReadOnlyList<TDoc> Documents);

/// <summary>
/// One project's fully-merged, deterministically-ordered contribution set, ready for the single
/// writer to resolve and persist.
/// </summary>
public sealed record ProjectContributions(long OwnerProjectId, string Name, DocumentContributionSet Contributions)
{
    /// <summary>Time the producer spent materializing the project (its compilation and documents), in milliseconds.</summary>
    public long CompileMs { get; init; }

    /// <summary>Time the producer spent analyzing and merging the project's documents, in milliseconds.</summary>
    public long AnalyzeMs { get; init; }
}

/// <summary>
/// A bounded producer → single-consumer pipeline for the document-oriented extractor. Analysis
/// workers extract per-document contributions in parallel (up to
/// <see cref="ExtractionParallelismOptions.MaxParallelism"/>); each project's per-document sets are
/// merged by ascending document ordinal into one deterministic set and handed to a bounded channel;
/// a single consumer drains the channel and persists (the only stage that touches SQLite). Projects
/// are produced in the caller's order, so persistence order — and therefore the canonical output — is
/// independent of task completion order (acceptance criterion 1).
/// </summary>
/// <remarks>
/// Memory is bounded (criteria 2 &amp; 6): the producer analyzes at most
/// <see cref="ExtractionParallelismOptions.ProjectsInFlight"/> projects at once and holds at most that many plus
/// <see cref="ExtractionParallelismOptions.QueueCapacity"/> of their Roslyn-free contribution sets, and the channel
/// buffers at most <see cref="ExtractionParallelismOptions.QueueCapacity"/> more. SQLite is never touched concurrently (criterion 3): only the
/// consumer runs <paramref name="persist"/>. Teardown is clean (criterion 4): the producer always
/// completes the channel in a <c>finally</c>, and a consumer failure cancels the linked token so a
/// producer parked on a full channel is released — no stage deadlocks.
/// </remarks>
public static class ParallelExtractionPipeline
{
    /// <param name="projects">Project descriptors in deterministic order. Each is invoked once, to materialize that
    /// project's compilation and documents just before extraction; up to
    /// <see cref="ExtractionParallelismOptions.ProjectsInFlight"/> run at once (issue #270), so a descriptor must be
    /// safe to run concurrently with the others.</param>
    /// <param name="extractDocument">Pure, CPU-bound per-document extraction (run in parallel). Must
    /// not touch SQLite or any shared mutable state that is not itself thread-safe. Receives the
    /// pipeline's linked token so in-flight analysis observes cancellation caused by a consumer/worker
    /// fault, not only external cancellation.</param>
    /// <param name="persist">Persists one project's contributions on the single consumer thread, in
    /// project order. The only stage permitted to touch the SQLite writer. Receives the linked token
    /// and should honour it so a large project's persist loop tears down promptly on cancellation.</param>
    public static async Task RunAsync<TDoc>(
        IReadOnlyList<Func<CancellationToken, Task<ProjectExtraction<TDoc>>>> projects,
        Func<TDoc, CancellationToken, DocumentContributionSet> extractDocument,
        Func<ProjectContributions, CancellationToken, Task> persist,
        ExtractionParallelismOptions options,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linkedCts.Token;

        var channel = Channel.CreateBounded<ProjectContributions>(
            new BoundedChannelOptions(options.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        var producer = Task.Run(
            () => ProduceAsync(projects, extractDocument, options, channel.Writer, token), token);

        try
        {
            // Drain in enqueue order (== project order) and persist on this single thread.
            await foreach (var contributions in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                await persist(contributions, token).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation or a persist failure observed here: release a producer that may be parked
            // on a full channel so the pipeline cannot deadlock, then rethrow after it unwinds.
            linkedCts.Cancel();
            throw;
        }
        finally
        {
            // Always observe the producer so its exceptions surface and no task is orphaned. The
            // producer completes the channel in its own finally, so this never hangs.
            await producer.ConfigureAwait(false);
        }
    }

    // Issue #270: up to ProjectsInFlight projects are materialized and analyzed at once, their documents sharing one
    // MaxParallelism-wide set of workers, so a project's last (often largest) documents no longer leave the other
    // workers idle. Each project's documents still merge by ascending ordinal, and finished projects are written in
    // project order, so the persisted output is unchanged. ProjectsInFlight = 1 is the one-project-at-a-time pipeline.
    // A finished project waiting behind a slower head does not hold an analysis slot, so the workers move on. The
    // producer holds at most ProjectsInFlight + QueueCapacity projects (analyzing or finished), and the channel up to
    // QueueCapacity more: that bounds the contribution sets in memory at about ProjectsInFlight + 2 x QueueCapacity + 2
    // (one being written, one being persisted).
    private static async Task ProduceAsync<TDoc>(
        IReadOnlyList<Func<CancellationToken, Task<ProjectExtraction<TDoc>>>> projects,
        Func<TDoc, CancellationToken, DocumentContributionSet> extractDocument,
        ExtractionParallelismOptions options,
        ChannelWriter<ProjectContributions> writer,
        CancellationToken token)
    {
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producerToken = producerCts.Token;
        using var workers = new SemaphoreSlim(Math.Max(1, options.MaxParallelism));
        var inFlight = new Queue<Task<ProjectContributions>>();
        try
        {
            var next = 0;
            var analyzing = Math.Max(1, options.ProjectsInFlight);
            var held = analyzing + Math.Max(1, options.QueueCapacity);
            while (next < projects.Count || inFlight.Count > 0)
            {
                // Write finished projects at the head, in project order, before starting another one: with one
                // project in flight this is exactly the one-project-at-a-time pipeline.
                if (inFlight.Count > 0 && inFlight.Peek().IsCompleted)
                {
                    var contributions = await inFlight.Dequeue().ConfigureAwait(false);
                    await writer.WriteAsync(contributions, producerToken).ConfigureAwait(false);
                    continue;
                }

                // A project behind the head that failed fails the run now, not when it reaches the head.
                if (inFlight.FirstOrDefault(task => task.IsFaulted || task.IsCanceled) is { } failed)
                    await failed.ConfigureAwait(false);

                // A free analysis slot (finished projects waiting behind the head do not hold one) takes the next
                // project, within the bound on projects held.
                if (next < projects.Count && inFlight.Count < held && inFlight.Count(task => !task.IsCompleted) < analyzing)
                {
                    inFlight.Enqueue(ProduceProjectAsync(projects[next++], extractDocument, options, workers, producerToken));
                    continue;
                }

                // Wait for a project to finish, among the unfinished ones only (a finished project would wake this at
                // once, every time). The head was unfinished a moment ago but may have just finished: then none are
                // left to wait for, and the next pass writes it.
                var unfinished = inFlight.Where(task => !task.IsCompleted).ToList();
                if (unfinished.Count > 0)
                    await Task.WhenAny(unfinished).ConfigureAwait(false);
            }

            writer.Complete();
        }
        catch (Exception ex)
        {
            // Stop the projects still in flight and observe them, so none is orphaned, then fault the channel so the
            // consumer stops after draining and rethrows. Unwrap a single aggregated worker exception so the original
            // fault is what surfaces.
            await producerCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(inFlight).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Their cancellation (or a second fault) adds nothing to the first.
            }
            writer.Complete(Unwrap(ex));
        }
    }

    private static async Task<ProjectContributions> ProduceProjectAsync<TDoc>(
        Func<CancellationToken, Task<ProjectExtraction<TDoc>>> makeProject,
        Func<TDoc, CancellationToken, DocumentContributionSet> extractDocument,
        ExtractionParallelismOptions options,
        SemaphoreSlim workers,
        CancellationToken token)
    {
        // Off the producer's thread, so the projects in flight materialize and analyze concurrently.
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var project = await makeProject(token).ConfigureAwait(false);
        var compileMs = watch.ElapsedMilliseconds;
        var merged = await ExtractProjectAsync(project, extractDocument, options, workers, token).ConfigureAwait(false);
        return new ProjectContributions(project.OwnerProjectId, project.Name, merged)
        {
            CompileMs = compileMs,
            AnalyzeMs = watch.ElapsedMilliseconds - compileMs
        };
    }

    private static async Task<DocumentContributionSet> ExtractProjectAsync<TDoc>(
        ProjectExtraction<TDoc> project,
        Func<TDoc, CancellationToken, DocumentContributionSet> extractDocument,
        ExtractionParallelismOptions options,
        SemaphoreSlim workers,
        CancellationToken token)
    {
        var documents = project.Documents;
        var perDocument = new DocumentContributionSet?[documents.Count];

        if (options.MaxParallelism <= 1 || documents.Count <= 1)
        {
            for (var i = 0; i < documents.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                await workers.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    perDocument[i] = extractDocument(documents[i], token);
                }
                finally
                {
                    workers.Release();
                }
            }
        }
        else
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, documents.Count),
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = token },
                async (i, ct) =>
                {
                    // One worker slot across every project in flight.
                    await workers.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        perDocument[i] = extractDocument(documents[i], ct);
                    }
                    finally
                    {
                        workers.Release();
                    }
                }).ConfigureAwait(false);
        }

        // Fold per-document sets into one project set in ascending document ordinal — this is what
        // makes the merged output independent of the order the parallel workers finished. Release each
        // source set immediately after folding so only the growing merged set (plus not-yet-folded
        // sets) is retained: transient peak stays ~one project's payload, not two copies of it.
        var merged = new DocumentContributionSet();
        for (var i = 0; i < perDocument.Length; i++)
        {
            merged.MergeFrom(perDocument[i]!);
            perDocument[i] = null;
        }
        return merged;
    }

    private static Exception Unwrap(Exception ex)
        => ex is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : ex;
}
