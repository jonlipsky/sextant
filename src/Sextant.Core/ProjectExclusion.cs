using System.Text.RegularExpressions;
using System.Xml;

namespace Sextant.Core;

/// <summary>The machine-readable reasons a project version is an exclusion rather than a coverage gap (issue #351).</summary>
public static class ProjectExclusionReason
{
    /// <summary>
    /// The target framework has an OS-specific suffix and the failure is one this worker's platform produces
    /// (<see cref="PlatformEvidence"/>). The suffix alone is never enough.
    /// </summary>
    public const string PlatformUnavailable = "platform_unavailable";

    /// <summary>A declared project file does not exist in the checkout (a stale solution entry).</summary>
    public const string ProjectFileMissing = "project_file_missing";

    /// <summary>A non-SDK-style project file whose imported targets cannot be found on a modern worker.</summary>
    public const string LegacyProjectFormat = "legacy_project_format";
}

/// <summary>What proved that a platform-specific project version failed because of its platform.</summary>
public static class PlatformEvidence
{
    /// <summary>The platform workload (or its reference pack) is missing.</summary>
    public const string WorkloadMissing = "workload_missing";

    /// <summary>The SDK has no application host for the platform's runtime identifier.</summary>
    public const string NoApplicationHost = "no_application_host";

    /// <summary>A Windows-only tool or architecture requirement failed on a non-Windows worker.</summary>
    public const string WindowsTooling = "windows_tooling";

    /// <summary>The version was compiled against a platform reference pack because its workload is missing.</summary>
    public const string PlatformPackBinding = "platform_pack_binding";

    /// <summary>A legacy Xamarin project reported that it needs desktop MSBuild (Visual Studio), which this worker never has.</summary>
    public const string DesktopMsbuildRequired = "desktop_msbuild_required";
}

/// <summary>
/// A project version the worker structurally cannot build, recorded instead of a gap
/// (<see cref="SnapshotCoverage.Exclusions"/>). Serialized snake_case.
/// </summary>
/// <param name="Project">The project file relative to its repository root (forward slashes).</param>
/// <param name="TargetFramework">The target framework of the version, when known.</param>
/// <param name="Reason">A <see cref="ProjectExclusionReason"/> value.</param>
/// <param name="Detail">For <c>platform_unavailable</c>, the platform and the evidence kind (<c>ios; workload_missing</c>).</param>
public sealed record ProjectExclusion(
    string Project,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? TargetFramework,
    string Reason,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Detail = null);

/// <summary>The operating system a worker builds on, for <see cref="ProjectExclusionClassifier"/>.</summary>
public readonly record struct WorkerPlatform(bool IsWindows, bool IsMacOS)
{
    /// <summary>The OS this process runs on.</summary>
    public static WorkerPlatform Current { get; } = new(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());
}

/// <summary>
/// The one classifier deciding whether a project version is EXCLUDED (issue #351): structurally unbuildable on this
/// worker, so its absence is not an indexing gap. An exclusion needs EVIDENCE, never only a target framework or a
/// file shape; everything unclear stays a gap.
/// </summary>
public static partial class ProjectExclusionClassifier
{
    /// <summary>The largest project file read to classify it as legacy.</summary>
    public const int MaxProjectFileBytes = 1024 * 1024;

    /// <summary>The most directory entries read to prove a project file is missing rather than differing only by case.</summary>
    public const int MaxDirectoryEntries = 20_000;

    private static readonly string[] Platforms =
        ["maccatalyst", "macos", "tvos", "watchos", "ios", "android", "tizen", "windows"];

    private static readonly HashSet<string> ApplePlatforms = new(StringComparer.Ordinal)
        { "ios", "maccatalyst", "macos", "tvos", "watchos" };

