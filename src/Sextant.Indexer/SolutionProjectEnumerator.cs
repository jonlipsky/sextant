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

    private static IEnumerable<string> ParseSln(string solutionPath)
    {
        var paths = new List<string>();
        var header = false;
        var inProject = false;
        var inGlobal = false;
        string? sectionEnd = null;
        foreach (var line in File.ReadLines(solutionPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!header)
            {
                if (!trimmed.StartsWith("Microsoft Visual Studio Solution File, Format Version ", StringComparison.Ordinal))
                    throw new FormatException("Invalid solution header.");
                header = true;
                continue;
            }
            if (trimmed == "EndProject")
            {
                if (!inProject || sectionEnd != null) throw new FormatException("Unmatched EndProject.");
                inProject = false;
                continue;
            }
            if (trimmed == "Global")
            {
                if (inProject || inGlobal) throw new FormatException("Invalid Global block.");
                inGlobal = true;
                continue;
            }
            if (trimmed == "EndGlobal")
            {
                if (!inGlobal || sectionEnd != null) throw new FormatException("Unmatched EndGlobal.");
                inGlobal = false;
                continue;
            }
            if (trimmed.StartsWith("ProjectSection(", StringComparison.Ordinal) ||
                trimmed.StartsWith("GlobalSection(", StringComparison.Ordinal))
            {
                var projectSection = trimmed.StartsWith("ProjectSection(", StringComparison.Ordinal);
                if (sectionEnd != null || !(projectSection ? inProject : inGlobal) ||
                    !trimmed.Contains(") =", StringComparison.Ordinal))
                    throw new FormatException("Invalid solution section.");
                sectionEnd = projectSection ? "EndProjectSection" : "EndGlobalSection";
                continue;
            }
            if (trimmed is "EndProjectSection" or "EndGlobalSection")
            {
                if (sectionEnd != trimmed) throw new FormatException("Unmatched section end.");
                sectionEnd = null;
                continue;
            }
            if (!trimmed.StartsWith("Project(", StringComparison.Ordinal))
            {
                if (trimmed.StartsWith('#')) continue;
                if (sectionEnd != null)
                {
                    if (!trimmed.Contains('=')) throw new FormatException("Invalid section entry.");
                    continue;
                }
                if (inProject || inGlobal ||
                    (!trimmed.StartsWith("VisualStudioVersion =", StringComparison.Ordinal) &&
                     !trimmed.StartsWith("MinimumVisualStudioVersion =", StringComparison.Ordinal)))
                    throw new FormatException("Unrecognized solution entry.");
                continue;
            }

            if (inProject || inGlobal) throw new FormatException("Invalid project block.");
            inProject = true;

            // Classic .sln project entries are strictly positional:
            //   Project("{typeGuid}") = "DisplayName", "relative\path.csproj", "{projectGuid}"
            // Split on the separating '=' so the type GUID on the left (itself a quoted token) is not
            // miscounted, then take the SECOND quoted token on the right — the path field. Taking it
            // positionally (rather than scanning for the first token with a recognized extension) is
            // correct even when the DISPLAY NAME itself ends in a project extension; an extension scan
            // would pick the name and resolve it to the wrong folder. Solution folders put a bare folder
            // name in the path field, so the extension check below excludes them. Parsing is per-entry: a
            // malformed line invalidates the entire enumeration, never a strict subset.
            var separator = trimmed.IndexOf('=');
            if (separator < 0)
                throw new FormatException("Invalid project entry.");

            var tokens = QuotedToken.Matches(trimmed[(separator + 1)..]);
            var typeTokens = QuotedToken.Matches(trimmed[..separator]);
            if (tokens.Count != 3 || typeTokens.Count != 1 ||
                !Guid.TryParse(typeTokens[0].Groups[1].Value, out _) ||
                !Guid.TryParse(tokens[2].Groups[1].Value, out _))
                throw new FormatException("Invalid project entry.");

            var path = tokens[1].Groups[1].Value;
            if (RecognizedExtensions.Contains(Path.GetExtension(path)))
                paths.Add(path);
        }
        if (!header || inProject || inGlobal || sectionEnd != null)
            throw new FormatException("Truncated solution.");
        return paths;
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
