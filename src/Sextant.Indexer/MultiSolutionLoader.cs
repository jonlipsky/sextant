using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// Per-solution coverage for a multi-solution load (issue #109): how many projects the solution declared,
/// how many were indexed, and which were skipped-with-reason. Lets the service report PARTIAL coverage
/// instead of presenting a subset as complete.
/// </summary>
/// <param name="SolutionPath">The solution this coverage describes.</param>
/// <param name="DeclaredProjectCount">Distinct recognized projects the solution declared.</param>
/// <param name="LoadedProjectCount">Declared projects that loaded (declared minus skipped).</param>
/// <param name="SkippedProjects">Declared projects that failed to load, with reasons.</param>
public sealed record SolutionCoverage(
    string SolutionPath,
    int DeclaredProjectCount,
    int LoadedProjectCount,
    IReadOnlyList<SkippedProject> SkippedProjects);

/// <summary>
/// The projects one selected solution DECLARES (absolute paths, declaration order). A multi-solution load
/// opens projects individually, so the combined <see cref="Solution"/> has no solution file of its own;
/// the orchestrator uses this to record each selected solution's <c>solution → project</c> mapping (the
/// <c>solution:</c> query scope) exactly as the single-solution path does from <see cref="Solution.FilePath"/>.
/// </summary>
/// <param name="SolutionPath">The absolute solution path.</param>
/// <param name="DeclaredProjects">The absolute paths of the projects the solution declares.</param>
public sealed record SolutionMembership(string SolutionPath, IReadOnlyList<string> DeclaredProjects);

/// <summary>
/// The outcome of loading one or more solutions into a single workspace: the combined (possibly partial)
/// <see cref="Solution"/> spanning the UNION of all solutions' projects (de-duplicated by project
/// identity), the union of skipped projects, and per-solution coverage.
/// </summary>
public sealed record MultiSolutionLoadResult(
    Solution Solution,
    IReadOnlyList<SkippedProject> SkippedProjects,
    IReadOnlyList<SolutionCoverage> Solutions)
{
    /// <summary>True when at least one declared project across the selected solutions failed to load.</summary>
    public bool IsPartial => SkippedProjects.Count > 0;

    /// <summary>
    /// The distinct (full-path) project files declared across ALL selected solutions, in first-appearance
    /// order — the denominator for checkout coverage (issue #119).
    /// </summary>
    public IReadOnlyList<string> DeclaredProjects { get; init; } = [];

    /// <summary>
    /// Each selected solution's declared projects, in selection order. Passed to
    /// <see cref="IndexOrchestrator.IndexSolutionAsync"/> so a multi-solution snapshot keeps its
    /// <c>solution → project</c> mappings (issue #124).
    /// </summary>
    public IReadOnlyList<SolutionMembership> Membership { get; init; } = [];
}

