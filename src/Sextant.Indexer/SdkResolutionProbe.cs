using Microsoft.Build.Locator;

namespace Sextant.Indexer;

/// <summary>
/// Locates the <c>global.json</c> hostfxr would honor for a directory: the first file named
/// <c>global.json</c> found walking LEXICALLY up from the directory to the file-system root — the same walk
/// hostfxr performs for <c>hostfxr_resolve_sdk2(working_dir)</c> (issue #113).
/// </summary>
public static class GlobalJsonLocator
{
    public const string FileName = "global.json";

    /// <summary>
    /// The path comparer matching the host file system's usual case sensitivity (case-insensitive on Windows
    /// and macOS, case-sensitive elsewhere), so distinct directories are never merged on Linux and one
    /// physical file is never reported twice on Windows.
    /// </summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The full path of the nearest <c>global.json</c> at or above <paramref name="directory"/>, or null.</summary>
    public static string? FindNearest(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, FileName);
            if (File.Exists(candidate))
                return candidate;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    /// <summary>
    /// The directories whose <c>global.json</c> can govern SDK resolution while loading
    /// <paramref name="solutionPaths"/>: each solution's directory plus the directory of every project the
    /// solution DECLARES (read statically, never evaluated). Roslyn launches its BuildHost — which resolves
    /// the SDK through hostfxr — from the solution directory for a whole-solution load and from a project's
    /// directory when projects are opened individually (the multi-solution and per-project fallback paths),
    /// so a pin anywhere on those paths can fail the load. Distinct, in first-appearance order.
    /// </summary>
    public static IReadOnlyList<string> EvaluationDirectories(IEnumerable<string> solutionPaths)
    {
        var seen = new HashSet<string>(PathComparer);
        var result = new List<string>();
        foreach (var solution in solutionPaths)
        {
            Add(Path.GetDirectoryName(Path.GetFullPath(solution)));
            foreach (var project in SolutionProjectEnumerator.Enumerate(solution))
                Add(Path.GetDirectoryName(project));
        }
        return result;

        void Add(string? directory)
        {
            if (!string.IsNullOrEmpty(directory) && seen.Add(directory))
                result.Add(directory);
        }
    }
}

/// <summary>The outcome of asking hostfxr which .NET SDK it resolves for a working directory.</summary>
public sealed record SdkResolutionProbeResult
{
    /// <summary>The SDK version hostfxr resolved (null when resolution failed or no SDK instance was visible).</summary>
    public string? ResolvedSdkVersion { get; init; }

    /// <summary>The classified failure when hostfxr could not resolve an SDK; null on success.</summary>
    public HostFxrSdkResolutionError? Error { get; init; }

    /// <summary>True when hostfxr resolved an SDK for the working directory.</summary>
    public bool Resolved => Error is null;
}

/// <summary>
/// Asks hostfxr — the authority the Roslyn BuildHost uses — whether an SDK resolves for a working directory,
/// so a <c>global.json</c> pin that would fail the BuildHost can be detected BEFORE the load (issue #113).
/// </summary>
public interface ISdkResolutionProbe
{
    /// <summary>Resolves the SDK exactly as <c>hostfxr_resolve_sdk2(working_dir)</c> would.</summary>
    SdkResolutionProbeResult Probe(string workingDirectory);

    /// <summary>The installed SDK versions visible to this process, newest first (empty when unknown).</summary>
    IReadOnlyList<string> ListInstalledSdks();
}

/// <summary>
/// The production <see cref="ISdkResolutionProbe"/>: MSBuildLocator's .NET SDK discovery, which calls
/// <c>hostfxr_resolve_sdk2</c> with the given working directory (honoring <c>global.json</c> and its
/// <c>rollForward</c> policy) and throws the same <see cref="InvalidOperationException"/> the BuildHost
/// throws when resolution fails. The first instance it yields is the resolved SDK. Read-only: it never
/// registers an MSBuild instance.
/// </summary>
public sealed class HostFxrSdkResolutionProbe : ISdkResolutionProbe
{
    public static HostFxrSdkResolutionProbe Instance { get; } = new();

    public SdkResolutionProbeResult Probe(string workingDirectory)
    {
        try
        {
            var versions = Query(workingDirectory);
            return new SdkResolutionProbeResult { ResolvedSdkVersion = versions.FirstOrDefault() };
        }
        catch (InvalidOperationException ex) when (HostFxrSdkResolutionError.TryParse(ex.Message, out var error))
        {
            return new SdkResolutionProbeResult { Error = error };
        }
    }

    public IReadOnlyList<string> ListInstalledSdks()
    {
        // Any directory whose global.json (if any) resolves lists every installed SDK. Try the process's own
        // install directory first, then the temp directory; neither is part of a checkout.
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                return Query(directory)
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
                    .ToList();
            }
            catch (InvalidOperationException)
            {
                // That directory is under an unsatisfiable pin too; try the next one.
            }
        }
        return [];
    }

    private static List<string> Query(string workingDirectory) =>
        MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
            {
                DiscoveryTypes = DiscoveryType.DotNetSdk,
                WorkingDirectory = workingDirectory,
                AllowAllRuntimeVersions = true
            })
            .Select(i => i.Version.ToString())
            .ToList();
}
