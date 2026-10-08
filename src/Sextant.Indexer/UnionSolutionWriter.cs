using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Sextant.Indexer;

/// <summary>
/// Generates the solution a multi-solution union loads with ONE <c>MSBuildWorkspace.OpenSolutionAsync</c> (issue #268):
/// one BuildHost evaluates every project in it, instead of the BuildHost started and stopped by each
/// <c>OpenProjectAsync</c>. The solution is written OUTSIDE the checkout (job scratch), next to a copy of the
/// <c>global.json</c> the selected solutions resolve, so it resolves the same .NET SDK and <c>msbuild-sdks</c> they
/// do while nothing is ever written into the checkout. The projects are listed in union order.
/// </summary>
internal static partial class UnionSolutionWriter
{
    /// <summary>The <c>global.json</c> keys whose effect the hostfxr resolution result already captures.</summary>
    private static readonly HashSet<string> ResolutionKeys = new(StringComparer.Ordinal) { "version", "rollForward", "allowPrerelease" };

    /// <summary>
    /// Splits a multi-solution union into the projects the generated solution evaluates exactly as opening each would,
    /// and the rest, which are opened on their own as before. Roslyn's BuildHost resolves the .NET SDK, and MSBuild
    /// resolves <c>msbuild-sdks</c>, from the <c>global.json</c> nearest the SOLUTION when it loads a solution and
    /// nearest the PROJECT when it opens one (issue #113's model, <see cref="GlobalJsonLocator"/>); the generated
    /// solution resolves the selected solutions' shared one. A project is held back when:
    /// <list type="bullet">
    /// <item>its evaluation reads <c>$(SolutionDir)</c> and friends, which a solution sets and a single open does
    /// not, or imports a file that cannot be resolved statically to tell;</item>
    /// <item>its nearest <c>global.json</c> is another file that resolves another SDK through hostfxr, sets other
    /// <c>sdk</c> keys, or pins differently an <c>msbuild-sdks</c> entry its evaluation names;</item>
    /// <item>it references (through a literal <c>ProjectReference</c>) a held-back project, which the solution load
    /// would otherwise pull in and evaluate there.</item>
    /// </list>
    /// The shared <c>global.json</c> is the one the selected solutions' common directory resolves. Nothing loads in one
    /// pass when it sets <c>sdk</c> keys whose meaning depends on its location (such as <c>paths</c>).
    /// </summary>
    public static UnionPartition Partition(
        IReadOnlyList<string> solutionPaths, IReadOnlyList<string> projectPaths, ISdkResolutionProbe? probe = null)
    {
        probe ??= HostFxrSdkResolutionProbe.Instance;
        var common = CommonDirectory(solutionPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!).ToList());
        if (common is null)
            return AllIndividually(projectPaths, "the selected solutions share no common directory");
        var shared = GlobalJsonLocator.FindNearest(common);
        if (ReadGlobalJson(shared) is not { } sharedPins)
            return AllIndividually(projectPaths, $"the selected solutions' global.json ('{shared}') cannot be read");
        if (sharedPins.OtherSdkKeys.Count > 0)
            return AllIndividually(projectPaths,
                $"the selected solutions' global.json sets {string.Join(", ", sharedPins.OtherSdkKeys.Keys)}, which a copy would not reproduce");

        var sdkByGlobalJson = new Dictionary<string, string?>(GlobalJsonLocator.PathComparer);
        string? ResolvedSdk(string directory, string? globalJson)
        {
            // hostfxr's answer depends only on the global.json it finds (and the installed SDKs).
            var key = globalJson ?? string.Empty;
            if (!sdkByGlobalJson.TryGetValue(key, out var version))
                sdkByGlobalJson[key] = version = probe.Probe(directory).ResolvedSdkVersion;
            return version;
        }

        var heldBack = new HashSet<string>(GlobalJsonLocator.PathComparer);
        string? firstReason = null;
        foreach (var project in projectPaths)
            if (Unfaithful(project) is { } why)
            {
                heldBack.Add(Path.GetFullPath(project));
                firstReason ??= why;
            }

        // A project that references a held-back one would pull it into the generated solution and evaluate it there,
        // so it is held back too, until no remaining project references one. A reference this cannot read is caught
        // after the load instead (SolutionLoader.LoadUnionInOnePassAsync).
        var references = projectPaths.Select(Path.GetFullPath).Distinct(GlobalJsonLocator.PathComparer)
            .ToDictionary(p => p, LiteralProjectReferences, GlobalJsonLocator.PathComparer);
        for (var changed = heldBack.Count > 0; changed;)
        {
            changed = false;
            foreach (var (project, referenced) in references)
                if (!heldBack.Contains(project) && referenced.Any(heldBack.Contains))
                {
                    heldBack.Add(project);
                    changed = true;
                }
        }

