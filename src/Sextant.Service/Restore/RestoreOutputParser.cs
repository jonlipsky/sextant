using System.Text.RegularExpressions;
using Sextant.Indexer;

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
    private readonly HashSet<string> _restored = new(CheckoutInventory.PathComparer);
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

    /// <summary>The line the generated per-project traversal prints after a project's restore task succeeded.</summary>
    internal const string RestoredSentinel = "sextant-restore-ok: ";

    /// <summary>Feeds one line of restore output.</summary>
    public void Accept(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(RestoredSentinel, StringComparison.Ordinal))
        {
            var path = trimmed[RestoredSentinel.Length..].Trim();
            if (path.Length > 0 && Path.IsPathRooted(path))
            {
                lock (_gate)
                    _restored.Add(Path.GetFullPath(path));
            }
            return;
        }
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
            Record(Relativize(projectMatch.Groups["path"].Value), code, packageId, projectMatch.Groups["msg"].Value);
            return;
        }

        var generalMatch = GeneralError().Match(line);
        if (generalMatch.Success)
        {
            // MSB3202 names the missing project file in its text ("The project file "<path>" was not found."), while the
            // line itself ends in the solution: the missing project is the one it belongs to.
            if (string.Equals(generalMatch.Groups["code"].Value, "MSB3202", StringComparison.OrdinalIgnoreCase)
                && MissingProjectFile().Match(line) is { Success: true } missing
                && IsRootedInsideCheckout(missing.Groups["path"].Value))
            {
                Record(Relativize(missing.Groups["path"].Value), "MSB3202", null, line[(generalMatch.Index + generalMatch.Length)..]);
                return;
            }
            // An MSBuild error that carries no project prefix still names the project in its trailing "[<project>]"
            // (issue #296: a retried project's evaluation error, for example NETSDK1013).
            var trailing = TrailingProject().Match(line);
            if (trailing.Success)
            {
                Record(
                    Relativize(trailing.Groups["path"].Value), generalMatch.Groups["code"].Value.ToUpperInvariant(), null,
                    line[(generalMatch.Index + generalMatch.Length)..]);
                return;
            }
            lock (_gate)
            {
                if (_generalCodes.Count < MaxGeneralCodes)
                    _generalCodes.Add(generalMatch.Groups["code"].Value.ToUpperInvariant());
            }
        }
    }

    /// <summary>True when the traversal positively reported that this project's restore task succeeded (issue #290).</summary>
    public bool WasRestored(string projectPath)
    {
        lock (_gate)
            return _restored.Contains(Path.GetFullPath(projectPath));
    }

    /// <summary>Drops the failures recorded for a project a repair retry restored.</summary>
    public void Forget(string projectPath)
    {
        var key = Relativize(projectPath);
        lock (_gate)
            _projects.Remove(key);
    }

    /// <summary>Adds everything <paramref name="other"/> recorded (issue #296: a failed attempt's findings, kept when its retry could not run).</summary>
    public void MergeFrom(RestoreOutputParser other)
    {
        var projects = other.Projects();
        var general = other.GeneralCodes();
        var dropped = other.ProjectsDropped;
        var unreachable = other.SourceUnreachableGeneral;
        var restored = other.RestoredProjects();
        lock (_gate)
        {
            _restored.UnionWith(restored);
            foreach (var issue in projects)
            {
                if (!_projects.TryGetValue(issue.Project, out var accumulator))
                {
                    if (_projects.Count >= MaxProjects)
                    {
                        _projectsDropped++;
                        continue;
                    }
                    accumulator = new Accumulator();
                    _projects[issue.Project] = accumulator;
                }
                Absorb(accumulator, issue);
            }
            foreach (var code in general)
                if (_generalCodes.Count < MaxGeneralCodes)
                    _generalCodes.Add(code);
            _projectsDropped += dropped;
            _sourceUnreachable |= unreachable;
        }
    }

    /// <summary>
    /// Adds findings recorded by an earlier job (issue #349: the failure of a whole-solution restore the restore state
    /// remembers, so a later job does not run it again to learn the same thing).
    /// </summary>
    public void AddFindings(IEnumerable<PackageRestoreProjectIssue> issues, IEnumerable<string> generalCodes)
    {
        lock (_gate)
        {
            foreach (var issue in issues)
            {
                if (!_projects.TryGetValue(issue.Project, out var accumulator))
                {
                    if (_projects.Count >= MaxProjects)
                    {
                        _projectsDropped++;
                        continue;
                    }
                    accumulator = new Accumulator();
                    _projects[issue.Project] = accumulator;
                }
                Absorb(accumulator, issue);
            }
            foreach (var code in generalCodes)
                if (_generalCodes.Count < MaxGeneralCodes)
                    _generalCodes.Add(code);
        }
    }

    private static void Absorb(Accumulator accumulator, PackageRestoreProjectIssue issue)
    {
        foreach (var code in issue.Codes)
            accumulator.Codes.Add(code);
        foreach (var package in issue.MissingPackages)
            accumulator.MissingPackages.Add(package);
        foreach (var (code, message) in issue.Messages)
            accumulator.Messages.TryAdd(code, message);
        accumulator.TransientMessage |= issue.TransientMessage;
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
                    Messages = new Dictionary<string, string>(p.Value.Messages),
                    TransientMessage = p.Value.TransientMessage,
                    SourceUnreachable = p.Value.Codes.Overlaps(SourceUnreachableCodes)
                })
                .ToList();
        }
    }

    private string[] RestoredProjects()
    {
        lock (_gate)
            return [.. _restored];
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

    private void Record(string project, string code, string? packageId, string? message = null)
    {
        var sanitized = string.IsNullOrWhiteSpace(message) ? null : RestoreErrorClassifier.Sanitize(message, _checkoutDir);
        var transient = RestoreErrorClassifier.IsTransientMessage(sanitized);
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
            if (!string.IsNullOrEmpty(sanitized) && accumulator.Messages.Count < MaxValuesPerProject)
                accumulator.Messages.TryAdd(code, sanitized);
            accumulator.TransientMessage |= transient;
        }
    }

    /// <summary>The key <see cref="Projects"/> uses for a project file (issue #290).</summary>
    internal string ProjectKey(string projectPath) => Relativize(projectPath);

    private bool IsRootedInsideCheckout(string path)
    {
        try
        {
            var trimmed = path.Trim();
            if (!Path.IsPathRooted(trimmed))
                return false;
            var relative = Path.GetRelativePath(_checkoutDir, Path.GetFullPath(trimmed));
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
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
    // project-scoped restore error. The path stops at the first project extension followed by " : error"; an MSBuild
    // evaluation error carries the position of the offending line, "<project path>(104,3): error MSB4019: ...", and is
    // the same shape (the file it names is the project that failed, never the file that is missing).
    [GeneratedRegex(@"^\s*(?<path>[^\r\n]+?\.(?:cs|vb|fs)proj)(?:\(\d+(?:,\d+)*\))?\s*:\s*error\s+(?<code>(?:NU|NETSDK|MSB)\d{4})\s*:\s*(?<msg>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectError();

    // MSB3202: 'The project file "<path>" was not found.'
    [GeneratedRegex(@"The project file ""(?<path>[^""\r\n]+?\.(?:cs|vb|fs)proj)"" was not found",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MissingProjectFile();

    // Any other "error XX1234:" line (an MSBuild switch error, a solution that cannot be read, ...).
    [GeneratedRegex(@"(?:^|[\s:])error\s+(?<code>[A-Za-z]{2,8}\d{3,5})\s*:",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GeneralError();

    // The "[<project path>]" an MSBuild error line ends with.
    [GeneratedRegex(@"\[(?<path>[^\[\]\r\n]+?\.(?:cs|vb|fs)proj)\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingProject();

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
        public SortedDictionary<string, string> Messages { get; } = new(StringComparer.Ordinal);
        public bool TransientMessage { get; set; }
    }
}
