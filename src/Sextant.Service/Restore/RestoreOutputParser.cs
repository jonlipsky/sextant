using System.Text.RegularExpressions;

namespace Sextant.Service.Restore;

/// <summary>
/// Reduces <c>dotnet restore</c> console output to the facts the worker records: which projects failed to
/// restore, with which NuGet error codes, and which package ids could not be found. The raw messages are never
/// kept: they can name package source URLs, and a source URL can carry credentials. Project paths are made
/// relative to the checkout (a path outside it keeps only its file name). Thread-safe, because stdout and
/// stderr are drained concurrently.
/// </summary>
internal sealed partial class RestoreOutputParser(string checkoutDir)
{
    /// <summary>The cap on projects tracked; later projects only count towards <see cref="ProjectsDropped"/>.</summary>
    internal const int MaxProjects = 500;

    /// <summary>The cap on package ids and error codes kept per project.</summary>
    internal const int MaxValuesPerProject = 20;

    /// <summary>The cap on error codes that name no project (for example an MSBuild switch error).</summary>
    internal const int MaxGeneralCodes = 10;

    private const int MaxPackageIdLength = 100;

    // Restore codes meaning "a package with this id (or version) was not found in any source".
    private static readonly HashSet<string> MissingPackageCodes = new(StringComparer.Ordinal) { "NU1101", "NU1102", "NU1103" };

    // Restore codes meaning "a configured package source could not be reached". The runner passes
    // --ignore-failed-sources, under which NuGet reports the failure as the WARNING NU1801 instead of the error
    // NU1301, so NU1801 is accepted from a warning line too (a repository's TreatWarningsAsErrors makes it an error).
    internal static readonly IReadOnlySet<string> SourceUnreachableCodes = new HashSet<string>(StringComparer.Ordinal) { "NU1301", "NU1801" };

    private readonly string _checkoutDir = Path.GetFullPath(checkoutDir);
    private readonly object _gate = new();
    private readonly Dictionary<string, Accumulator> _projects = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _generalCodes = new(StringComparer.Ordinal);
    private int _projectsDropped;
    private bool _sourceUnreachable;

    /// <summary>A source-unreachable code was reported on a line that named no project.</summary>
    public bool SourceUnreachableGeneral
    {
        get
        {
            lock (_gate)
                return _sourceUnreachable;
        }
    }

    /// <summary>Projects that reported errors after <see cref="MaxProjects"/> were already tracked.</summary>
    public int ProjectsDropped
    {
        get
        {
            lock (_gate)
                return _projectsDropped;
        }
    }

    /// <summary>Feeds one line of restore output.</summary>
    public void Accept(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        // Checked first: an NU1801 message can itself contain "error" ("An error occurred while sending ...").
        if (line.Contains("NU1801", StringComparison.OrdinalIgnoreCase) && TryAcceptSourceWarning(line))
            return;
        if (!line.Contains("error", StringComparison.OrdinalIgnoreCase))
            return;

        var projectMatch = ProjectError().Match(line);
        if (projectMatch.Success)
        {
            var code = projectMatch.Groups["code"].Value.ToUpperInvariant();
            string? packageId = null;
            if (MissingPackageCodes.Contains(code))
            {
                var package = MissingPackage().Match(projectMatch.Groups["msg"].Value);
                if (package.Success && package.Groups["id"].Value.Length <= MaxPackageIdLength)
                    packageId = package.Groups["id"].Value;
            }
            Record(Relativize(projectMatch.Groups["path"].Value), code, packageId);
            return;
        }

        var generalMatch = GeneralError().Match(line);
        if (generalMatch.Success)
        {
            lock (_gate)
            {
                if (_generalCodes.Count < MaxGeneralCodes)
                    _generalCodes.Add(generalMatch.Groups["code"].Value.ToUpperInvariant());
            }
        }
    }

