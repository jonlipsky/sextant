using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sextant.Indexer;

/// <summary>
/// Builds a source generator that a project references as a PROJECT (<c>OutputItemType="Analyzer"</c>), which a design-time
/// load never builds (issue #352). MSBuildWorkspace hands the consuming project an <see cref="UnresolvedAnalyzerReference"/>
/// for the generator's <c>bin/</c> output because nothing produced it, so every member the generator adds is missing and
/// the project reports hundreds of errors and unbound names.
/// </summary>
/// <remarks>
/// <para>
/// For every unresolved analyzer reference whose path is the output of a project the workspace loaded (the solution's
/// projects or their <c>ProjectReference</c> closure), <see cref="ResolveAsync"/> compiles that project with Roslyn from the
/// workspace (no <c>dotnet build</c>, no MSBuild target), emits it and the projects it references to memory with
/// deterministic compilation, and replaces the reference with an <see cref="AnalyzerFileReference"/>. A generator that does
/// not compile or load keeps the unresolved reference and is reported through the diagnostic callback. TRUST: MSBuild
/// evaluation runs in the sandbox with a scrubbed environment, but a generator built from repository source runs INSIDE the
/// service process, as generators that ship in NuGet packages already do. This widens that existing exposure to code in the
/// repository itself; it is contained only by #76 (out-of-process isolation) and can be turned off with
/// <c>SEXTANT_SERVICE_BUILD_GENERATOR_PROJECTS=false</c> (a null cache directory builds nothing).
/// </para>
/// <para>
/// An assembly load context is never unloaded, so a long-lived service must not make one per job. The emitted set (plus the
/// NuGet dependency paths it resolves through) is hashed, and ONE process-wide loader and reference exist per hash, whose
/// assemblies stay in a stable directory under <c>cacheDirectory</c> (the service's cache root), because the reference reads
/// them lazily. The cost therefore grows only when generator SOURCE changes, bounded by <see cref="MaxCachedGenerators"/>
/// SUCCESSFUL loads: past it nothing is loaded, the generator is reported and a Warning-level line is logged; the count
/// resets on restart. A failed build or load is never counted and is retried by a later job. Without a cache directory
/// nothing is built. The first use after a restart removes hash directories nothing holds and <c>*.tmp</c> leftovers.
/// </para>
/// <para>
/// A generator's NuGet dependency (Scriban, say) is found among the metadata-reference paths of the compilations in its
/// closure, then COPIED into the hash directory and hashed by content, so an entry never depends on a checkout or package
/// cache path that a later job may have removed or changed. Two projects of the closure that produce one assembly name fail the build rather than overwrite each other.
/// </para>
/// </remarks>
public static class GeneratorProjects
{
    /// <summary>The most distinct generator builds one process loads; past it a new one is reported and not loaded.</summary>
    public const int MaxCachedGenerators = 64;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private const int MaxProblemChars = 300;

    private const int MaxRememberedFailures = 1024;
    private const string DependencyDirectory = "deps";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Lazy<Entry>> Entries = new(StringComparer.Ordinal);
    // Deterministic load failures (the bytes would fail again), kept out of Entries so they never count against the cap.
    private static readonly Dictionary<string, string> Failures = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SweptDirectories = new(PathComparer);
    private static int s_contexts;
    private static int s_cap = MaxCachedGenerators;

    /// <summary>Generator loaders created by this process (tests assert a repeated generator creates none).</summary>
    internal static int LoadedContexts => Volatile.Read(ref s_contexts);

    /// <summary>Distinct successful (or in-flight) generator builds this process holds.</summary>
    internal static int CachedEntries
    {
        get { lock (Gate) return Entries.Count; }
    }

    internal static int CapForTests
    {
        get => Volatile.Read(ref s_cap);
        set => Volatile.Write(ref s_cap, value);
    }

    private sealed record Entry(AnalyzerFileReference? Reference, string Problem);