        var onePass = projectPaths.Where(p => !heldBack.Contains(Path.GetFullPath(p))).ToList();
        var individually = projectPaths.Where(p => heldBack.Contains(Path.GetFullPath(p))).ToList();
        return new UnionPartition(onePass.Count > 0, shared, onePass, individually, firstReason ?? string.Empty);

        string? Unfaithful(string project)
        {
            var name = Path.GetFileName(project);
            if (EvaluationText(project) is not { } text)
                return $"'{name}' imports a file that cannot be resolved statically";
            if (SolutionPropertyReference().IsMatch(text))
                return $"'{name}' reads $(SolutionDir) or a related property, which a solution load sets and a single open does not";

            var directory = Path.GetDirectoryName(Path.GetFullPath(project))!;
            var own = GlobalJsonLocator.FindNearest(directory);
            if (SameFile(own, shared))
                return null;
            if (ResolvedSdk(common, shared) is not { } sharedSdk
                || ResolvedSdk(directory, own) is not { } ownSdk || !string.Equals(ownSdk, sharedSdk, StringComparison.Ordinal))
                return $"'{name}' resolves its own global.json to a different SDK";
            if (ReadGlobalJson(own) is not { } ownPins)
                return $"'{name}' has a global.json that cannot be read";
            if (!SameEntries(ownPins.OtherSdkKeys, sharedPins.OtherSdkKeys))
                return $"'{name}' has a global.json with other sdk settings";
            var differing = ownPins.MsBuildSdks.Keys.Concat(sharedPins.MsBuildSdks.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(sdk => ownPins.MsBuildSdks.GetValueOrDefault(sdk) != sharedPins.MsBuildSdks.GetValueOrDefault(sdk));
            return differing.FirstOrDefault(sdk => text.Contains(sdk, StringComparison.OrdinalIgnoreCase)) is { } used
                ? $"'{name}' uses the MSBuild SDK '{used}', which its own global.json pins differently"
                : null;
        }
    }

    private static UnionPartition AllIndividually(IReadOnlyList<string> projectPaths, string reason) =>
        new(false, null, [], projectPaths.ToList(), reason);

