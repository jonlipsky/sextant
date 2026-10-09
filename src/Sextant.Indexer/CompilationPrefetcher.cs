using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// Compiles the projects a phase will visit, in the order it visits them, ahead of it (issue #269). Roslyn caches a
/// project's compilation on its <see cref="Solution"/>, so when the phase reaches a project its own
/// <c>GetCompilationAsync</c> returns the prefetched one instead of compiling while every other core idles. At most
/// <c>window</c> projects are compiled ahead of the one the phase has reached (which also bounds how many compile at
/// once). Compilation is deterministic, so prefetching changes no output. A failed prefetch is ignored: the phase
/// compiles that project itself and sees the failure.
/// </summary>
internal sealed class CompilationPrefetcher : IAsyncDisposable
{
    private readonly SemaphoreSlim _ahead;
    private readonly CancellationTokenSource _cts;
    private readonly Task _running;

    /// <param name="projects">The projects the phase will visit, in order; it calls <see cref="Reached"/> once for each.</param>
    /// <param name="window">How many projects may compile ahead of the phase. Below 2, nothing is prefetched.</param>
    public CompilationPrefetcher(IReadOnlyList<Project> projects, int window, CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ahead = new SemaphoreSlim(Math.Max(0, window));
        _running = window < 2 || projects.Count == 0 ? Task.CompletedTask : Task.Run(() => RunAsync(projects, _cts.Token));
    }

    /// <summary>The phase has reached the next project in order: one more may compile ahead.</summary>
    public void Reached() => _ahead.Release();

    private async Task RunAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        var compiling = new List<Task>(projects.Count);
        try
        {
            foreach (var project in projects)
            {
                await _ahead.WaitAsync(cancellationToken).ConfigureAwait(false);
                compiling.Add(Task.Run(() => project.GetCompilationAsync(cancellationToken), cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // The phase ended (or was cancelled) before every project was started.
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
