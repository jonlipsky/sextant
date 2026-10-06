using System.Text.RegularExpressions;
using System.Xml;

namespace Sextant.Indexer;

/// <summary>
/// Enumerates the project files DECLARED in a solution without evaluating them, so a resilient loader
/// can open each one individually and isolate a per-project load failure (issue #90). Supports the
/// classic <c>.sln</c> format and the XML <c>.slnx</c> format; only projects Roslyn can open
/// (<c>.csproj</c>/<c>.vbproj</c>/<c>.fsproj</c>) are returned, so solution folders and unrecognized
/// entries are ignored. A failed read discards every project, but is distinct from a readable empty solution.
/// </summary>
internal static class SolutionProjectEnumerator
{
    internal sealed record Result(bool IsReadable, IReadOnlyList<string> Projects);

    private static readonly HashSet<string> RecognizedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".vbproj", ".fsproj" };

    // Classic .sln project line: Project("{typeGuid}") = "Name", "relative\path.csproj", "{projectGuid}"
    // A match timeout is passed defensively (rule S6444) so a pathological line can never hang parsing;
    // the pattern itself is linear, so the timeout is a belt-and-braces bound, not an expected path.
    private static readonly Regex QuotedToken =
        new("\"([^\"]*)\"", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Returns the absolute, de-duplicated paths of the recognized projects declared in
    /// <paramref name="solutionPath"/>, preserving declaration order. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Enumerate(string solutionPath) => Read(solutionPath).Projects;

    public static Result Read(string solutionPath)
    {
        try
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
            var relativePaths = Path.GetExtension(solutionPath).ToLowerInvariant() switch
            {
                ".slnx" => ParseSlnx(solutionPath),
                ".sln" => ParseSln(solutionPath),
                _ => throw new FormatException("Unrecognized solution format.")
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var relative in relativePaths)
            {
                var normalized = relative.Replace('\\', Path.DirectorySeparatorChar)
                                         .Replace('/', Path.DirectorySeparatorChar);
                var absolute = Path.GetFullPath(Path.Combine(solutionDir, normalized));
                if (RecognizedExtensions.Contains(Path.GetExtension(absolute)) && seen.Add(absolute))
                    result.Add(absolute);
            }
            return new Result(true, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                   or FormatException or ArgumentException or RegexMatchTimeoutException)
        {
            return new Result(false, []);
        }
    }

    private enum SlnState
    {
        Header,
        TopLevel,
        Project,
        ProjectSection,
        Global,
        GlobalSection
    }

    private static IEnumerable<string> ParseSln(string solutionPath)
    {
        var paths = new List<string>();
        var state = SlnState.Header;
        foreach (var line in File.ReadLines(solutionPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            state = ReadSlnEntry(trimmed, state, paths);
        }
        if (state != SlnState.TopLevel)
            throw new FormatException("Truncated solution.");
        return paths;
    }

    private static SlnState ReadSlnEntry(string entry, SlnState state, List<string> paths)
    {
        if (state == SlnState.Header)
        {
            if (!entry.StartsWith("Microsoft Visual Studio Solution File, Format Version ", StringComparison.Ordinal))
                throw new FormatException("Invalid solution header.");
            return SlnState.TopLevel;
        }
        if (entry.StartsWith('#'))
            return state;
        switch (entry)
        {
            case "EndProject":
                if (state != SlnState.Project) throw new FormatException("Unmatched EndProject.");
                return SlnState.TopLevel;
            case "Global":
                if (state != SlnState.TopLevel) throw new FormatException("Invalid Global block.");
                return SlnState.Global;
            case "EndGlobal":
                if (state != SlnState.Global) throw new FormatException("Unmatched EndGlobal.");
                return SlnState.TopLevel;
            case "EndProjectSection":
                if (state != SlnState.ProjectSection) throw new FormatException("Unmatched section end.");
                return SlnState.Project;
            case "EndGlobalSection":
                if (state != SlnState.GlobalSection) throw new FormatException("Unmatched section end.");
                return SlnState.Global;
        }
        if (entry.StartsWith("ProjectSection(", StringComparison.Ordinal))
        {
            if (state != SlnState.Project || !entry.Contains(") =", StringComparison.Ordinal))
                throw new FormatException("Invalid solution section.");
            return SlnState.ProjectSection;
        }
        if (entry.StartsWith("GlobalSection(", StringComparison.Ordinal))
        {
            if (state != SlnState.Global || !entry.Contains(") =", StringComparison.Ordinal))
                throw new FormatException("Invalid solution section.");
            return SlnState.GlobalSection;
        }
        if (entry.StartsWith("Project(", StringComparison.Ordinal))
        {
            if (state != SlnState.TopLevel) throw new FormatException("Invalid project block.");
            var path = ReadSlnProjectPath(entry);
            if (RecognizedExtensions.Contains(Path.GetExtension(path)))
                paths.Add(path);
            return SlnState.Project;
        }
        if (state is SlnState.ProjectSection or SlnState.GlobalSection)
        {
            if (!entry.Contains('=')) throw new FormatException("Invalid section entry.");
            return state;
        }
        if (state == SlnState.TopLevel &&
            (entry.StartsWith("VisualStudioVersion =", StringComparison.Ordinal) ||
             entry.StartsWith("MinimumVisualStudioVersion =", StringComparison.Ordinal)))
            return state;
        throw new FormatException("Unrecognized solution entry.");
    }

    private static string ReadSlnProjectPath(string entry)
    {
        // The path is the second RHS token, even when the display name ends in a project extension.
        var separator = entry.IndexOf('=');
        if (separator < 0)
            throw new FormatException("Invalid project entry.");
        var tokens = QuotedToken.Matches(entry[(separator + 1)..]);
        var typeTokens = QuotedToken.Matches(entry[..separator]);
        if (tokens.Count != 3 || typeTokens.Count != 1 ||
            !Guid.TryParse(typeTokens[0].Groups[1].Value, out _) ||
            !Guid.TryParse(tokens[2].Groups[1].Value, out _))
            throw new FormatException("Invalid project entry.");
        return tokens[1].Groups[1].Value;
    }

    private static IEnumerable<string> ParseSlnx(string solutionPath)
    {
        var paths = new List<string>();
        // Harden against XXE: prohibit DTD processing and disable external entity resolution outright
        // (a solution file is repo-controlled, but the parser must never fetch external entities). Modern
        // .NET already defaults to these, but setting them explicitly is required to be provably safe.
        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(solutionPath, settings);
        reader.MoveToContent();
        if (reader.Name != "Solution")
            throw new FormatException("Invalid solution root.");
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.Name, "Project", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = reader.GetAttribute("Path");
            if (string.IsNullOrWhiteSpace(path))
                throw new FormatException("Project has no path.");
            paths.Add(path);
        }
        return paths;
    }
}
