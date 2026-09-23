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
    /// <summary>The bare project file name, for compact diagnostics.</summary>
    public string ProjectName => Path.GetFileName(ProjectPath);
}

/// <summary>
/// The outcome of a resilient solution load: the (possibly partial) <see cref="Solution"/> plus any
/// projects that were skipped because they failed to evaluate. A non-empty <see cref="SkippedProjects"/>
/// means the index is PARTIAL — the loadable projects were still indexed rather than the whole run
/// failing (issue #90).
/// </summary>
public sealed record SolutionLoadResult(Solution Solution, IReadOnlyList<SkippedProject> SkippedProjects)
{
    /// <summary>True when at least one declared project failed to load.</summary>
    public bool IsPartial => SkippedProjects.Count > 0;
}
