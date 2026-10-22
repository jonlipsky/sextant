using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>The package restore, or null when the job ended before restoring.</summary>
    public JobRestoreTimings? Restore { get; init; }

    /// <summary>The solution load, or null when the job ended before loading.</summary>
    public JobLoadTimings? Load { get; init; }

    /// <summary>The slowest projects of the extraction phases, slowest first (at most <see cref="JobTimingsRecorder.Top"/>).</summary>
    public IReadOnlyList<ProjectTiming> SlowestProjects { get; init; } = [];

    /// <summary>
    /// What the symbol phase's longest compilations took (issue #288), most expensive first: the hints a later job of the
    /// same repository gives its compilation prefetcher.
    /// </summary>
    [JsonConverter(typeof(CompileCostsConverter))]
    public IReadOnlyList<ProjectCompileCost> CompileCosts { get; init; } = [];
}

/// <summary>
/// Reads the compile costs leniently: an entry that is not an object with a string <c>key</c> and an integer <c>ms</c>
/// is skipped, so one bad entry never makes the rest of a stored document unreadable.
/// </summary>
internal sealed class CompileCostsConverter : JsonConverter<IReadOnlyList<ProjectCompileCost>>
{
    public override IReadOnlyList<ProjectCompileCost> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var costs = new List<ProjectCompileCost>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return costs;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() is { } text
                && entry.TryGetProperty("ms", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out var value))
                costs.Add(new ProjectCompileCost(text, value));
        }
        return costs;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ProjectCompileCost> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var cost in value)
        {
            writer.WriteStartObject();
            writer.WriteString("key", cost.Key);
            writer.WriteNumber("ms", cost.Ms);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>One project's compile time in the symbol phase (issue #288).</summary>
/// <param name="Key">The project's <see cref="CompileCostHints.KeyOf"/> key: repo-relative path and target framework.</param>
/// <param name="Ms">Compile time (ended minus started), in milliseconds.</param>
public sealed record ProjectCompileCost(string Key, long Ms);

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

/// <summary>The package restore of a job (issue #272).</summary>
public sealed record JobRestoreTimings
{
    /// <summary>
    /// <c>off</c> (restore disabled), <c>solution</c> (one selected solution), <c>union</c> (one traversal over the
    /// selected solutions' project union), <c>per_solution</c> (the fallback: one restore per selected solution) or
    /// <c>reused</c> (no restore ran: the repository's saved assets were replayed because no restore input changed).
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>Why the union could not be used (<see cref="Restore.PackageRestoreOutcome.UnionFallbackReason"/>).</summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// <c>persistent</c> when the restore used the repository's own package folder kept between its jobs, or
    /// <c>scratch</c> when it used a cold folder in job scratch.
    /// </summary>
    public required string PackageFolder { get; init; }

    /// <summary>Wall-clock time of the restore, in milliseconds.</summary>
    public long WallMs { get; init; }

    /// <summary>True when the restore ran out of time (the bound is <see cref="TimeoutMs"/>) and was killed.</summary>
    public bool TimedOut { get; init; }

    /// <summary>The bound the restore ran under, in milliseconds.</summary>
    public long TimeoutMs { get; init; }

    /// <summary>Every restore process the job ran, in order: kind, solution, projects, up-to-date projects, time, result (issue #272).</summary>
    public IReadOnlyList<Restore.RestorePassTiming> Passes { get; init; } = [];

    /// <summary>The restore-state cache decision (<c>reused</c>/<c>partial</c>/<c>miss</c>/<c>unavailable</c>), or null when not in play (issue #272).</summary>
    public Restore.RestoreReuseOutcome? Reuse { get; init; }

    /// <summary>True when at least one restore pass ran with static-graph evaluation (issue #272); see <see cref="StaticGraphFallbacks"/>.</summary>
    public bool StaticGraph { get; init; }

    /// <summary>Static-graph passes that failed as a whole and were re-run once without it (issue #272).</summary>
    public int StaticGraphFallbacks { get; init; }

    /// <summary>Solutions whose whole restore failed and was retried project by project (issue #304).</summary>
    public int SolutionsRetriedPerProject { get; init; }

    /// <summary>Projects restored one by one by those retries (issue #304).</summary>
    public int ProjectsRetried { get; init; }

    /// <summary>Selected solutions not restored because an earlier one had already restored all their projects (issue #290).</summary>
    public int SolutionsSkippedRedundant { get; init; }

    /// <summary>File names of the skipped solutions (issue #290).</summary>
    public IReadOnlyList<string> SkippedSolutionNames { get; init; } = [];

    /// <summary>Solutions restored only for their not-yet-covered projects (issue #290).</summary>
    public int SolutionsPartiallySkipped { get; init; }

    /// <summary>Projects restored that way (issue #290).</summary>
    public int ProjectsRestoredPartially { get; init; }

    /// <summary>File names of the partially skipped solutions (issue #290).</summary>
    public IReadOnlyList<string> PartiallySkippedSolutionNames { get; init; } = [];

    /// <summary>Projects that reported restore errors (issue #349).</summary>
    public int ProjectsFailed { get; init; }

    /// <summary>The top restore error codes with the number of projects reporting each, whether it reproduces, and a sanitized sample message (issue #349).</summary>
    public IReadOnlyList<Restore.RestoreErrorSummary> ErrorCodes { get; init; } = [];

    /// <summary>True when a failure is transient or unrecognised, so the failing set was not cached as failing (issue #349).</summary>
    public bool FailuresNotProvablyDeterministic { get; init; }

    /// <summary>Per-solution passes that skipped static-graph evaluation because it failed for that solution under the same inputs (issue #349).</summary>
    public int StaticGraphSkippedByMemo { get; init; }

    /// <summary>Solutions covered by projects replayed from the restore state (issue #349).</summary>
    public int SolutionsCoveredByReplay { get; init; }
}

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
    private JobRestoreTimings? _restore;
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

    /// <summary>Records how the package restore ran and which package folder it used (issue #272).</summary>
    public void RecordRestore(Restore.PackageRestoreOutcome restore, bool persistentPackageFolder)
    {
        ArgumentNullException.ThrowIfNull(restore);
        var timings = new JobRestoreTimings
        {
            Mode = !restore.Enabled ? "off"
                : restore.Reuse is { Decision: "reused" } ? "reused"
                : restore.UsedProjectUnion ? "union"
                : restore.SolutionsSelected > 1 ? "per_solution"
                : "solution",
            FallbackReason = restore.UnionFallbackReason,
            PackageFolder = persistentPackageFolder ? "persistent" : "scratch",
            WallMs = (long)restore.Elapsed.TotalMilliseconds,
            TimedOut = restore.TimedOut,
            TimeoutMs = (long)restore.Timeout.TotalMilliseconds,
            Passes = restore.Passes,
            Reuse = restore.Reuse,
            StaticGraph = restore.StaticGraphPasses > 0,
            StaticGraphFallbacks = restore.StaticGraphFallbacks,
            SolutionsRetriedPerProject = restore.SolutionsRetriedPerProject,
            ProjectsRetried = restore.ProjectsRetried,
            SolutionsSkippedRedundant = restore.SolutionsSkippedRedundant,
            SkippedSolutionNames = restore.SkippedSolutionNames,
            SolutionsPartiallySkipped = restore.SolutionsPartiallySkipped,
            ProjectsRestoredPartially = restore.ProjectsRestoredPartially,
            PartiallySkippedSolutionNames = restore.PartiallySkippedSolutionNames,
            ProjectsFailed = restore.Projects.Count,
            ErrorCodes = restore.ErrorCodes(),
            FailuresNotProvablyDeterministic = restore.HasNonDeterministicFailure,
            StaticGraphSkippedByMemo = restore.StaticGraphSkippedByMemo,
            SolutionsCoveredByReplay = restore.SolutionsCoveredByReplay
        };
        lock (_gate) _restore = timings;
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
            IReadOnlyList<ProjectCompileCost> compileCosts = [];
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
                compileCosts = CompileCostsOf(indexing.Projects);
            }

            return new JobTimings
            {
                TotalMs = _total.ElapsedMilliseconds,
                CpuMs = cpu,
                ChildCpuMs = children,
                Phases = phases,
                Restore = _restore,
                Load = _load,
                SlowestProjects = slowest,
                CompileCosts = compileCosts
            };
        }
    }

    /// <summary>Projects recorded as compile costs: at least this long (the prefetcher promotes from 2 s).</summary>
    internal const long MinCompileCostMs = 1000;

    /// <summary>At most this many compile costs are recorded per job.</summary>
    internal const int MaxCompileCosts = 100;

    internal static IReadOnlyList<ProjectCompileCost> CompileCostsOf(IEnumerable<ProjectTiming> projects) =>
        projects
            .Where(p => p.Phase == IndexPhaseNames.ExtractingSymbols && p.Key != null
                        && (p.CompileCostMs != null || (p.CompileStartedAtMs != null && p.CompileEndedAtMs != null)))
            .Select(p => new ProjectCompileCost(
                p.Key!, p.CompileCostMs ?? p.CompileEndedAtMs!.Value - p.CompileStartedAtMs!.Value))
            .Where(c => c.Ms >= MinCompileCostMs)
            .OrderByDescending(c => c.Ms)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(MaxCompileCosts)
            .ToList();

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