    /// <summary>The per-project restore failures seen so far, ordered by project path.</summary>
    public IReadOnlyList<PackageRestoreProjectIssue> Projects()
    {
        lock (_gate)
        {
            return _projects
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new PackageRestoreProjectIssue
                {
                    Project = p.Key,
                    Codes = [.. p.Value.Codes],
                    MissingPackages = [.. p.Value.MissingPackages],
                    SourceUnreachable = p.Value.Codes.Overlaps(SourceUnreachableCodes)
                })
                .ToList();
        }
    }

    /// <summary>Error codes that named no project, ordered.</summary>
    public IReadOnlyList<string> GeneralCodes()
    {
        lock (_gate)
            return [.. _generalCodes];
    }

    // A source-unreachable WARNING (NU1801 under --ignore-failed-sources): recorded against its project when the
    // line names one, else as a general flag. Only the code is kept, never the message (it names the source URL).
    private bool TryAcceptSourceWarning(string line)
    {
        var projectMatch = ProjectSourceWarning().Match(line);
        if (projectMatch.Success)
        {
            Record(Relativize(projectMatch.Groups["path"].Value), projectMatch.Groups["code"].Value.ToUpperInvariant(), null);
            return true;
        }
        if (!GeneralSourceWarning().IsMatch(line))
            return false;
        lock (_gate)
            _sourceUnreachable = true;
        return true;
    }

    private void Record(string project, string code, string? packageId)
    {
        lock (_gate)
        {
            if (!_projects.TryGetValue(project, out var accumulator))
            {
                if (_projects.Count >= MaxProjects)
                {
                    _projectsDropped++;
                    return;
                }
                accumulator = new Accumulator();
                _projects[project] = accumulator;
            }
            if (accumulator.Codes.Count < MaxValuesPerProject)
                accumulator.Codes.Add(code);
            if (packageId is not null && accumulator.MissingPackages.Count < MaxValuesPerProject)
                accumulator.MissingPackages.Add(packageId);
        }
    }

    private string Relativize(string path)
    {
        var trimmed = path.Trim();
        string full;
        try
        {
            full = Path.GetFullPath(trimmed, _checkoutDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var name = Path.GetFileName(trimmed.Replace('\\', '/').TrimEnd('/'));
            return name.Length > 0 ? name : trimmed;
        }

        var relative = Path.GetRelativePath(_checkoutDir, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return Path.GetFileName(full);
        }
        return relative.Replace('\\', '/');
    }

    // "<project path> : error NU1101: <message> [<solution path>]", the console logger's shape for a
    // project-scoped restore error. The path stops at the first project extension followed by " : error".
    [GeneratedRegex(@"^\s*(?<path>[^\r\n]+?\.(?:cs|vb|fs)proj)\s*:\s*error\s+(?<code>NU\d{4})\s*:\s*(?<msg>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectError();

    // Any other "error XX1234:" line (an MSBuild switch error, a solution that cannot be read, ...).
    [GeneratedRegex(@"(?:^|[\s:])error\s+(?<code>[A-Za-z]{2,8}\d{3,5})\s*:",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GeneralError();

    // "<project path> : warning NU1801: Unable to load the service index for source ... [<solution path>]".
    [GeneratedRegex(@"^\s*(?<path>[^\r\n]+?\.(?:cs|vb|fs)proj)\s*:\s*warning\s+(?<code>NU1801)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectSourceWarning();

    // An NU1801 warning that names no project.
    [GeneratedRegex(@"(?:^|[\s:])warning\s+NU1801\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GeneralSourceWarning();

    // NU1101 "Unable to find package Foo.Bar. No packages exist ...", NU1102 "Unable to find package Foo with
    // version (>= 1.0)", NU1103 "Unable to find a stable package Foo with version ...".
    [GeneratedRegex(@"Unable to find (?:a stable )?package '?(?<id>[A-Za-z0-9_.-]+?)'?\.?(?:\s|$)",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MissingPackage();

    private sealed class Accumulator
    {
        public SortedSet<string> Codes { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> MissingPackages { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
