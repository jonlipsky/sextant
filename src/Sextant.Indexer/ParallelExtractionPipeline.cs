using System.Threading.Channels;

namespace Sextant.Indexer;

/// <summary>
/// A project's materialized per-document extraction input in deterministic order. The producer
/// acquires it just-in-time (one project's compilation at a time) so completed projects' compilations
/// and semantic models can be released and peak memory stays bounded.
/// </summary>
public sealed record ProjectExtraction<TDoc>(long OwnerProjectId, string Name, IReadOnlyList<TDoc> Documents);

/// <summary>
/// One project's fully-merged, deterministically-ordered contribution set, ready for the single
/// writer to resolve and persist.
/// </summary>
public sealed record ProjectContributions(long OwnerProjectId, string Name, DocumentContributionSet Contributions);

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
/// Memory is bounded (criteria 2 &amp; 6) because the producer holds only the current project's
/// compilation and the channel buffers at most <see cref="ExtractionParallelismOptions.QueueCapacity"/>
/// Roslyn-free contribution sets. SQLite is never touched concurrently (criterion 3): only the
/// consumer runs <paramref name="persist"/>. Teardown is clean (criterion 4): the producer always
/// completes the channel in a <c>finally</c>, and a consumer failure cancels the linked token so a
/// producer parked on a full channel is released — no stage deadlocks.
/// </remarks>
public static class ParallelExtractionPipeline
{
    /// <param name="projects">Project descriptors in deterministic order. Each is invoked once, in
    /// order, to materialize that project's compilation and documents just before extraction.</param>
    /// <param name="extractDocument">Pure, CPU-bound per-document extraction (run in parallel). Must
    /// not touch SQLite or any shared mutable state that is not itself thread-safe.</param>
    /// <param name="persist">Persists one project's contributions on the single consumer thread, in
    /// project order. The only stage permitted to touch the SQLite writer.</param>
    public static async Task RunAsync<TDoc>(
        IReadOnlyList<Func<CancellationToken, Task<ProjectExtraction<TDoc>>>> projects,
        Func<TDoc, DocumentContributionSet> extractDocument,
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

    private static async Task ProduceAsync<TDoc>(
        IReadOnlyList<Func<CancellationToken, Task<ProjectExtraction<TDoc>>>> projects,
        Func<TDoc, DocumentContributionSet> extractDocument,
        ExtractionParallelismOptions options,
        ChannelWriter<ProjectContributions> writer,
        CancellationToken token)
    {
        try
        {
            foreach (var makeProject in projects)
            {
                token.ThrowIfCancellationRequested();
                var project = await makeProject(token).ConfigureAwait(false);
                var merged = await ExtractProjectAsync(project, extractDocument, options, token)
                    .ConfigureAwait(false);
                await writer.WriteAsync(
                        new ProjectContributions(project.OwnerProjectId, project.Name, merged), token)
                    .ConfigureAwait(false);
            }

            writer.Complete();
        }
        catch (Exception ex)
        {
            // Fault the channel so the consumer stops after draining and rethrows. Unwrap a single
            // aggregated worker exception so the original fault is what surfaces.
            writer.Complete(Unwrap(ex));
        }
    }

    private static async Task<DocumentContributionSet> ExtractProjectAsync<TDoc>(
        ProjectExtraction<TDoc> project,
        Func<TDoc, DocumentContributionSet> extractDocument,
        ExtractionParallelismOptions options,
        CancellationToken token)
    {
        var documents = project.Documents;
        var perDocument = new DocumentContributionSet[documents.Count];

        if (options.MaxParallelism <= 1 || documents.Count <= 1)
        {
            for (var i = 0; i < documents.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                perDocument[i] = extractDocument(documents[i]);
            }
        }
        else
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, documents.Count),
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = token },
                (i, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    perDocument[i] = extractDocument(documents[i]);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        }

        // Fold per-document sets into one project set in ascending document ordinal — this is what
        // makes the merged output independent of the order the parallel workers finished.
        var merged = new DocumentContributionSet();
        foreach (var set in perDocument)
            merged.MergeFrom(set);
        return merged;
    }

    private static Exception Unwrap(Exception ex)
        => ex is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : ex;
}
