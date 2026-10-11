using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sextant.Indexer;

/// <summary>
/// Makes a loaded project's compiler inputs match what <c>csc</c> receives, so its compilation, including the
/// code source generators add, binds the way the real build does (issue #294).
/// </summary>
/// <remarks>
/// <para>
/// MSBuildWorkspace adds one additional document for every <c>AdditionalFiles</c> item the design-time build
/// passes, duplicates included, while the compiler opens each additional file once. A project that lists its
/// <c>.razor</c> files again (<c>&lt;RazorComponent Include="**\*.razor" /&gt;</c> on top of the Razor SDK's
/// own glob) builds fine, but in the workspace the Razor generator sees every component twice, throws on the
/// duplicate hint name (CS8785) and contributes nothing, so no component of that project exists for binding.
/// <see cref="Normalize"/> keeps the first additional document per path, as the compiler does.
/// </para>
/// <para>
/// An analyzer or generator assembly built against a newer Roslyn than the indexer's own cannot load in it
/// (Roslyn reports <c>ReferencesNewerCompiler</c> and skips it without a compilation error). The Razor generator
/// ships inside the .NET SDK and follows the SDK's compiler, so an SDK newer than the indexer's Roslyn package
/// silently removes every Razor component from the compilation. <see cref="Normalize"/> reports each such
/// assembly once, so the gap is visible in the load diagnostics rather than only as unbound names.
/// </para>
/// </remarks>
public static class CompilerInputs
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly Version HostCompilerVersion = typeof(Compilation).Assembly.GetName().Version!;

    /// <summary>
    /// Removes duplicate additional documents from every project in <paramref name="solution"/> and reports, through
    /// <paramref name="onDiagnostic"/>, analyzer assemblies that need a newer compiler than the indexer's. Returns the
    /// solution unchanged when no project has a duplicate.
    /// </summary>
    public static Solution Normalize(Solution solution, Action<string>? onDiagnostic = null)
    {
        ReportAnalyzersNeedingNewerCompiler(solution, onDiagnostic);
        return DeduplicateAdditionalDocuments(solution, onDiagnostic);
    }

    internal static Solution DeduplicateAdditionalDocuments(Solution solution, Action<string>? onDiagnostic)
    {
        var removed = 0;
        var projectsChanged = 0;
        foreach (var projectId in solution.ProjectIds)
        {
            var seen = new HashSet<string>(PathComparer);
            var duplicates = solution.GetProject(projectId)!.AdditionalDocuments
                .Where(d => d.FilePath is { Length: > 0 } path && !seen.Add(Path.GetFullPath(path)))
                .Select(d => d.Id)
                .ToList();
            if (duplicates.Count == 0)
                continue;
            solution = solution.RemoveAdditionalDocuments([.. duplicates]);
            removed += duplicates.Count;
            projectsChanged++;
        }

        if (removed > 0)
            onDiagnostic?.Invoke(
                $"Removed {removed} duplicate additional file(s) from {projectsChanged} project(s); the compiler reads " +
                "each additional file once, and source generators fail on the repeats.");
        return solution;
    }

    internal static void ReportAnalyzersNeedingNewerCompiler(Solution solution, Action<string>? onDiagnostic)
    {
        if (onDiagnostic is null)
            return;
        var projectsByAnalyzer = new Dictionary<string, int>(PathComparer);
        foreach (var project in solution.Projects)
        {
            foreach (var path in project.AnalyzerReferences.OfType<AnalyzerFileReference>()
                         .Select(r => r.FullPath).Distinct(PathComparer))
                projectsByAnalyzer[path] = projectsByAnalyzer.GetValueOrDefault(path) + 1;
        }

        foreach (var (path, projects) in projectsByAnalyzer.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (RequiredCompilerVersion(path) is { } required && required > HostCompilerVersion)
                onDiagnostic(
                    $"Analyzer '{Path.GetFileName(path)}' needs Roslyn {required} but the indexer runs {HostCompilerVersion}, " +
                    $"so it cannot load in {projects} project(s): code its source generators add (Razor components, for " +
                    $"example) is missing and names that use it do not bind. Analyzer path: {path}");
        }
    }

    /// <summary>
    /// The highest <c>Microsoft.CodeAnalysis</c> assembly version the analyzer at <paramref name="path"/> references,
    /// or null when it references none or cannot be read (Roslyn reports an unreadable analyzer on its own).
    /// </summary>
    internal static Version? RequiredCompilerVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;
            var metadata = pe.GetMetadataReader();
            Version? required = null;
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                if (metadata.StringComparer.Equals(reference.Name, "Microsoft.CodeAnalysis")
                    && (required is null || reference.Version > required))
                    required = reference.Version;
            }
            return required;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }
}
