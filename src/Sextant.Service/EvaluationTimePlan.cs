using Sextant.Core;

namespace Sextant.Service;

/// <summary>
/// How the worker spends a sandboxed evaluation's time budget (issue #245), so a checkout too large to index in
/// time publishes a PARTIAL snapshot before the sandbox's hard abort instead of failing with nothing. Measured on
/// a 61-solution monorepo (468 declared projects): the restore, the load and the extraction each took about a
/// third of the run, with symbols and occurrences about equal inside the extraction.
/// <list type="bullet">
///   <item>The package restore gets at most <see cref="RestoreShare"/> of the budget.</item>
///   <item>Extraction (symbols, relationships, references, calls, comments) stops at
///   <see cref="ExtractionShare"/> of the budget; the rest is left for dependencies, the API surface and the
///   publish.</item>
///   <item>The load may use <see cref="LoadShare"/> of the time between the restore's end and that deadline;
///   projects not opened by then are reported, never loaded.</item>
///   <item>The symbol phase may use <see cref="IndexTimeBudget.SymbolShare"/> of the time left after the load.</item>
/// </list>
/// The sandbox's abort at the full budget stays the backstop for any single step that will not stop (one
/// project that takes too long to open or compile).
/// </summary>
public sealed record EvaluationTimePlan
{
    /// <summary>The share of the budget the package restore may use.</summary>
    public const double RestoreShare = 0.2;

    /// <summary>
    /// The share of the budget after which no more relationships, references, calls or comments are extracted.
    /// </summary>
    public const double ExtractionShare = 0.9;

    /// <summary>The share of the time between the restore's end and the extraction deadline the load may use.</summary>
    public const double LoadShare = 0.45;

    /// <summary>The clock every deadline is read against.</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>The whole evaluation budget the sandbox enforces.</summary>
    public required TimeSpan Budget { get; init; }

    /// <summary>When the evaluation started.</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>The plan for an evaluation starting now under <paramref name="budget"/>.</summary>
    public static EvaluationTimePlan Begin(TimeProvider clock, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget), budget, "The budget must be positive.");
        return new EvaluationTimePlan { Clock = clock, Budget = budget, Start = clock.GetUtcNow() };
    }

    /// <summary>The longest the package restore may run.</summary>
    public TimeSpan RestoreLimit => Scale(Budget, RestoreShare);

    /// <summary>The instant after which no more relationships, references, calls or comments are extracted.</summary>
    public DateTimeOffset ExtractionDeadline => Start + Scale(Budget, ExtractionShare);

    /// <summary>The load's deadline when the restore ended at <paramref name="restoreEnd"/>.</summary>
    public IndexDeadline LoadDeadline(DateTimeOffset restoreEnd)
    {
        var at = restoreEnd >= ExtractionDeadline
            ? restoreEnd
            : restoreEnd + Scale(ExtractionDeadline - restoreEnd, LoadShare);
        return new IndexDeadline(Clock, at);
    }

    /// <summary>The orchestrator's share of the plan, given the projects the load left out.</summary>
    public IndexTimeBudget ForIndexing(string checkoutRoot, IReadOnlyList<string> notLoaded) => new()
    {
        Clock = Clock,
        Budget = Budget,
        ExtractionDeadline = ExtractionDeadline,
        CheckoutRoot = checkoutRoot,
        NotLoaded = notLoaded
    };

    /// <summary>One log line describing the plan.</summary>
    public string Describe() =>
        $"time budget: {TimeBudgetCoverageBuilder.FormatBudget((long)Budget.TotalSeconds)}; restore up to " +
        $"{RestoreLimit.TotalSeconds:0}s, extraction until +{(ExtractionDeadline - Start).TotalSeconds:0}s.";

    private static TimeSpan Scale(TimeSpan span, double share) => TimeSpan.FromTicks((long)(span.Ticks * share));
}
