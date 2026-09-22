namespace Sextant.Core.Platform;

/// <summary>
/// How aggressively the router escalates off the default (Linux) worker (Phase 15, configurable per
/// repository/profile).
/// </summary>
public enum PlatformRoutingMode
{
    /// <summary>
    /// Escalate a project to a native (Windows/macOS/workload) worker when Linux evaluation is
    /// demonstrably insufficient AND a compatible worker exists. The default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Never escalate: only the default (Linux) worker is used. A project Linux cannot evaluate is marked
    /// unsupported rather than routed. Suited to a purely local dev box or a Linux-only deployment that
    /// deliberately forgoes native routing.
    /// </summary>
    LinuxOnly = 1
}

/// <summary>
/// The per-repository / per-profile policy that governs platform routing (Phase 15). It never changes
/// what a worker CAN evaluate — that is the capability fingerprint — only whether the router is ALLOWED
/// to escalate off the default worker and to which platforms. Because it is an orchestration choice
/// rather than an extraction-semantics choice, it is deliberately NOT folded into the Phase-8
/// configuration hash: the producing worker's capability fingerprint already records what actually ran.
/// </summary>
public sealed record PlatformRoutingPolicy
{
    /// <summary>The escalation mode.</summary>
    public PlatformRoutingMode Mode { get; init; } = PlatformRoutingMode.Auto;

    /// <summary>
    /// The OS families the router may escalate to (subject to <see cref="Mode"/>). Empty means "any
    /// native worker that is compatible" (the default). A restricted set lets an operator, for example,
    /// enable Windows routing but withhold macOS while capacity is provisioned.
    /// </summary>
    public IReadOnlyList<PlatformOperatingSystem> AllowedNativeOperatingSystems { get; init; } = [];

    /// <summary>The default policy: escalate automatically to any compatible native worker.</summary>
    public static PlatformRoutingPolicy Default { get; } = new();

    /// <summary>A policy that never leaves the default (Linux) worker.</summary>
    public static PlatformRoutingPolicy LinuxOnly { get; } = new() { Mode = PlatformRoutingMode.LinuxOnly };

    /// <summary>Whether the router may escalate to a native worker at all under this policy.</summary>
    public bool AllowsNativeRouting => Mode == PlatformRoutingMode.Auto;

    /// <summary>Whether escalating to <paramref name="os"/> is permitted under this policy.</summary>
    public bool AllowsEscalationTo(PlatformOperatingSystem os)
    {
        if (!AllowsNativeRouting) return false;
        return AllowedNativeOperatingSystems.Count == 0 || AllowedNativeOperatingSystems.Contains(os);
    }

    /// <summary>
    /// Parses a policy from a configured mode name (case/whitespace-insensitive). An unrecognized value
    /// resolves to <see cref="Default"/> so a typo degrades to the safe default rather than failing a run.
    /// </summary>
    public static PlatformRoutingPolicy Parse(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "linux_only" or "linux-only" or "linuxonly" or "linux" => LinuxOnly,
        _ => Default
    };
}
