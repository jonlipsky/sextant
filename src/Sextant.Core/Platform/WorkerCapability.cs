using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sextant.Core.Platform;

/// <summary>
/// The operating system family a Sextant index worker runs on. Roslyn/MSBuild project evaluation can
/// depend on OS-specific SDKs, workloads, and reference packs, so the OS is the coarsest routing axis:
/// Linux is the default worker; a genuinely Windows- or Apple-bound project graph is routed to a native
/// worker only when Linux evaluation is demonstrably insufficient (Phase 15).
/// </summary>
public enum PlatformOperatingSystem
{
    /// <summary>Unknown / unclassified host OS.</summary>
    Unknown = 0,

    /// <summary>Linux — the default Sextant worker for ordinary and portable project graphs.</summary>
    Linux = 1,

    /// <summary>Windows — required for Windows-only SDK imports, native tasks, and some targeting packs.</summary>
    Windows = 2,

    /// <summary>macOS — the authoritative route for Apple workloads / Xcode-dependent evaluation.</summary>
    MacOS = 3
}

/// <summary>Helpers for classifying and naming <see cref="PlatformOperatingSystem"/> values.</summary>
public static class PlatformOperatingSystems
{
    /// <summary>The OS family of the current host, from <see cref="RuntimeInformation"/>.</summary>
    public static PlatformOperatingSystem Detect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return PlatformOperatingSystem.Linux;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return PlatformOperatingSystem.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return PlatformOperatingSystem.MacOS;
        return PlatformOperatingSystem.Unknown;
    }

    /// <summary>The stable lowercase wire name for an OS family (used in fingerprints and diagnostics).</summary>
    public static string ToName(this PlatformOperatingSystem os) => os switch
    {
        PlatformOperatingSystem.Linux => "linux",
        PlatformOperatingSystem.Windows => "windows",
        PlatformOperatingSystem.MacOS => "macos",
        _ => "unknown"
    };

    /// <summary>
    /// Maps a target-platform identifier (the <c>-windows</c> / <c>-ios</c> / <c>-maccatalyst</c> / …
    /// suffix of a target framework moniker) to the OS family that can authoritatively evaluate it, or
    /// <c>null</c> when the platform is portable / OS-agnostic. Apple platforms fold to macOS.
    /// </summary>
    public static PlatformOperatingSystem? OwningOsForTargetPlatform(string? targetPlatform) =>
        targetPlatform?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "windows" => PlatformOperatingSystem.Windows,
            "ios" or "maccatalyst" or "macos" or "tvos" or "watchos" => PlatformOperatingSystem.MacOS,
            _ => null
        };
}

/// <summary>
/// A stable, comparable advertisement of everything a Sextant index worker can faithfully evaluate: its
/// OS + architecture, the .NET SDK feature bands and optional workloads it has installed, the
/// targeting/reference packs it can restore, the target platforms it can evaluate, and any custom
/// tool labels an operator attaches (Phase 15). A project's discovered
/// <see cref="CapabilityRequirement"/> is matched against this by <see cref="Satisfies"/>, and the
/// deterministic <see cref="Fingerprint"/> is recorded in snapshot provenance and folded into the
/// Phase-9 snapshot identity so a snapshot built under one capability set is never silently reused under
/// an incompatible one (acceptance criterion 5).
///
/// The model is ProcessStack- and service-AGNOSTIC (it lives in core): the routing DECISION consumes it,
/// while the native-worker placement/execution that acts on it is wired behind a service-side seam
/// (Phase 14, deferred).
/// </summary>
public sealed record WorkerCapability
{
    /// <summary>The OS family this worker runs on.</summary>
    public required PlatformOperatingSystem OperatingSystem { get; init; }

    /// <summary>The process architecture (e.g. <c>x64</c>, <c>arm64</c>), lowercased.</summary>
    public string Architecture { get; init; } = "unknown";

    /// <summary>Installed .NET SDK feature bands (e.g. <c>8.0.400</c>), normalized + sorted.</summary>
    public IReadOnlyList<string> SdkFeatureBands { get; init; } = [];

    /// <summary>Installed optional workload ids (e.g. <c>android</c>, <c>ios</c>, <c>maui</c>), normalized + sorted.</summary>
    public IReadOnlyList<string> InstalledWorkloads { get; init; } = [];

    /// <summary>Restorable targeting/reference pack ids (e.g. <c>Microsoft.WindowsDesktop.App</c>), normalized + sorted.</summary>
    public IReadOnlyList<string> ReferencePacks { get; init; } = [];

