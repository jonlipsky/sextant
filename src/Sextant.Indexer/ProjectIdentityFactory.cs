using Microsoft.CodeAnalysis;
using Sextant.Core;

namespace Sextant.Indexer;

/// <summary>
/// Builds TFM-aware <see cref="ProjectIdentity"/> values from Roslyn <see cref="Project"/> instances.
/// </summary>
/// <remarks>
/// A multi-targeted csproj is loaded by MSBuildWorkspace as one <see cref="Project"/> per evaluated
/// target framework, each with a distinct <see cref="Project.Id"/>. The architecture treats each
/// evaluated target framework as a distinct logical project, so the evaluated TFM read from the
/// per-instance Roslyn project — not the first <c>&lt;TargetFramework(s)&gt;</c> value in the raw
/// csproj XML — is folded into the canonical id. This keeps a <c>#if</c>-conditional member under one
/// framework from being deleted when its sibling framework is processed.
/// </remarks>
public static class ProjectIdentityFactory
{
    /// <summary>
    /// Builds the full logical identity for a Roslyn project: submodule-aware remote URL and
    /// repo-relative path, the evaluated per-instance target framework, assembly name, and
    /// test-project flag. The canonical id folds in the evaluated TFM.
    /// </summary>
    public static ProjectIdentity Create(
        Project project,
        IReadOnlyList<SubmoduleInfo> submodules,
        string? repoRoot)
    {
        var targetFramework = ResolveEvaluatedTargetFramework(project);

        ProjectIdentity identity;
        if (repoRoot != null && project.FilePath != null)
        {
            var submodule = SubmoduleDiscovery.FindContainingSubmodule(project.FilePath, submodules, repoRoot);
            if (submodule != null)
            {
                var submoduleFullPath = Path.GetFullPath(Path.Combine(repoRoot, submodule.Path));
                identity = GitRemoteResolver.ResolveForSubmodule(
                    project.FilePath, submodule.RemoteUrl, submoduleFullPath, targetFramework);
            }
            else
            {
                identity = GitRemoteResolver.Resolve(project.FilePath, targetFramework);
            }
        }
        else
        {
            identity = GitRemoteResolver.Resolve(project.FilePath!, targetFramework);
        }

        return identity with
        {
            AssemblyName = project.AssemblyName,
            TargetFramework = targetFramework,
            IsTestProject = TestProjectDetector.IsTestProject(project)
        };
    }

    /// <summary>
    /// Resolves the evaluated target framework of a Roslyn project instance. MSBuildWorkspace
    /// disambiguates each framework of a multi-targeted project by appending "(tfm)" to the project
    /// name (e.g. <c>Foo(net10.0)</c>); a single-target project carries no such suffix. Delegates to
    /// the pure <see cref="ResolveTfm"/> after reading the csproj's declared frameworks. Returns the
    /// empty string (never null) when no framework can be determined, so identity hashing stays
    /// deterministic.
    /// </summary>
    public static string ResolveEvaluatedTargetFramework(Project project)
        => ResolveTfm(project.Name, ReadDeclaredTargetFrameworks(project.FilePath));

    /// <summary>
    /// Pure target-framework resolution from a Roslyn project name and the csproj's declared
    /// frameworks. Separated from the file/Roslyn read so the identity-critical branch ordering is
    /// unit-testable without an MSBuild-loaded project.
    /// </summary>
    internal static string ResolveTfm(string? projectName, IReadOnlyList<string> declaredFrameworks)
    {
        var suffix = ExtractParentheticalSuffix(projectName);

        // A parenthetical suffix on the Roslyn project name is MSBuildWorkspace's own per-instance
        // disambiguator for a multi-targeted project (a single-target project carries no suffix), so
        // when present it identifies this instance's evaluated framework. Confirm it against the
        // declared set when possible; otherwise trust a suffix that still looks like a TFM. Both run
        // BEFORE the single-declared fallback so a project whose <TargetFrameworks> is an unevaluated
        // MSBuild expression — which the raw-XML read cannot expand, collapsing the declared set —
        // still keeps its evaluated instances on distinct identities instead of one shared row.
        if (suffix != null)
        {
            if (declaredFrameworks.Any(d => string.Equals(d, suffix, StringComparison.OrdinalIgnoreCase)))
                return suffix;
            if (LooksLikeTfm(suffix))
                return suffix;
        }

        // Single-target project (no per-instance suffix): its sole declared framework is the
        // evaluated one.
        if (declaredFrameworks.Count == 1)
            return declaredFrameworks[0];

        // Deterministic last resort; never null so identity hashing stays stable.
        return declaredFrameworks.Count > 0 ? declaredFrameworks[0] : string.Empty;
    }

