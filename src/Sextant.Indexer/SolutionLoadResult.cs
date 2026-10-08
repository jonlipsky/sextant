using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// A project that was declared in the solution but could not be loaded, together with the reason it
/// was skipped. Surfacing these lets the indexer report a PARTIAL index (issue #90) — a single
/// unloadable legacy/non-SDK project no longer aborts the whole solution into an empty database.
/// </summary>
/// <param name="ProjectPath">Absolute path to the project file that could not be loaded.</param>
/// <param name="Reason">A concise human-readable reason (the load exception or workspace diagnostic).</param>
public sealed record SkippedProject(string ProjectPath, string Reason)
{
    public string? TargetFramework { get; init; }

    /// <summary>The bare project file name, for compact diagnostics.</summary>
    public string ProjectName => Path.GetFileName(ProjectPath);
}

/// <summary>A failed evaluation that still exposed documents usable for a partial index.</summary>
public sealed record DegradedProject(string ProjectPath, string? TargetFramework, string Reason);

/// <summary>
/// The outcome of a resilient solution load: the (possibly partial) <see cref="Solution"/> plus any
/// projects that were skipped because they failed to evaluate. A non-empty <see cref="SkippedProjects"/>
/// or <see cref="DegradedProjects"/> list means the index is PARTIAL — usable documents were still indexed rather than the whole run
/// failing (issue #90).
/// </summary>
public sealed record SolutionLoadResult(Solution Solution, IReadOnlyList<SkippedProject> SkippedProjects)
{
    /// <summary>True when failed or unproven evaluation makes the index partial.</summary>
    public bool IsPartial => SkippedProjects.Count > 0 || DegradedProjects.Count > 0 || UnattributedFailureCount > 0;

    /// <summary>Failed project versions whose surviving documents can still be indexed.</summary>
    public IReadOnlyList<DegradedProject> DegradedProjects { get; init; } = [];

    /// <summary>Failure diagnostics whose owner could not be resolved safely.</summary>
    public int UnattributedFailureCount { get; init; }

    /// <summary>
    /// Declared projects (absolute paths, declaration order) the load did not open because its deadline
    /// passed. They are neither loaded nor skipped: nothing is known about whether they would have loaded.
    /// </summary>
    public IReadOnlyList<string> DeferredProjects { get; init; } = [];

    /// <summary>
    /// Per-open load timings (issue #267), in open order: one entry per project opened individually, with the
    /// projects that open added to the workspace. Empty for a whole-solution load, which is one call.
    /// </summary>
    public IReadOnlyList<Sextant.Core.ProjectTiming> LoadTimings { get; init; } = [];
}
