using System.Text.RegularExpressions;
using System.Xml;

namespace Sextant.Indexer;

/// <summary>
/// Enumerates the project files DECLARED in a solution without evaluating them, so a resilient loader
/// can open each one individually and isolate a per-project load failure (issue #90). Supports the
/// classic <c>.sln</c> format and the XML <c>.slnx</c> format; only projects Roslyn can open
/// (<c>.csproj</c>/<c>.vbproj</c>/<c>.fsproj</c>) are returned, so solution folders and unrecognized
/// entries are ignored. Parsing is best-effort and never throws: an unreadable/unknown solution yields
/// an empty list and the caller degrades to the previous whole-solution load.
/// </summary>
internal static class SolutionProjectEnumerator
{
    private static readonly HashSet<string> RecognizedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".vbproj", ".fsproj" };

    // Classic .sln project line: Project("{typeGuid}") = "Name", "relative\path.csproj", "{projectGuid}"
    private static readonly Regex QuotedToken = new("\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// Returns the absolute, de-duplicated paths of the recognized projects declared in
    /// <paramref name="solutionPath"/>, preserving declaration order. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Enumerate(string solutionPath)
    {
        try
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
            var relativePaths = string.Equals(Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase)
                ? ParseSlnx(solutionPath)
                : ParseSln(solutionPath);

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
            return result;
        }
        catch
        {
            // Best-effort: a solution we cannot parse simply yields no declared projects, so the caller
            // falls back to the standard whole-solution load rather than failing here.
            return [];
        }
    }

    private static IEnumerable<string> ParseSln(string solutionPath)
    {
        foreach (var line in File.ReadLines(solutionPath))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("Project(", StringComparison.Ordinal))
                continue;

            // Classic .sln project entries are strictly positional:
            //   Project("{typeGuid}") = "DisplayName", "relative\path.csproj", "{projectGuid}"
            // Split on the separating '=' so the type GUID on the left (itself a quoted token) is not
            // miscounted, then take the SECOND quoted token on the right — the path field. Taking it
            // positionally (rather than scanning for the first token with a recognized extension) is
            // correct even when the DISPLAY NAME itself ends in a project extension; an extension scan
            // would pick the name and resolve it to the wrong folder. Solution folders put a bare folder
            // name in the path field, so the extension check below excludes them. Parsing is per-entry: a
            // malformed line is skipped, not fatal to the whole enumeration.
            var separator = trimmed.IndexOf('=');
            if (separator < 0)
                continue;

            var tokens = QuotedToken.Matches(trimmed[(separator + 1)..]);
            if (tokens.Count < 2)
                continue;

            var path = tokens[1].Groups[1].Value;
            if (RecognizedExtensions.Contains(Path.GetExtension(path)))
                yield return path;
        }
    }

    private static IEnumerable<string> ParseSlnx(string solutionPath)
    {
        var paths = new List<string>();
        using var reader = XmlReader.Create(solutionPath, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.Name, "Project", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = reader.GetAttribute("Path");
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }
        return paths;
    }
}
