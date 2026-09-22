namespace Sextant.Core.Platform;

/// <summary>
/// How a project's <see cref="CapabilityRequirement"/> was discovered — the routing rule "use demonstrated
/// evaluation success plus policy, not the TFM name alone" turns on this distinction.
/// </summary>
public enum RequirementSource
{
    /// <summary>Inferred from the project's declared target framework / properties (a HINT, not a verdict).</summary>
    Declared = 0,

    /// <summary>Proven by an actual Linux evaluation failure (a missing SDK/workload/reference pack).</summary>
    DemonstratedFailure = 1
}

/// <summary>
/// The capabilities a single project graph needs in order to be faithfully evaluated (Phase 15). A
/// <em>portable</em> requirement (<see cref="IsPortable"/>) is satisfied by any worker — the common case
/// for <c>netstandard</c>, ordinary <c>netX.Y</c>, and many <c>netX.Y-windows</c> class libraries that a
/// Linux worker can evaluate with restored reference packs. A non-portable requirement names the OS,
/// target platform, workloads, reference packs, and custom tools a worker must provide.
///
/// Crucially, a requirement <em>derived from the TFM string</em> (<see cref="RequirementSource.Declared"/>)
/// is only a hint: routing escalates to a native worker only when Linux evaluation is
/// <see cref="RequirementSource.DemonstratedFailure"/> insufficient AND policy permits.
/// </summary>
public sealed record CapabilityRequirement
{
    /// <summary>The OS family required, or null when OS-agnostic.</summary>
    public PlatformOperatingSystem? RequiredOperatingSystem { get; init; }

    /// <summary>The target platform that must be evaluatable (e.g. <c>windows</c>, <c>ios</c>), or null.</summary>
    public string? RequiredTargetPlatform { get; init; }

    /// <summary>Required optional workload ids, normalized + sorted.</summary>
    public IReadOnlyList<string> RequiredWorkloads { get; init; } = [];

    /// <summary>Required targeting/reference pack ids, normalized + sorted.</summary>
    public IReadOnlyList<string> RequiredReferencePacks { get; init; } = [];

    /// <summary>Required custom tool labels (SDK resolvers / external toolchains), normalized + sorted.</summary>
    public IReadOnlyList<string> RequiredCustomTools { get; init; } = [];

    /// <summary>How this requirement was discovered.</summary>
    public RequirementSource Source { get; init; } = RequirementSource.Declared;

    /// <summary>A human-readable reason (e.g. the workspace diagnostic) backing this requirement.</summary>
    public string? Reason { get; init; }

    /// <summary>A portable requirement any worker satisfies — no OS, platform, workload, pack, or tool constraint.</summary>
    public bool IsPortable =>
        RequiredOperatingSystem is null
        && string.IsNullOrEmpty(RequiredTargetPlatform)
        && RequiredWorkloads.Count == 0
        && RequiredReferencePacks.Count == 0
        && RequiredCustomTools.Count == 0;

    /// <summary>A portable requirement — the default for ordinary/portable project graphs.</summary>
    public static CapabilityRequirement Portable { get; } = new();

    /// <summary>
    /// Builds a requirement for a target platform (e.g. <c>windows</c>, <c>ios</c>), inferring the owning
    /// OS family (Apple platforms → macOS). Every collection is normalized so equal requirements compare
    /// equal via their contents.
    /// </summary>
    public static CapabilityRequirement ForTargetPlatform(
        string? targetPlatform,
        RequirementSource source = RequirementSource.Declared,
        string? reason = null,
        IEnumerable<string>? workloads = null,
        IEnumerable<string>? referencePacks = null,
        IEnumerable<string>? customTools = null)
    {
        var platform = Normalize(targetPlatform);
        return new CapabilityRequirement
        {
            RequiredTargetPlatform = platform.Length > 0 ? platform : null,
            RequiredOperatingSystem = PlatformOperatingSystems.OwningOsForTargetPlatform(platform),
            RequiredWorkloads = NormalizeSet(workloads),
            RequiredReferencePacks = NormalizeSet(referencePacks),
            RequiredCustomTools = NormalizeSet(customTools),
            Source = source,
            Reason = reason
        };
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static IReadOnlyList<string> NormalizeSet(IEnumerable<string>? values)
    {
        if (values is null) return [];
        return values
            .Select(Normalize)
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// A project graph's capability requirement tagged with the project it belongs to. The routing decision
/// (<see cref="CapabilityRouter"/>) produces one placement per project id so a partial/unsupported
/// outcome can name exactly which projects lacked a compatible worker (acceptance criterion 4).
/// </summary>
public sealed record ProjectCapabilityRequirement
{
    /// <summary>A stable project identifier (canonical id or repo-relative path) for diagnostics.</summary>
    public required string ProjectId { get; init; }

    /// <summary>The project's repo-relative path, when known, for diagnostics.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>The discovered capability requirement for this project.</summary>
    public required CapabilityRequirement Requirement { get; init; }
}
