using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// Compiles the projects a phase will visit, in the order it visits them, ahead of it (issue #269). Roslyn caches a
/// project's compilation on its <see cref="Solution"/>, so when the phase reaches a project its own
/// <c>GetCompilationAsync</c> returns the prefetched one instead of compiling while every other core idles. At most
/// <c>window</c> projects are compiled ahead of the one the phase has reached, which also bounds how many compile at
/// once.
/// The prefetcher keeps no compilation itself: what stays cached is Roslyn's to keep or drop. Compilation is
/// deterministic, so prefetching changes no output. A failed prefetch is ignored: the phase compiles that project
/// itself and sees the failure. <see cref="Stop"/> ends prefetching early (the phase will visit no more projects).
/// </summary>
internal sealed class CompilationPrefetcher : IAsyncDisposable
{
    private readonly SemaphoreSlim _ahead;
    private readonly CancellationTokenSource _cts;
    private readonly Task _running;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<ProjectId, CompileTiming>? _timings;

    /// <summary>
    /// When a prefetched compilation was requested, started and ended, in ms since the prefetcher was created.
    /// Written by a compile thread, read by the phase.
    /// </summary>
    internal sealed class CompileTiming
    {
        private long _started = -1;
        private long _ended = -1;
        private int _succeeded;

        public long RequestedMs { get; init; }
        public long StartedMs => Volatile.Read(ref _started);
        public long EndedMs => Volatile.Read(ref _ended);

        /// <summary>Whether the prefetch finished without failing or being cancelled; meaningful once <see cref="EndedMs"/> is set.</summary>
        public bool Succeeded => Volatile.Read(ref _succeeded) == 1;

        internal void Start(long nowMs) => Volatile.Write(ref _started, Math.Max(nowMs, RequestedMs));

        internal void End(long nowMs, bool succeeded)
        {
            // The outcome is published before the end time, so a reader that sees the end sees the outcome.
            Volatile.Write(ref _succeeded, succeeded ? 1 : 0);
            Volatile.Write(ref _ended, Math.Max(nowMs, Math.Max(StartedMs, RequestedMs)));
        }
    }

    /// <summary>Milliseconds since the prefetcher was created (the symbol phase's start).</summary>
    public long NowMs => _clock.ElapsedMilliseconds;

    /// <summary>The prefetched compile of a project, or null when it was not requested (or timings are not recorded).</summary>
    public CompileTiming? TimingOf(ProjectId id) => _timings != null && _timings.TryGetValue(id, out var t) ? t : null;

    /// <summary>Whether the prefetch of the project succeeded and has finished: the walk's compile is then a prefetch hit.</summary>
    public bool IsHit(ProjectId id) => TimingOf(id) is { EndedMs: >= 0, Succeeded: true };

    /// <summary>
    /// The compile window to record for a project: the prefetch's when it succeeded, else the walk's own around its
    /// <c>GetCompilationAsync</c> call (a failed or cancelled prefetch is a miss). Ordered requested &lt;= started &lt;= ended.
    /// </summary>
    public (long Requested, long Started, long Ended) CompileWindow(ProjectId id, long walkRequestedMs, long walkStartedMs, long walkEndedMs)
    {
        if (TimingOf(id) is { EndedMs: >= 0, Succeeded: true } t)
            return (t.RequestedMs, t.StartedMs, t.EndedMs);
        var started = Math.Max(walkStartedMs, walkRequestedMs);
        return (walkRequestedMs, started, Math.Max(walkEndedMs, started));
    }

    /// <param name="projects">The projects the phase will visit, in order; it calls <see cref="Reached"/> once for each.</param>
    /// <param name="window">How many projects may compile ahead of the phase. Below 2, nothing is prefetched.</param>
    /// <param name="compile">Compiles one project; the default is <see cref="Project.GetCompilationAsync"/>.</param>
    /// <param name="recordTimings">Whether to record each prefetched compile's timing (only when metrics are collected).</param>
    public CompilationPrefetcher(
        IReadOnlyList<Project> projects, int window, CancellationToken cancellationToken,
        Func<Project, CancellationToken, Task>? compile = null, bool recordTimings = false)
    {
        if (recordTimings)
            _timings = new();
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
                var timing = _timings != null ? new CompileTiming { RequestedMs = NowMs } : null;
                if (timing != null)
                    _timings![project.Id] = timing;
                compiling.Add(Task.Run(async () =>
                {
                    timing?.Start(NowMs);
                    var succeeded = false;
                    try
                    {
                        await compile(project, cancellationToken).ConfigureAwait(false);
                        succeeded = true;
                    }
                    finally
                    {
                        timing?.End(NowMs, succeeded);
                    }
                }, cancellationToken));
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
