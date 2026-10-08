namespace Sextant.Core;

/// <summary>
/// Terminal outcome of an indexing run or a single phase.
/// </summary>
public enum IndexRunStatus
{
    /// <summary>Initial, non-terminal state: the run has not yet reported a terminal outcome.</summary>
    Running,

    /// <summary>The run finished all phases successfully.</summary>
    Completed,

    /// <summary>The run was cooperatively cancelled before completing.</summary>
    Cancelled,

    /// <summary>The run stopped because of an unhandled error.</summary>
    Failed
}

/// <summary>
/// Timing and volume for one named indexing phase. Recorded even when the run is
/// later cancelled or fails, so partial progress is never lost.
/// </summary>
public sealed class PhaseMetric
{
    /// <summary>Phase identifier, matching the phase names emitted via <c>IndexingProgress</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Wall-clock duration of the phase in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Number of projects processed during the phase.</summary>
    public int ProjectsProcessed { get; set; }

    /// <summary>Outcome of the phase (Completed once the phase finishes; Running if interrupted mid-phase).</summary>
    public IndexRunStatus Status { get; set; } = IndexRunStatus.Running;

    /// <summary>
    /// CPU time this process spent during the phase, in milliseconds (all threads; issue #267). Divided by
    /// <see cref="DurationMs"/> it is the parallelism the phase achieved.
    /// </summary>
    public long CpuMs { get; set; }

    /// <summary>
    /// CPU time of child processes that exited during the phase (restore, MSBuild BuildHosts), in milliseconds, or null
    /// where the platform does not report it (issue #267). A child still running when the phase ends is not counted.
    /// </summary>
    public long? ChildCpuMs { get; set; }
}

/// <summary>
/// Where the time went for one project in one phase (issue #267). Compilation, analysis and persistence are measured
/// separately: in the occurrence phase they overlap across projects (a producer analyzes while a single consumer
/// persists), so their sum can exceed the phase's wall-clock time.
/// </summary>
public sealed class ProjectTiming
{
    /// <summary>The phase (a <see cref="PhaseMetric.Name"/>, or <c>load</c>).</summary>
    public required string Phase { get; init; }

    /// <summary>The project's display name (for <c>load</c>, the project file name).</summary>
    public required string Project { get; init; }

    /// <summary>Wall-clock time spent on this project in this phase, in milliseconds.</summary>
    public long WallMs { get; set; }

    /// <summary>Time to obtain the project's compilation, in milliseconds (0 when it was already built).</summary>
    public long CompileMs { get; set; }

    /// <summary>Time spent analyzing the project's documents, in milliseconds.</summary>
    public long AnalyzeMs { get; set; }

    /// <summary>Time spent writing the project's rows and committing its batch, in milliseconds.</summary>
    public long PersistMs { get; set; }

    /// <summary>Rows written for the project in this phase.</summary>
    public long Rows { get; set; }

    /// <summary>For <c>load</c>: projects added to the workspace by this open (the project plus references it pulled in).</summary>
    public int? ProjectsAdded { get; set; }
}

/// <summary>
/// Row-level volume and duplication metrics gathered from the index after a run.
/// </summary>
public sealed class RowCountMetrics
{
    public long Projects { get; set; }
    public long Symbols { get; set; }
    public long References { get; set; }

    /// <summary>
    /// Count of semantically-distinct reference occurrences. The occurrence key is
    /// <c>(symbol_id, file_path, line, reference_kind)</c>.
    /// </summary>
    public long DistinctReferenceOccurrences { get; set; }

    /// <summary>Reference rows beyond the first per distinct occurrence key (<see cref="References"/> − <see cref="DistinctReferenceOccurrences"/>).</summary>
    public long DuplicateReferenceRows { get; set; }

    public long Relationships { get; set; }
    public long CallGraphEdges { get; set; }
    public long Comments { get; set; }

    /// <summary>Fraction of reference rows that are duplicate occurrences (0 when there are no references).</summary>
    public double DuplicateReferenceRatio =>
        References == 0 ? 0 : (double)DuplicateReferenceRows / References;
}

/// <summary>
/// On-disk storage metrics. <see cref="FinalDbBytes"/> is measured after a WAL
/// checkpoint; <see cref="PeakDbPlusWalBytes"/> captures the transient peak of the
/// main database plus its write-ahead log sampled during the run.
/// </summary>
public sealed class StorageMetrics
{
    /// <summary>Size of the main database file after checkpoint, in bytes.</summary>
    public long FinalDbBytes { get; set; }

