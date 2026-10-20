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
    internal const int DefaultRepositoryReclaimDays = 14;
    internal const int DefaultRepositoryReclaimMaxPerPass = 5;
    internal const int DefaultRetentionPassSeconds = 600;
    internal const int DefaultRetentionBurstSeconds = 120;
    internal const int DefaultRetentionContinueMinutes = 5;
    internal const int DefaultTickDeadlineSeconds = 900;
    internal const int DefaultBranchUpstreamCheckMinutes = 60;
    internal const int DefaultBranchUpstreamCheckMaxRepositoriesPerTick = 10;

    /// <summary>The longest upstream-check interval, in minutes (7 days); a longer value is clamped to it.</summary>
    public const int MaxBranchUpstreamCheckMinutes = 7 * 24 * 60;

    /// <summary>The largest per-tick upstream-check repository cap the environment can set.</summary>
    public const int MaxBranchUpstreamCheckMaxRepositoriesPerTick = 1000;

    /// <summary>The longest retention hold of the writer, in seconds (1 hour); a longer value is clamped to it.</summary>
    public const int MaxRetentionBurstSeconds = 3600;

    /// <summary>The longest tick deadline, in seconds (1 day); a longer value is clamped to it.</summary>
    public const int MaxTickDeadlineSeconds = 86400;

    /// <summary>The longest wait before a retention continuation, in minutes (1 day); a longer value is clamped to it.</summary>
    public const int MaxRetentionContinueMinutes = 1440;

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

    /// <summary>The largest repository-reclaim threshold, in days (about ten years); a larger value is clamped to it.</summary>
    public const int MaxRepositoryReclaimDays = 3650;

    /// <summary>The largest per-tick repository-reclaim cap the environment can set.</summary>
    public const int MaxRepositoryReclaimMaxPerPass = 1000;

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
    /// A repository with no branch pointer, no pending work and no catalog activity (branch, job, snapshot) for at least
    /// this many days has its checkout, git object store and package folder deleted (issue #291). <c>0</c> disables the
    /// rule. <c>SEXTANT_SERVICE_REPOSITORY_RECLAIM_DAYS</c> (default 14; clamped to <see cref="MaxRepositoryReclaimDays"/>).
    /// </summary>
    public int RepositoryReclaimDays { get; init; } = DefaultRepositoryReclaimDays;

    /// <summary>
    /// The most repositories one tick reclaims (the longest inactive first); the rest wait for the next tick.
    /// <c>SEXTANT_SERVICE_REPOSITORY_RECLAIM_MAX_PER_PASS</c> (default 5).
    /// </summary>
    public int RepositoryReclaimMaxPerPass { get; init; } = DefaultRepositoryReclaimMaxPerPass;

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
    /// The time budget, in seconds, of one scheduled retention pass, counted as writer-HELD time (the wait for turns is not
    /// spent); also the ceiling of the <c>seconds</c> an interactive
    /// <c>POST /control/retention</c> may ask for (its default stays 10). <c>SEXTANT_SERVICE_RETENTION_PASS_SECONDS</c>
    /// (default 600; clamped to <see cref="MaxRetentionPassSeconds"/>).
    /// </summary>
    public int RetentionPassSeconds { get; init; } = DefaultRetentionPassSeconds;

    /// <summary>
    /// How long the scheduled retention pass may keep the writer in ONE priority turn (issue #333): it runs its batches
    /// back to back inside the hold and releases the gate once the hold reaches this (or the pass is done), then queues
    /// again on the priority lane. While a backlog exists this gives retention about <c>burst / (burst + production)</c>
    /// of the writer, and delays a waiting production by at most this plus one batch. <c>0</c> releases after every
    /// batch. <c>SEXTANT_SERVICE_RETENTION_BURST_SECONDS</c> (default 120; clamped to <see cref="MaxRetentionBurstSeconds"/>).
    /// </summary>
    public int RetentionBurstSeconds { get; init; } = DefaultRetentionBurstSeconds;

    /// <summary>
    /// After a scheduled pass that ends with work left but made progress, the next pass is scheduled this many minutes
    /// later instead of at the next <see cref="Interval"/> tick. <c>0</c> turns the early continuation off.
    /// <c>SEXTANT_SERVICE_RETENTION_CONTINUE_MINUTES</c> (default 5; clamped to <see cref="MaxRetentionContinueMinutes"/>).
    /// </summary>
    public int RetentionContinueMinutes { get; init; } = DefaultRetentionContinueMinutes;

    /// <summary>
    /// ONE deadline for a whole maintenance tick, shared by every step (idle-branch retire, reclaim, retention): each step
    /// waits and works only for what is left of it, so the loop never blocks longer. <c>SEXTANT_SERVICE_MAINTENANCE_TICK_DEADLINE_SECONDS</c>
    /// (default 900; clamped to <see cref="MaxTickDeadlineSeconds"/>).
    /// </summary>
    public int TickDeadlineSeconds { get; init; } = DefaultTickDeadlineSeconds;

    /// <summary>
    /// The least time between upstream-branch checks of one repository (issue #338): the loop lists the remote's
    /// branches and retires the non-default pointers whose branch is gone (absent on two consecutive listings).
    /// <c>0</c> disables the step. <c>SEXTANT_SERVICE_BRANCH_UPSTREAM_CHECK_MINUTES</c> (default 60; clamped to <see cref="MaxBranchUpstreamCheckMinutes"/>).
    /// </summary>
    public int BranchUpstreamCheckMinutes { get; init; } = DefaultBranchUpstreamCheckMinutes;

    /// <summary>
    /// The most repositories one tick lists upstream (the least recently checked first).
    /// <c>SEXTANT_SERVICE_BRANCH_UPSTREAM_CHECK_MAX_REPOSITORIES_PER_TICK</c> (default 10).
    /// </summary>
    public int BranchUpstreamCheckMaxRepositoriesPerTick { get; init; } = DefaultBranchUpstreamCheckMaxRepositoriesPerTick;

    /// <summary>A one-line description of the effective policy for the startup log.</summary>
    public string Describe()
    {
        if (Interval <= TimeSpan.Zero)
            return "maintenance: loop disabled (no idle-branch retirement, no scheduled retention).";
        var idle = BranchIdleRetireDays > 0
            ? $"retires non-default branches idle > {BranchIdleRetireDays} day(s), at most {BranchIdleRetireMaxPerPass} per tick"
            : "idle-branch retirement disabled";
        var reclaim = RepositoryReclaimDays > 0
            ? $"reclaims disk of repositories inactive >= {RepositoryReclaimDays} day(s), at most {RepositoryReclaimMaxPerPass} per tick"
            : "repository reclaim disabled";
        var upstream = BranchUpstreamCheckMinutes > 0
            ? $"checks upstream branches of each repository every {BranchUpstreamCheckMinutes} min, at most {BranchUpstreamCheckMaxRepositoriesPerTick} repositories per tick"
            : "upstream-branch check disabled";
        var retention = ScheduledRetention
            ? $"scheduled retention daily from {RetentionHourUtc:D2}:00 UTC"
            : "scheduled retention disabled";
        return $"maintenance: every {Interval.TotalMinutes:0.##} min; {idle}; {upstream}; {reclaim}; {retention}.";
    }

    /// <summary>The policy with the loop off (no ticks at all).</summary>
    public static MaintenancePolicy Disabled { get; } = new() { Interval = TimeSpan.Zero };
}
