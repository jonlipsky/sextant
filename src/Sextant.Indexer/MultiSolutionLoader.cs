using Microsoft.CodeAnalysis;
using Sextant.Core;

namespace Sextant.Indexer;

/// <summary>
/// Per-solution coverage for a multi-solution load (issue #109): how many projects the solution declared,
/// how many were indexed, and which were skipped-with-reason. Lets the service report PARTIAL coverage
/// instead of presenting a subset as complete.
/// </summary>
/// <param name="SolutionPath">The solution this coverage describes.</param>
/// <param name="DeclaredProjectCount">Distinct recognized projects the solution declared.</param>
/// <param name="LoadedProjectCount">Declared project files with a usable variant, including degraded documents.</param>
/// <param name="SkippedProjects">Declared projects that failed to load, with reasons.</param>
public sealed record SolutionCoverage(
    string SolutionPath,
    int DeclaredProjectCount,
    int LoadedProjectCount,
    IReadOnlyList<SkippedProject> SkippedProjects)
{
    /// <summary>Declared projects the load did not open because its deadline passed.</summary>
    public int DeferredProjectCount { get; init; }

    public bool IsReadable { get; init; } = true;
}

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
    /// <summary>True when a load or solution-read failure prevents proving healthy coverage.</summary>
    public bool IsPartial => SkippedProjects.Count > 0 || DegradedProjects.Count > 0
        || UnattributedFailureCount > 0 || Solutions.Any(s => !s.IsReadable);

    public IReadOnlyList<DegradedProject> DegradedProjects { get; init; } = [];

    public int UnattributedFailureCount { get; init; }

    /// <summary>
    /// How the projects were loaded (issue #267): <see cref="SolutionLoadModes.Solution"/> (one whole-solution open)
    /// or <see cref="SolutionLoadModes.Union"/> (each project of the multi-solution union opened individually).
    /// </summary>
    public string LoadMode { get; init; } = SolutionLoadModes.Solution;

    /// <summary>Wall-clock time of the whole load, in milliseconds (issue #267).</summary>
    public long LoadMs { get; init; }

    /// <summary>Per-open timings of a union load, in open order (issue #267); empty for a whole-solution load.</summary>
    public IReadOnlyList<Sextant.Core.ProjectTiming> LoadTimings { get; init; } = [];

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

    /// <summary>
    /// Declared projects (absolute paths) the union load did not open because its deadline passed. They are
    /// neither loaded nor skipped. Always empty for a single selected solution, whose whole-solution load has
    /// no per-project step to stop at.
    /// </summary>
    public IReadOnlyList<string> DeferredProjects { get; init; } = [];
}