    private sealed record Emitted(string Name, byte[] Bytes);

    private sealed record BuildResult(AnalyzerFileReference? Reference, string Problem, bool Reused, bool CapReached = false);

    // Matches the NuGet reference-assembly layout .../ref/<tfm>/<name>.dll, never a bare directory called "ref".
    private static readonly System.Text.RegularExpressions.Regex TargetFrameworkFolder = new(
        @"^(net|netstandard|netcoreapp)\d[\d.]*(-[a-z0-9.]+)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    internal static bool IsReferenceAssemblyPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Length >= 3
            && string.Equals(segments[^3], "ref", StringComparison.OrdinalIgnoreCase)
            && TargetFrameworkFolder.IsMatch(segments[^2]);
    }

    /// <summary>
    /// Replaces each resolvable unresolved generator reference in <paramref name="solution"/>. Returns the solution unchanged
    /// when there is none, or when <paramref name="cacheDirectory"/> is null.
    /// </summary>
    public static async Task<Solution> ResolveAsync(
        Solution solution, string? cacheDirectory, Action<string>? onDiagnostic = null,
        CancellationToken cancellationToken = default, Action<string>? onWarning = null)
    {
        if (cacheDirectory is null)
            return solution;

        var unresolved = new Dictionary<string, List<ProjectId>>(PathComparer);
        foreach (var project in solution.Projects)
        {
            foreach (var reference in project.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>())
            {
                if (string.IsNullOrEmpty(reference.FullPath))
                    continue;
                var path = Path.GetFullPath(reference.FullPath);
                if (!unresolved.TryGetValue(path, out var users))
                    unresolved[path] = users = [];
                users.Add(project.Id);
            }
        }
        if (unresolved.Count == 0)
            return solution;

        var producers = new Dictionary<string, Project>(PathComparer);
        foreach (var project in solution.Projects)
        {
            if (!string.IsNullOrEmpty(project.OutputFilePath))
                producers.TryAdd(Path.GetFullPath(project.OutputFilePath), project);
        }

        foreach (var (path, users) in unresolved.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!producers.TryGetValue(path, out var generator))
                continue;

            var (reference, problem, reused, capReached) = await BuildAsync(solution, generator, cacheDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (reference is null)
            {
                if (capReached)
                    onWarning?.Invoke(
                        $"Source generator project '{generator.Name}' was not built: this process already holds {CapForTests} " +
                        "distinct generator builds and loads no more until the service restarts.");
                onDiagnostic?.Invoke(
                    $"Source generator project '{generator.Name}' is referenced as an analyzer by {users.Count} project(s) but " +
                    $"could not be built from source ({problem}); the code it generates is missing and names that use it do not bind.");
                continue;
            }

            foreach (var id in users)
            {
                var current = solution.GetProject(id)!;
                solution = solution.WithProjectAnalyzerReferences(
                    id,
                    current.AnalyzerReferences.Select(r =>
                        r is UnresolvedAnalyzerReference u && !string.IsNullOrEmpty(u.FullPath)
                        && PathComparer.Equals(Path.GetFullPath(u.FullPath), path)
                            ? (AnalyzerReference)reference
                            : r).ToList());
            }
            onDiagnostic?.Invoke(
                $"{(reused ? "Reused" : "Built")} source generator project '{generator.Name}' from source for {users.Count} project(s); " +
                "its generated code is part of their compilation.");
        }
        return solution;
    }

