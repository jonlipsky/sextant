namespace Sextant.Core;

/// <summary>
/// A wall-clock deadline read through a <see cref="TimeProvider"/>, so a test can drive it with a hand-rolled
/// clock instead of waiting.
/// </summary>
/// <param name="Clock">The clock the deadline is read against.</param>
/// <param name="At">The instant the deadline passes.</param>
public sealed record IndexDeadline(TimeProvider Clock, DateTimeOffset At)
{
    /// <summary>True once <see cref="Clock"/> reads <see cref="At"/> or later.</summary>
    public bool HasPassed => Clock.GetUtcNow() >= At;
}

/// <summary>
/// The share of a sandboxed evaluation's time budget the orchestrator may spend extracting a full index. The
/// service worker sets it on <see cref="SnapshotContext.TimeBudget"/> when its evaluation runs under a time
/// budget, so a checkout too large to index within the budget publishes what was indexed as a PARTIAL snapshot
/// instead of being aborted with nothing. Not part of snapshot identity. <c>null</c> (the local CLI/daemon path,
/// and a worker without a time budget) keeps indexing unbounded and byte-identical.
/// <para>
/// The symbol phase may run until <see cref="SymbolShare"/> of the time left at its start before
/// <see cref="ExtractionDeadline"/>; later projects are registered but not indexed. Relationships, references,
/// calls and comments stop at <see cref="ExtractionDeadline"/>. Projects of a Phase-12 provider (submodule)
/// snapshot are never deferred: a provider is shared and reused, so a mapped but empty provider project would be
/// reused empty.
/// </para>
/// </summary>
public sealed record IndexTimeBudget
{
    /// <summary>The default <see cref="SymbolShare"/>: symbols and occurrences took about the same time on a large monorepo.</summary>
    public const double DefaultSymbolShare = 0.4;

    /// <summary>The clock every deadline is read against.</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>The whole evaluation budget, used only to describe the gap.</summary>
    public required TimeSpan Budget { get; init; }

    /// <summary>
    /// The instant after which no more relationships, references, calls or comments are extracted. The rest of
    /// the budget is left for dependencies, the API surface and the publish.
    /// </summary>
    public required DateTimeOffset ExtractionDeadline { get; init; }

    /// <summary>The share (0 to 1) of the time left before <see cref="ExtractionDeadline"/> the symbol phase may use.</summary>
    public double SymbolShare { get; init; } = DefaultSymbolShare;

    /// <summary>The checkout root that solution paths in the recorded gap are made relative to; <c>null</c> keeps file names.</summary>
    public string? CheckoutRoot { get; init; }

    /// <summary>
    /// The absolute paths of declared projects the loader did not open because its share of the budget ran out.
    /// They are not in the workspace; the orchestrator only reports them.
    /// </summary>
    public IReadOnlyList<string> NotLoaded { get; init; } = [];

    /// <summary>The symbol phase's deadline when it starts at <paramref name="symbolStart"/>.</summary>
    public DateTimeOffset SymbolDeadline(DateTimeOffset symbolStart)
    {
        if (symbolStart >= ExtractionDeadline)
            return symbolStart;
        var share = Math.Clamp(SymbolShare, 0, 1);
        return symbolStart + TimeSpan.FromTicks((long)((ExtractionDeadline - symbolStart).Ticks * share));
    }
}
