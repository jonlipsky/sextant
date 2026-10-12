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
        var assessment = Assess(solutionPaths, projectPaths, probe);
        if (assessment.AllHeldBackReason is { } allReason)
            return AllIndividually(projectPaths, allReason);
        var shared = assessment.SharedGlobalJson;
        var heldBack = new HashSet<string>(assessment.HeldBack, GlobalJsonLocator.PathComparer);
        var firstReason = assessment.FirstReason;

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
    }

    /// <summary>
    /// The projects whose evaluation would differ between the generated union solution (or any other selected
    /// solution) and a load of their own: <see cref="Partition"/>'s per-project test, before the held-back set is
    /// closed over project references. <see cref="SolutionAssessment.AllHeldBackReason"/> is set when nothing can be
    /// told apart (no common directory, an unreadable or location-dependent shared <c>global.json</c>), and then every
    /// project must be treated as sensitive.
    /// </summary>
    internal static SolutionAssessment Assess(
        IReadOnlyList<string> solutionPaths, IReadOnlyList<string> projectPaths, ISdkResolutionProbe? probe = null,
        bool sameDirectory = false)
    {
        probe ??= HostFxrSdkResolutionProbe.Instance;
        if (sameDirectory)
            return AssessSolutionProperties(projectPaths);
        var common = CommonDirectory(solutionPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!).ToList());
        if (common is null)
            return SolutionAssessment.All("the selected solutions share no common directory");
        var shared = GlobalJsonLocator.FindNearest(common);
        if (ReadGlobalJson(shared) is not { } sharedPins)
            return SolutionAssessment.All($"the selected solutions' global.json ('{shared}') cannot be read");
        if (sharedPins.OtherSdkKeys.Count > 0)
            return SolutionAssessment.All(
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
        return new SolutionAssessment(shared, heldBack, firstReason, null);

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

    // Solutions in one directory share the global.json chain, so only what a solution load itself sets can differ.
    private static SolutionAssessment AssessSolutionProperties(IReadOnlyList<string> projectPaths)
    {
        var heldBack = new HashSet<string>(GlobalJsonLocator.PathComparer);
        var categories = new Dictionary<string, string>(GlobalJsonLocator.PathComparer);
        foreach (var project in projectPaths)
        {
            var full = Path.GetFullPath(project);
            if (EvaluationText(project, ignoreToolsetImports: true) is not { } text)
                categories[full] = "unresolvable import";
            else if (SolutionPropertyReference().IsMatch(text))
                categories[full] = "SolutionDir property";
            else
                continue;
            heldBack.Add(full);
        }
        return new SolutionAssessment(null, heldBack, null, null) { Categories = categories };
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
    /// <c>Directory.Build.props</c>, <c>Directory.Build.targets</c> and <c>Directory.Packages.props</c> above it, and
    /// every file they import, with <c>$(MSBuildThisFileDirectory)</c>, <c>$(MSBuildProjectDirectory)</c> and
    /// <c>GetPathOfFileAbove</c> resolved (conditions ignored: any could apply). Null when an import cannot be resolved
    /// this way. Not read: the build files NuGet packages contribute (through restore's <c>obj/*.nuget.g.*</c>), so a
    /// package that reads <c>$(SolutionDir)</c> or names an <c>msbuild-sdks</c> pin is not detected.
    /// </summary>
    internal static string? EvaluationText(string projectPath, bool ignoreToolsetImports = false)
    {
        var project = Path.GetFullPath(projectPath);
        var projectDirectory = Path.GetDirectoryName(project)!;
        var pending = new Queue<string>();
        pending.Enqueue(project);
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
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
                if (ignoreToolsetImports && IsToolsetImport(import.Attribute("Project")?.Value))
                    continue;
                var importValue = import.Attribute("Project")?.Value;
                if (ignoreToolsetImports)
                    importValue = ThroughFileAboveProperty(importValue, document) ?? importValue;
                if (ResolveImport(importValue, fileDirectory, projectDirectory) is not { } resolved)
                    return null;
                if (File.Exists(resolved))
                    pending.Enqueue(resolved);
            }
        }
        return text.ToString();
    }

    // `<Import Project="$(P)" />` where the same file sets `<P>$([MSBuild]::GetPathOfFileAbove(...))</P>`: the import is that call.
    private static string? ThroughFileAboveProperty(string? value, XDocument document)
    {
        if (value is null || PropertyOnly().Match(value.Trim()) is not { Success: true } match)
            return null;
        var definitions = document.Descendants()
            .Where(e => e.Name.LocalName == match.Groups["name"].Value && !e.HasElements)
            .Select(e => e.Value.Trim()).ToList();
        return definitions.Count == 1 && definitions[0].Contains("GetPathOfFileAbove", StringComparison.Ordinal) ? definitions[0] : null;
    }

    [GeneratedRegex(@"^\$\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\)$")]
    private static partial Regex PropertyOnly();

    // A file under the MSBuild toolset or an installed SDK: its text is not the repository's and reads no solution property.
    private static bool IsToolsetImport(string? value) =>
        value is not null && ToolsetImport().IsMatch(value);

    [GeneratedRegex(@"^\s*\$\(\s*(MSBuildExtensionsPath(32|64)?|MSBuildToolsPath(32|64)?|MSBuildBinPath|MSBuildSDKsPath|VSToolsPath)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ToolsetImport();

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

/// <summary>
/// <see cref="UnionSolutionWriter.Assess"/>'s verdict: the projects held back from a one-pass load because their
/// evaluation depends on which solution (or <c>global.json</c>) loads them, or, when <see cref="AllHeldBackReason"/>
/// is set, every project.
/// </summary>
internal sealed record SolutionAssessment(
    string? SharedGlobalJson, IReadOnlySet<string> HeldBack, string? FirstReason, string? AllHeldBackReason)
{
    /// <summary>Held-back project to the category of the reason, when the assessment recorded one.</summary>
    public IReadOnlyDictionary<string, string> Categories { get; init; } = new Dictionary<string, string>();

    public static SolutionAssessment All(string reason) =>
        new(null, new HashSet<string>(), null, reason);
}

/// <summary>
/// Which projects of a multi-solution selection can evaluate differently depending on the solution they are
/// restored or loaded through (issue #290): the same detection the union load uses (<c>$(SolutionDir)</c> and
/// related properties, a different SDK or <c>msbuild-sdks</c> pin through another <c>global.json</c>).
/// </summary>
public static class SolutionSensitivity
{
    /// <summary>
    /// The full paths of the projects of <paramref name="projectPaths"/> whose evaluation depends on the selected
    /// solution. When the selection cannot be told apart at all, every project is returned.
    /// </summary>
    public static IReadOnlySet<string> Find(IReadOnlyList<string> solutionPaths, IReadOnlyList<string> projectPaths)
        => Find(solutionPaths, projectPaths, sameDirectory: false);

    /// <summary>
    /// As above; with <paramref name="sameDirectory"/> (every solution is in one directory, so they share one
    /// <c>global.json</c>, <c>NuGet.config</c> and <c>Directory.Solution.*</c> chain) a differing SDK or
    /// <c>msbuild-sdks</c> pin is not a difference between the solutions: only <c>$(SolutionDir)</c>-family
    /// properties and the configuration mapping are.
    /// </summary>
    public static IReadOnlySet<string> Find(
        IReadOnlyList<string> solutionPaths, IReadOnlyList<string> projectPaths, bool sameDirectory)
        => Classify(solutionPaths, projectPaths, sameDirectory).Keys.ToHashSet(GlobalJsonLocator.PathComparer);

    /// <summary>
    /// The projects <paramref name="solutionPath"/> declares that it does NOT map to the project's own default
    /// configuration (<c>Debug|Any CPU</c>, what a standalone restore evaluates with), or whose mapping cannot be read
    /// reliably. Restoring any other project on its own is identical to restoring it inside the solution (issue #290).
    /// An unreadable solution returns every declared project.
    /// </summary>
    public static IReadOnlySet<string> FindNotDefaultMapped(string solutionPath)
    {
        var result = new HashSet<string>(GlobalJsonLocator.PathComparer);
        var mapping = SolutionConfigurationMapping.Read(solutionPath);
        foreach (var project in SolutionProjectEnumerator.Enumerate(solutionPath))
        {
            var full = Path.GetFullPath(project);
            if (mapping is null || !mapping.TryGetValue(full, out var value) || value is null
                || !value.Equals("Debug|Any CPU", StringComparison.OrdinalIgnoreCase))
                result.Add(full);
        }
        return result;
    }

    /// <summary>The distinct configuration mappings read for <paramref name="projects"/> (diagnostics), or <c>unreadable</c>.</summary>
    public static string DescribeMapping(string solutionPath, IEnumerable<string> projects)
    {
        var mapping = SolutionConfigurationMapping.Read(solutionPath);
        if (mapping is null)
            return "unreadable";
        return string.Join(" | ", projects.Select(p => mapping.TryGetValue(p, out var v) ? v : null).Distinct().Take(2)
            .Select(v => v is null ? "none" : v.Length > 80 ? v[..80] + "..." : v));
    }

    /// <summary>The sensitive projects (see <see cref="Find(IReadOnlyList{string}, IReadOnlyList{string}, bool)"/>) with the category of the reason.</summary>
    public static IReadOnlyDictionary<string, string> Classify(
        IReadOnlyList<string> solutionPaths, IReadOnlyList<string> projectPaths, bool sameDirectory)
    {
        var result = new Dictionary<string, string>(GlobalJsonLocator.PathComparer);
        var assessment = UnionSolutionWriter.Assess(solutionPaths, projectPaths, sameDirectory: sameDirectory);
        if (assessment.AllHeldBackReason is not null)
        {
            foreach (var project in projectPaths)
                result[Path.GetFullPath(project)] = "global.json/SDK (selection cannot be told apart)";
            return result;
        }

        foreach (var project in assessment.HeldBack)
            result[project] = assessment.Categories.TryGetValue(project, out var category) ? category : "global.json/SDK or msbuild-sdks";
        foreach (var project in SolutionConfigurationMapping.FindDivergent(solutionPaths))
            result.TryAdd(project, "configuration mapping");
        return result;
    }
}

/// <summary>
/// The project Configuration|Platform each solution maps a project to for its default solution configuration, the
/// one <c>dotnet restore &lt;solution&gt;</c> evaluates with (issue #290). A mapping that cannot be read reliably is
/// <c>null</c>, which callers treat as "differs".
/// </summary>
internal static class SolutionConfigurationMapping
{
    private const string ImplicitDefault = "Debug|Any CPU";

    private static readonly Regex ProjectLine = new(
        "^Project\\(\"[^\"]*\"\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"([^\"]*)\"\\s*,\\s*\"(\\{[^\"]*\\})\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>The projects of <paramref name="solutionPaths"/> mapped differently (or unreliably) by the solutions declaring them.</summary>
    public static IReadOnlySet<string> FindDivergent(IReadOnlyList<string> solutionPaths)
    {
        var seen = new Dictionary<string, string?>(GlobalJsonLocator.PathComparer);
        var divergent = new HashSet<string>(GlobalJsonLocator.PathComparer);
        foreach (var solution in solutionPaths)
        {
            var declared = SolutionProjectEnumerator.Enumerate(solution);
            var mapping = Read(solution);
            foreach (var project in declared)
            {
                var value = mapping is not null && mapping.TryGetValue(project, out var m) ? m : null;
                if (value is null)
                    divergent.Add(project);
                if (seen.TryGetValue(project, out var earlier))
                {
                    if (earlier is null || !string.Equals(earlier, value, StringComparison.Ordinal))
                        divergent.Add(project);
                }
                else
                    seen[project] = value;
            }
        }
        return divergent;
    }

    /// <summary>Project full path to mapping, or <c>null</c> when the solution cannot be read reliably.</summary>
    internal static IReadOnlyDictionary<string, string?>? Read(string solutionPath)
    {
        try
        {
            return Path.GetExtension(solutionPath).ToLowerInvariant() switch
            {
                ".sln" => ReadSln(solutionPath),
                ".slnx" => ReadSlnx(solutionPath),
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                   or ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string NormalizePlatform(string value) =>
        value.Trim().Equals("AnyCPU", StringComparison.OrdinalIgnoreCase) ? "Any CPU" : value.Trim();

    private static string NormalizePair(string value)
    {
        var parts = value.Split('|', 2);
        return parts.Length == 2 ? parts[0].Trim() + "|" + NormalizePlatform(parts[1]) : value.Trim();
    }

    private static Dictionary<string, string?>? ReadSln(string solutionPath)
    {
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
        var guidToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var solutionConfigs = new List<string>();
        var projectLines = new List<(string Guid, string Key, string Value)>();
        var hasProjectSection = false;
        var section = string.Empty;
        foreach (var raw in File.ReadLines(solutionPath))
        {
            var line = raw.Trim();
            if (line.StartsWith("Project(", StringComparison.OrdinalIgnoreCase))
            {
                var match = ProjectLine.Match(line);
                if (match.Success)
                {
                    var relative = match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    guidToProject[match.Groups[2].Value] = Path.GetFullPath(Path.Combine(solutionDir, relative));
                }
                continue;
            }
            if (line.StartsWith("GlobalSection(SolutionConfigurationPlatforms)", StringComparison.Ordinal))
            {
                section = "solution";
                continue;
            }
            if (line.StartsWith("GlobalSection(ProjectConfigurationPlatforms)", StringComparison.Ordinal))
            {
                section = "project";
                hasProjectSection = true;
                continue;
            }
            if (line.StartsWith("EndGlobalSection", StringComparison.Ordinal))
            {
                section = string.Empty;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (section == "solution")
                solutionConfigs.Add(NormalizePair(key));
            else if (section == "project" && key.StartsWith('{'))
            {
                var guidEnd = key.IndexOf('}');
                if (guidEnd > 0 && guidEnd + 1 < key.Length && key[guidEnd + 1] == '.')
                    projectLines.Add((key[..(guidEnd + 1)], key[(guidEnd + 2)..], value));
            }
        }

        var result = new Dictionary<string, string?>(GlobalJsonLocator.PathComparer);
        if (solutionConfigs.Count == 0 && !hasProjectSection)
        {
            foreach (var project in guidToProject.Values)
                result[project] = ImplicitDefault;
            return result;
        }

        var defaultConfig = DefaultSolutionConfiguration(solutionConfigs);
        foreach (var (guid, project) in guidToProject)
        {
            string? mapped;
            if (defaultConfig is not null)
            {
                var active = projectLines.Where(l => l.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)
                        && l.Key.EndsWith(".ActiveCfg", StringComparison.Ordinal)
                        && NormalizePair(l.Key[..^".ActiveCfg".Length]).Equals(defaultConfig, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Value).ToList();
                mapped = active.Count == 1 ? NormalizePair(active[0]) : null;
            }
            else
            {
                // The default configuration is not certain, so compare every solution configuration and its mappings.
                var lines = projectLines.Where(l => l.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase))
                    .Select(l => NormalizePair(l.Key) + "=" + NormalizePair(l.Value)).Order(StringComparer.Ordinal);
                mapped = "sln:" + string.Join(";", solutionConfigs.Order(StringComparer.Ordinal)) + "/" + string.Join(";", lines);
            }
            result[project] = mapped;
        }
        return result;
    }

    // Debug|Any CPU when it is the Debug configuration's only candidate (a "Mixed Platforms" sibling makes the choice
    // uncertain), or the sole configuration; otherwise null.
    private static string? DefaultSolutionConfiguration(IReadOnlyList<string> configs)
    {
        var distinct = configs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 1)
            return distinct[0];
        if (distinct.Contains(ImplicitDefault, StringComparer.OrdinalIgnoreCase)
            && !distinct.Contains("Debug|Mixed Platforms", StringComparer.OrdinalIgnoreCase))
            return ImplicitDefault;
        return null;
    }

    private static Dictionary<string, string?>? ReadSlnx(string solutionPath)
    {
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using var reader = XmlReader.Create(solutionPath, settings);
        var root = XDocument.Load(reader).Root;
        if (root is null || root.Name.LocalName != "Solution")
            return null;

        var topLevel = string.Join(";", root.Elements()
            .Where(e => e.Name.LocalName.Equals("Configurations", StringComparison.OrdinalIgnoreCase))
            .Select(Canonical).Order(StringComparer.Ordinal));
        // Platform names only (no build types) selects Debug|AnyCPU like a .slnx without the block (measured with
        // dotnet restore on this SDK, whatever the platform order).
        var platformsOnly = topLevel.Length > 0 && root.Elements()
            .Where(e => e.Name.LocalName.Equals("Configurations", StringComparison.OrdinalIgnoreCase))
            .All(c => c.Attributes().Count() == 0 && c.Elements().All(p => p.Name.LocalName.Equals("Platform", StringComparison.OrdinalIgnoreCase)
                && !p.Elements().Any() && p.Attributes().All(at => at.Name.LocalName == "Name")))
            && root.Elements().Where(e => e.Name.LocalName.Equals("Configurations", StringComparison.OrdinalIgnoreCase))
                .SelectMany(c => c.Elements()).Any(p => NormalizePlatform(p.Attribute("Name")?.Value ?? "") == "Any CPU");
        var result = new Dictionary<string, string?>(GlobalJsonLocator.PathComparer);
        foreach (var project in root.Descendants().Where(e => e.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase)))
        {
            var path = project.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var full = Path.GetFullPath(Path.Combine(solutionDir,
                path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            var own = string.Join(";", project.Elements().Select(Canonical).Order(StringComparer.Ordinal));
            result[full] = own.Length == 0 && (topLevel.Length == 0 || platformsOnly)
                ? ImplicitDefault
                : "slnx:" + topLevel + "/" + own;
        }
        return result;
    }

    private static string Canonical(XElement element) =>
        element.Name.LocalName + "[" + string.Join(",", element.Attributes().Select(a => a.Name.LocalName + "=" + a.Value).Order(StringComparer.Ordinal))
        + "]{" + string.Join(";", element.Elements().Select(Canonical).Order(StringComparer.Ordinal)) + "}";
}