    /// <summary>
    /// Writes the union's solution, and the <c>global.json</c> it resolves (a copy of <paramref name="globalJson"/>, or
    /// an empty one so nothing above the directory applies), into <paramref name="directory"/>, which must be outside
    /// the checkout. Returns the solution's path.
    /// </summary>
    public static string Write(string directory, IReadOnlyList<string> projectPaths, string? globalJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (projectPaths.Count == 0)
            throw new ArgumentException("A union needs at least one project.", nameof(projectPaths));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, GlobalJsonLocator.FileName),
            globalJson is null ? "{}\n" : File.ReadAllText(globalJson), new UTF8Encoding(false));
        var path = Path.Combine(Path.GetFullPath(directory), "SextantUnion.slnx");
        File.WriteAllText(path, Render(directory, projectPaths), new UTF8Encoding(false));
        return path;
    }

    internal static string Render(string solutionDirectory, IReadOnlyList<string> projectPaths)
    {
        var xml = new StringBuilder();
        xml.Append("<Solution>\n");
        foreach (var project in projectPaths)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(solutionDirectory), Path.GetFullPath(project))
                .Replace('\\', '/');
            xml.Append("  <Project Path=\"").Append(SecurityElement.Escape(relative)).Append("\" />\n");
        }
        xml.Append("</Solution>\n");
        return xml.ToString();
    }

    /// <summary>A <c>global.json</c>'s <c>msbuild-sdks</c> pins and its <c>sdk</c> keys other than the resolution ones.</summary>
    internal sealed record GlobalJsonPins(
        IReadOnlyDictionary<string, string> MsBuildSdks, IReadOnlyDictionary<string, string> OtherSdkKeys);

    // Empty pins for no file; null when the file cannot be read.
    internal static GlobalJsonPins? ReadGlobalJson(string? globalJson)
    {
        var sdks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var other = new Dictionary<string, string>(StringComparer.Ordinal);
        if (globalJson is null)
            return new GlobalJsonPins(sdks, other);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(globalJson),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (root.TryGetProperty("msbuild-sdks", out var pins))
            {
                if (pins.ValueKind != JsonValueKind.Object)
                    return null;
                foreach (var pin in pins.EnumerateObject())
                    sdks[pin.Name] = pin.Value.ToString();
            }
            if (root.TryGetProperty("sdk", out var sdk))
            {
                if (sdk.ValueKind != JsonValueKind.Object)
                    return null;
                foreach (var key in sdk.EnumerateObject().Where(k => !ResolutionKeys.Contains(k.Name)))
                    other[key.Name] = key.Value.GetRawText();
            }
            return new GlobalJsonPins(sdks, other);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool SameEntries(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var v) && v == e.Value);

    /// <summary>
    /// The text of every file a project's evaluation reads statically: the project, the nearest
    /// <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> above it, and every file they import, with
    /// <c>$(MSBuildThisFileDirectory)</c>, <c>$(MSBuildProjectDirectory)</c> and <c>GetPathOfFileAbove</c> resolved
    /// (conditions ignored: any could apply). Null when an import cannot be resolved this way.
    /// </summary>
    internal static string? EvaluationText(string projectPath)
    {
        var project = Path.GetFullPath(projectPath);
        var projectDirectory = Path.GetDirectoryName(project)!;
        var pending = new Queue<string>();
        pending.Enqueue(project);
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            if (FindAbove(name, projectDirectory) is { } file)
                pending.Enqueue(file);

        var seen = new HashSet<string>(GlobalJsonLocator.PathComparer);
        var text = new StringBuilder();
        while (pending.TryDequeue(out var file))
        {
            if (!seen.Add(file))
                continue;
            if (seen.Count > 64)
                return null;
            XDocument document;
            try
            {
                text.Append(File.ReadAllText(file)).Append('\n');
                using var reader = XmlReader.Create(file,
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(reader);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                return null;
            }

            var fileDirectory = Path.GetDirectoryName(file)!;
            foreach (var import in document.Descendants().Where(e => e.Name.LocalName == "Import"))
            {
                if (import.Attribute("Sdk") is not null)
                    continue; // An SDK import: its name is in the text already.
                if (ResolveImport(import.Attribute("Project")?.Value, fileDirectory, projectDirectory) is not { } resolved)
                    return null;
                if (File.Exists(resolved))
                    pending.Enqueue(resolved);
            }
        }
        return text.ToString();
    }

    private static string? ResolveImport(string? value, string fileDirectory, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var expanded = value.Trim()
            .Replace("$(MSBuildThisFileDirectory)", fileDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildProjectDirectory)", projectDirectory, StringComparison.OrdinalIgnoreCase);
        if (FileAbove().Match(expanded) is { Success: true } above)
            return FindAbove(above.Groups["name"].Value, Path.GetFullPath(above.Groups["start"].Value, fileDirectory));
        if (expanded.Contains("$(", StringComparison.Ordinal) || expanded.IndexOfAny(['*', '?', ';', '@', '%']) >= 0)
            return null;
        return Path.GetFullPath(expanded.Replace('\\', Path.DirectorySeparatorChar), fileDirectory);
    }

    private static string? FindAbove(string name, string directory)
    {
        for (var current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
        {
            var candidate = Path.Combine(current, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    [GeneratedRegex(@"\$\(\s*Solution(Dir|Path|Name|FileName|Ext)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex SolutionPropertyReference();

    [GeneratedRegex(@"^\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'(?<name>[^'$]+)'\s*,\s*'(?<start>[^'$]*)'\s*\)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileAbove();

    // The projects a project file references by a literal path (conditions included: any could apply). An include with
    // MSBuild syntax or a wildcard cannot be resolved statically and is left out.
    internal static IReadOnlyList<string> LiteralProjectReferences(string projectPath)
    {
        try
        {
            using var reader = XmlReader.Create(projectPath,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            return XDocument.Load(reader).Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include) && include.IndexOfAny(['$', '@', '%', '*', '?', ';']) < 0)
                .Select(include => Path.GetFullPath(
                    include!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), directory))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            return [];
        }
    }

    internal static string? CommonDirectory(IReadOnlyList<string> directories)
    {
        if (directories.Count == 0)
            return null;
        var common = Path.GetFullPath(directories[0]);
        foreach (var directory in directories.Skip(1).Select(Path.GetFullPath))
            while (!IsSameOrUnder(directory, common))
            {
                if (Path.GetDirectoryName(common) is not { } parent)
                    return null;
                common = parent;
            }
        return common;
    }

    private static bool IsSameOrUnder(string directory, string ancestor)
    {
        var relative = Path.GetRelativePath(ancestor, directory);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool SameFile(string? a, string? b) => GlobalJsonLocator.PathComparer.Equals(a ?? "", b ?? "");
}

/// <summary>
/// How a multi-solution union loads (issue #268): <see cref="OnePass"/> in one generated solution that resolves
/// <see cref="GlobalJson"/> (when <see cref="CanLoadInOnePass"/>), then <see cref="Individually"/> each opened on its
/// own. Both keep union order. <see cref="Reason"/> says why the first held-back project was.
/// </summary>
internal sealed record UnionPartition(
    bool CanLoadInOnePass, string? GlobalJson, IReadOnlyList<string> OnePass, IReadOnlyList<string> Individually,
    string Reason);
