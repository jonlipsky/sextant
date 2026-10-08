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
        if (report.Load != null)
            report.Load.SlowestOpens = Relabel(report.Load.SlowestOpens).ToList();
        return report;
    }

    // Project names identify the repository's structure: keep each timing, labelled by position (issue #267).
    private static IEnumerable<Core.ProjectTiming> Relabel(IEnumerable<Core.ProjectTiming> timings) =>
        timings.Select((t, i) => new Core.ProjectTiming
        {
            Phase = t.Phase,
            Project = $"project-{i + 1}",
            WallMs = t.WallMs,
            CompileMs = t.CompileMs,
            AnalyzeMs = t.AnalyzeMs,
            PersistMs = t.PersistMs,
            Rows = t.Rows,
            ProjectsAdded = t.ProjectsAdded
        });

    private static void Scrub(Core.IndexingMetrics? metrics)
    {
        if (metrics == null) return;

        // Diagnostic messages can embed absolute paths, symbol names, and source text.
        // Keep the count for signal; drop the message bodies entirely.
        metrics.WorkspaceDiagnostics.Clear();
        metrics.ReplaceProjects(Relabel(metrics.Projects));

        // FailureReason may contain a path or symbol; replace with a category.
        if (metrics.FailureReason != null)
            metrics.FailureReason = "redacted";
    }
}
