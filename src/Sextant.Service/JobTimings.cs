using System.Diagnostics;
using System.Text.Json;
using Sextant.Core;
using Sextant.Indexer;

namespace Sextant.Service;

/// <summary>
/// Where one job's time went (issue #267): wall and CPU time per phase, the load's shape and process accounting, and
/// the slowest projects. Recorded on the job (<c>snapshot_job_timings</c>), returned by <c>GET /control/status</c>, and
/// aggregated per phase by <c>/control/metrics</c>. Telemetry only: nothing a snapshot contains depends on it.
/// </summary>
public sealed record JobTimings
{
    /// <summary>Wall-clock time of the whole job on the worker, in milliseconds.</summary>
    public long TotalMs { get; init; }

    /// <summary>CPU time of the service process over the job, in milliseconds (includes concurrent query traffic).</summary>
    public long CpuMs { get; init; }

    /// <summary>CPU time of child processes that exited during the job (restore, BuildHosts), or null if unknown.</summary>
    public long? ChildCpuMs { get; init; }

    /// <summary>
    /// Phases in execution order: the worker's (checkout … coverage scan), then the indexer's, then
    /// <c>indexer_other</c>: the indexer's time outside its phases (setup, the publish transaction, post-publish
    /// maintenance, and a phase a failure interrupted before it was stamped).
    /// </summary>
    public IReadOnlyList<JobPhaseTiming> Phases { get; init; } = [];

    /// <summary>The solution load, or null when the job ended before loading.</summary>
    public JobLoadTimings? Load { get; init; }

    /// <summary>The slowest projects of the extraction phases, slowest first (at most <see cref="JobTimingsRecorder.Top"/>).</summary>
    public IReadOnlyList<ProjectTiming> SlowestProjects { get; init; } = [];
}

/// <summary>One phase of a job (issue #267). <c>cpu_ms / wall_ms</c> is the parallelism it achieved.</summary>
/// <param name="Name">The phase (<see cref="IndexPhaseNames"/>): <c>checkout</c>, <c>sdk_pin</c>, <c>restore</c>, <c>load</c>,
/// <c>coverage_scan</c>, an indexer phase (<c>extracting_symbols</c>, …) or <c>indexer_other</c>.</param>
/// <param name="WallMs">Wall-clock duration, in milliseconds.</param>
/// <param name="CpuMs">CPU time of the service process during the phase, in milliseconds, or null when not measured.</param>
/// <param name="ChildCpuMs">CPU time of child processes that exited during the phase, or null if unknown.</param>
/// <param name="Projects">Projects the phase processed, for indexer phases.</param>
/// <param name="Status">An indexer phase's outcome (<c>completed</c>, <c>cancelled</c>, …); null for worker phases.</param>
public sealed record JobPhaseTiming(
    string Name, long WallMs, long? CpuMs, long? ChildCpuMs, int? Projects = null, string? Status = null);

/// <summary>The solution load of a job (issue #267).</summary>
public sealed record JobLoadTimings
{
    /// <summary><c>solution</c> (one whole-solution open) or <c>union</c> (projects opened individually).</summary>
    public required string Mode { get; init; }

    /// <summary>Wall-clock time of the load, in milliseconds.</summary>
    public long WallMs { get; init; }

    /// <summary>Projects in the loaded solution.</summary>
    public int ProjectsLoaded { get; init; }

    /// <summary>Individual opens (union mode); 0 for a whole-solution load.</summary>
    public int ProjectsOpened { get; init; }

    /// <summary>MSBuild BuildHost processes observed during the load, or null where they cannot be observed.</summary>
    public ChildProcessUsage? BuildHosts { get; init; }

    /// <summary>The slowest individual opens, slowest first (union mode).</summary>
    public IReadOnlyList<ProjectTiming> SlowestOpens { get; init; } = [];
}