/// <summary>
/// Loads a deterministic set of solutions for ONE checkout into a single Roslyn workspace so a monorepo
/// can be indexed WHOLE (issue #109). The projects declared across all selected solutions are unioned and
/// DE-DUPLICATED by project file path (a project shared by several solution heads is loaded once), which —
/// because one checkout's absolute project path is 1:1 with its project identity
/// <c>(git-remote, repo-relative path)</c> — yields the union-of-projects-by-identity the acceptance
/// criteria require. A single selected solution (a one-solution checkout, or a one-entry config) preserves
/// the existing fast whole-solution load path byte-for-byte. Several solutions — an explicit list, or the
/// no-config default union of every discovered solution (issue #124) — load their union with one open of a
/// generated solution (issue #268), falling back to opening each project individually to isolate a load fault
/// or stop at the deadline. The generated solution sits in job scratch, outside the checkout; a project whose
/// evaluation reads <c>$(SolutionDir)</c> is opened individually, as before, so neither path sets it for one.
/// </summary>
public static class MultiSolutionLoader
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The comparer the union uses to de-duplicate project files (shared with membership mapping).</summary>
    internal static StringComparer ProjectPathComparer => PathComparer;

    /// <summary>
    /// Loads <paramref name="solutionPaths"/> (already selected + deterministically ordered by
    /// <see cref="SolutionSelector"/>) into one workspace. A single solution takes the standard resilient
    /// whole-solution path; multiple solutions load the union of their declared projects with one open of a
    /// generated solution for every project it evaluates as faithfully (<see cref="UnionSolutionWriter.Partition"/>),
    /// then the rest project by project, with per-project fault isolation. When <paramref name="deadline"/> passes during a
    /// project-by-project union load, the projects not yet opened are returned in
    /// <see cref="MultiSolutionLoadResult.DeferredProjects"/>; <paramref name="onProgress"/> receives a line per open.
    /// </summary>
    public static async Task<MultiSolutionLoadResult> LoadAsync(
        IReadOnlyList<string> solutionPaths,
        Action<string>? onDiagnostic = null,
        IndexDeadline? deadline = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        string? scratchDirectory = null)
    {
        if (solutionPaths.Count == 0)
            throw new ArgumentException("At least one solution path is required.", nameof(solutionPaths));

        var reads = solutionPaths.Select(SolutionProjectEnumerator.Read).ToList();
        var perSolutionDeclared = solutionPaths
            .Select((sln, i) => (Solution: sln, Declared: reads[i].Projects))
            .ToList();

        SolutionLoadResult loaded;
        var union = ComputeUnion(perSolutionDeclared);
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        if (solutionPaths.Count == 1)
        {
            // Preserve the single-solution fast path (OpenSolutionAsync) when exactly ONE
            // solution is selected (a one-solution checkout or a one-entry config), so that case — and its
            // determinism/parity test coverage — is unchanged. A multi-solution no-config checkout selects the
            // union (#124) and takes the per-project branch below.
            loaded = await SolutionLoader.LoadSolutionResilientlyAsync(
                solutionPaths[0], onDiagnostic, cancellationToken).ConfigureAwait(false);
        }
        else if (UnionSolutionWriter.Partition(solutionPaths, union) is var partition && partition.CanLoadInOnePass)
        {
            if (partition.Individually.Count > 0)
                onDiagnostic?.Invoke(
                    $"{partition.Individually.Count} of the union's {union.Count} project(s) are opened individually, " +
                    $"outside the one-pass load: {partition.Reason}.");
            // The generated solution goes in job scratch, never the checkout; without scratch, in a temporary directory.
            var generatedDirectory = scratchDirectory is null
                ? Path.Combine(Path.GetTempPath(), $"sextant-union-{Guid.NewGuid():N}")
                : Path.Combine(scratchDirectory, $"union-solution-{Guid.NewGuid():N}");
            try
            {
                loaded = await SolutionLoader.LoadUnionInOnePassAsync(
                    union, partition, generatedDirectory, onDiagnostic, deadline, onProgress, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                TryDeleteDirectory(generatedDirectory);
            }
        }
        else
        {
            onDiagnostic?.Invoke($"Loading the solution union project by project: {partition.Reason}.");
            loaded = await SolutionLoader.LoadProjectsResilientlyAsync(
                union, onDiagnostic, deadline, onProgress, cancellationToken).ConfigureAwait(false);
        }

        var coverage = BuildCoverage(perSolutionDeclared, loaded.SkippedProjects, loaded.DeferredProjects, loaded.Solution)
            .Select((c, i) => c with { IsReadable = reads[i].IsReadable }).ToList();
        return new MultiSolutionLoadResult(loaded.Solution, loaded.SkippedProjects, coverage)
        {
            LoadMode = solutionPaths.Count == 1 ? SolutionLoadModes.Solution
                : loaded.LoadedProjectByProject ? SolutionLoadModes.UnionPerProject
                : SolutionLoadModes.Union,
            LoadMs = loadWatch.ElapsedMilliseconds,
            LoadTimings = loaded.LoadTimings,
            DeclaredProjects = union,
            Membership = perSolutionDeclared
                .Select(p => new SolutionMembership(Path.GetFullPath(p.Solution), p.Declared))
                .ToList(),
            DeferredProjects = loaded.DeferredProjects,
            DegradedProjects = loaded.DegradedProjects,
            UnattributedFailureCount = loaded.UnattributedFailureCount
        };
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Outside the checkout: a leftover generated solution in scratch or temp is never read again.
        }
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
    /// it (each head that expected it has reduced coverage). A deferred project (not opened before the load
    /// deadline) is likewise not counted as loaded for any solution that declared it.
    /// </summary>
    internal static IReadOnlyList<SolutionCoverage> BuildCoverage(
        IReadOnlyList<(string Solution, IReadOnlyList<string> Declared)> perSolutionDeclared,
        IReadOnlyList<SkippedProject> skippedProjects,
        IReadOnlyList<string>? deferredProjects = null,
        Solution? loadedSolution = null)
    {
        var skippedByPath = skippedProjects.GroupBy(s => Path.GetFullPath(s.ProjectPath), PathComparer)
            .ToDictionary(g => g.Key, g => g.ToList(), PathComparer);
        var deferred = new HashSet<string>((deferredProjects ?? []).Select(Path.GetFullPath), PathComparer);
        var usable = loadedSolution?.Projects.Where(p => p.FilePath != null &&
                (p.Documents.Any() || !skippedByPath.ContainsKey(Path.GetFullPath(p.FilePath))))
            .Select(p => Path.GetFullPath(p.FilePath!)).ToHashSet(PathComparer);

        var result = new List<SolutionCoverage>(perSolutionDeclared.Count);
        foreach (var (solution, declared) in perSolutionDeclared)
        {
            var declaredFull = declared
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .ToList();

            var solutionSkipped = declaredFull
                .Where(skippedByPath.ContainsKey)
                .SelectMany(p => skippedByPath[p])
                .ToList();
            var solutionDeferred = declaredFull.Count(deferred.Contains);

            result.Add(new SolutionCoverage(
                solution,
                declaredFull.Count,
                usable != null ? declaredFull.Count(usable.Contains)
                    : declaredFull.Count - solutionSkipped.Select(s => Path.GetFullPath(s.ProjectPath)).Distinct(PathComparer).Count() - solutionDeferred,
                solutionSkipped)
            {
                DeferredProjectCount = solutionDeferred
            });
        }
        return result;
    }
}

/// <summary>The load modes <see cref="MultiSolutionLoadResult.LoadMode"/> reports (issue #267).</summary>
public static class SolutionLoadModes
{
    /// <summary>One whole-solution open (<c>OpenSolutionAsync</c>).</summary>
    public const string Solution = "solution";

    /// <summary>The multi-solution union, loaded with one open of a generated solution (issue #268).</summary>
    public const string Union = "union";

    /// <summary>
    /// The multi-solution union, each project opened individually (<c>OpenProjectAsync</c>): the fallback when the
    /// one-pass open aborts or runs past the load deadline.
    /// </summary>
    public const string UnionPerProject = "union_per_project";
}
