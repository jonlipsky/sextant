namespace Sextant.Indexer;

/// <summary>
/// Structured progress report from the indexing pipeline.
/// </summary>
public sealed class IndexingProgress
{
    /// <summary>Current phase of indexing (e.g., "registering_projects", "extracting_symbols").</summary>
    public required string Phase { get; init; }

    /// <summary>Human-readable description of what's happening.</summary>
    public required string Description { get; init; }

    /// <summary>Name of the project currently being processed (null between projects).</summary>
    public string? CurrentProject { get; init; }

    /// <summary>1-based index of the current project being processed.</summary>
    public int ProjectIndex { get; init; }

    /// <summary>Total number of projects in the solution.</summary>
    public int ProjectCount { get; init; }

    /// <summary>Number of items processed in the current phase (files, symbols, etc.).</summary>
    public int ItemsProcessed { get; init; }

    /// <summary>Total items expected in the current phase (0 if unknown).</summary>
    public int ItemsTotal { get; init; }
}
