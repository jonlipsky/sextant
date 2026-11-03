using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// The metadata-only images that stand in for already-compiled projects (issue #388). A window of
/// <see cref="CompilationWindows"/> that references a project outside it would otherwise carry that project (and its
/// own references, transitively) as source and compile it again. Here the referenced project's declarations, private
/// ones included, are emitted once into an in-memory image and every window references the image, so a window
/// compiles only its own syntax trees. A project whose declarations do not emit (an unresolved type in a signature
/// is an error) has no image and stays a source project of the windows that reference it, exactly as without images.
/// </summary>
internal sealed class SkeletonCache
{
    private readonly Solution _source;
    private readonly CancellationToken _lifetime;
    private readonly HashSet<ProjectId> _referenced;
    private readonly ConcurrentDictionary<ProjectId, Lazy<Task<Skeleton?>>> _skeletons = new();
    private long _bytes;
    private int _recorded;
    private int _built;
    private int _failed;

    /// <param name="source">The solution whose projects the images stand in for.</param>
    /// <param name="lifetime">
    /// Ends the cache's own compilations. A caller's token only bounds its wait: an image is shared, so a cancelled
    /// caller must not leave a cancelled result for the next one.
    /// </param>
    public SkeletonCache(Solution source, CancellationToken lifetime = default)
    {
        _source = source;
        _lifetime = lifetime;
        _referenced = source.Projects.SelectMany(p => p.ProjectReferences).Select(r => r.ProjectId).ToHashSet();
    }

    /// <summary>The images kept, their size, how many came from a compilation a window already held, and how many failed.</summary>
    public (int Images, long Bytes, int FromWindows, int Failed) Stats =>
        (_skeletons.Count - _failed, Interlocked.Read(ref _bytes), _recorded, _failed);

    /// <summary>Images built by a compilation of their own because no window held one yet.</summary>
    public int ImagesCompiledOnDemand => Volatile.Read(ref _built);

    internal sealed record Skeleton(string Name, AssemblyMetadata Metadata);

    /// <summary>Keeps the image of a project whose compilation a window already built, unless one exists.</summary>
    public void Record(ProjectId id, Project project, Compilation compilation)
    {
        if (!_referenced.Contains(id) || _skeletons.ContainsKey(id))
            return;
        var mine = new Lazy<Task<Skeleton?>>(() =>
        {
            Interlocked.Increment(ref _recorded);
            return Task.FromResult(Emit(project.Name, compilation));
        });
        if (ReferenceEquals(_skeletons.GetOrAdd(id, mine), mine))
            _ = mine.Value;
    }

    private Task<Skeleton?> GetAsync(ProjectId id, CancellationToken cancellationToken)
        => _skeletons.GetOrAdd(id, key => new Lazy<Task<Skeleton?>>(() => BuildOneAsync(key, _lifetime))).Value
            .WaitAsync(cancellationToken);

    private async Task<Skeleton?> BuildOneAsync(ProjectId id, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _built);
        var built = await BuildAsync([id], cancellationToken).ConfigureAwait(false);
        if (built?.Solution.GetProject(id) is not { } project)
            return Failed();
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        return compilation == null ? Failed() : Emit(project.Name, compilation);
    }

    private Skeleton? Failed()
    {
        Interlocked.Increment(ref _failed);
        return null;
    }

    private Skeleton? Emit(string name, Compilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(metadataOnly: true, includePrivateMembers: true), cancellationToken: _lifetime);
        if (!result.Success)
            return Failed();
        Interlocked.Add(ref _bytes, stream.Length);
        return new Skeleton(name, AssemblyMetadata.CreateFromImage(ImmutableArray.Create(stream.ToArray())));
    }

    /// <summary>
    /// A solution of <paramref name="roots"/> in which every referenced project outside it is an image, plus the
    /// projects whose declarations do not emit (and what those reference in turn). Null when a document cannot be
    /// reloaded from disk. <c>Owners</c> maps each image reference to the project it stands for.
    /// </summary>
    public async Task<(Solution Solution, IReadOnlyDictionary<MetadataReference, ProjectId> Owners)?> BuildAsync(
        IReadOnlyList<ProjectId> roots, CancellationToken cancellationToken)
    {
        var included = new List<ProjectId>(roots);
        var members = new HashSet<ProjectId>(roots);
        var images = new Dictionary<ProjectId, Skeleton>();
        for (var i = 0; i < included.Count; i++)
        {
            var project = _source.GetProject(included[i]);
            if (project == null)
                continue;
            foreach (var reference in project.ProjectReferences)
            {
                var target = reference.ProjectId;
                if (members.Contains(target) || images.ContainsKey(target))
                    continue;
                if (await GetAsync(target, cancellationToken).ConfigureAwait(false) is { } image)
                    images[target] = image;
                else if (members.Add(target))
                    included.Add(target);
            }
        }

        var owners = new Dictionary<MetadataReference, ProjectId>();
        var infos = new List<ProjectInfo>();
        foreach (var id in included)
        {
            if (_source.GetProject(id) is not { } project)
                continue;
            var projectReferences = new List<ProjectReference>();
            var metadata = new List<MetadataReference>(project.MetadataReferences);
            foreach (var reference in project.ProjectReferences)
            {
                if (members.Contains(reference.ProjectId))
                {
                    projectReferences.Add(reference);
                    continue;
                }

                var image = images[reference.ProjectId];
                var imageReference = image.Metadata.GetReference(
                    aliases: reference.Aliases, embedInteropTypes: reference.EmbedInteropTypes, display: image.Name);
                owners[imageReference] = reference.ProjectId;
                metadata.Add(imageReference);
            }

            var info = CompilationWindows.ToInfo(project, projectReferences, metadata);
            if (info == null)
                return null;
            infos.Add(info);
        }

        var workspace = new AdhocWorkspace(_source.Workspace.Services.HostServices);
        var solutionInfo = SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), _source.FilePath, infos, _source.AnalyzerReferences);
        return (workspace.AddSolution(solutionInfo), owners);
    }
}