    /// <summary>
    /// The target-framework family identifiers a parenthesized project-name suffix may start with to
    /// be treated as a TFM when no csproj is available to confirm it. Covers the non-.NET families a
    /// multi-targeted project can produce (<c>uap</c>, <c>tizen</c>, Xamarin/Mono) so it is not
    /// limited to <c>net*</c>, while still rejecting build configurations/platforms
    /// (<c>Debug</c>, <c>AnyCPU</c>).
    /// </summary>
    private static readonly string[] KnownTfmPrefixes =
    {
        "net",          // net10.0, net48, netstandard2.0, netcoreapp3.1, net8.0-windows
        "uap",          // uap10.0
        "tizen",        // tizen8.0
        "monoandroid", "monotouch", "xamarin", // Xamarin/Mono families
        "portable",     // PCL profiles (portable-net45+win8)
        "sl", "windowsphone", "wp", "wpa", "win", // legacy Silverlight/Windows/Phone
    };

    /// <summary>
    /// Extracts the trimmed inner text of a trailing balanced <c>(...)</c> from a Roslyn project name
    /// (e.g. <c>Foo(net10.0)</c> → <c>net10.0</c>). Returns null when the name carries no such
    /// suffix. Pure and deterministic; unit-tested directly. This is the raw per-instance signal;
    /// <see cref="ResolveEvaluatedTargetFramework"/> confirms it against the declared frameworks.
    /// </summary>
    internal static string? ExtractParentheticalSuffix(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var open = name.LastIndexOf('(');
        if (open < 0 || !name.EndsWith(')')) return null;

        var inner = name.Substring(open + 1, name.Length - open - 2).Trim();
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>
    /// Heuristic used only when a csproj cannot confirm a name suffix: true when the suffix starts
    /// with a known framework family (<see cref="KnownTfmPrefixes"/>) and contains a digit (the
    /// version). Rejects build configurations/platforms such as <c>Debug</c>, <c>Release</c>,
    /// <c>x86</c>, and <c>AnyCPU</c>.
    /// </summary>
    internal static bool LooksLikeTfm(string suffix)
        => suffix.Any(char.IsDigit)
           && KnownTfmPrefixes.Any(p => suffix.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> ReadDeclaredTargetFrameworks(string? projectFilePath)
    {
        if (projectFilePath == null || !File.Exists(projectFilePath)) return Array.Empty<string>();
        try
        {
            var xml = System.Xml.Linq.XDocument.Load(projectFilePath);
            var frameworks = new List<string>();
            foreach (var single in xml.Descendants("TargetFramework"))
                AddIfLiteralTfm(frameworks, single.Value);
            foreach (var multi in xml.Descendants("TargetFrameworks"))
            {
                foreach (var v in (multi.Value ?? string.Empty)
                             .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    AddIfLiteralTfm(frameworks, v);
            }
            return frameworks;
        }
        catch { return Array.Empty<string>(); }
    }

    // Keeps only literal framework values. An unevaluated MSBuild expression (e.g. "$(LibTfms)") is
    // useless for confirming a per-instance suffix and must never be returned as a resolved TFM or
    // stored as one, so it is dropped; resolution then falls back to the per-instance name suffix.
    private static void AddIfLiteralTfm(List<string> frameworks, string? value)
    {
        var v = value?.Trim();
        if (!string.IsNullOrEmpty(v) && !v!.Contains("$("))
            frameworks.Add(v);
    }
}
