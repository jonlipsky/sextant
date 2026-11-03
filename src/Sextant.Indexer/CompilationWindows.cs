using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// Bounds the compilations a phase keeps alive (issue #388). Roslyn caches each project's compilation on the
/// <see cref="Solution"/> lineage that built it, and a derived solution shares the cache of every project it did not
/// change, so one solution walked end to end holds every compilation it ever built (a large repository exceeded the
/// sandbox memory budget that way). The projects a phase visits are split, in order, into windows of
/// <c>windowSize</c>; each window compiles on its own solution, rebuilt from the project definitions in a new
/// workspace, so nothing is shared with the others. Dropping a window drops its compilations. A project in a window
/// compiles its dependencies from that window's solution, so a dependency is compiled again once per window that
/// needs it: the price of the bound. Compilation is deterministic, so no output changes. With the source solution
/// untouched (it is only read, never compiled) and at most one window of projects, the source solution is used as is.
/// </summary>
internal sealed class CompilationWindows
{
    private readonly Solution _source;
    private readonly Dictionary<ProjectId, int> _windowOf = [];
    private readonly Dictionary<int, Lazy<Task<WindowSolution>>> _windows = [];
    private readonly IReadOnlyList<ProjectId> _order;
    private readonly SkeletonCache? _skeletons;
    private readonly Dictionary<int, TaskCompletionSource> _reached = [];
    private readonly bool _enabled;
    private readonly int _size;
    private readonly object _gate = new();

    public CompilationWindows(
        Solution source, IReadOnlyList<ProjectId> order, int windowSize, SkeletonCache? skeletons = null, int minProjects = 0)
    {
        _source = source;
        _order = order;
        _skeletons = skeletons;
        _size = windowSize;
        _enabled = windowSize > 0 && order.Count > windowSize && order.Count > minProjects;
        if (!_enabled)
            return;
        for (var i = 0; i < order.Count; i++)
            _windowOf[order[i]] = i / windowSize;
    }

    /// <summary>The window a project belongs to (0 when windows are off).</summary>
    public int WindowOf(ProjectId id) => _windowOf.TryGetValue(id, out var window) ? window : 0;

    /// <summary>The phase has reached a project of <paramref name="window"/>: prefetching into it may begin.</summary>
    public void Reached(int window)
    {
        if (!_enabled)
            return;
        lock (_gate)
        {
            for (var w = 0; w <= window; w++)
                ReachedSource(w).TrySetResult();
        }
    }

    /// <summary>
    /// Completes once the phase reaches the window of the project at <paramref name="position"/> in the order. A
    /// prefetch waits on it so it never compiles a window's closure while the previous window's is still in use.
    /// </summary>
    public Task WhenReached(int position)
    {
        if (!_enabled)
            return Task.CompletedTask;
        lock (_gate)
            return ReachedSource(position / _size).Task;
    }

    private TaskCompletionSource ReachedSource(int window)
    {
        if (!_reached.TryGetValue(window, out var source))
            _reached[window] = source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return source;
    }

    /// <summary>The solution the project must be compiled and resolved on, and the project itself on it.</summary>
    public (Solution Solution, Project? Project) Of(ProjectId id)
    {
        var window = OfAsync(id, CancellationToken.None).GetAwaiter().GetResult();
        return (window.Solution, window.Project);
    }

    /// <summary>
    /// The solution the project must be compiled and resolved on, the project itself on it, and, when dependencies
    /// stand in as skeleton images, the project each of those references belongs to.
    /// </summary>
    public async Task<WindowProject> OfAsync(ProjectId id, CancellationToken cancellationToken)
    {
        var window = await WindowOfAsync(WindowOf(id), cancellationToken).ConfigureAwait(false);
        return new WindowProject(window.Solution, window.Solution.GetProject(id), window.Owners);
    }

