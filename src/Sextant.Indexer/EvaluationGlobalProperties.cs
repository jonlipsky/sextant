using System.Text.RegularExpressions;

namespace Sextant.Indexer;

/// <summary>
/// The MSBuild global properties an evaluation of a repository's projects runs with. The
/// <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> load (<see cref="SolutionLoader"/>) and the service's
/// package restore (<c>PackageRestoreRunner</c>) both derive them from the same project set with <see cref="For"/>, so
/// restore and load evaluate the same project graph.
/// <para>
/// <see cref="EnableWindowsTargeting"/> (issue #293): on a non-Windows host the .NET SDK refuses to evaluate or restore a
/// project targeting <c>net*-windows*</c> (WPF, WinUI) with <c>NETSDK1100</c> unless it is <c>true</c>. With it, restore
/// downloads the Windows reference packs (<c>Microsoft.WindowsDesktop.App.Ref</c>, and the Windows SDK projection for a
/// <c>-windows10.0.x</c> TFM) as ordinary packages, and the project's code binds. But it also makes EVERY project's
/// restore download <c>Microsoft.WindowsDesktop.App.Ref</c> as a transitive targeting pack, which fails on a repository
/// whose feeds do not serve it. So it is set only when <see cref="TargetsWindows"/> finds a Windows target among the
/// projects; a repository without one evaluates and restores exactly as before. The decision depends only on the
/// checkout's files, never on the host, so the indexed output does not either.
/// </para>
/// </summary>
public static class EvaluationGlobalProperties
{
    /// <summary>The SDK property that allows Windows-targeting projects to evaluate and restore on any OS.</summary>
    public const string EnableWindowsTargeting = "EnableWindowsTargeting";

    private const long MaxScannedFileBytes = 4 * 1024 * 1024;

    private static readonly IReadOnlyList<KeyValuePair<string, string>> WindowsTargeting = [new(EnableWindowsTargeting, "true")];

    // A Windows TFM (net10.0-windows, net8.0-windows10.0.19041.0, $(Net)-windows10.0...), or a Windows UI framework switch.
    private static readonly Regex WindowsMarker = new(
        @"(?:\bnet\d+(?:\.\d+)?|\))-windows|<\s*(?:UseWPF|UseWinUI|UseWindowsForms)\b[^>]*>\s*true\s*<",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex XmlComment = new(
        "<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly string[] DirectoryBuildFiles = ["Directory.Build.props", "Directory.Build.targets"];

    /// <summary>
    /// The properties for evaluating <paramref name="projectPaths"/> (absolute project files): empty, or
    /// <c>EnableWindowsTargeting=true</c> when <see cref="TargetsWindows"/> finds a Windows target among them.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> For(IEnumerable<string> projectPaths)
        => TargetsWindows(projectPaths) ? WindowsTargeting : [];

    /// <summary>The properties for a solution's declared projects, or for one project file.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ForSolutionOrProject(string path)
        => For(Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx"
            ? SolutionProjectEnumerator.Enumerate(path)
            : [Path.GetFullPath(path)]);

    /// <summary>
    /// A cheap static scan (no evaluation: restore runs before the load): true when a project file, or the first
    /// <c>Directory.Build.props</c>/<c>Directory.Build.targets</c> MSBuild would import for it (searching upward from its
    /// directory), names a Windows TFM (<c>net*-windows*</c>, or a property-built <c>$(...)-windows*</c>) or sets
    /// <c>UseWPF</c>, <c>UseWinUI</c> or <c>UseWindowsForms</c> to <c>true</c> (with or without a <c>Condition</c>). XML
    /// comments are ignored.
    /// <para>
    /// Limits: a marker in a file those import (or in a <c>.props</c> a NuGet package adds) is not seen, so such a
    /// project still loads degraded off Windows, as before. A marker inside a false condition still counts, which only
    /// costs that repository the reference-pack download. A Windows-targeting project outside
    /// <paramref name="projectPaths"/> is not scanned. Unreadable files count as no marker.
    /// </para>
    /// </summary>
    public static bool TargetsWindows(IEnumerable<string> projectPaths)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projectPaths)
        {
            if (HasMarker(project, scanned))
                return true;
            var directory = Path.GetDirectoryName(Path.GetFullPath(project));
            foreach (var name in DirectoryBuildFiles)
            {
                if (FindUpward(directory, name) is { } file && HasMarker(file, scanned))
                    return true;
            }
        }
        return false;
    }

    private static string? FindUpward(string? directory, string fileName)
    {
        for (var dir = directory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static bool HasMarker(string file, HashSet<string> scanned)
    {
        var full = Path.GetFullPath(file);
        if (!scanned.Add(full))
            return false;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MaxScannedFileBytes)
                return false;
            var text = XmlComment.Replace(File.ReadAllText(full), string.Empty);
            return WindowsMarker.IsMatch(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
