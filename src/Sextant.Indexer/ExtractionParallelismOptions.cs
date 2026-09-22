using Sextant.Core;

namespace Sextant.Indexer;

/// <summary>
/// Resolved tunables for the document-oriented extractor's bounded parallel pipeline
/// (<see cref="ParallelExtractionPipeline"/>). Analysis workers extract per-document contributions in
/// parallel up to <see cref="MaxParallelism"/>; the completed per-project contribution sets flow to
/// the single SQLite writer through a bounded channel of capacity <see cref="QueueCapacity"/>, which
/// bounds outstanding contribution memory and applies backpressure when persistence lags extraction.
/// </summary>
public sealed record ExtractionParallelismOptions
{
    /// <summary>
    /// Upper bound on how many CPUs the pipeline uses when its configured value is left at auto,
    /// deliberately conservative because more workers stop helping once MSBuild/restore or the single
    /// writer is the bottleneck (and to keep peak Roslyn memory bounded). Validated by the benchmark
    /// parallelism sweep.
    /// </summary>
    public const int DefaultParallelismCap = 8;

    /// <summary>Maximum degree of parallelism for per-document analysis. Always >= 1.</summary>
    public int MaxParallelism { get; init; } = 1;

    /// <summary>
    /// Bounded channel capacity (in per-project contribution sets) between the parallel producers and
    /// the single writer. Always >= 1.
    /// </summary>
    public int QueueCapacity { get; init; } = 1;

    /// <summary>Single-threaded options (no parallel analysis), used as a safe fallback and by tests.</summary>
    public static ExtractionParallelismOptions Sequential { get; } = new() { MaxParallelism = 1, QueueCapacity = 1 };

    /// <summary>Auto-resolved options for the current machine.</summary>
    public static ExtractionParallelismOptions Default { get; } = Resolve(0, 0);

    /// <summary>
    /// Resolves raw (possibly 0/auto) configuration values against the current machine. A
    /// non-positive <paramref name="configuredParallelism"/> auto-resolves to
    /// <c>min(ProcessorCount, <see cref="DefaultParallelismCap"/>)</c>; a positive value is honored
    /// but still clamped to the processor count so a misconfiguration cannot oversubscribe the CPU. A
    /// non-positive <paramref name="configuredQueueCapacity"/> auto-resolves to a small multiple of
    /// the parallelism so the writer stays fed without buffering an unbounded backlog.
    /// </summary>
    public static ExtractionParallelismOptions Resolve(int configuredParallelism, int configuredQueueCapacity)
    {
        var processors = Math.Max(1, Environment.ProcessorCount);
        var parallelism = configuredParallelism > 0
            ? Math.Min(configuredParallelism, processors)
            : Math.Min(processors, DefaultParallelismCap);

        var capacity = configuredQueueCapacity > 0
            ? configuredQueueCapacity
            : Math.Max(2, parallelism);

        return new ExtractionParallelismOptions { MaxParallelism = parallelism, QueueCapacity = capacity };
    }

    /// <summary>Builds resolved options from the repo <see cref="SextantConfiguration"/>.</summary>
    public static ExtractionParallelismOptions FromConfiguration(SextantConfiguration config)
        => Resolve(config.MaxParallelism, config.ExtractionQueueCapacity);
}