    private static async Task<BuildResult> BuildAsync(
        Solution solution, Project generator, string cacheDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var emitted = new Dictionary<ProjectId, Emitted>();
            var owners = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
            var references = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var problem = await EmitWithReferencesAsync(solution, generator.Id, emitted, owners, references, cancellationToken)
                .ConfigureAwait(false);
            if (problem is not null)
                return new BuildResult(null, problem, false);

            var primary = emitted[generator.Id];
            // Emitted assemblies shadow a NuGet reference of the same name.
            foreach (var name in owners.Keys)
                references.Remove(name);
            // Copy what the generator loads now: the checkout or package cache may be gone, or changed, by a later job.
            var dependencies = new List<Emitted>();
            foreach (var (name, path) in references)
                dependencies.Add(new Emitted(name, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
            var hash = HashOf(primary.Name, emitted.Values, dependencies);

            Lazy<Entry> lazy;
            var reused = true;
            lock (Gate)
            {
                if (SweptDirectories.Add(Path.GetFullPath(cacheDirectory)))
                    Sweep(cacheDirectory);
                if (Failures.TryGetValue(hash, out var known))
                    return new BuildResult(null, known, true);
                if (!Entries.TryGetValue(hash, out var existing))
                {
                    if (Entries.Count >= CapForTests)
                        return new BuildResult(
                            null, $"this process already holds {CapForTests} distinct generator builds and loads no more", false, true);
                    reused = false;
                    var assemblies = emitted.Values.ToList();
                    existing = new Lazy<Entry>(
                        () => Materialize(hash, cacheDirectory, primary.Name, assemblies, dependencies),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    Entries[hash] = existing;
                }
                lazy = existing;
            }

            var entry = lazy.Value;
            return new BuildResult(entry.Reference, entry.Problem, reused);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return new BuildResult(null, $"{ex.GetType().Name}: {Cut(ex.Message)}", false);
        }
    }

    // Best effort, once per cache directory per process: nothing else uses the directory and the map starts empty, so a
    // hash directory not held by an entry, and any *.tmp file, is a leftover of an earlier run.
    private static void Sweep(string cacheDirectory)
    {
        try
        {
            var root = Path.GetFullPath(cacheDirectory);
            if (!Directory.Exists(root))
                return;
            var inUse = Entries.Keys.Select(h => h[..32]).ToHashSet(StringComparer.Ordinal);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (name.Length != 32 || !name.All(Uri.IsHexDigit) || inUse.Contains(name))
                    continue;
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            }
            foreach (var temp in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    // Writes the emitted set under the hash's own directory (the process holds one entry per hash, so no two jobs race
    // on it) and loads the generator once. An IO failure is not cached: the next job tries again.
    private static Entry Materialize(
        string hash, string cacheDirectory, string primaryName, IReadOnlyList<Emitted> assemblies,
        IReadOnlyList<Emitted> dependencies)
    {
        try
        {
            var directory = Path.Combine(Path.GetFullPath(cacheDirectory), hash[..32]);
            Directory.CreateDirectory(directory);
            foreach (var assembly in assemblies)
                WriteIfChanged(Path.Combine(directory, assembly.Name + ".dll"), assembly.Bytes);
            if (dependencies.Count > 0)
                Directory.CreateDirectory(Path.Combine(directory, DependencyDirectory));
            foreach (var dependency in dependencies)
                WriteIfChanged(Path.Combine(directory, DependencyDirectory, dependency.Name + ".dll"), dependency.Bytes);

            var loader = new SiblingLoader(directory);
            Interlocked.Increment(ref s_contexts);
            var reference = new AnalyzerFileReference(Path.Combine(directory, primaryName + ".dll"), loader);
            string? loadFailure = null;
            reference.AnalyzerLoadFailed += (_, e) => loadFailure ??= e.Message;
            try
            {
                _ = reference.GetGeneratorsForAllLanguages();
                _ = reference.GetAnalyzersForAllLanguages();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loadFailure ??= $"{ex.GetType().Name}: {ex.Message}";
            }
            if (loadFailure is null)
                return new Entry(reference, string.Empty);

            // The same bytes fail the same way: remember it, outside the cap.
            var problem = "it did not load: " + Cut(loadFailure);
            lock (Gate)
            {
                Entries.Remove(hash);
                if (Failures.Count >= MaxRememberedFailures)
                    Failures.Clear();
                Failures[hash] = problem;
            }
            return new Entry(null, problem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never cached by the Lazy: the next job builds it again.
            lock (Gate)
                Entries.Remove(hash);
            return new Entry(null, $"{ex.GetType().Name}: {Cut(ex.Message)}");
        }
    }

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path) && new FileInfo(path).Length == bytes.Length
            && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            return;
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static string HashOf(
        string primary, IEnumerable<Emitted> assemblies, IEnumerable<Emitted> dependencies)
    {
        var lines = new List<string> { "primary:" + primary };
        lines.AddRange(assemblies.Select(a => $"asm:{a.Name}:{Convert.ToHexString(SHA256.HashData(a.Bytes))}"));
        lines.AddRange(dependencies.Select(d => $"dep:{d.Name}:{Convert.ToHexString(SHA256.HashData(d.Bytes))}"));
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    // Emits the project and, first, every project it references, to memory by assembly name. owners maps each assembly
    // name to the project that produced it; references collects the NuGet (file) references of every compilation.
    private static async Task<string?> EmitWithReferencesAsync(
        Solution solution, ProjectId id, Dictionary<ProjectId, Emitted> emitted, Dictionary<string, ProjectId> owners,
        SortedDictionary<string, string> references, CancellationToken cancellationToken)
    {
        if (emitted.ContainsKey(id))
            return null;
        var project = solution.GetProject(id);
        if (project is null)
            return "a referenced project is not in the workspace";
        emitted[id] = new Emitted(string.Empty, []); // a cycle never recurses
        foreach (var dependency in project.ProjectReferences)
        {
            if (await EmitWithReferencesAsync(solution, dependency.ProjectId, emitted, owners, references, cancellationToken)
                    .ConfigureAwait(false) is { } failure)
                return failure;
        }

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
            return $"'{project.Name}' has no compilation";
        var name = compilation.AssemblyName ?? project.AssemblyName;
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return $"'{project.Name}' has an unusable assembly name";
        if (owners.TryGetValue(name, out var other) && other != id)
            return $"'{project.Name}' and '{solution.GetProject(other)?.Name}' both produce the assembly '{name}'";
        owners[name] = id;

        foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
        {
            if (string.IsNullOrEmpty(reference.FilePath) || compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol symbol)
                continue;
            var path = Path.GetFullPath(reference.FilePath);
            // Reference assemblies (the target framework's, a package's ref/ folder) carry no implementation.
            if (IsReferenceAssemblyPath(path))
                continue;
            references.TryAdd(symbol.Identity.Name, path);
        }

        using var stream = new MemoryStream();
        var result = compilation.WithOptions(compilation.Options.WithDeterministic(true))
            .Emit(stream, cancellationToken: cancellationToken);
        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            return $"'{project.Name}' has {errors.Count} compile error(s), first: " +
                   Cut(errors.FirstOrDefault()?.ToString() ?? "unknown");
        }
        emitted[id] = new Emitted(name, stream.ToArray());
        return null;
    }

    private static string Cut(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= MaxProblemChars ? single : single[..(MaxProblemChars - 3)] + "...";
    }

    /// <summary>
    /// Loads an emitted generator and the assemblies emitted beside it, then (only when the host cannot) a NuGet dependency
    /// from the <c>deps</c> folder of the generator's own directory. Anything else, the compiler included, comes from the
    /// default context, so the generator binds to the host's Roslyn.
    /// </summary>
    private sealed class SiblingLoader : AssemblyLoadContext, IAnalyzerAssemblyLoader
    {
        private readonly string _directory;

        public SiblingLoader(string directory)
            : base(isCollectible: false)
        {
            _directory = directory;
            Resolving += ResolveDependency;
        }

        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => LoadFromAssemblyPath(Path.GetFullPath(fullPath));

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null || assemblyName.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
                return null;
            var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }

        // Raised only after the default context could not resolve the name.
        private Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            if (assemblyName.Name is not { } name)
                return null;
            var path = Path.Combine(_directory, DependencyDirectory, name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