/// <summary>
/// Loads a deterministic set of solutions for ONE checkout into a single Roslyn workspace so a monorepo
/// can be indexed WHOLE (issue #109). The projects declared across all selected solutions are unioned and
/// DE-DUPLICATED by project file path (a project shared by several solution heads is loaded once), which —
/// because one checkout's absolute project path is 1:1 with its project identity
/// <c>(git-remote, repo-relative path)</c> — yields the union-of-projects-by-identity the acceptance
/// criteria require. A single selected solution (a one-solution checkout, or a one-entry config) preserves
/// the existing fast whole-solution load path byte-for-byte. Several solutions — an explicit list, or the
/// no-config default union of every discovered solution (issue #124) — take the per-project union path,
/// which opens each project individually (no solution context, so <c>$(SolutionDir)</c> is not set by a
/// solution) to isolate per-project load faults.
/// </summary>
public static class MultiSolutionLoader
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The comparer the union uses to de-duplicate project files (shared with membership mapping).</summary>
    internal static StringComparer ProjectPathComparer => PathComparer;

    /// <summary>
    /// Loads <paramref name="solutionPaths"/> (already selected + deterministically ordered by
    /// <see cref="SolutionSelector"/>) into one workspace. A single solution takes the standard resilient
    /// whole-solution path; multiple solutions load the union of their declared projects individually with
    /// per-project fault isolation.
    /// </summary>
    public static async Task<MultiSolutionLoadResult> LoadAsync(
        IReadOnlyList<string> solutionPaths,
        Action<string>? onDiagnostic = null,
        CancellationToken cancellationToken = default)
    {
        if (solutionPaths.Count == 0)
            throw new ArgumentException("At least one solution path is required.", nameof(solutionPaths));

        // The DECLARED-project set of each solution is read STATICALLY (SolutionProjectEnumerator), never by
        // MSBuild-evaluating the solution: the whole point of #109 is that the selected solutions frequently
        // cannot be evaluated on this worker's platform (iOS/Android/Mac/WPF/Unity heads on Linux), so a
        // Roslyn OpenSolutionAsync cross-check is not available as an authority here. The enumerator is
        // therefore the coverage authority, and it is deliberately CONSERVATIVE: any parse/I/O failure (even
        // mid-file) discards the partial read and yields an EMPTY declared list — it never emits a non-empty
        // strict subset. An empty declared list surfaces as DeclaredProjectCount == 0 downstream, which the
        // worker forces to Partial (never Complete), so a solution the enumerator could not fully read is
        // reported as a coverage gap rather than silently under-covered.
        var perSolutionDeclared = solutionPaths
            .Select(sln => (Solution: sln, Declared: SolutionProjectEnumerator.Enumerate(sln)))
            .ToList();

        SolutionLoadResult loaded;
        var union = ComputeUnion(perSolutionDeclared);
        if (solutionPaths.Count == 1)
        {
            // Preserve the byte-identical single-solution fast path (OpenSolutionAsync) when exactly ONE
            // solution is selected (a one-solution checkout or a one-entry config), so that case — and its
            // determinism/parity test coverage — is unchanged. A multi-solution no-config checkout selects the
            // union (#124) and takes the per-project branch below.
            loaded = await SolutionLoader.LoadSolutionResilientlyAsync(
                solutionPaths[0], onDiagnostic, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            loaded = await SolutionLoader.LoadProjectsResilientlyAsync(
                union, onDiagnostic, cancellationToken).ConfigureAwait(false);
        }

        var coverage = BuildCoverage(perSolutionDeclared, loaded.SkippedProjects);
        return new MultiSolutionLoadResult(loaded.Solution, loaded.SkippedProjects, coverage)
        {
            DeclaredProjects = union,
            Membership = perSolutionDeclared
                .Select(p => new SolutionMembership(Path.GetFullPath(p.Solution), p.Declared))
                .ToList()
        };
    }

    /// <summary>
    /// The union of the declared project paths across all solutions, de-duplicated by full path and
    /// preserving first-appearance order (solution order, then declaration order within a solution) so the
    /// combined workspace's project order — and therefore the index — is deterministic.
    /// </summary>
    internal static List<string> ComputeUnion(
        IReadOnlyList<(string Solution, IReadOnlyList<string> Declared)> perSolutionDeclared)
    {
        var union = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var (_, declared) in perSolutionDeclared)
            foreach (var project in declared)
            {
                var full = Path.GetFullPath(project);
                if (seen.Add(full))
                    union.Add(full);
            }
        return union;
    }

    /// <summary>
    /// Attributes the union's skipped projects back to each solution that declared them, producing
    /// per-solution coverage. A project skipped once is reported skipped for every solution that declared
    /// it (each head that expected it has reduced coverage).
    /// </summary>
    internal static IReadOnlyList<SolutionCoverage> BuildCoverage(
        IReadOnlyList<(string Solution, IReadOnlyList<string> Declared)> perSolutionDeclared,
        IReadOnlyList<SkippedProject> skippedProjects)
    {
        var skippedByPath = new Dictionary<string, SkippedProject>(PathComparer);
        foreach (var skip in skippedProjects)
            skippedByPath[Path.GetFullPath(skip.ProjectPath)] = skip;

        var result = new List<SolutionCoverage>(perSolutionDeclared.Count);
        foreach (var (solution, declared) in perSolutionDeclared)
        {
            var declaredFull = declared
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .ToList();

            var solutionSkipped = declaredFull
                .Where(skippedByPath.ContainsKey)
                .Select(p => skippedByPath[p])
                .ToList();

            result.Add(new SolutionCoverage(
                solution,
                declaredFull.Count,
                declaredFull.Count - solutionSkipped.Count,
                solutionSkipped));
        }
        return result;
    }
}
