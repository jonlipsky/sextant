namespace Sextant.Core;

/// <summary>One project's raw binding counts, as the orchestrator accumulated them while persisting its occurrences.</summary>
/// <param name="Project">The project file relative to its repository root (forward slashes).</param>
/// <param name="TargetFramework">The evaluated target framework, when known.</param>
/// <param name="NamesExamined">Identifier names the extractor examined.</param>
/// <param name="UnboundNames">Names that did not bind to any symbol.</param>
/// <param name="UnboundInvocations">Invocations that did not bind to one method.</param>
/// <param name="CandidateOccurrences">References and call edges stored against candidate symbols.</param>
public sealed record ProjectBindingCounts(
    string Project,
    string? TargetFramework,
    long NamesExamined,
    long UnboundNames,
    long UnboundInvocations,
    long CandidateOccurrences);

/// <summary>
/// Turns per-project binding counts into the <see cref="BindingHealth"/> of a snapshot's coverage and folds it
/// into the verdict. Pure, so the threshold and the wording are unit-testable without an index.
/// <para>
/// A compile-clean project leaves no name unbound, so any unbound name is a symptom: a missing reference, an
/// unrestored package, a project whose transitive references were not loaded. A few are tolerated (generated
/// code, a deliberate compile error in a test fixture); a project is DEGRADED once at least
/// <see cref="DegradedMinUnboundNames"/> names, and at least <see cref="DegradedMinUnboundRatio"/> of the names
/// it uses, did not bind. Every degraded project makes the coverage verdict partial and is named in the reason,
/// because references and calls inside it may be missing from query results.
/// </para>
/// </summary>
public static class BindingHealthBuilder
{
    /// <summary>The minimum count of unbound names for a project to be degraded.</summary>
    public const long DegradedMinUnboundNames = 25;

    /// <summary>The minimum share of a project's examined names that must be unbound for it to be degraded.</summary>
    public const double DegradedMinUnboundRatio = 0.005;

    /// <summary>How many degraded projects the coverage reason names before summarising the rest.</summary>
    public const int MaxNamedInReason = 5;

    /// <summary>True when <paramref name="unboundNames"/> of <paramref name="namesExamined"/> crosses the degraded threshold.</summary>
    public static bool IsDegraded(long namesExamined, long unboundNames)
        => unboundNames >= DegradedMinUnboundNames
           && unboundNames >= DegradedMinUnboundRatio * Math.Max(namesExamined, 1);

    /// <summary>
    /// Builds the snapshot's binding health from per-project counts (one entry per indexed project version;
    /// entries for the same project file and framework are summed) and the producer's per-project load issues,
    /// keyed by the same repository-relative project path.
    /// </summary>
    public static BindingHealth Build(
        IEnumerable<ProjectBindingCounts> projects, IReadOnlyDictionary<string, string>? loadIssues = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var merged = projects
            .GroupBy(p => (p.Project, TargetFramework: string.IsNullOrEmpty(p.TargetFramework) ? null : p.TargetFramework))
            .Select(g => new ProjectBindingCounts(
                g.Key.Project, g.Key.TargetFramework,
                g.Sum(p => p.NamesExamined), g.Sum(p => p.UnboundNames),
                g.Sum(p => p.UnboundInvocations), g.Sum(p => p.CandidateOccurrences)))
            .ToList();

        var issues = loadIssues ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = merged
            .Select(p => new ProjectBindingHealth
            {
                Project = p.Project,
                TargetFramework = p.TargetFramework,
                NamesExamined = p.NamesExamined,
                UnboundNames = p.UnboundNames,
                UnboundInvocations = p.UnboundInvocations,
                CandidateOccurrences = p.CandidateOccurrences,
                Degraded = IsDegraded(p.NamesExamined, p.UnboundNames),
                LoadIssue = issues.TryGetValue(p.Project, out var issue) ? issue : null
            })
            .ToList();

        // A load issue for a project no indexed version matched (it did not load, or was not extracted) is
        // still listed, so the restore failure behind a missing project is not lost.
        var indexed = new HashSet<string>(merged.Select(p => p.Project), StringComparer.Ordinal);
        foreach (var (project, issue) in issues.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            if (!indexed.Contains(project))
                entries.Add(new ProjectBindingHealth { Project = project, LoadIssue = issue });
        }

        var listed = entries
            .Where(e => e.UnboundNames > 0 || e.UnboundInvocations > 0 || e.LoadIssue is not null)
            .OrderByDescending(e => e.Degraded)
            .ThenByDescending(e => e.UnboundNames)
            .ThenByDescending(e => e.UnboundInvocations)
            .ThenBy(e => e.Project, StringComparer.Ordinal)
            .ThenBy(e => e.TargetFramework, StringComparer.Ordinal)
            .Take(BindingHealth.MaxProjects)
            .ToList();

        return new BindingHealth
        {
            NamesExamined = merged.Sum(p => p.NamesExamined),
            UnboundNames = merged.Sum(p => p.UnboundNames),
            UnboundInvocations = merged.Sum(p => p.UnboundInvocations),
            CandidateOccurrences = merged.Sum(p => p.CandidateOccurrences),
            ProjectsDegraded = entries.Count(e => e.Degraded),
            Projects = listed
        };
    }

    /// <summary>
    /// Attaches <paramref name="health"/> to <paramref name="coverage"/>. When any project is degraded the verdict
    /// becomes partial and a reason names the worst projects; otherwise the verdict is unchanged.
    /// </summary>
    public static SnapshotCoverage Apply(SnapshotCoverage coverage, BindingHealth health)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(health);
        if (health.ProjectsDegraded == 0)
            return coverage with { Binding = health };

        var degraded = health.Projects.Where(p => p.Degraded).ToList();
        var named = degraded.Take(MaxNamedInReason)
            .Select(p => $"{p.Project}{(p.TargetFramework is { Length: > 0 } tfm ? $" ({tfm})" : string.Empty)}: " +
                         $"{p.UnboundNames} unbound name(s)")
            .ToList();
        var more = health.ProjectsDegraded - named.Count;
        var reason =
            $"Code in {health.ProjectsDegraded} project(s) did not fully compile on the indexer, so references and " +
            $"calls inside them may be missing ({string.Join("; ", named)}" +
            (more > 0 ? $"; +{more} more" : string.Empty) + "). Calls that failed to bind are kept as candidate matches.";
        return coverage with
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            Reasons = [.. coverage.Reasons, reason],
            Binding = health
        };
    }
}
