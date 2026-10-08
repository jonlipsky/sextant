namespace Sextant.Core;

/// <summary>
/// The names of indexing phases (issue #267): the indexer's own (<see cref="PhaseMetric.Name"/>), the service worker's
/// steps around it, and the order they run in. One source for the orchestrator, the worker, the metrics that aggregate
/// them per phase, and the benchmark harness.
/// </summary>
public static class IndexPhaseNames
{
    // The service worker's steps, before indexing.
    public const string Checkout = "checkout";
    public const string SdkPin = "sdk_pin";
    public const string Restore = "restore";
    public const string Load = "load";
    public const string CoverageScan = "coverage_scan";

    // The indexer's phases.
    public const string RegisteringProjects = "registering_projects";
    public const string ExtractingSymbols = "extracting_symbols";
    public const string ExtractingOccurrences = "extracting_occurrences";
    public const string ExtractingRelationships = "extracting_relationships";
    public const string ExtractingReferences = "extracting_references";
    public const string ExtractingComments = "extracting_comments";
    public const string ExtractingCallGraph = "extracting_call_graph";
    public const string RecordingDependencies = "recording_dependencies";
    public const string CapturingApiSurface = "capturing_api_surface";

    /// <summary>The indexer's time outside its phases: setup, the publish transaction, post-publish maintenance.</summary>
    public const string IndexerOther = "indexer_other";

    /// <summary>Every known phase, in the order a job runs them.</summary>
    public static IReadOnlyList<string> ExecutionOrder { get; } =
    [
        Checkout, SdkPin, Restore, Load, CoverageScan,
        RegisteringProjects, ExtractingSymbols, ExtractingOccurrences, ExtractingRelationships, ExtractingReferences,
        ExtractingComments, ExtractingCallGraph, RecordingDependencies, CapturingApiSurface, IndexerOther
    ];

    /// <summary>The position of <paramref name="name"/> in <see cref="ExecutionOrder"/>, or -1 for an unknown phase.</summary>
    public static int OrderOf(string name)
    {
        for (var i = 0; i < ExecutionOrder.Count; i++)
        {
            if (string.Equals(ExecutionOrder[i], name, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
