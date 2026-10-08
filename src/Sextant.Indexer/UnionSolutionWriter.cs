using System.Security;
using System.Text;

namespace Sextant.Indexer;

/// <summary>
/// Writes the deduplicated project union of a multi-solution checkout as one generated <c>.slnx</c> (issue #268), so
/// the union loads with ONE <c>MSBuildWorkspace.OpenSolutionAsync</c>, which shares one MSBuild BuildHost, instead of
/// one <c>OpenProjectAsync</c> per project, each of which starts and stops its own BuildHost with cold evaluation
/// caches. The projects are listed in union (first-appearance) order, relative to the generated file.
/// </summary>
internal static class UnionSolutionWriter
{
    /// <summary>
    /// Splits a multi-solution union into the projects one generated solution evaluates exactly as opening each would,
    /// and the rest (issue #268). Roslyn's BuildHost resolves the .NET SDK, and MSBuild resolves <c>msbuild-sdks</c>,
    /// from the <c>global.json</c> nearest the SOLUTION when it loads a solution, and nearest the PROJECT when it opens
    /// one (issue #113's model, <see cref="GlobalJsonLocator"/>). The generated solution goes in the selected
    /// solutions' common directory. A project belongs in it when its nearest <c>global.json</c> is that directory's,
    /// or when hostfxr resolves the same SDK from its own directory and it references none of the <c>msbuild-sdks</c>
    /// the two files pin differently. Every other project keeps being opened on its own, in union order.
    /// </summary>
    public static UnionPartition Partition(
        IReadOnlyList<string> solutionPaths, IReadOnlyList<string> projectPaths, ISdkResolutionProbe? probe = null)
    {
        probe ??= HostFxrSdkResolutionProbe.Instance;
        var common = CommonDirectory(solutionPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!).ToList());
        if (common is null)
            return new UnionPartition(null, [], projectPaths.ToList(), "the selected solutions share no common directory");

        var shared = GlobalJsonLocator.FindNearest(common);
        var sharedPins = ReadMsBuildSdks(shared);
        var sdkByGlobalJson = new Dictionary<string, string?>(GlobalJsonLocator.PathComparer);
        string? ResolvedSdk(string directory, string? globalJson)
        {
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
        // so it is held back too, until no remaining project references one. (A reference this cannot read is caught
        // after the load instead: see SolutionLoader.LoadUnionInOnePassAsync.)
        var references = projectPaths.ToDictionary(
            p => Path.GetFullPath(p), p => LiteralProjectReferences(p), GlobalJsonLocator.PathComparer);
        for (var changed = heldBack.Count > 0; changed;)
        {
            changed = false;
            foreach (var (project, referenced) in references)
                if (!heldBack.Contains(project) && referenced.FirstOrDefault(heldBack.Contains) is { } held)
                {
                    heldBack.Add(project);
                    changed = true;
                }
        }

        var onePass = projectPaths.Where(p => !heldBack.Contains(Path.GetFullPath(p))).ToList();
        var individually = projectPaths.Where(p => heldBack.Contains(Path.GetFullPath(p))).ToList();
        return new UnionPartition(onePass.Count > 0 ? common : null, onePass, individually, firstReason ?? string.Empty);

        string? Unfaithful(string project)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(project))!;
            var own = GlobalJsonLocator.FindNearest(directory);
            if (SameFile(own, shared))
                return null;
            if (ResolvedSdk(common, shared) is not { } sharedSdk || ResolvedSdk(directory, own) is not { } ownSdk
                || !string.Equals(ownSdk, sharedSdk, StringComparison.Ordinal))
                return $"'{Path.GetFileName(project)}' resolves its own global.json to a different SDK";
            if (ReadMsBuildSdks(own) is not { } ownPins || sharedPins is null)
                return $"'{Path.GetFileName(project)}' has a global.json whose msbuild-sdks cannot be compared";
            var differing = ownPins.Keys.Concat(sharedPins.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => ownPins.GetValueOrDefault(name) != sharedPins.GetValueOrDefault(name));
            return differing.FirstOrDefault(name => ReferencesSdk(directory, project, name)) is { } used
                ? $"'{Path.GetFileName(project)}' uses the MSBuild SDK '{used}', which its own global.json pins differently"
                : null;
        }
    }

    // The projects a project file references by a literal path (conditions included: any could apply). An include with
    // MSBuild syntax or a wildcard cannot be resolved statically and is left out.
    internal static IReadOnlyList<string> LiteralProjectReferences(string projectPath)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(projectPath,
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            return System.Xml.Linq.XDocument.Load(reader).Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include) && include.IndexOfAny(['$', '@', '%', '*', '?', ';']) < 0)
                .Select(include => Path.GetFullPath(
                    include!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), directory))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException
                                       or ArgumentException)
        {
            return [];
        }
    }

    private static bool SameFile(string? a, string? b) => GlobalJsonLocator.PathComparer.Equals(a ?? "", b ?? "");

    // The "msbuild-sdks" pins of a global.json (name → version); empty for none, null when it cannot be read.
    internal static IReadOnlyDictionary<string, string?>? ReadMsBuildSdks(string? globalJson)
    {
        var pins = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (globalJson is null)
            return pins;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(globalJson),
                new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true
                });
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            if (document.RootElement.TryGetProperty("msbuild-sdks", out var sdks))
            {
                if (sdks.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return null;
                foreach (var sdk in sdks.EnumerateObject())
                    pins[sdk.Name] = sdk.Value.ToString();
            }
            return pins;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // Conservative: the project file, or a Directory.Build.props/targets above it, mentions the SDK's name at all.
    // A false positive only keeps the per-project load.
    private static bool ReferencesSdk(string projectDirectory, string project, string sdkName)
    {
        try
        {
            if (File.ReadAllText(project).Contains(sdkName, StringComparison.OrdinalIgnoreCase))
                return true;
            for (var directory = projectDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    var file = Path.Combine(directory, name);
                    if (File.Exists(file) && File.ReadAllText(file).Contains(sdkName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Writes the union of <paramref name="projectPaths"/> (absolute) into <paramref name="directory"/> under a unique
    /// hidden name. The caller deletes it once the open has read it.
    /// </summary>
    public static string Write(string directory, IReadOnlyList<string> projectPaths)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (projectPaths.Count == 0)
            throw new ArgumentException("A union needs at least one project.", nameof(projectPaths));
        var path = Path.Combine(Path.GetFullPath(directory), $".sextant-union-{Guid.NewGuid():N}.slnx");
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

    internal static string? CommonDirectory(IReadOnlyList<string> directories)
    {
        if (directories.Count == 0)
            return null;
        var common = Path.GetFullPath(directories[0]);
        foreach (var directory in directories.Skip(1).Select(Path.GetFullPath))
        {
            while (!IsSameOrUnder(directory, common))
            {
                var parent = Path.GetDirectoryName(common);
                if (parent is null)
                    return null;
                common = parent;
            }
        }
        return common;
    }

    private static bool IsSameOrUnder(string directory, string ancestor)
    {
        var relative = Path.GetRelativePath(ancestor, directory);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}

/// <summary>
/// How a multi-solution union loads (issue #268): <see cref="OnePass"/> in one generated solution in
/// <see cref="Directory"/> (null when none can), then <see cref="Individually"/> each opened on its own. Both keep union
/// order. <see cref="Reason"/> says why the first project left out of the one pass was.
/// </summary>
internal sealed record UnionPartition(
    string? Directory, IReadOnlyList<string> OnePass, IReadOnlyList<string> Individually, string Reason);
