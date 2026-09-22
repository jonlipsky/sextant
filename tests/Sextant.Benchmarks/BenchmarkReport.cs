using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sextant.Core;

namespace Sextant.Benchmarks;

/// <summary>Machine/runtime context recorded so results are comparable across runs.</summary>
public sealed class BenchmarkEnvironment
{
    public required string TimestampUtc { get; init; }
    public required string OsDescription { get; init; }
    public required string RuntimeVersion { get; init; }
    public required string Architecture { get; init; }
    public int ProcessorCount { get; init; }

    /// <summary>Free-form machine label. Cleared when a report is redacted.</summary>
    public string? MachineDescription { get; set; }

    /// <summary>Git commit the run was produced from. Cleared when a report is redacted.</summary>
    public string? GitCommit { get; set; }
}

/// <summary>
/// The reduction goals this initiative is measured against. Recorded in every report so
/// later phases can compare against them or document a revised threshold with rationale.
/// </summary>
public sealed class ReductionTargets
{
    /// <summary>Target initial-indexing speedup versus the recorded baseline.</summary>
    public double RuntimeSpeedupFactor { get; set; } = 5.0;

    /// <summary>Target reduction in peak main-database-plus-WAL disk usage (fraction, 0..1).</summary>
    public double PeakDiskReductionFraction { get; set; } = 0.70;

    public string Rationale { get; set; } =
        "Initiative acceptance target: initial indexing at least 5x faster and peak DB+WAL " +
        "at least 70% lower than the recorded baseline, unless a revised threshold is documented and approved.";
}

/// <summary>A complete benchmark report for one corpus, covering full and (optionally) incremental indexing.</summary>
public sealed class BenchmarkReport
{
    public required string CorpusName { get; set; }
    public required string SchemaVersion { get; init; }
    public bool Redacted { get; set; }
    public required BenchmarkEnvironment Environment { get; init; }
    public ReductionTargets Targets { get; set; } = new();

    /// <summary>Which extractor produced this report — <c>document</c> (Phase 5) or <c>legacy</c>.</summary>
    public string? ExtractorMode { get; set; }

    /// <summary>Effective document-extractor analysis parallelism for this run (resolved from the
    /// requested value against the host), recorded so a parallelism sweep is self-describing.</summary>
    public int? ExtractionParallelism { get; set; }

    public IndexingMetrics? FullIndex { get; set; }
    public IndexingMetrics? IncrementalIndex { get; set; }

    public const string CurrentSchemaVersion = "1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Renders a compact human-readable summary of the report.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Sextant indexing benchmark — {CorpusName}");
        sb.AppendLine();
        if (Redacted)
            sb.AppendLine("> Redacted report: machine, commit, paths, and diagnostic detail are omitted.").AppendLine();

        sb.AppendLine($"- Generated: {Environment.TimestampUtc}");
        sb.AppendLine($"- OS: {Environment.OsDescription} ({Environment.Architecture}, {Environment.ProcessorCount} logical cores)");
        sb.AppendLine($"- Runtime: {Environment.RuntimeVersion}");
        if (Environment.MachineDescription != null) sb.AppendLine($"- Machine: {Environment.MachineDescription}");
        if (Environment.GitCommit != null) sb.AppendLine($"- Commit: {Environment.GitCommit}");
        sb.AppendLine($"- Targets: {Targets.RuntimeSpeedupFactor:0.#}x runtime, {Targets.PeakDiskReductionFraction:P0} peak-disk reduction");
        if (ExtractorMode != null)
            sb.AppendLine($"- Extractor: {ExtractorMode}" +
                          (ExtractionParallelism != null ? $" (parallelism {ExtractionParallelism})" : string.Empty));
        sb.AppendLine();

        AppendRun(sb, "Full index", FullIndex);
        AppendRun(sb, "Incremental index", IncrementalIndex);
        return sb.ToString();
    }

    private static void AppendRun(StringBuilder sb, string title, IndexingMetrics? m)
    {
        if (m == null) return;
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine($"- Status: **{m.Status}**{(m.FailureReason == null ? "" : $" — {m.FailureReason}")}");
        sb.AppendLine($"- Projects: {m.ProjectCount}");
        if (m.ChangedFileCount > 0) sb.AppendLine($"- Changed files: {m.ChangedFileCount}");
        sb.AppendLine($"- Solution load: {FormatMs(m.SolutionLoadMs)}");
        sb.AppendLine($"- Indexing total: {FormatMs(m.TotalDurationMs)}");
        sb.AppendLine($"- Workspace diagnostics: {m.WorkspaceDiagnosticCount}");
        sb.AppendLine();

        if (m.Phases.Count > 0)
        {
            sb.AppendLine("| Phase | Duration | Projects | Status |");
            sb.AppendLine("|---|--:|--:|---|");
            foreach (var p in m.Phases)
                sb.AppendLine($"| {p.Name} | {FormatMs(p.DurationMs)} | {p.ProjectsProcessed} | {p.Status} |");
            sb.AppendLine();
        }

        var r = m.Rows;
        sb.AppendLine("| Rows | Count |");
        sb.AppendLine("|---|--:|");
        sb.AppendLine($"| projects | {r.Projects:N0} |");
        sb.AppendLine($"| symbols | {r.Symbols:N0} |");
        sb.AppendLine($"| references | {r.References:N0} |");
        sb.AppendLine($"| distinct reference occurrences | {r.DistinctReferenceOccurrences:N0} |");
        sb.AppendLine($"| duplicate reference rows | {r.DuplicateReferenceRows:N0} ({r.DuplicateReferenceRatio:P1}) |");
        sb.AppendLine($"| relationships | {r.Relationships:N0} |");
        sb.AppendLine($"| call graph edges | {r.CallGraphEdges:N0} |");
        sb.AppendLine($"| comments | {r.Comments:N0} |");
        sb.AppendLine();

        var s = m.Storage;
        sb.AppendLine("| Storage / memory | Bytes |");
        sb.AppendLine("|---|--:|");
        sb.AppendLine($"| final database | {FormatBytes(s.FinalDbBytes)} |");
        sb.AppendLine($"| final WAL | {FormatBytes(s.FinalWalBytes)} |");
        sb.AppendLine($"| final SHM | {FormatBytes(s.FinalShmBytes)} |");
        sb.AppendLine($"| peak database + WAL | {FormatBytes(s.PeakDbPlusWalBytes)} |");
        sb.AppendLine($"| peak WAL | {FormatBytes(s.PeakWalBytes)} |");
        sb.AppendLine($"| peak SHM | {FormatBytes(s.PeakShmBytes)} |");
        sb.AppendLine($"| peak staged artifacts | {FormatBytes(s.PeakStagedArtifactBytes)} |");
        sb.AppendLine($"| peak managed memory | {FormatBytes(m.Memory.PeakManagedBytes)} |");
        sb.AppendLine($"| peak working set | {FormatBytes(m.Memory.PeakWorkingSetBytes)} |");
        sb.AppendLine();
    }

    private static string FormatMs(long ms) =>
        ms >= 1000 ? $"{ms / 1000.0:0.00} s" : $"{ms} ms";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
