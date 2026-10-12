using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

/// <summary>
/// Compiles the projects a phase will visit, in the order it visits them, ahead of it (issue #269). Roslyn caches a
/// project's compilation on its <see cref="Solution"/>, so when the phase reaches a project its own
/// <c>GetCompilationAsync</c> returns the prefetched one instead of compiling while every other core idles. Permits
/// (<c>window</c> of them) bound the prefetch: a project requested in walk order holds one until the phase reaches it,
/// and a project requested early because of cost hints (issue #288) holds one only while its compile runs. So at most
/// <c>window</c> prefetch compiles run at once, apart from one the phase has already reached and is waiting on (that
/// is the phase's own compile, shared through Roslyn's cache). A project the phase has reached is never requested.
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
    private readonly object _gate = new();
    private readonly int _total;
    private readonly bool[] _requested;
    private readonly HashSet<int> _promotedPositions = [];
    private readonly Func<int, Task>? _beforePermit;
    private int _reachedCount;

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

    /// <summary>The projects (key, hinted compile ms) requested ahead of their walk position because of the hints, most expensive first.</summary>
    public IReadOnlyList<(string Key, long CostMs)> Promoted { get; } = [];

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

    /// <summary>
    /// The compile cost to use as a later job's hint: the whole compile, not just the walk's wait. When the walk
    /// reached the project while its prefetch was still compiling, the cost runs from the prefetch's start to the
    /// walk's end (the walk's own window would be only its remaining wait). A finished successful prefetch costs its
    /// own duration; otherwise the walk's own window.
    /// </summary>
    public long CompileCostMs(ProjectId id, long walkRequestedMs, long walkStartedMs, long walkEndedMs)
    {
        var timing = TimingOf(id);
        if (timing is { EndedMs: < 0, StartedMs: >= 0 })
            return Math.Max(0, walkEndedMs - timing.StartedMs);
        var (_, started, ended) = CompileWindow(id, walkRequestedMs, walkStartedMs, walkEndedMs);
        return ended - started;
    }

    /// <param name="projects">The projects the phase will visit, in order; it calls <see cref="Reached"/> once for each.</param>
    /// <param name="window">How many projects may compile ahead of the phase. Below 2, nothing is prefetched.</param>
    /// <param name="compile">Compiles one project; the default is <see cref="Project.GetCompilationAsync"/>.</param>
    /// <param name="recordTimings">Whether to record each prefetched compile's timing (only when metrics are collected).</param>
    /// <param name="hints">Historical compile costs (issue #288): the expensive projects are requested first. Null or empty keeps walk order.</param>
    /// <param name="keyOf">The <see cref="CompileCostHints.KeyOf"/> key of a project; required for <paramref name="hints"/> to apply.</param>
    public CompilationPrefetcher(
        IReadOnlyList<Project> projects, int window, CancellationToken cancellationToken,
        Func<Project, CancellationToken, Task>? compile = null, bool recordTimings = false,
        CompileCostHints? hints = null, Func<Project, string?>? keyOf = null,
        Func<int, Task>? beforePermit = null)
    {
        _beforePermit = beforePermit;
        _total = projects.Count;
        _requested = new bool[_total];
        var requestOrder = Enumerable.Range(0, _total).ToList();
        if (hints is { Count: > 0 } && keyOf != null && window >= 2)
        {
            var keys = projects.Select(keyOf).ToList();
            var (order, promotedCount) = CompilePrefetchPolicy.PrefetchOrder(keys, hints.CostMs, window);
            Promoted = order
                .Take(promotedCount)
                .Select(index => (keys[index]!, hints.CostMs[keys[index]!]))
                .ToList();
            if (promotedCount > 0)
            {
                requestOrder = order.ToList();
                _promotedPositions.UnionWith(order.Take(promotedCount));
            }
        }
        if (recordTimings)
            _timings = new();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ahead = new SemaphoreSlim(Math.Max(0, window));
        compile ??= static async (project, token) => _ = await project.GetCompilationAsync(token).ConfigureAwait(false);
        _running = window < 2 || projects.Count == 0
            ? Task.CompletedTask
            // None on purpose: RunAsync observes the token itself and must always complete so Dispose can await it.
            : Task.Run(() => RunAsync(projects, requestOrder, compile, _cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// The phase has reached the next project in walk order. A project requested in walk order returns its permit now,
    /// so one more may compile ahead; a cost-hinted project returned its permit when its compile ended, and a project
    /// not yet requested holds none, and will not be requested.
    /// </summary>
    public void Reached()
    {
        lock (_gate)
        {
            var position = _reachedCount++;
            if (position >= _total || _promotedPositions.Contains(position) || !_requested[position])
                return;
        }
        _ahead.Release();
    }

    /// <summary>The phase will visit no more projects: start no more compilations, and cancel the running ones.</summary>
    public void Stop() => _cts.Cancel();

    private async Task RunAsync(
        IReadOnlyList<Project> projects, IReadOnlyList<int> requestOrder,
        Func<Project, CancellationToken, Task> compile, CancellationToken cancellationToken)
    {
        // Each compile task completes without a result, so holding it here never keeps a compilation alive.
        var compiling = new List<Task>(projects.Count);
        try
        {
            foreach (var position in requestOrder)
            {
                var project = projects[position];
                var promoted = _promotedPositions.Contains(position);
                lock (_gate)
                {
                    if (position < _reachedCount)
                        continue;
                }
                if (_beforePermit != null)
                    await _beforePermit(position).ConfigureAwait(false);
                await _ahead.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (position < _reachedCount)
                    {
                        // Reached while waiting for a permit: it never held one, so give this one back.
                        _ahead.Release();
                        continue;
                    }
                    _requested[position] = true;
                }
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
                        if (promoted)
                            _ahead.Release();
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
