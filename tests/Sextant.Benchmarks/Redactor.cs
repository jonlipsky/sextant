namespace Sextant.Benchmarks;

/// <summary>
/// Removes identifying information from a benchmark report before it leaves the machine.
/// Used for the opt-in private-monorepo path so no absolute paths, repository names,
/// symbol names, or source snippets can be published.
/// </summary>
public static class Redactor
{
    /// <summary>
    /// Structurally strips identifying data: drops machine/commit labels, discards all
    /// captured diagnostic message text (keeping only counts), and forces a generic corpus
    /// name. Because run metrics are aggregate numbers, this leaves nothing that can identify
    /// the source repository while preserving every quantitative measurement.
    /// </summary>
    public static BenchmarkReport Apply(BenchmarkReport report)
    {
        report.Redacted = true;
        report.CorpusName = "external";
        report.Environment.MachineDescription = null;
        report.Environment.GitCommit = null;

        Scrub(report.FullIndex);
        Scrub(report.IncrementalIndex);
        return report;
    }

    private static void Scrub(Core.IndexingMetrics? metrics)
    {
        if (metrics == null) return;

        // Diagnostic messages can embed absolute paths, symbol names, and source text.
        // Keep the count for signal; drop the message bodies entirely.
        metrics.WorkspaceDiagnostics.Clear();

        // FailureReason may contain a path or symbol; replace with a category.
        if (metrics.FailureReason != null)
            metrics.FailureReason = "redacted";
    }
}