    /// <summary>Target platforms this worker can faithfully evaluate (e.g. <c>windows</c>, <c>ios</c>), normalized + sorted.</summary>
    public IReadOnlyList<string> TargetPlatforms { get; init; } = [];

    /// <summary>Operator-attached custom tool labels (external toolchains / SDK resolvers), normalized + sorted.</summary>
    public IReadOnlyList<string> CustomToolLabels { get; init; } = [];

    /// <summary>
    /// Builds a capability with every collection normalized (trimmed, lowercased, de-duplicated, sorted)
    /// so two logically-equal capabilities always produce the same <see cref="Fingerprint"/>.
    /// </summary>
    public static WorkerCapability Create(
        PlatformOperatingSystem operatingSystem,
        string? architecture = null,
        IEnumerable<string>? sdkFeatureBands = null,
        IEnumerable<string>? installedWorkloads = null,
        IEnumerable<string>? referencePacks = null,
        IEnumerable<string>? targetPlatforms = null,
        IEnumerable<string>? customToolLabels = null) => new()
        {
            OperatingSystem = operatingSystem,
            Architecture = Normalize(architecture) is { Length: > 0 } a ? a : "unknown",
            SdkFeatureBands = NormalizeSet(sdkFeatureBands),
            InstalledWorkloads = NormalizeSet(installedWorkloads),
            ReferencePacks = NormalizeSet(referencePacks),
            TargetPlatforms = NormalizeSet(targetPlatforms),
            CustomToolLabels = NormalizeSet(customToolLabels)
        };

    /// <summary>
    /// The current host's default capability: its OS + architecture, and its own OS family as an
    /// evaluatable target platform. It advertises NO optional workloads or extra reference packs (probing
    /// them is expensive and would make the fingerprint non-deterministic), so it represents exactly what
    /// a bare local machine can evaluate: its own platform plus portable project graphs. This is the
    /// default (Linux, on the service) placement's capability and, on a local dev box, the fingerprint
    /// stamped into that machine's own snapshots. Deterministic per host.
    /// </summary>
    public static WorkerCapability LocalDefault { get; } = BuildLocalDefault();

    private static WorkerCapability BuildLocalDefault()
    {
        var os = PlatformOperatingSystems.Detect();
        return Create(
            os,
            architecture: RuntimeInformation.OSArchitecture.ToString(),
            targetPlatforms: os == PlatformOperatingSystem.Unknown ? [] : [os.ToName()]);
    }

    /// <summary>
    /// Whether this worker can faithfully evaluate a project with the given <paramref name="requirement"/>:
    /// its OS matches (or the requirement is OS-agnostic), it can evaluate the required target platform,
    /// and it has every required workload, reference pack, and custom tool. A portable requirement is
    /// satisfied by any worker.
    /// </summary>
    public bool Satisfies(CapabilityRequirement requirement)
    {
        if (requirement.IsPortable)
            return true;

        if (requirement.RequiredOperatingSystem is { } os && os != OperatingSystem)
            return false;

        if (requirement.RequiredTargetPlatform is { Length: > 0 } platform
            && !TargetPlatforms.Contains(platform, StringComparer.Ordinal))
            return false;

        return Contains(InstalledWorkloads, requirement.RequiredWorkloads)
            && Contains(ReferencePacks, requirement.RequiredReferencePacks)
            && Contains(CustomToolLabels, requirement.RequiredCustomTools);
    }

    /// <summary>
    /// The stable capability fingerprint: a SHA-256 (hex) over the ordered, normalized advertisement.
    /// Deterministic across runs on the same worker, distinct across materially different ones. Recorded
    /// in snapshot provenance and folded into the snapshot identity for cache-compatibility.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var canonical = string.Join(
                ';',
                "v=1",
                $"os={OperatingSystem.ToName()}",
                $"arch={Architecture}",
                $"sdk={string.Join(',', SdkFeatureBands)}",
                $"workloads={string.Join(',', InstalledWorkloads)}",
                $"refpacks={string.Join(',', ReferencePacks)}",
                $"platforms={string.Join(',', TargetPlatforms)}",
                $"tools={string.Join(',', CustomToolLabels)}");
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexStringLower(bytes);
        }
    }

    private static bool Contains(IReadOnlyList<string> have, IReadOnlyList<string> required)
    {
        if (required.Count == 0) return true;
        var set = new HashSet<string>(have, StringComparer.Ordinal);
        return required.All(set.Contains);
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