    /// <summary>Size of the write-ahead log after the run (before checkpoint truncation), in bytes.</summary>
    public long FinalWalBytes { get; set; }

    /// <summary>Size of the shared-memory (-shm) index file after the run, in bytes.</summary>
    public long FinalShmBytes { get; set; }

    /// <summary>Peak observed size of main database plus WAL during the run, in bytes.</summary>
    public long PeakDbPlusWalBytes { get; set; }

    /// <summary>Peak observed size of the write-ahead log during the run, in bytes.</summary>
    public long PeakWalBytes { get; set; }

    /// <summary>Peak observed size of the shared-memory (-shm) index file during the run, in bytes.</summary>
    public long PeakShmBytes { get; set; }

    /// <summary>
    /// Peak observed size of staged (not-yet-published) index artifacts during the run, in bytes.
    /// With the in-file generation model this is the transient main-DB + WAL + SHM footprint while a
    /// staging generation is being built.
    /// </summary>
    public long PeakStagedArtifactBytes { get; set; }
}

/// <summary>
/// Peak process memory observed during a run.
/// </summary>
public sealed class MemoryMetrics
{
    /// <summary>Peak managed heap size observed during the run, in bytes.</summary>
    public long PeakManagedBytes { get; set; }

    /// <summary>Peak process working set observed during the run, in bytes.</summary>
    public long PeakWorkingSetBytes { get; set; }
}

/// <summary>
/// Aggregate metrics for a single full or incremental indexing run. The collector is
/// populated in place as the run progresses, so a caller that catches a cancellation
/// or failure still holds every phase result recorded up to that point.
/// </summary>
public sealed class IndexingMetrics
{
    /// <summary>"full" or "incremental".</summary>
    public required string Mode { get; init; }

    /// <summary>Overall run outcome. Starts as Running until a terminal state is recorded.</summary>
    public IndexRunStatus Status { get; set; } = IndexRunStatus.Running;

    /// <summary>Populated when <see cref="Status"/> is Cancelled or Failed.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Time spent loading the Roslyn solution, in milliseconds.</summary>
    public long SolutionLoadMs { get; set; }

    /// <summary>Total wall-clock time for the run (excludes solution load), in milliseconds.</summary>
    public long TotalDurationMs { get; set; }

    /// <summary>
    /// Whether the indexer counts rows across the whole catalog after a successful run (<see cref="Rows"/>). A full
    /// scan of every reference row, so the service, which needs only timings, turns it off (issue #267).
    /// </summary>
    public bool CollectRowCounts { get; init; } = true;

    /// <summary>Number of projects in the loaded solution.</summary>
    public int ProjectCount { get; set; }

    /// <summary>For incremental runs, the number of changed files processed.</summary>
    public int ChangedFileCount { get; set; }

    /// <summary>Per-phase timing, in execution order.</summary>
    public List<PhaseMetric> Phases { get; } = [];

    private readonly List<ProjectTiming> _projects = [];

    /// <summary>Per-project timings (issue #267), in the order they were recorded. Safe to read after the run.</summary>
    public IReadOnlyList<ProjectTiming> Projects
    {
        get { lock (_projects) return _projects.ToList(); }
    }

    /// <summary>
    /// Records one project's timing. Thread-safe: the occurrence phase's producer and consumer record concurrently.
    /// </summary>
    public void RecordProject(ProjectTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        lock (_projects) _projects.Add(timing);
    }

    /// <summary>Replaces every recorded project timing (used to redact project names from a report).</summary>
    public void ReplaceProjects(IEnumerable<ProjectTiming> timings)
    {
        ArgumentNullException.ThrowIfNull(timings);
        var copy = timings.ToList();
        lock (_projects)
        {
            _projects.Clear();
            _projects.AddRange(copy);
        }
    }

    public RowCountMetrics Rows { get; set; } = new();
    public StorageMetrics Storage { get; set; } = new();
    public MemoryMetrics Memory { get; set; } = new();

    /// <summary>Workspace diagnostic messages captured while loading the solution.</summary>
    public List<string> WorkspaceDiagnostics { get; } = [];

    /// <summary>Number of workspace diagnostics captured (may exceed the retained sample).</summary>
    public int WorkspaceDiagnosticCount { get; set; }
}