    // What the SDK and MSBuild report for a project whose platform workload is not installed (or not installable).
    // The ONE definition: PlatformReferencePacks.IsWorkloadFailure delegates here.
    [GeneratedRegex(@"NETSDK1147|NETSDK1139|NETSDK1178|isn't supported on this platform|Platform version is not present",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WorkloadFailurePattern();

    [GeneratedRegex(@"you need to use desktop msbuild",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DesktopMsbuildPattern();

    [GeneratedRegex(@"There is no application host available for the specified RuntimeIdentifier",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NoApplicationHostPattern();

    [GeneratedRegex(@"WindowsAppSDKSelfContained requires a supported Windows architecture|XamlCompiler output file|(?<![\w.-])mt\.exe\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WindowsToolingPattern();

    // What MSBuildWorkspace reports for a non-SDK project whose imported targets are absent: the import is silently
    // dropped and the project then has no Compile target ("Project does not contain 'Compile' target.", confirmed
    // by a real load of a Xamarin.iOS-style project), or MSBuild names the missing import.
    [GeneratedRegex(@"The imported project .+ was not found|Project does not contain 'Compile' target",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MissingImportPattern();

    /// <summary>True when an evaluation failure message is the missing-workload failure.</summary>
    public static bool IsWorkloadFailure(string? reason) => Matches(WorkloadFailurePattern(), reason);

    /// <summary>
    /// The OS platform of a target framework (<c>net10.0-ios17.0</c> gives <c>ios</c>), or null for a plain framework
    /// (<c>net10.0</c>, <c>netstandard2.0</c>, <c>net45</c>) or an unrecognised suffix.
    /// </summary>
    public static string? PlatformOf(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
            return null;
        // Xamarin TFMs carry the platform in the framework name (xamarin.ios, xamarinios10), not after a dash.
        if (targetFramework.StartsWith("xamarin", StringComparison.OrdinalIgnoreCase))
            return "xamarin";
        var dash = targetFramework.IndexOf('-');
        if (dash < 0)
            return null;
        var suffix = targetFramework[(dash + 1)..];
        foreach (var platform in Platforms)
            if (suffix.StartsWith(platform, StringComparison.OrdinalIgnoreCase)
                && (suffix.Length == platform.Length || !char.IsLetter(suffix[platform.Length])))
                return platform;
        return null;
    }

    /// <summary>
    /// The platform <paramref name="targetFramework"/> names when this worker cannot normally build it: Apple
    /// platforms unless macOS, <c>windows</c> unless Windows, <c>android</c>/<c>tizen</c> always. This is only the
    /// first half of an exclusion: <see cref="EvidenceFor"/> must find the platform's own failure too.
    /// </summary>
    public static string? UnavailablePlatform(string? targetFramework, WorkerPlatform? worker = null)
    {
        var os = worker ?? WorkerPlatform.Current;
        var platform = PlatformOf(targetFramework);
        return platform switch
        {
            "ios" or "maccatalyst" or "macos" or "tvos" or "watchos" => os.IsMacOS ? null : platform,
            "windows" => os.IsWindows ? null : platform,
            "android" or "tizen" or "xamarin" => platform,
            _ => null
        };
    }

    /// <summary>
    /// The kind of evidence in <paramref name="reason"/> that a version of <paramref name="platform"/> failed because
    /// of its platform, or null. <paramref name="platformPackBound"/> (the version was bound against a platform
    /// reference pack) is itself evidence that the workload is missing. The message need not name the version's own
    /// target framework: a macOS version is blocked by a sibling's platform failure. A message that merely mentions
    /// a platform is not evidence, and an SDK-pin failure never is.
    /// </summary>
    public static string? EvidenceFor(string platform, string? reason, bool platformPackBound = false)
    {
        if (platformPackBound)
            return PlatformEvidence.PlatformPackBinding;
        if (string.IsNullOrEmpty(reason))
            return null;
        if (IsWorkloadFailure(reason))
            return PlatformEvidence.WorkloadMissing;
        if ((ApplePlatforms.Contains(platform) || platform == "android") && Matches(NoApplicationHostPattern(), reason))
            return PlatformEvidence.NoApplicationHost;
        if (platform == "xamarin" && Matches(DesktopMsbuildPattern(), reason))
            return PlatformEvidence.DesktopMsbuildRequired;
        if (platform == "windows" && Matches(WindowsToolingPattern(), reason))
            return PlatformEvidence.WindowsTooling;
        return null;
    }

    /// <summary>
    /// Classifies one project version. <paramref name="projectFile"/> is its full path and
    /// <paramref name="failureReason"/> the load failure that left it skipped or degraded.
    /// <paramref name="platformPackBound"/> marks a version bound against a platform reference pack.
    /// <paramref name="unpopulatedSubmodules"/> holds the full paths of declared submodules that are not checked out
    /// (a missing file inside one keeps that submodule's own gap) and <paramref name="checkoutRoot"/> the checkout
    /// (an existing project file outside it is never an exclusion; a declared one that provably does not exist is missing wherever it is declared). Null when the
    /// version is not an exclusion.
    /// </summary>
    public static ProjectExclusion? Classify(
        string project, string? targetFramework, string projectFile, string? failureReason,
        bool platformPackBound = false, IReadOnlyList<string>? unpopulatedSubmodules = null,
        string? checkoutRoot = null, WorkerPlatform? worker = null)
    {
        targetFramework = string.IsNullOrWhiteSpace(targetFramework) ? null : targetFramework;

        if (UnavailablePlatform(targetFramework, worker) is { } platform
            && EvidenceFor(platform, failureReason, platformPackBound) is { } evidence)
        {
            return new ProjectExclusion(
                project, targetFramework, ProjectExclusionReason.PlatformUnavailable, $"{platform}; {evidence}");
        }

        if (!File.Exists(projectFile))
        {
            return !IsInside(projectFile, unpopulatedSubmodules) && IsProvablyMissing(projectFile)
                ? new ProjectExclusion(project, targetFramework, ProjectExclusionReason.ProjectFileMissing)
                : null;
        }

        // A file that exists outside the checkout is a sibling repository's: never classified as legacy.
        if (checkoutRoot is not null && !IsInside(projectFile, [checkoutRoot]))
            return null;

        return IsLegacyProjectFormat(projectFile, failureReason)
            ? new ProjectExclusion(project, targetFramework, ProjectExclusionReason.LegacyProjectFormat)
            : null;
    }

    /// <summary>
    /// True when <paramref name="projectFile"/> does not exist and no entry of the directory it would be in (or of the
    /// deepest directory that exists) differs from the missing name only by case: on a case-sensitive file system a
    /// case-only mismatch is a real gap, not a stale entry. Reads at most <see cref="MaxDirectoryEntries"/> entries;
    /// a larger directory, or any read failure, is not provable and is false.
    /// </summary>
    public static bool IsProvablyMissing(string projectFile)
    {
        try
        {
            var full = Path.GetFullPath(projectFile);
            if (File.Exists(full) || Directory.Exists(full))
                return false;
            var name = Path.GetFileName(full);
            var directory = Path.GetDirectoryName(full);
            while (directory is not null && !Directory.Exists(directory))
            {
                name = Path.GetFileName(directory);
                directory = Path.GetDirectoryName(directory);
            }
            if (directory is null || name.Length == 0)
                return false;
            var seen = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++seen > MaxDirectoryEntries)
                    return false;
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the file is a non-SDK-style project (its root <c>Project</c> has no <c>Sdk</c> attribute, no
    /// <c>&lt;Sdk&gt;</c> child and no <c>&lt;Import Sdk="..."&gt;</c>) AND <paramref name="failureReason"/> is a
    /// missing-import failure (<see cref="MissingImportPattern"/>); for the "no Compile target" form the project must
    /// also import a path built from a property, which is what went missing. A file over
    /// <see cref="MaxProjectFileBytes"/>, or a read or parse failure, is false.
    /// </summary>
    public static bool IsLegacyProjectFormat(string projectFile, string? failureReason)
    {
        if (!Matches(MissingImportPattern(), failureReason))
            return false;
        try
        {
            var info = new FileInfo(projectFile);
            if (!info.Exists || info.Length > MaxProjectFileBytes)
                return false;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxProjectFileBytes
            };
            using var stream = new FileStream(projectFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = XmlReader.Create(stream, settings);
            var root = System.Xml.Linq.XDocument.Load(reader).Root;
            if (root is null || !string.Equals(root.Name.LocalName, "Project", StringComparison.Ordinal))
                return false;
            if (root.Attributes().Any(a => string.Equals(a.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase)))
                return false;
            var imports = root.Descendants().Where(e => string.Equals(e.Name.LocalName, "Import", StringComparison.Ordinal)).ToList();
            if (root.Descendants().Any(e => string.Equals(e.Name.LocalName, "Sdk", StringComparison.Ordinal) && e.Parent == root)
                || imports.Any(e => e.Attributes().Any(a => string.Equals(a.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase))))
                return false;
            var named = failureReason is not null && failureReason.Contains("imported project", StringComparison.OrdinalIgnoreCase);
            return named || imports.Any(e =>
                e.Attribute("Project")?.Value.Contains("$(", StringComparison.Ordinal) == true);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>True when two target frameworks are the same version; a blank one is the null framework.</summary>
    public static bool SameFramework(string? a, string? b)
        => string.Equals(string.IsNullOrWhiteSpace(a) ? null : a, string.IsNullOrWhiteSpace(b) ? null : b, StringComparison.Ordinal);

    /// <summary>
    /// The exact (project, target framework) versions <paramref name="exclusions"/> excludes, for
    /// <see cref="IsVersionExcluded"/>. A null target framework is its own key and covers no other version.
    /// </summary>
    public static HashSet<(string Project, string? TargetFramework)> VersionKeys(IEnumerable<ProjectExclusion>? exclusions)
        => (exclusions ?? []).Select(e => (e.Project, TargetFramework: Normalize(e.TargetFramework))).ToHashSet();

    /// <summary>True when exactly this project version is excluded.</summary>
    public static bool IsVersionExcluded(
        HashSet<(string Project, string? TargetFramework)> keys, string project, string? targetFramework)
        => keys.Contains((project, Normalize(targetFramework)));

    private static string? Normalize(string? tfm) => string.IsNullOrWhiteSpace(tfm) ? null : tfm;

    /// <summary>
    /// Records <paramref name="exclusions"/> on <paramref name="coverage"/>: the distinct versions in deterministic
    /// order (capped at <see cref="SnapshotCoverage.MaxExclusions"/> in the serialized list; the full set rides
    /// on <see cref="SnapshotCoverage.AllExclusions"/>), the uncapped count, and one note. The verdict is never
    /// changed. Unchanged when there are none.
    /// </summary>
    public static SnapshotCoverage Record(SnapshotCoverage coverage, IEnumerable<ProjectExclusion> exclusions)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var all = Distinct(exclusions);
        if (all.Count == 0)
            return coverage;
        return coverage with
        {
            Exclusions = all.Take(SnapshotCoverage.MaxExclusions).ToList(),
            AllExclusions = all,
            ProjectVersionsExcluded = all.Count,
            Notes = [.. coverage.Notes ?? [], Describe(all)]
        };
    }

    /// <summary>The distinct exclusions, ordered by project, target framework, then reason.</summary>
    public static IReadOnlyList<ProjectExclusion> Distinct(IEnumerable<ProjectExclusion> exclusions)
        => exclusions
            .GroupBy(e => (e.Project, e.TargetFramework, e.Reason))
            .Select(g => g.First())
            .OrderBy(e => e.Project, StringComparer.Ordinal)
            .ThenBy(e => e.TargetFramework, StringComparer.Ordinal)
            .ThenBy(e => e.Reason, StringComparer.Ordinal)
            .ToList();

    private static string Describe(IReadOnlyList<ProjectExclusion> all)
    {
        var parts = new List<string>();
        foreach (var group in all.GroupBy(e => e.Reason).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var detail = group
                .GroupBy(e => e.Detail?.Split(';')[0])
                .Where(d => d.Key is not null)
                .OrderByDescending(d => d.Count()).ThenBy(d => d.Key, StringComparer.Ordinal)
                .Select(d => $"{d.Key} {d.Count()}")
                .ToList();
            parts.Add(detail.Count > 0 ? $"{group.Key} ({string.Join(", ", detail)})" : $"{group.Key} ({group.Count()})");
        }
        var listed = all.Count > SnapshotCoverage.MaxExclusions
            ? $"the first {SnapshotCoverage.MaxExclusions} are listed in `exclusions`"
            : "they are listed in `exclusions`";
        return $"{all.Count} project version(s) were excluded because this worker cannot build them: " +
               $"{string.Join(", ", parts)}; {listed}.";
    }

    private static bool Matches(Regex pattern, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsInside(string file, IReadOnlyList<string>? directories)
    {
        if (directories is null)
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(file);
        return directories.Any(d =>
        {
            var dir = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
            return full.StartsWith(dir, comparison);
        });
    }
}