/// <summary>
/// Collects a job's <see cref="JobTimings"/> as the worker runs (issue #267). Phases are measured with
/// <see cref="Phase"/> scopes; the indexer's own phases come from the <see cref="IndexingMetrics"/> it fills. Safe to
/// read from the worker's failure handlers: whatever was recorded before a failure is kept.
/// </summary>
public sealed class JobTimingsRecorder
{
    /// <summary>How many slowest projects and opens are kept.</summary>
    public const int Top = 10;

    private readonly object _gate = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly ProcessCpuClock _start = ProcessCpuClock.Now();
    private readonly List<JobPhaseTiming> _phases = [];
    private JobLoadTimings? _load;
    private IndexingMetrics? _indexing;
    private long _indexWallMs;

    /// <summary>Measures a worker phase until the returned scope is disposed.</summary>
    public IDisposable Phase(string name) => new Scope(this, name);

    /// <summary>Records the solution load and what was observed of its BuildHost processes.</summary>
    public void RecordLoad(MultiSolutionLoadResult load, ChildProcessUsage? buildHosts)
    {
        ArgumentNullException.ThrowIfNull(load);
        var timings = new JobLoadTimings
        {
            Mode = load.LoadMode,
            WallMs = load.LoadMs,
            ProjectsLoaded = load.Solution.ProjectIds.Count,
            ProjectsOpened = load.LoadTimings.Count,
            BuildHosts = buildHosts,
            SlowestOpens = load.LoadTimings.OrderByDescending(t => t.WallMs).Take(Top).ToList()
        };
        lock (_gate) _load = timings;
    }

    /// <summary>Records the indexer run: its phases, and its wall time (what its phases do not cover is <c>indexer_other</c>).</summary>
    public void RecordIndexing(IndexingMetrics indexing, long indexWallMs)
    {
        ArgumentNullException.ThrowIfNull(indexing);
        lock (_gate)
        {
            _indexing = indexing;
            _indexWallMs = indexWallMs;
        }
    }

    /// <summary>The timings recorded so far.</summary>
    public JobTimings Build()
    {
        var (cpu, children) = ProcessCpuClock.Now().Since(_start);
        lock (_gate)
        {
            var phases = new List<JobPhaseTiming>(_phases);
            IReadOnlyList<ProjectTiming> slowest = [];
            if (_indexing is { } indexing)
            {
                foreach (var phase in indexing.Phases)
                {
                    phases.Add(new JobPhaseTiming(
                        phase.Name, phase.DurationMs, phase.CpuMs, phase.ChildCpuMs, phase.ProjectsProcessed,
                        phase.Status.ToString().ToLowerInvariant()));
                }
                var other = _indexWallMs - indexing.Phases.Sum(p => p.DurationMs);
                if (other > 0)
                    phases.Add(new JobPhaseTiming(IndexPhaseNames.IndexerOther, other, null, null));
                slowest = indexing.Projects.OrderByDescending(p => p.WallMs).Take(Top).ToList();
            }

            return new JobTimings
            {
                TotalMs = _total.ElapsedMilliseconds,
                CpuMs = cpu,
                ChildCpuMs = children,
                Phases = phases,
                Load = _load,
                SlowestProjects = slowest
            };
        }
    }

    private void Add(JobPhaseTiming phase)
    {
        lock (_gate) _phases.Add(phase);
    }

    private sealed class Scope(JobTimingsRecorder owner, string name) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly ProcessCpuClock _cpu = ProcessCpuClock.Now();
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            var (cpu, children) = ProcessCpuClock.Now().Since(_cpu);
            owner.Add(new JobPhaseTiming(name, _watch.ElapsedMilliseconds, cpu, children));
        }
    }
}

/// <summary>Reads stored <see cref="JobTimings"/> documents (issue #267).</summary>
public static class JobTimingsJson
{
    /// <summary>The stored form of <paramref name="timings"/>: the service wire format (snake_case).</summary>
    public static string Serialize(JobTimings timings) => JsonSerializer.Serialize(timings, ServiceJson.Options);

    /// <summary>The timings in <paramref name="json"/>, or null when there are none or they cannot be read.</summary>
    public static JobTimings? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<JobTimings>(json, ServiceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
