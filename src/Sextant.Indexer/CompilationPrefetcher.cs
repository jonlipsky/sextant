using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// Compiles the projects a phase will visit, in the order it visits them, ahead of it (issue #269). Roslyn caches a
/// project's compilation on its <see cref="Solution"/>, so when the phase reaches a project its own
/// <c>GetCompilationAsync</c> returns the prefetched one instead of compiling while every other core idles. At most
/// <c>window</c> projects are compiled ahead of the one the phase has reached, which also bounds how many compile at
/// once. The prefetcher keeps no compilation itself: what stays cached is Roslyn's to keep or drop. Compilation is
/// deterministic, so prefetching changes no output. A failed prefetch is ignored: the phase compiles that project
/// itself and sees the failure. <see cref="Stop"/> ends prefetching early (the phase will visit no more projects).
/// </summary>
internal sealed class CompilationPrefetcher : IAsyncDisposable
{
    private readonly SemaphoreSlim _ahead;
    private readonly CancellationTokenSource _cts;
    private readonly Task _running;

    /// <param name="projects">The projects the phase will visit, in order; it calls <see cref="Reached"/> once for each.</param>
    /// <param name="window">How many projects may compile ahead of the phase. Below 2, nothing is prefetched.</param>
    /// <param name="compile">Compiles one project; the default is <see cref="Project.GetCompilationAsync"/>.</param>
    public CompilationPrefetcher(
        IReadOnlyList<Project> projects, int window, CancellationToken cancellationToken,
        Func<Project, CancellationToken, Task>? compile = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ahead = new SemaphoreSlim(Math.Max(0, window));
        compile ??= static async (project, token) => _ = await project.GetCompilationAsync(token).ConfigureAwait(false);
        _running = window < 2 || projects.Count == 0
            ? Task.CompletedTask
            : Task.Run(() => RunAsync(projects, compile, _cts.Token));
    }

    /// <summary>The phase has reached the next project in order: one more may compile ahead.</summary>
    public void Reached() => _ahead.Release();

    /// <summary>The phase will visit no more projects: start no more compilations, and cancel the running ones.</summary>
    public void Stop() => _cts.Cancel();

    private async Task RunAsync(
        IReadOnlyList<Project> projects, Func<Project, CancellationToken, Task> compile, CancellationToken cancellationToken)
    {
        // Each compile task completes without a result, so holding it here never keeps a compilation alive.
        var compiling = new List<Task>(projects.Count);
        try
        {
            foreach (var project in projects)
            {
                await _ahead.WaitAsync(cancellationToken).ConfigureAwait(false);
                compiling.Add(Task.Run(() => compile(project, cancellationToken), cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped (or the run was cancelled) before every project was started.
        }
        try
        {
            await Task.WhenAll(compiling).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A cancelled or failed prefetch: the phase compiles that project itself.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _running.ConfigureAwait(false);
        _cts.Dispose();
        _ahead.Dispose();
    }
}
