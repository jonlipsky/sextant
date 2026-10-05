namespace Sextant.Core;

/// <summary>
/// Builds the <see cref="TimeBudgetCoverage"/> of a snapshot whose evaluation ran out of time and folds it into
/// the coverage verdict. Pure, so the wording and the verdict are unit-testable without an index.
/// </summary>
public static class TimeBudgetCoverageBuilder
{
    /// <summary>How many unfinished solutions the coverage reason names before summarising the rest.</summary>
    public const int MaxNamedInReason = 5;

    /// <summary>
    /// The coverage of what a time-budgeted evaluation left out. <paramref name="unfinishedSolutions"/> is in
    /// selection order and is capped at <see cref="TimeBudgetCoverage.MaxSolutions"/>; its full count is kept.
    /// </summary>
    public static TimeBudgetCoverage Build(
        TimeSpan budget, int projectsNotLoaded, int projectsNotIndexed, int projectsNotFullyExtracted,
        IReadOnlyList<string> unfinishedSolutions)
    {
        ArgumentNullException.ThrowIfNull(unfinishedSolutions);
        return new TimeBudgetCoverage
        {
            BudgetSeconds = (long)Math.Round(budget.TotalSeconds),
            ProjectsNotLoaded = projectsNotLoaded,
            ProjectsNotIndexed = projectsNotIndexed,
            ProjectsNotFullyExtracted = projectsNotFullyExtracted,
            SolutionsUnfinished = unfinishedSolutions.Count,
            UnfinishedSolutions = unfinishedSolutions.Take(TimeBudgetCoverage.MaxSolutions).ToList()
        };
    }

    /// <summary>
    /// Attaches <paramref name="budget"/> to <paramref name="coverage"/>. When anything was left out the verdict
    /// becomes partial and the reason is put FIRST, because on a repository too large for the budget it is the
    /// largest gap (the lean query warning counts it first too). Unchanged when nothing was left out.
    /// </summary>
    public static SnapshotCoverage Apply(SnapshotCoverage coverage, TimeBudgetCoverage budget)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(budget);
        if (!budget.Exhausted)
            return coverage;
        return coverage with
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            Reasons = [Describe(budget, coverage.SolutionsSelected), .. coverage.Reasons],
            TimeBudget = budget
        };
    }

    /// <summary>The one-sentence coverage reason for <paramref name="budget"/>.</summary>
    public static string Describe(TimeBudgetCoverage budget, int solutionsSelected)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var text = $"The indexing time budget ({FormatBudget(budget.BudgetSeconds)}) ran out before the whole checkout was indexed";
        if (budget.SolutionsUnfinished > 0)
        {
            var named = budget.UnfinishedSolutions.Take(MaxNamedInReason).ToList();
            var more = budget.SolutionsUnfinished - named.Count;
            var of = solutionsSelected >= budget.SolutionsUnfinished ? $" of {solutionsSelected}" : string.Empty;
            text += $": {budget.SolutionsUnfinished}{of} selected solution(s) are unfinished ({string.Join(", ", named)}" +
                    (more > 0 ? $"; +{more} more" : string.Empty) + ")";
        }
        return text +
               $". {budget.ProjectsNotLoaded} project(s) were not loaded, {budget.ProjectsNotIndexed} have no symbols, " +
               $"and {budget.ProjectsNotFullyExtracted} are missing relationships, references, calls or comments.";
    }

    /// <summary>A budget as whole minutes when it is one (<c>30 min</c>), else seconds (<c>90 s</c>).</summary>
    public static string FormatBudget(long seconds) =>
        seconds > 0 && seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
}