    /// <summary>
    /// Compiles the project on its window and, when its compilation is the first to exist, keeps its skeleton for the
    /// windows that reference it. Null when the project has no compilation.
    /// </summary>
    public async Task<Compilation?> CompileAsync(ProjectId id, CancellationToken cancellationToken)
    {
        var window = await OfAsync(id, cancellationToken).ConfigureAwait(false);
        if (window.Project == null)
            return null;
        var compilation = await window.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation != null)
            _skeletons?.Record(id, window.Project, compilation);
        return compilation;
    }

    /// <summary>
    /// The assemblies of <paramref name="project"/>'s skeleton references, each mapped to the project it stands for,
    /// read from its <paramref name="compilation"/>.
    /// </summary>
    public static Dictionary<IAssemblySymbol, ProjectId> SkeletonAssemblies(WindowProject window, Compilation compilation)
    {
        var result = new Dictionary<IAssemblySymbol, ProjectId>(SymbolEqualityComparer.Default);
        if (window.Owners == null || window.Project == null)
            return result;
        foreach (var reference in window.Project.MetadataReferences)
        {
            if (window.Owners.TryGetValue(reference, out var owner)
                && compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                result[assembly] = owner;
        }
        return result;
    }

    internal sealed record WindowSolution(Solution Solution, IReadOnlyDictionary<MetadataReference, ProjectId>? Owners);

    /// <summary>A project on its window's solution; <c>Owners</c> maps skeleton references to their projects.</summary>
    public sealed record WindowProject(
        Solution Solution, Project? Project, IReadOnlyDictionary<MetadataReference, ProjectId>? Owners);

    /// <summary>Whether the order is split into windows (otherwise the source solution is used as is).</summary>
    public bool Enabled => _enabled;

    /// <summary>Forgets every window, e.g. once the phase is over, so the next phase does not keep one alive.</summary>
    public void ReleaseAll() => ReleaseBefore(int.MaxValue);

    /// <summary>Forgets the solutions of windows before <paramref name="window"/>; work still using one keeps it alive.</summary>
    public void ReleaseBefore(int window)
    {
        if (!_enabled)
            return;
        var released = false;
        lock (_gate)
        {
            foreach (var old in _windows.Keys.Where(w => w < window).ToList())
                released |= _windows.Remove(old);
        }

        // A dropped window is garbage the runtime would otherwise leave until its own gen2 trigger, and the
        // sandbox budget counts the process working set, not the live heap.
        if (released)
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    private Task<WindowSolution> WindowOfAsync(int window, CancellationToken cancellationToken)
    {
        if (!_enabled)
            return Task.FromResult(new WindowSolution(_source, null));
        Lazy<Task<WindowSolution>> lazy;
        lock (_gate)
        {
            if (!_windows.TryGetValue(window, out lazy!))
                _windows[window] = lazy = new Lazy<Task<WindowSolution>>(() => BuildWindowAsync(window));
        }
        return lazy.Value.WaitAsync(cancellationToken);
    }

    private async Task<WindowSolution> BuildWindowAsync(int window)
    {
        if (_skeletons != null)
        {
            var ids = _order.Skip(window * _size).Take(_size).ToList();
            var built = await _skeletons.BuildAsync(ids, CancellationToken.None).ConfigureAwait(false);
            if (built != null)
                return new WindowSolution(built.Value.Solution, built.Value.Owners);
        }

        return new WindowSolution(Detach(_source) ?? _source, null);
    }

    /// <summary>
    /// A solution with the same projects, references and documents but no compilation in common with
    /// <paramref name="source"/>, or null when a document cannot be reloaded from disk.
    /// </summary>
    internal static Solution? Detach(Solution source)
    {
        var infos = new List<ProjectInfo>();
        foreach (var project in source.Projects)
        {
            var info = ToInfo(project);
            if (info == null)
                return null;
            infos.Add(info);
        }

        var workspace = new AdhocWorkspace(source.Workspace.Services.HostServices);
        var solutionInfo = SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), source.FilePath, infos, source.AnalyzerReferences);
        return workspace.AddSolution(solutionInfo);
    }

    internal static ProjectInfo? ToInfo(
        Project project, IEnumerable<ProjectReference>? projectReferences = null,
        IEnumerable<MetadataReference>? metadataReferences = null)
    {
        var documents = new List<DocumentInfo>();
        foreach (var document in project.Documents)
        {
            if (document.FilePath == null)
                return null;
            documents.Add(DocumentInfo.Create(
                document.Id, document.Name, document.Folders, document.SourceCodeKind,
                new FileTextLoader(document.FilePath, defaultEncoding: null), document.FilePath));
        }

        var additional = new List<DocumentInfo>();
        foreach (var document in project.AdditionalDocuments)
        {
            if (document.FilePath == null)
                return null;
            additional.Add(DocumentInfo.Create(
                document.Id, document.Name, document.Folders, SourceCodeKind.Regular,
                new FileTextLoader(document.FilePath, defaultEncoding: null), document.FilePath));
        }

        var analyzerConfigs = new List<DocumentInfo>();
        foreach (var document in project.AnalyzerConfigDocuments)
        {
            if (document.FilePath == null)
                return null;
            analyzerConfigs.Add(DocumentInfo.Create(
                document.Id, document.Name, document.Folders, SourceCodeKind.Regular,
                new FileTextLoader(document.FilePath, defaultEncoding: null), document.FilePath));
        }

        return ProjectInfo.Create(
                project.Id, project.Version, project.Name, project.AssemblyName, project.Language,
                project.FilePath, project.OutputFilePath, project.CompilationOptions, project.ParseOptions,
                documents, projectReferences ?? project.ProjectReferences, metadataReferences ?? project.MetadataReferences,
                project.AnalyzerReferences,
                additional, project.IsSubmission)
            .WithAnalyzerConfigDocuments(analyzerConfigs.Select(d => d))
            .WithDefaultNamespace(project.DefaultNamespace)
            .WithOutputRefFilePath(project.OutputRefFilePath)
            .WithCompilationOutputInfo(project.CompilationOutputInfo);
    }
}
