namespace Sextant.Service;

/// <summary>
/// The service-owned maintenance loop's policy (issue #299). Every <see cref="Interval"/> the loop runs one tick: the
/// idle-branch rule retires non-default branch pointers that have not moved for more than
/// <see cref="BranchIdleRetireDays"/> days (at most <see cref="BranchIdleRetireMaxPerPass"/> per tick), then, once a
/// day at or after <see cref="RetentionHourUtc"/>, the bounded retention pass runs when the writer is free.
/// </summary>
public sealed record MaintenancePolicy
{
    internal const int DefaultIntervalMinutes = 60;
    internal const int DefaultBranchIdleRetireDays = 7;
    internal const int DefaultBranchIdleRetireMaxPerPass = 20;
    internal const int DefaultRetentionHourUtc = 3;
    internal const int DefaultRetentionPassSeconds = 600;

    /// <summary>The longest retention pass, in seconds (1 hour); a longer value is clamped to it.</summary>
    public const int MaxRetentionPassSeconds = 3600;

    /// <summary>The longest tick interval, in minutes (7 days); a longer value is clamped to it.</summary>
    public const int MaxIntervalMinutes = 7 * 24 * 60;

    /// <summary>
    /// How long a retired-head record (issue #299) is kept: an idle-retired branch pushed again within it is re-created
    /// by the push's <c>expected_head_commit</c> (its retired head). The loop prunes older records.
    /// </summary>
    public static readonly TimeSpan RetiredHeadRetention = TimeSpan.FromDays(90);

    /// <summary>The largest idle threshold, in days (about ten years); a larger value is clamped to it.</summary>
    public const int MaxBranchIdleRetireDays = 3650;

    /// <summary>The largest per-tick retire cap the environment can set.</summary>
    public const int MaxBranchIdleRetireMaxPerPass = 1000;

    /// <summary>
    /// How often the loop ticks. <see cref="TimeSpan.Zero"/> disables the loop: no idle-branch retirement and no
    /// scheduled retention. <c>SEXTANT_SERVICE_MAINTENANCE_INTERVAL_MINUTES</c> (default 60; <c>0</c> disables; clamped to <see cref="MaxIntervalMinutes"/>).
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(DefaultIntervalMinutes);

    /// <summary>
    /// A non-default branch whose pointer has not moved for more than this many days is retired. <c>0</c> disables the
    /// rule. <c>SEXTANT_SERVICE_BRANCH_IDLE_RETIRE_DAYS</c> (default 7; clamped to <see cref="MaxBranchIdleRetireDays"/>).
    /// </summary>
    public int BranchIdleRetireDays { get; init; } = DefaultBranchIdleRetireDays;

    /// <summary>
    /// The most branches one tick retires (the least recently moved first); the rest wait for the next tick.
    /// <c>SEXTANT_SERVICE_BRANCH_IDLE_RETIRE_MAX_PER_PASS</c> (default 20).
    /// </summary>
    public int BranchIdleRetireMaxPerPass { get; init; } = DefaultBranchIdleRetireMaxPerPass;

    /// <summary>
    /// Whether the loop runs the bounded retention pass once a day. <c>SEXTANT_SERVICE_RETENTION_SCHEDULE</c>
    /// (default true).
    /// </summary>
    public bool ScheduledRetention { get; init; } = true;

    /// <summary>
    /// The UTC hour (0–23) from which the day's scheduled retention runs: the first tick at or after it runs the pass,
    /// once per UTC day. <c>SEXTANT_SERVICE_RETENTION_SCHEDULE_HOUR_UTC</c> (default 3).
    /// </summary>
    public int RetentionHourUtc { get; init; } = DefaultRetentionHourUtc;

    /// <summary>
    /// The time budget, in seconds, of one scheduled retention pass; also the ceiling of the <c>seconds</c> an interactive
    /// <c>POST /control/retention</c> may ask for (its default stays 10). <c>SEXTANT_SERVICE_RETENTION_PASS_SECONDS</c>
    /// (default 600; clamped to <see cref="MaxRetentionPassSeconds"/>).
    /// </summary>
    public int RetentionPassSeconds { get; init; } = DefaultRetentionPassSeconds;

    /// <summary>A one-line description of the effective policy for the startup log.</summary>
    public string Describe()
    {
        if (Interval <= TimeSpan.Zero)
            return "maintenance: loop disabled (no idle-branch retirement, no scheduled retention).";
        var idle = BranchIdleRetireDays > 0
            ? $"retires non-default branches idle > {BranchIdleRetireDays} day(s), at most {BranchIdleRetireMaxPerPass} per tick"
            : "idle-branch retirement disabled";
        var retention = ScheduledRetention
            ? $"scheduled retention daily from {RetentionHourUtc:D2}:00 UTC"
            : "scheduled retention disabled";
        return $"maintenance: every {Interval.TotalMinutes:0.##} min; {idle}; {retention}.";
    }

    /// <summary>The policy with the loop off (no ticks at all).</summary>
    public static MaintenancePolicy Disabled { get; } = new() { Interval = TimeSpan.Zero };
}
