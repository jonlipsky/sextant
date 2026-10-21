using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store;

/// <summary>One generation (an <c>index_runs</c> row) as classified by a retention pass.</summary>
public sealed record RetentionGeneration
{
    public required long Id { get; init; }
    public required string Status { get; init; }
    public string? Profile { get; init; }
    public long? CompletedAt { get; init; }

    /// <summary>Why this generation was protected/retained/deleted (human-readable).</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// The outcome of a retention pass (Phase 8, criteria 4 &amp; 5) — identical shape whether planned
/// (dry-run) or executed. Reports which generations were protected (never eligible), retained (kept
/// within the keep window), and deleted (or would be), plus the API-history and source-blob rows
/// reclaimed. Only execution measures reusable page bytes; planning never simulates writes.
/// </summary>
public sealed record RetentionReport
{
    public required bool DryRun { get; init; }
    public IReadOnlyList<RetentionGeneration> Protected { get; init; } = [];
    public IReadOnlyList<RetentionGeneration> Retained { get; init; } = [];
    public IReadOnlyList<RetentionGeneration> Deleted { get; init; } = [];
    public int ApiSnapshotsDeleted { get; init; }
    public int FileVersionsDeleted { get; init; }

    /// <summary>Immutable snapshot rows reclaimed by snapshot-data GC (issue #46), with their catalog metadata.</summary>
    public int SnapshotsDeleted { get; init; }

    /// <summary>Project-version rows (and their cascaded symbols/occurrences/comments/api rows) reclaimed by snapshot-data GC (issue #46).</summary>
    public int SnapshotProjectVersionsDeleted { get; init; }

    /// <summary>
    /// Snapshots the pass computed as collectable but did not delete because the in-batch re-check found them
    /// protected by then (issue #303). The next pass recomputes them.
    /// </summary>
    public int SnapshotsSkipped { get; init; }

    /// <summary>
    /// Snapshots whose deletion unit ran past <c>UnitMaxSeconds</c> and was rolled back whole (issue #303); the pass
    /// went on with the next one and a later pass retries it.
    /// </summary>
    public IReadOnlyList<long> OversizedSnapshotIds { get; init; } = [];

    public int Oversized => OversizedSnapshotIds.Count;

    /// <summary>
    /// Snapshots the service left out of this pass's plan because an earlier pass rolled their unit back as oversized
    /// (issue #303); they stay out for the rest of the UTC day. Reported as <c>oversized_deferred</c>.
    /// </summary>
    public IReadOnlyList<long> OversizedDeferredSnapshotIds { get; init; } = [];

    public int OversizedDeferred => OversizedDeferredSnapshotIds.Count;

    /// <summary>
    /// Child rows (occurrences, symbols, ...) removed by bounded project-version delete chunks (issue #252), whether or
    /// not their project row is gone yet. A chunk that did not finish its project still counts as progress.
    /// </summary>
    public long ProjectChunkRowsDeleted { get; init; }

    /// <summary>Pages an incremental vacuum handed back to the file system at the end of an executed pass (issue #252).</summary>
    public long PagesVacuumed { get; init; }

    /// <summary>The pass stopped vacuuming with free pages still left (issue #329); vacuum is then progress that continues.</summary>
    public bool VacuumLeft { get; init; }

    public long ReclaimedBytes { get; init; }
    /// <summary>Space estimates are unavailable on a read-only plan; execution measures reusable pages, not disk shrinkage.</summary>
    public bool ReclaimedBytesKnown { get; init; }
    public bool MoreRemaining { get; init; }
    public string? StopReason { get; init; }

    /// <summary>
    /// The source-text sweep moved on the backlog this pass: it deleted a blob, advanced its cursor or finished a cycle
    /// (issue #336). False on a dry run and for a slice that spent its budget before handling a blob.
    /// </summary>
    public bool SourceTextSweepAdvanced { get; init; }

    /// <summary>The source-text walk reached the end of the store this pass: one full cycle is done (issue #336).</summary>
    public bool SourceTextCycleCompleted { get; init; }

    /// <summary>
    /// The file-version sweep moved this pass: its cursor advanced or it finished a cycle (issue #336). Like
    /// <see cref="SourceTextSweepAdvanced"/>, it is what shows blob backlog progress, instead of the deletion counts.
    /// </summary>
    public bool FileSweepAdvanced { get; init; }

    /// <summary>
    /// Tail steps an executed pass stepped over because one batch could not finish them even cut down (issue #329):
    /// an API commit for the rest of the UTC day, a file-version span until the sweep wraps; one line each.
    /// </summary>
    public IReadOnlyList<string> TailSkipped { get; init; } = [];

    /// <summary>
    /// Which steps an executed service pass left work in (issue #329), e.g. <c>snapshots=0 api=0 files=cursor@120</c>;
    /// null on a dry run and below the service.
    /// </summary>
    public string? LeftWork { get; init; }

    /// <summary>Write batches an executed pass ran (issue #303).</summary>
    public int Batches { get; init; }

    /// <summary>The longest time one batch held the writer, in milliseconds (issue #303). Zero on a dry run.</summary>
    public double MaxBatchWriterHoldMs { get; init; }

    /// <summary>
    /// Stored source-text blobs (issue #244) the pass deleted because no file version references them any more.
    /// Null on a dry run and where no source-text store is kept.
    /// </summary>
    public int? SourceTextsDeleted { get; init; }

    /// <summary>Where an executed service pass spent its time (issue #321). Null on a dry run and below the service.</summary>
    public RetentionPassTimings? Timings { get; init; }

    /// <summary>True when the closing audit row of an executed service pass was skipped (the writer stayed busy past its cap).</summary>
    public bool AuditSkipped { get; init; }
}

/// <summary>
/// Step timings of one executed service retention pass (issue #321), so an overrun is diagnosable from one log line:
/// planning, queueing for the writer, deleting under it, the source-text sweep (tail), vacuum, and the closing audit row.
/// </summary>
public sealed record RetentionPassTimings
{
    public double PlanMs { get; init; }
    public double WriterWaitMs { get; init; }
    public double DeleteMs { get; init; }
    public double TailMs { get; init; }
    public double VacuumMs { get; init; }
    public double AuditMs { get; init; }
    public int Batches { get; init; }

    public string ToLogText() => FormattableString.Invariant(
        $"plan {PlanMs:F0} ms, writer wait {WriterWaitMs:F0} ms, delete {DeleteMs:F0} ms, tail {TailMs:F0} ms, vacuum {VacuumMs:F0} ms, audit {AuditMs:F0} ms, {Batches} batch(es)");
}

/// <summary>
/// What an executed pass computed on a read connection (issue #303): the generation classification and the
/// candidates the write batches then delete. A candidate is only a candidate: every batch re-verifies it under the
/// writer, so anything that became protected since the plan was computed is skipped.
/// </summary>
public sealed class RetentionPlan
{
    private readonly List<long> _snapshots;
    private readonly List<RetentionGeneration> _runs;
    private readonly List<string> _commits;
    private int _snapshotCursor;
    private int _runCursor;
    private int _commitCursor;
    private readonly FileSweepCursor _fileSweep;
    // This plan's own sweep position, seeded from the shared cursor; the shared one moves only when a batch commits.
    private long _filePosition;
    // The shared position as this plan last saw it: a finished sweep rewinds it only while it still holds that value.
    private long _fileSeen;
    private bool _filePublishPending;
    private int _sweepSpansLeft;
    private bool _fileVersionsDone;
    private readonly bool _sweepActive;
    private readonly long _fileStart;
    private bool _apiStale;
    private bool _apiTurn = true;

    internal RetentionPlan(RetentionReport report, List<long> snapshots, List<RetentionGeneration> runs,
        List<string> commits, bool apiDeferred, string? stopReason = null, HashSet<long>? deletableRunIds = null,
        Dictionary<long, long?>? planRunIds = null, ApiHistoryPlan? apiHistory = null, bool pruneFileVersions = false,
        FileSweepCursor? fileSweep = null, int sweepSpans = RetentionService.MaxSweepSpansPerPass)
    {
        _fileSweep = fileSweep ?? new FileSweepCursor();
        _filePosition = _fileSeen = _fileStart = _fileSweep.Position;
        _sweepActive = pruneFileVersions;
        _sweepSpansLeft = sweepSpans;
        ApiHistory = apiHistory ?? new ApiHistoryPlan([], []);
        _fileVersionsDone = !pruneFileVersions;
        PlanRunIds = planRunIds ?? [];
        DeletableRunIds = deletableRunIds ?? [];
        Report = report;
        _snapshots = snapshots;
        SnapshotSet = snapshots.ToHashSet();
        _runs = runs;
        _commits = commits;
        _apiDeferred = apiDeferred;
        StopReason = stopReason;
    }

    /// <summary>The classification (protected/retained counts) with nothing deleted yet.</summary>
    public RetentionReport Report { get; }

    /// <summary>Set when the plan could not be computed within its budget; there is nothing to execute.</summary>
    public string? StopReason { get; }

    /// <summary>The API-history candidates could not be computed in time; the next pass does them.</summary>
    public bool ApiDeferred => _apiDeferred || _apiStale;
    private readonly bool _apiDeferred;

    internal HashSet<long> SnapshotSet { get; }

    /// <summary>The generation each candidate was on when planned; a candidate that moved is no longer one.</summary>
    internal Dictionary<long, long?> PlanRunIds { get; }

    /// <summary>Generations the plan may delete; a candidate on any other generation is a quota eviction.</summary>
    internal HashSet<long> DeletableRunIds { get; }
    public int SnapshotCandidates => _snapshots.Count;

    /// <summary>Drops candidates from the work list (before any batch ran); returns those that were in it.</summary>
    public IReadOnlyList<long> ExcludeSnapshots(IReadOnlyCollection<long> ids)
    {
        var excluded = _snapshots.Where(ids.Contains).ToList();
        if (excluded.Count > 0) _snapshots.RemoveAll(ids.Contains);
        return excluded;
    }

    internal long? PeekSnapshot() => _snapshotCursor < _snapshots.Count ? _snapshots[_snapshotCursor] : null;
    internal void AdvanceSnapshot() => _snapshotCursor++;
    internal RetentionGeneration? PeekRun() => _runCursor < _runs.Count ? _runs[_runCursor] : null;
    internal void AdvanceRun() => _runCursor++;
    internal string? PeekCommit() => _commitCursor < _commits.Count ? _commits[_commitCursor] : null;
    internal void AdvanceCommit() => _commitCursor++;
    /// <summary>What each step still has queued, for the pass log line (issue #329).</summary>
    public string DescribeLeft() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"snapshots={_snapshots.Count - _snapshotCursor} runs={_runs.Count - _runCursor} " +
        $"api={(_apiStale ? "stale" : _commits.Count - _commitCursor)} files={(_fileVersionsDone ? "0" : $"cursor@{_filePosition}")}");

    internal bool HasWork => PeekSnapshot() != null || PeekRun() != null || (PeekCommit() != null && !_apiStale) || SweepPending;

    /// <summary>
    /// The API-history keep window and the planned newest capture of each candidate commit, so a batch can re-verify a
    /// candidate with index seeks instead of regrouping the whole API table (issue #317).
    /// </summary>
    internal ApiHistoryPlan ApiHistory { get; }

    /// <summary>The last <c>file_versions</c> id the orphan sweep has examined; the sweep resumes after it.</summary>
    internal long FileVersionCursor => _filePosition;
    internal bool FileVersionsDone => _fileVersionsDone;

    /// <summary>This plan runs the file-version sweep at all (false when the policy or the caller turned it off).</summary>
    public bool FileSweepActive => _sweepActive;

    /// <summary>The sweep finished its cycle (the cursor wrapped) while this plan ran it.</summary>
    public bool FileSweepCompleted => _sweepActive && _fileVersionsDone;

    /// <summary>The sweep's cursor moved or its cycle finished during this plan (issue #336).</summary>
    public bool FileSweepAdvanced => _sweepActive && (_fileVersionsDone || _filePosition != _fileStart);

    /// <summary>The sweep is on and has ids left, whether or not this pass's span allowance is used up.</summary>
    public bool FileSweepOpen => _sweepActive && !_fileVersionsDone;

    /// <summary>The shared cursor, which also holds how far a tail step has been cut down today.</summary>
    internal FileSweepCursor Tuning => _fileSweep;

    // A span that cannot fit in one batch even at its smallest is stepped over up to its last existing id (so it is
    // examined again when the sweep wraps); the position is published at once because the batch that skipped it rolled back.
    internal void SkipFileSpan(long lastIdInSpan)
    {
        _filePosition = Math.Max(_filePosition, lastIdInSpan);
        _sweepSpansLeft--;
        _filePublishPending = true;
        PublishFileSweep();
    }

    /// <summary>The sweep has ids left and this pass's span allowance is not used up.</summary>
    internal bool SweepPending => !_fileVersionsDone && _sweepSpansLeft > 0;

    // Moves only this plan's position; PublishFileSweep shares it once the batch has committed.
    internal void AdvanceFileVersions(long lastExamined, bool done)
    {
        _filePosition = done ? 0 : lastExamined;
        _fileVersionsDone = done;
        _sweepSpansLeft--;
        _filePublishPending = true;
    }

    // Called after a batch commits. Progress only moves the shared cursor forward; a finished sweep rewinds it only if
    // no other pass moved it since this plan last saw it, so one pass finishing never resets another's progress.
    internal void PublishFileSweep()
    {
        if (!_filePublishPending) return;
        _filePublishPending = false;
        _fileSeen = _fileVersionsDone ? (_fileSweep.TryRewind(_fileSeen) ? 0 : _fileSeen) : _fileSweep.Advance(_filePosition);
    }

    /// <summary>
    /// A planned API candidate could not be re-verified with index seeks; the rest of the API history waits for the
    /// next plan instead of running the full regroup inside a batch.
    /// </summary>
    internal bool ApiStale => _apiStale;
    internal void MarkApiStale() => _apiStale = true;

    /// <summary>Whose turn the next tail batch is: API history (true) or the blob sweep (false).</summary>
    internal bool ApiTurn { get => _apiTurn; set => _apiTurn = value; }

    // Restore never touches the shared cursor: a rolled-back batch published nothing.
    internal PlanMark Mark() => new(_snapshotCursor, _runCursor, _commitCursor, _filePosition, _fileVersionsDone, _apiStale, _apiTurn, _sweepSpansLeft, _filePublishPending);
    internal void Restore(PlanMark mark) =>
        (_snapshotCursor, _runCursor, _commitCursor, _filePosition, _fileVersionsDone, _apiStale, _apiTurn, _sweepSpansLeft, _filePublishPending) =
        (mark.Snapshot, mark.Run, mark.Commit, mark.FileCursor, mark.FileDone, mark.ApiStale, mark.ApiTurn, mark.SweepSpansLeft, mark.FilePublishPending);
}

internal readonly record struct PlanMark(int Snapshot, int Run, int Commit, long FileCursor, bool FileDone, bool ApiStale, bool ApiTurn,
    int SweepSpansLeft, bool FilePublishPending);

/// <summary>
/// Where the orphan-blob sweep resumes: the last <c>file_versions</c> id it examined. The service keeps one for its
/// lifetime so each pass continues where the last one stopped (issue #317); a restart begins again at 0. Plans seed a
/// private position from it and publish back through <see cref="Advance"/>/<see cref="TryRewind"/> only on commit.
/// </summary>
public sealed class FileSweepCursor
{
    private readonly object _gate = new();
    private long _position;

    internal long Position
    {
        get { lock (_gate) return _position; }
    }

    /// <summary>Moves the position forward (never back); returns the position now held.</summary>
    internal long Advance(long to)
    {
        lock (_gate)
        {
            if (to > _position) _position = to;
            return _position;
        }
    }

    /// <summary>Starts a cycle over (the service does it when a scheduled retention day begins).</summary>
    public void Reset()
    {
        lock (_gate) _position = 0;
    }

    /// <summary>Rewinds to 0 only if the position is still <paramref name="expected"/>.</summary>
    internal bool TryRewind(long expected)
    {
        lock (_gate)
        {
            if (_position != expected) return false;
            _position = 0;
            return true;
        }
    }

    // What a tail step that could not finish inside one batch has been cut down to (issue #329). Shared like the
    // position, so the next batch and the next pass of the UTC day start from the smaller unit; ResetTuning ends the day.
    internal const int MinSweepSpan = 64;
    internal const int MinApiDeleteRows = 8;
    private int _sweepSpan = RetentionService.FileSweepSpan;
    private int _apiDeleteRows = RetentionService.BatchRowLimit;
    private readonly HashSet<string> _skippedCommits = [];

    internal int SweepSpan
    {
        get { lock (_gate) return _sweepSpan; }
    }

    internal int ApiDeleteRows
    {
        get { lock (_gate) return _apiDeleteRows; }
    }

    /// <summary>Halves the sweep span; false when it is already as small as it goes.</summary>
    internal bool TryShrinkSweepSpan()
    {
        lock (_gate)
        {
            if (_sweepSpan <= MinSweepSpan) return false;
            _sweepSpan = Math.Max(MinSweepSpan, _sweepSpan / 2);
            _cleanTailBatches = 0;
            return true;
        }
    }

    /// <summary>Halves the rows one API delete removes; false when it is already as small as it goes.</summary>
    internal bool TryShrinkApiDeleteRows()
    {
        lock (_gate)
        {
            if (_apiDeleteRows <= MinApiDeleteRows) return false;
            _apiDeleteRows = Math.Max(MinApiDeleteRows, _apiDeleteRows / 2);
            _cleanTailBatches = 0;
            return true;
        }
    }

    private const int CleanBatchesToGrow = 8;
    private int _cleanTailBatches;

    /// <summary>A tail batch that ran a step and committed; after enough of them a cut-down unit doubles back toward its default.</summary>
    internal void NoteCleanTailBatch()
    {
        lock (_gate)
        {
            if (_sweepSpan >= RetentionService.FileSweepSpan && _apiDeleteRows >= RetentionService.BatchRowLimit)
            {
                _cleanTailBatches = 0;
                return;
            }
            if (++_cleanTailBatches < CleanBatchesToGrow) return;
            _cleanTailBatches = 0;
            _sweepSpan = Math.Min(RetentionService.FileSweepSpan, _sweepSpan * 2);
            _apiDeleteRows = Math.Min(RetentionService.BatchRowLimit, _apiDeleteRows * 2);
        }
    }

    internal void SkipCommit(string commit)
    {
        lock (_gate) _skippedCommits.Add(commit);
    }

    internal bool IsCommitSkipped(string commit)
    {
        lock (_gate) return _skippedCommits.Contains(commit);
    }

    /// <summary>Forgets what a tail step was cut down to (the service calls it when the UTC day rolls over).</summary>
    public void ResetTuning()
    {
        lock (_gate)
        {
            _sweepSpan = RetentionService.FileSweepSpan;
            _apiDeleteRows = RetentionService.BatchRowLimit;
            _skippedCommits.Clear();
            _cleanTailBatches = 0;
        }
    }
}

/// <summary>
/// What a plan knows about the API history: the newest-first keep window (commit, newest capture) and the newest
/// capture each deletable commit had when planned.
/// </summary>
internal sealed record ApiHistoryPlan(List<(string Commit, long Newest)> Window, Dictionary<string, long> Candidates);

/// <summary>
/// Applies retention limits (<see cref="RetentionPolicy"/>) to a single index database while honoring a
/// protected set assembled from <see cref="IRetentionProtectionProvider"/>s (Phase 8, criteria 4 &amp; 5).
/// It garbage-collects superseded local generations (the <c>index_runs</c> ledger), trims API-surface
/// history beyond the keep window (<c>api_surface_snapshots</c>), and prunes source blobs no longer
/// referenced by any live symbol/occurrence/comment (<c>file_versions</c>).
///
/// Two invariants are absolute: the currently-servable last-complete generation is NEVER deleted (a
/// hard guard independent of the provider set, so retention can never trip the Phase-7 rebuild gate),
/// and anything a provider protects — branch/PR snapshots, submodule pins, active overlays once those
/// land — is spared even if it falls outside a keep window.
///
/// A pass is two-phase (issue #303). The plan (<see cref="CreatePlan"/>) classifies everything with set-based SQL on
/// any connection (the service uses an independent reader, never holding the writer). The batches
/// (<see cref="ExecuteBatch(RetentionPlan, CancellationToken, TimeSpan?, Action?)"/>) then delete a few candidates each under
/// <c>BEGIN IMMEDIATE</c>, re-verifying with cheap direct checks that each is still unprotected before touching it.
/// </summary>
public sealed class RetentionService
{
    private readonly SqliteConnection _connection;

    /// <summary>Records each batch's write-lock wait and hold (issue #342); unset leaves them untracked.</summary>
    public WriteLockTracker? WriteLockTracker { get; init; }

    private WriteLockTracker Tracker => WriteLockTracker ?? Sextant.Store.WriteLockTracker.Untracked;
    private readonly RetentionPolicy _policy;
    private readonly IReadOnlyList<IRetentionProtectionProvider> _providers;
    private readonly FileSweepCursor? _fileSweep;
    // False when the caller already ran this sweep's one cycle for the day (issue #336): the plan carries no file work.
    private readonly bool _sweepFileVersions;
    private RetentionWorkBudget? _budget;

    /// <summary>True while a batch is running that deleted no snapshot data (ledger/API/blob work only).</summary>
    public bool TailBatchInProgress { get; private set; }

    /// <summary>True when the running batch deleted a project row or a snapshot (a chunk of rows alone is not).</summary>
    public bool ProjectOrSnapshotDeletedInBatch { get; private set; }

    /// <summary>The default pass budget (an interactive run); the service passes its own.</summary>
    public static TimeSpan PassTimeLimit { get; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest one write batch may run, which bounds how long it holds the writer.</summary>
    public static TimeSpan BatchTimeLimit { get; } = TimeSpan.FromSeconds(2);
    public const int BatchRowLimit = 256;

    /// <summary>
    /// The rows one write batch deletes directly from a project version's child tables (issue #252): occurrences,
    /// symbols, relationships and so on, in chunks, with the project row last. Cascaded flow rows and file-version
    /// SET NULL updates ride on top of these.
    /// </summary>
    public const int ProjectChunkRows = 5000;

    /// <summary>Test seam: the chunk size handed to project-version deletes.</summary>
    internal int ProjectChunkRowLimit { get; set; } = ProjectChunkRows;

    /// <summary>The <c>file_versions</c> ids one batch of the orphan-blob sweep examines (issue #317).</summary>
    internal const int FileSweepSpan = 4096;

    /// <summary>
    /// The spans one plan's sweep examines (256 x 4096 = about a million ids). Each id costs three index seeks (a few
    /// microseconds), so a pass spends seconds of writer time on it rather than its whole budget; a larger table
    /// finishes over several passes because the shared cursor persists, and a pass that only swept stops without
    /// reporting <c>time_budget</c>.
    /// </summary>
    public const int MaxSweepSpansPerPass = 256;

    /// <summary>Test seam: the per-plan sweep allowance handed to plans this service creates.</summary>
    public int SweepSpansPerPass { get; set; } = MaxSweepSpansPerPass;

    /// <summary>Test seam: runs inside a batch's transaction right before a snapshot unit is deleted.</summary>
    internal Action<long>? BeforeUnitDelete { get; set; }

    public RetentionService(
        SqliteConnection connection,
        RetentionPolicy policy,
        IEnumerable<IRetentionProtectionProvider>? providers = null,
        FileSweepCursor? fileSweep = null,
        bool sweepFileVersions = true)
    {
        _sweepFileVersions = sweepFileVersions;
        _fileSweep = fileSweep;
        _connection = connection;
        _policy = policy.Normalized();
        _providers = (providers ?? DefaultProviders).ToList();
    }

    /// <summary>The providers active today. Phase 9/10/12 append branch/PR/overlay/pin providers here.</summary>
    public static IReadOnlyList<IRetentionProtectionProvider> DefaultProviders { get; } =
        [new LastCompleteRunProtection(), new BranchPointerProtection(), new OverlayBaseProtection(), new SubmoduleProviderProtection(), new PullRequestSnapshotProtection()];

    /// <summary>Reports what retention would do without modifying the database.</summary>
    public RetentionReport Plan(CancellationToken cancellationToken = default, TimeSpan? timeLimit = null)
    {
        using var budget = new RetentionWorkBudget(_connection, timeLimit ?? PassTimeLimit, cancellationToken);
        _budget = budget;
        try
        {
            budget.Check();
            Exec("BEGIN;");
            var (report, snapshots, _, protections) = ClassifyCore();
            var (commits, _) = DeletableApiCommits(protections);
            var projects = snapshots.Sum(id => CountRows(RetentionCount.ProjectVersions, id));
            var apiRows = commits.Sum(commit => CountRows(RetentionCount.ApiRows, commit));
            var files = _policy.PruneSupersededSourceBlobs ? OrphanFileVersionIds(protections).Count : 0;
            return report with
            {
                DryRun = true,
                SnapshotsDeleted = snapshots.Count,
                SnapshotProjectVersionsDeleted = projects,
                ApiSnapshotsDeleted = apiRows,
                FileVersionsDeleted = files
            };
        }
        catch (Exception ex) when (IsBudgetOrBusy(ex, budget))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new RetentionReport { DryRun = true, MoreRemaining = true, StopReason = StopReason(ex) };
        }
        finally
        {
            budget.Dispose();
            _budget = null;
            if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) Exec("ROLLBACK;");
        }
    }

    /// <summary>
    /// Computes the protected set and the candidates in one read transaction (no write). The generation/snapshot
    /// classification is bounded by <paramref name="timeLimit"/>; the API-history discovery gets at most half of what
    /// is left, and is deferred to the next pass when it does not fit, so a huge <c>api_surface_snapshots</c> table
    /// can never keep snapshot GC from progressing.
    /// </summary>
    public RetentionPlan CreatePlan(CancellationToken cancellationToken = default, TimeSpan? timeLimit = null)
    {
        var limit = timeLimit ?? PassTimeLimit;
        var started = Stopwatch.GetTimestamp();
        RetentionReport report;
        List<long> snapshots;
        HashSet<long> deletableRuns;
        Dictionary<long, long?> planRunIds;
        using (var budget = new RetentionWorkBudget(_connection, limit, cancellationToken))
        {
            _budget = budget;
            try
            {
                budget.Check();
                Exec("BEGIN;");
                (report, snapshots, deletableRuns, _) = ClassifyCore();
                planRunIds = RunIdsOf(snapshots);
            }
            catch (Exception ex) when (IsBudgetOrBusy(ex, budget))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new RetentionPlan(new RetentionReport { DryRun = false, MoreRemaining = true },
                    [], [], [], false, StopReason(ex));
            }
            finally
            {
                budget.Dispose();
                _budget = null;
                if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) Exec("ROLLBACK;");
            }
        }

        var runs = report.Deleted.ToList();
        var commits = new List<string>();
        var apiHistory = new ApiHistoryPlan([], []);
        var apiDeferred = false;
        var remaining = limit - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
            apiDeferred = true;
        else
        {
            using var budget = new RetentionWorkBudget(_connection, remaining / 2, cancellationToken);
            _budget = budget;
            try
            {
                budget.Check();
                Exec("BEGIN;");
                (commits, apiHistory) = DeletableApiCommits(BuildProtections());
            }
            catch (Exception ex) when (IsBudgetOrBusy(ex, budget))
            {
                cancellationToken.ThrowIfCancellationRequested();
                commits = [];
                apiDeferred = true;
            }
            finally
            {
                budget.Dispose();
                _budget = null;
                if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) Exec("ROLLBACK;");
            }
        }

        return new RetentionPlan(report with { DryRun = false, Deleted = [] }, snapshots, runs, commits, apiDeferred,
            deletableRunIds: deletableRuns, planRunIds: planRunIds, apiHistory: apiHistory,
            pruneFileVersions: _policy.PruneSupersededSourceBlobs && _sweepFileVersions, fileSweep: _fileSweep, sweepSpans: SweepSpansPerPass);
    }

    /// <summary>Applies bounded batches. Service callers acquire/release their writer gate around ExecuteBatch instead.</summary>
    public RetentionReport Execute(CancellationToken cancellationToken = default, TimeSpan? timeLimit = null)
    {
        var limit = timeLimit ?? PassTimeLimit;
        var started = Stopwatch.GetTimestamp();
        var plan = CreatePlan(cancellationToken, limit);
        if (plan.StopReason != null)
            return plan.Report with { StopReason = plan.StopReason };
        var report = plan.Report;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = limit - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return report with { MoreRemaining = true, StopReason = "time_budget" };
            var batch = ExecuteBatch(plan, cancellationToken, remaining < BatchTimeLimit ? remaining : BatchTimeLimit);
            report = Combine(report, batch);
            if (batch.StopReason != null) return report;
        } while (report.MoreRemaining);
        return plan.ApiDeferred ? report with { MoreRemaining = true, StopReason = "api_deferred" } : report;
    }

    /// <summary>One atomic batch computed from a fresh plan. Interrupted cascades roll back the entire batch.</summary>
    public RetentionReport ExecuteBatch(CancellationToken cancellationToken = default, TimeSpan? timeLimit = null,
        Action? beforeCommit = null)
    {
        var limit = timeLimit ?? BatchTimeLimit;
        var plan = CreatePlan(cancellationToken, limit);
        if (plan.StopReason != null)
            return plan.Report with { StopReason = plan.StopReason };
        return ExecuteBatch(plan, cancellationToken, limit, beforeCommit);
    }

    /// <summary>
    /// One atomic batch of <paramref name="plan"/>: under <c>BEGIN IMMEDIATE</c> it re-verifies each candidate with
    /// direct checks (still not pending, on the servable generation, pointed at by a branch or open pull request, and
    /// nothing outside the candidate set reads it through a base or provider edge), skips any that fail, and deletes one
    /// project cascade of the first that passes. Interrupted cascades roll back the whole batch and the plan's position.
    /// </summary>
    public RetentionReport ExecuteBatch(RetentionPlan plan, CancellationToken cancellationToken = default,
        TimeSpan? timeLimit = null, Action? beforeCommit = null, TimeSpan? passRemaining = null, TimeSpan? passLimit = null,
        bool enforceUnitStart = true)
    {
        var sinceCall = Stopwatch.StartNew();
        using var budget = new RetentionWorkBudget(_connection, timeLimit ?? BatchTimeLimit, cancellationToken);
        _budget = budget;
        var held = Stopwatch.StartNew();
        var mark = plan.Mark();
        string? tailStep = null;
        string? tailCommit = null;
        var tailRan = false;
        WriteLockHold? lockHold = null;
        try
        {
            budget.Check();
            var waitStarted = Tracker.BeginAcquire();
            try
            {
                Exec("BEGIN IMMEDIATE;");
            }
            catch
            {
                Tracker.AcquireFailed("retention-batch", null, waitStarted);
                throw;
            }
            lockHold = Tracker.Acquired("retention-batch", null, waitStarted);
            held.Restart();
            var protections = BuildProtections();
            var runStore = new IndexRunStore(_connection);
            var servableId = runStore.GetLastCompleteRun()?.Id;
            var openPullRequests = TableExists("pull_request_snapshots");
            var pageSize = ScalarLong("PRAGMA page_size;");
            var freeBefore = ScalarLong("PRAGMA freelist_count;");
            var projectsDeleted = 0;
            long chunkRowsDeleted = 0;
            var snapshotsDeleted = 0;
            var skipped = 0;

            // One project cascade at a time, never a whole multi-project snapshot in one transaction.
            while (plan.PeekSnapshot() is long id)
            {
                budget.Check();
                var verdict = VerifySnapshotDeletable(id, plan, servableId, openPullRequests, protections, out var affected);
                if (verdict != SnapshotVerdict.Deletable)
                {
                    if (verdict == SnapshotVerdict.Protected) skipped++;
                    plan.AdvanceSnapshot();
                    continue;
                }
                // A unit that could outlast the pass is not started (issue #321): it would be cut at the pass limit and
                // rolled back whole. Its cap is the unit cap, or half the pass when the pass is shorter than that.
                // The caller turns the rule off until the pass has made progress, so a pass whose planning ate most of
                // its budget still starts its first unit instead of starving every pass (issue #321).
                if (enforceUnitStart && passRemaining is { } left && passLimit is { } total)
                {
                    var need = TimeSpan.FromSeconds(_policy.UnitMaxSeconds);
                    if (total / 2 < need) need = total / 2;
                    if (left - sinceCall.Elapsed < need)
                        throw new UnitStartBudgetException();
                }
                // One unit always gets to finish, up to its own hard cap; a unit over the cap rolls back whole.
                var unitMark = plan.Mark();
                budget.BeginUnit(TimeSpan.FromSeconds(_policy.UnitMaxSeconds));
                try
                {
                    BeforeUnitDelete?.Invoke(id);
                    WithdrawAffectedPublication(affected);
                    projectsDeleted = DeleteProjectChunk(id, out var chunkRows) ? 1 : 0;
                    chunkRowsDeleted += chunkRows;
                    if (CountRows(RetentionCount.ProjectVersions, id) == 0)
                    {
                        snapshotsDeleted = DeleteEmptySnapshot(id);
                        plan.AdvanceSnapshot();
                    }
                    budget.Check();
                }
                catch (Exception ex) when (budget.UnitExpired && (ex is TimeoutException || ex is SqliteException { SqliteErrorCode: 9 }))
                {
                    if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) Exec("ROLLBACK;");
                    held.Stop();
                    plan.Restore(unitMark);
                    plan.AdvanceSnapshot();
                    return plan.Report with
                    {
                        DryRun = false,
                        OversizedSnapshotIds = [id],
                        SnapshotsSkipped = skipped,
                        Batches = 1,
                        MaxBatchWriterHoldMs = held.Elapsed.TotalMilliseconds,
                        MoreRemaining = plan.HasWork
                    };
                }
                budget.EndUnit();
                break;
            }

            // Do not remove ledger rows ahead of snapshot GC: they go once no snapshot data was touched in this batch.
            var deletedRuns = new List<RetentionGeneration>();
            var tailBatch = plan.PeekSnapshot() is null && projectsDeleted == 0 && snapshotsDeleted == 0;
            TailBatchInProgress = tailBatch;
            ProjectOrSnapshotDeletedInBatch = projectsDeleted > 0 || snapshotsDeleted > 0;
            if (tailBatch)
            {
                while (plan.PeekRun() is { } run && deletedRuns.Count < BatchRowLimit)
                {
                    budget.Check();
                    plan.AdvanceRun();
                    if (!VerifyRunDeletable(run.Id, servableId, protections)) continue;
                    runStore.DeleteRun(run.Id);
                    deletedRuns.Add(run);
                }
            }

            // The tail (ledger rows, API history, orphan blobs) runs only in a batch that deleted no snapshot data
            // (issue #317): a slow tail step that hits the batch limit rolls the whole batch back, and must never
            // take a finished deletion unit with it. API history and the blob sweep take turns, one per tail batch,
            // so neither starves the other; a side with nothing left yields its turn.
            var apiDeleted = 0;
            var fileDeleted = 0;
            if (tailBatch)
            {
                var apiWork = plan.PeekCommit() is not null && !plan.ApiStale;
                var sweepWork = plan.SweepPending;
                if (apiWork && (plan.ApiTurn || !sweepWork))
                {
                    var commit = plan.PeekCommit()!;
                    budget.Check();
                    plan.ApiTurn = false;
                    if (protections.IsCommitProtected(commit) || plan.Tuning.IsCommitSkipped(commit))
                        plan.AdvanceCommit();
                    else if (!IsOutsideApiKeepWindow(commit, plan))
                        plan.MarkApiStale();
                    else
                    {
                        tailStep = "api";
                        tailCommit = commit;
                        BeforeTailStep?.Invoke(tailStep);
                        var rows = plan.Tuning.ApiDeleteRows;
                        apiDeleted = DeleteApiCommits([commit], rows);
                        tailStep = null;
                        tailRan = true;
                        if (apiDeleted < rows) plan.AdvanceCommit();
                    }
                }
                else if (sweepWork)
                {
                    plan.ApiTurn = true;
                    tailStep = "sweep";
                    BeforeTailStep?.Invoke(tailStep);
                    fileDeleted = PruneOrphanFileVersions(plan, protections);
                    tailStep = null;
                    tailRan = true;
                }
            }
            var reclaimed = Math.Max(0, ScalarLong("PRAGMA freelist_count;") - freeBefore) * pageSize;
            budget.Check();
            beforeCommit?.Invoke();
            budget.Check();
            Exec("COMMIT;");
            if (lockHold is { } committed)
            {
                lockHold = null;
                Tracker.Released(committed);
            }
            plan.PublishFileSweep();
            if (tailRan) plan.Tuning.NoteCleanTailBatch();
            held.Stop();
            return plan.Report with
            {
                DryRun = false,
                Deleted = deletedRuns,
                SnapshotsDeleted = snapshotsDeleted,
                SnapshotProjectVersionsDeleted = projectsDeleted,
                ProjectChunkRowsDeleted = chunkRowsDeleted,
                SnapshotsSkipped = skipped,
                ApiSnapshotsDeleted = apiDeleted,
                FileVersionsDeleted = fileDeleted,
                ReclaimedBytes = reclaimed,
                ReclaimedBytesKnown = true,
                Batches = 1,
                MaxBatchWriterHoldMs = held.Elapsed.TotalMilliseconds,
                MoreRemaining = plan.HasWork || apiDeleted > 0
            };
        }
        catch (Exception ex) when (tailStep != null && IsBudgetOrBusy(ex, budget) && ex is not UnitStartBudgetException &&
            !cancellationToken.IsCancellationRequested && (timeLimit ?? BatchTimeLimit) >= BatchTimeLimit &&
            ex is not SqliteException { SqliteErrorCode: 5 or 6 })
        {
            // A tail step that ran the batch's own limit rolled back with nothing done; retried as is it would do the
            // same forever (issue #329). Cut it down and retry; at the smallest size step over it for the UTC day.
            plan.Restore(mark);
            held.Stop();
            var skipped = ShrinkOrSkipTailStep(plan, tailStep, tailCommit);
            return plan.Report with
            {
                DryRun = false,
                Batches = 1,
                MaxBatchWriterHoldMs = held.Elapsed.TotalMilliseconds,
                MoreRemaining = true,
                TailSkipped = skipped is null ? [] : [skipped]
            };
        }
        catch (Exception ex) when (IsBudgetOrBusy(ex, budget))
        {
            plan.Restore(mark);
            cancellationToken.ThrowIfCancellationRequested();
            return plan.Report with { DryRun = false, MoreRemaining = true, StopReason = StopReason(ex) };
        }
        catch
        {
            plan.Restore(mark);
            throw;
        }
        finally
        {
            budget.Dispose();
            _budget = null;
            // SQLITE_INTERRUPT can roll back a write transaction itself.
            try
            {
                if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) Exec("ROLLBACK;");
            }
            finally
            {
                if (lockHold is { } open)
                    Tracker.Released(open);
            }
        }
    }

    // Halves the unit a tail step works on; at the smallest size it is stepped over for the rest of the UTC day.
    // Returns the one log line for a skip, null for a shrink.
    private string? ShrinkOrSkipTailStep(RetentionPlan plan, string step, string? commit)
    {
        if (step == "api" && commit != null)
        {
            if (plan.Tuning.TryShrinkApiDeleteRows()) return null;
            plan.Tuning.SkipCommit(commit);
            plan.AdvanceCommit();
            return $"Retention rolled back the API-history delete of commit {commit}: it ran past {BatchTimeLimit.TotalSeconds}s even at {FileSweepCursor.MinApiDeleteRows} rows. It is skipped for the rest of the UTC day.";
        }
        if (plan.Tuning.TryShrinkSweepSpan()) return null;
        var from = plan.FileVersionCursor;
        var to = LastIdOfSpan(from, FileSweepCursor.MinSweepSpan);
        if (to is null)
        {
            // Nothing is left after the cursor: the sweep is complete rather than stuck.
            plan.AdvanceFileVersions(from, true);
            plan.PublishFileSweep();
            return null;
        }
        plan.SkipFileSpan(to.Value);
        return $"Retention rolled back the file-version sweep: the {FileSweepCursor.MinSweepSpan} file_versions ids after id {from} (through id {to.Value}) ran past {BatchTimeLimit.TotalSeconds}s. They are skipped until the sweep wraps.";
    }

    // The id of the span-th existing file_versions row after the cursor (or the last one if fewer remain). The batch
    // has timed out, so its transaction is ended and the progress handler lifted before this read.
    private long? LastIdOfSpan(long after, int span)
    {
        if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) Exec("ROLLBACK;");
        SQLitePCL.raw.sqlite3_progress_handler(_connection.Handle, 0, null, null);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(id) FROM (SELECT id FROM file_versions WHERE id > @after ORDER BY id LIMIT @span);";
        cmd.Parameters.AddWithValue("@after", after);
        cmd.Parameters.AddWithValue("@span", span);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    /// <summary>Test seam: runs inside a batch's transaction right before an API-delete or file-sweep tail step.</summary>
    internal Action<string>? BeforeTailStep { get; set; }

    /// <summary>
    /// One slice of the source-text sweep. Run it only under the service writer gate, and build the key set inside that
    /// same writer turn: an index job stores a blob before its file_versions row commits, and only the gate keeps a job
    /// from landing a blob between the key set and the deletes (so a key set is never reused across turns). The key
    /// build gets its own budget (<paramref name="keyBuildLimit"/>, default <paramref name="timeLimit"/>) and the walk's
    /// <paramref name="timeLimit"/> starts after it, so building the keys never starves the walk (issue #336).
    /// With a <paramref name="cursor"/> successive slices resume where the last stopped; a walk that reaches the end of
    /// the store with no stop reason is a finished cycle: <c>MoreRemaining</c> is false.
    /// </summary>
    public RetentionReport SweepSourceTexts(SourceTextStore texts, CancellationToken cancellationToken,
        TimeSpan timeLimit, TimeSpan? keyBuildLimit = null, SourceTextSweepCursor? cursor = null)
    {
        var versionBefore = cursor?.Version ?? 0;
        var cyclesBefore = cursor?.Cycles ?? 0;
        RetentionWorkBudget? keyBudget = null;
        RetentionWorkBudget? budget = null;
        var buildingKeys = true;
        try
        {
            IReadOnlySet<UInt128> keys;
            keyBudget = new RetentionWorkBudget(_connection, keyBuildLimit ?? timeLimit, cancellationToken);
            keyBudget.Check();
            keys = new FileStore(_connection).ReferencedSourceTextKeys();
            keyBudget.Check();
            keyBudget.Dispose();
            keyBudget = null;
            buildingKeys = false;

            var walkBudget = budget = new RetentionWorkBudget(_connection, timeLimit, cancellationToken);
            var deleted = texts.DeleteUnreferenced(keys, BatchRowLimit, () => walkBudget.ShouldStop, out var reason, cursor);
            cancellationToken.ThrowIfCancellationRequested();
            // Every "more remaining" names why (issue #329), as the sweep itself saw it.
            return new RetentionReport
            {
                DryRun = false, SourceTextsDeleted = deleted, MoreRemaining = reason != null, StopReason = reason,
                SourceTextSweepAdvanced = deleted > 0 || cursor != null && cursor.Version != versionBefore,
                SourceTextCycleCompleted = cursor != null && cursor.Cycles != cyclesBefore
            };
        }
        catch (Exception ex) when (IsBudgetOrBusy(ex, keyBudget, budget))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The key build timing out is its own reason: it made no progress, but it is a budget stop (issue #336).
            var reason = buildingKeys && ex is not SqliteException { SqliteErrorCode: 5 or 6 }
                ? KeysBudgetStopReason : StopReason(ex);
            return new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = reason };
        }
        finally
        {
            keyBudget?.Dispose();
            budget?.Dispose();
        }
    }

    /// <summary>The source-text sweep ran out of its key-build budget before it could walk anything (issue #336).</summary>
    public const string KeysBudgetStopReason = "source_text_keys_budget";

    /// <summary>The floor of the key-build budget of one source-text slice, whatever the pass has left.</summary>
    public static TimeSpan SourceTextKeyBuildFloor => BatchTimeLimit;

    private static bool IsBudgetOrBusy(Exception ex, RetentionWorkBudget budget) =>
        IsBudgetOrBusy(ex, budget, null);

    // Code 9 (interrupt) is a budget stop only when a budget actually asked to stop; anything else propagates.
    private static bool IsBudgetOrBusy(Exception ex, RetentionWorkBudget? budget, RetentionWorkBudget? other) =>
        ex is TimeoutException || ex is SqliteException sql &&
        (sql.SqliteErrorCode is 5 or 6 ||
         sql.SqliteErrorCode == 9 && (budget?.ShouldStop == true || other?.ShouldStop == true));

    private sealed class UnitStartBudgetException() : TimeoutException("Not enough pass budget left to start a deletion unit.");

    private static string StopReason(Exception ex) =>
        ex is UnitStartBudgetException ? "unit_budget" :
        ex is SqliteException { SqliteErrorCode: 5 or 6 } ? "sqlite_busy" : "time_budget";

    public static RetentionReport Combine(RetentionReport total, RetentionReport batch) => batch with
    {
        Deleted = total.Deleted.Concat(batch.Deleted).ToList(),
        SnapshotsDeleted = total.SnapshotsDeleted + batch.SnapshotsDeleted,
        SnapshotProjectVersionsDeleted = total.SnapshotProjectVersionsDeleted + batch.SnapshotProjectVersionsDeleted,
        SnapshotsSkipped = total.SnapshotsSkipped + batch.SnapshotsSkipped,
        OversizedSnapshotIds = total.OversizedSnapshotIds.Concat(batch.OversizedSnapshotIds).ToList(),
        OversizedDeferredSnapshotIds = total.OversizedDeferredSnapshotIds.Concat(batch.OversizedDeferredSnapshotIds).ToList(),
        ApiSnapshotsDeleted = total.ApiSnapshotsDeleted + batch.ApiSnapshotsDeleted,
        FileVersionsDeleted = total.FileVersionsDeleted + batch.FileVersionsDeleted,
        ProjectChunkRowsDeleted = total.ProjectChunkRowsDeleted + batch.ProjectChunkRowsDeleted,
        PagesVacuumed = total.PagesVacuumed + batch.PagesVacuumed,
        TailSkipped = total.TailSkipped.Concat(batch.TailSkipped).ToList(),
        ReclaimedBytes = total.ReclaimedBytes + batch.ReclaimedBytes,
        ReclaimedBytesKnown = total.ReclaimedBytesKnown || batch.ReclaimedBytesKnown,
        Batches = total.Batches + batch.Batches,
        MaxBatchWriterHoldMs = Math.Max(total.MaxBatchWriterHoldMs, batch.MaxBatchWriterHoldMs)
    };

    /// <summary>The generation classification and the orphaned-snapshot candidates (everything but API history).</summary>
    private (RetentionReport report, List<long> snapshots, HashSet<long> deletableRuns, RetentionProtectionSet protections) ClassifyCore()
    {
        var runStore = new IndexRunStore(_connection);
        var servable = runStore.GetLastCompleteRun();
        var protections = BuildProtections();

        var allRuns = runStore.GetAllRuns();
        var protectedGenerations = new List<RetentionGeneration>();
        var retainedGenerations = new List<RetentionGeneration>();
        var deletableRunIds = new List<long>();
        var deletedGenerations = new List<RetentionGeneration>();

        // Rank complete runs newest-first; keep the first KeepCompleteGenerations, GC the rest.
        var completeRank = 0;
        foreach (var run in allRuns)
        {
            _budget?.Check();
            var isServable = servable != null && run.Id == servable.Id;

            // Hard guard + provider protections: never delete the servable generation or a protected one.
            if (isServable || protections.IsGenerationProtected(run.Id))
            {
                var reason = isServable
                    ? "currently-servable last-complete generation"
                    : protections.Generations.GetValueOrDefault(run.Id, "protected");
                protectedGenerations.Add(ToGeneration(run, reason));
                if (run.Status == IndexRunState.Complete) completeRank++;
                continue;
            }

            if (run.Status == IndexRunState.Complete)
            {
                completeRank++;
                if (completeRank <= _policy.KeepCompleteGenerations)
                {
                    retainedGenerations.Add(ToGeneration(run, $"within keep window (newest {_policy.KeepCompleteGenerations})"));
                }
                else
                {
                    deletableRunIds.Add(run.Id);
                    deletedGenerations.Add(ToGeneration(run, "superseded generation beyond keep window"));
                }
            }
            else if (run.Status == IndexRunState.Abandoned)
            {
                deletableRunIds.Add(run.Id);
                deletedGenerations.Add(ToGeneration(run, "abandoned generation"));
            }
            else
            {
                // Staging (in-progress) runs are left untouched — a live writer owns them.
                retainedGenerations.Add(ToGeneration(run, "in-progress generation"));
            }
        }

        // Snapshot-data GC (issue #46): reclaim the semantic rows owned by snapshots that no retained
        // generation, branch pointer, overlay base, or retained consumer still references (issue #54).
        var orphanedSnapshotIds = ComputeOrphanedSnapshotIds(deletableRunIds);
        // A retained provider/base may own an expired run. Keep that ledger row until its last
        // surviving snapshot is collected; otherwise ON DELETE SET NULL manufactures legacy-unknown
        // ownership and makes healthy data permanently protected on the following pass.
        var retainedRunIds = RunIdsOfSnapshotsOutside(orphanedSnapshotIds);
        foreach (var generation in deletedGenerations.Where(g => retainedRunIds.Contains(g.Id)))
            protectedGenerations.Add(generation with { Reason = "generation owns retained snapshot data" });
        deletedGenerations.RemoveAll(g => retainedRunIds.Contains(g.Id));

        return (new RetentionReport
        {
            DryRun = true,
            Protected = protectedGenerations,
            Retained = retainedGenerations,
            Deleted = deletedGenerations
        }, orphanedSnapshotIds, deletableRunIds.ToHashSet(), protections);
    }

    private Dictionary<long, long?> RunIdsOf(List<long> snapshots)
    {
        var wanted = snapshots.ToHashSet();
        var runIds = new Dictionary<long, long?>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, run_id FROM snapshots;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _budget?.Check();
            var id = reader.GetInt64(0);
            if (wanted.Contains(id)) runIds[id] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        return runIds;
    }

    private HashSet<long> RunIdsOfSnapshotsOutside(List<long> orphaned)
    {
        var excluded = orphaned.ToHashSet();
        var runIds = new HashSet<long>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, run_id FROM snapshots WHERE run_id IS NOT NULL;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _budget?.Check();
            if (!excluded.Contains(reader.GetInt64(0))) runIds.Add(reader.GetInt64(1));
        }
        return runIds;
    }

    private RetentionProtectionSet BuildProtections()
    {
        var builder = new RetentionProtectionBuilder();
        foreach (var provider in _providers)
            provider.Contribute(_connection, builder);
        return builder.Build();
    }

    private enum SnapshotVerdict { Deletable, Protected, Gone }

    /// <summary>
    /// The in-batch re-check (issue #303), by direct lookups only. The candidate and everything that reads it through
    /// a base or provider edge (transitively; they are withdrawn with it) must still be in the plan's candidate set
    /// and still be neither pending, nor (when its generation is being deleted) on the servable generation, nor pointed at by a branch or an
    /// open pull request. A snapshot created after the plan was computed is in no candidate set, so it blocks.
    /// </summary>
    private SnapshotVerdict VerifySnapshotDeletable(long id, RetentionPlan plan, long? servableId, bool openPullRequests,
        RetentionProtectionSet protections, out List<long> affected)
    {
        affected = [];
        if (!SnapshotExists(id)) return SnapshotVerdict.Gone;

        var visited = new HashSet<long>();
        var frontier = new Queue<long>();
        frontier.Enqueue(id);
        while (frontier.Count > 0)
        {
            _budget?.Check();
            var current = frontier.Dequeue();
            if (!visited.Add(current)) continue;
            if (!plan.SnapshotSet.Contains(current) || !IsUnprotectedNow(current, plan, servableId, openPullRequests, protections))
                return SnapshotVerdict.Protected;
            affected.Add(current);
            foreach (var consumer in ConsumersOf(current)) frontier.Enqueue(consumer);
        }
        return SnapshotVerdict.Deletable;
    }

    private bool SnapshotExists(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM snapshots WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private const string UnprotectedNowWithPullRequestsSql = """
        SELECT s.status, s.run_id,
               EXISTS (SELECT 1 FROM branches b WHERE b.snapshot_id = s.id),
               EXISTS (SELECT 1 FROM pull_request_snapshots p WHERE p.snapshot_id = s.id AND p.state = 'open'),
               (SELECT r.status FROM index_runs r WHERE r.id = s.run_id)
        FROM snapshots s WHERE s.id = @id;
        """;

    private const string UnprotectedNowSql = """
        SELECT s.status, s.run_id,
               EXISTS (SELECT 1 FROM branches b WHERE b.snapshot_id = s.id),
               0,
               (SELECT r.status FROM index_runs r WHERE r.id = s.run_id)
        FROM snapshots s WHERE s.id = @id;
        """;

    private const string HardClosureWithPullRequestsSql = """
        WITH RECURSIVE c(id) AS (
            SELECT id FROM snapshots WHERE status = @pending
            UNION SELECT snapshot_id FROM branches WHERE snapshot_id IS NOT NULL
            UNION SELECT snapshot_id FROM pull_request_snapshots WHERE state = 'open' AND snapshot_id IS NOT NULL
            UNION SELECT s.id FROM snapshots s LEFT JOIN index_runs r ON r.id = s.run_id
                  WHERE s.status IN (@complete, @superseded)
                    AND (s.run_id IS NULL OR r.id IS NULL OR r.status = @abandoned)
            UNION SELECT e.t FROM c JOIN ret_edges e ON e.f = c.id
        )
        INSERT OR IGNORE INTO ret_hard (id) SELECT id FROM c;
        """;

    private const string HardClosureSql = """
        WITH RECURSIVE c(id) AS (
            SELECT id FROM snapshots WHERE status = @pending
            UNION SELECT snapshot_id FROM branches WHERE snapshot_id IS NOT NULL
            UNION SELECT s.id FROM snapshots s LEFT JOIN index_runs r ON r.id = s.run_id
                  WHERE s.status IN (@complete, @superseded)
                    AND (s.run_id IS NULL OR r.id IS NULL OR r.status = @abandoned)
            UNION SELECT e.t FROM c JOIN ret_edges e ON e.f = c.id
        )
        INSERT OR IGNORE INTO ret_hard (id) SELECT id FROM c;
        """;

    private const string KeptClosureSql = """
        WITH RECURSIVE c(id) AS (
            SELECT id FROM ret_hard
            UNION SELECT id FROM snapshots WHERE run_id IS NOT NULL AND run_id NOT IN (SELECT id FROM ret_runs)
            UNION SELECT e.t FROM c JOIN ret_edges e ON e.f = c.id
        )
        INSERT OR IGNORE INTO ret_kept (id) SELECT id FROM c;
        """;

    private enum ClosureKind { HardWithPullRequests, Hard, Kept }

    private bool IsUnprotectedNow(long id, RetentionPlan plan, long? servableId, bool openPullRequests,
        RetentionProtectionSet protections)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = openPullRequests ? UnprotectedNowWithPullRequestsSql : UnprotectedNowSql;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return true;
        if (reader.GetString(0) == SnapshotStatus.Pending) return false;
        long? runId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        // A candidate that changed generation since planning is no longer the candidate that was classified.
        if (!plan.PlanRunIds.TryGetValue(id, out var plannedRun)) return false;
        if (runId is null)
        {
            // Only a tombstone this pass withdrew (failed, ownership cleared) may lose its run; anything else has uncertain ownership.
            if (plannedRun is not null && reader.GetString(0) is SnapshotStatus.Complete or SnapshotStatus.Superseded) return false;
        }
        else if (plannedRun != runId) return false;
        if (runId is long run)
        {
            if (!reader.IsDBNull(4) && reader.GetString(4) == IndexRunState.Staging) return false;
            // A quota eviction is on a retained generation by design; a candidate on a deleted generation is not.
            if (plan.DeletableRunIds.Contains(run) && (servableId == run || protections.IsGenerationProtected(run)))
                return false;
        }
        return reader.GetInt64(2) == 0 && reader.GetInt64(3) == 0;
    }

    private List<long> ConsumersOf(long snapshotId)
    {
        var consumers = new List<long>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM snapshots WHERE base_snapshot_id = @id;";
            cmd.Parameters.AddWithValue("@id", snapshotId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) consumers.Add(reader.GetInt64(0));
        }
        if (TableExists("snapshot_dependencies"))
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT consumer_snapshot_id FROM snapshot_dependencies WHERE provider_snapshot_id = @id;";
            cmd.Parameters.AddWithValue("@id", snapshotId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) consumers.Add(reader.GetInt64(0));
        }
        return consumers;
    }

    private bool VerifyRunDeletable(long runId, long? servableId, RetentionProtectionSet protections)
    {
        if (runId == servableId || protections.IsGenerationProtected(runId)) return false;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT status FROM index_runs WHERE id = @id),
                   EXISTS (SELECT 1 FROM snapshots WHERE run_id = @id);
            """;
        cmd.Parameters.AddWithValue("@id", runId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0)) return false;
        return reader.GetString(0) != IndexRunState.Staging && reader.GetInt64(1) == 0;
    }

    private bool TableExists(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// A provider cascade removes consumers' dependency edges AND target occurrences. Withdraw every affected
    /// consumer before yielding (the verified closure, so each is a candidate that stays unprotected).
    /// NULL ownership keeps these tombstones GC-eligible even inside a retained run/quota window.
    /// </summary>
    private void WithdrawAffectedPublication(List<long> affected)
    {
        using var invalidate = _connection.CreateCommand();
        invalidate.CommandText = "UPDATE snapshots SET status = @failed, run_id = NULL, published_at = NULL WHERE id = @id;";
        invalidate.Parameters.AddWithValue("@failed", SnapshotStatus.Failed);
        var id = invalidate.Parameters.Add("@id", SqliteType.Integer);
        foreach (var current in affected)
        {
            _budget?.Check();
            id.Value = current;
            invalidate.ExecuteNonQuery();
        }
    }

    private static RetentionGeneration ToGeneration(IndexRun run, string reason) => new()
    {
        Id = run.Id,
        Status = run.Status,
        Profile = run.IndexingProfile,
        CompletedAt = run.CompletedAt,
        Reason = reason
    };

    /// <summary>API-history commits outside the keep window and not protected, oldest windowing first.</summary>
    private (List<string> Deletable, ApiHistoryPlan History) DeletableApiCommits(RetentionProtectionSet protections)
    {
        var commits = new List<(string Commit, long Newest)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT git_commit, MAX(captured_at) AS m
                FROM api_surface_snapshots
                GROUP BY git_commit
                ORDER BY m DESC, git_commit DESC;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _budget?.Check();
                commits.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        var deletable = new List<string>();
        var history = new ApiHistoryPlan([], []);
        for (var i = 0; i < commits.Count; i++)
        {
            _budget?.Check();
            if (i < _policy.ApiSnapshotKeepCommits)
            {
                history.Window.Add(commits[i]);
                continue;
            }
            if (protections.IsCommitProtected(commits[i].Commit)) continue;
            deletable.Add(commits[i].Commit);
            history.Candidates[commits[i].Commit] = commits[i].Newest;
        }
        return (deletable, history);
    }

    private long? NewestApiCapture(string commit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(captured_at) FROM api_surface_snapshots WHERE git_commit = @c;";
        cmd.Parameters.AddWithValue("@c", commit);
        return cmd.ExecuteScalar() is long newest ? newest : null;
    }

    // Re-verifies a planned API candidate with index seeks (issue #317): regrouping the whole API table per batch took
    // 4-14 s on a 25 M-row catalog. A commit is outside the keep window when at least `keep` window commits currently
    // rank above it by (newest capture, commit), the order the window is cut in. Both sides are read now, so a partial
    // delete of the candidate (which only lowers its newest capture) and a window commit gaining captures keep the
    // check exact. A planned commit that cannot be shown outside this way is never settled by the full regroup inside a
    // batch: the caller marks the plan's API step stale and the next plan recomputes it.
    private bool IsOutsideApiKeepWindow(string commit, RetentionPlan plan)
    {
        var history = plan.ApiHistory;
        if (!history.Candidates.ContainsKey(commit) || history.Window.Count < _policy.ApiSnapshotKeepCommits
            || NewestApiCapture(commit) is not long candidate)
            return false;
        foreach (var (windowCommit, _) in history.Window)
        {
            _budget?.Check();
            if (NewestApiCapture(windowCommit) is not long now) return false;
            var order = now.CompareTo(candidate);
            if (order == 0) order = string.CompareOrdinal(windowCommit, commit);
            if (order <= 0) return false;
        }
        return true;
    }

    private int DeleteApiCommits(List<string> commits, int rows)
    {
        var deleted = 0;
        foreach (var commit in commits)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM api_surface_snapshots WHERE id IN (SELECT id FROM api_surface_snapshots WHERE git_commit = @c LIMIT @limit);";
            cmd.Parameters.AddWithValue("@c", commit);
            cmd.Parameters.AddWithValue("@limit", rows);
            deleted += cmd.ExecuteNonQuery();
        }
        return deleted;
    }

    /// <summary>
    /// The immutable snapshots whose semantic DATA a retention pass may reclaim (issue #46): every
    /// snapshot NOT kept alive by a retained generation, a branch pointer, an open pull request, an overlay base
    /// it is layered on, or a submodule provider a retained consumer still references (issue #54). Computed as the
    /// complement of the RETAINED-snapshot closure so a shared provider/base is never orphaned while any
    /// retained snapshot still reads it. Pending snapshots are excluded (a live writer may own them).
    ///
    /// Issue #303: the two closures (hard-protected, then retained) are recursive-CTE set operations over a
    /// temp edge table (<c>base_snapshot_id</c> and <c>snapshot_dependencies</c>, consumer to what it reads), not
    /// managed fixpoint loops over rows read into memory. Same rules, same result.
    /// </summary>
    public List<long> ComputeOrphanedSnapshotIds(IReadOnlyCollection<long> deletableRunIds)
    {
        DropClosureTables();
        try
        {
            Exec("""
                CREATE TEMP TABLE ret_edges (f INTEGER NOT NULL, t INTEGER NOT NULL, PRIMARY KEY (f, t)) WITHOUT ROWID;
                CREATE TEMP TABLE ret_runs (id INTEGER PRIMARY KEY);
                CREATE TEMP TABLE ret_hard (id INTEGER PRIMARY KEY);
                CREATE TEMP TABLE ret_kept (id INTEGER PRIMARY KEY);
                """);
            Exec("INSERT OR IGNORE INTO ret_edges SELECT id, base_snapshot_id FROM snapshots WHERE base_snapshot_id IS NOT NULL;");
            if (TableExists("snapshot_dependencies"))
                Exec("INSERT OR IGNORE INTO ret_edges SELECT consumer_snapshot_id, provider_snapshot_id FROM snapshot_dependencies;");
            _budget?.Check();

            using (var insert = _connection.CreateCommand())
            {
                insert.CommandText = "INSERT OR IGNORE INTO ret_runs (id) VALUES (@id);";
                var p = insert.Parameters.Add("@id", SqliteType.Integer);
                foreach (var runId in deletableRunIds)
                {
                    _budget?.Check();
                    p.Value = runId;
                    insert.ExecuteNonQuery();
                }
            }

            // HARD protected closure (criterion 4): pending snapshots (a live writer may own them) plus every
            // snapshot pinned by a branch pointer or an open pull request, plus catalogs written before #127 may still
            // bind a published retry to an abandoned run (or NULL after its ledger row was GC'd): no reliable age can
            // be inferred from that pointer, so fail closed. Expanded across the base/provider chains.
            RunClosure(TableExists("pull_request_snapshots") ? ClosureKind.HardWithPullRequests : ClosureKind.Hard);

            // Retained: the hard-protected set plus everything on a generation this pass is NOT deleting, expanded.
            RunClosure(ClosureKind.Kept);

            if (_policy.MaxSnapshotsPerRepository <= 0)
                return ReadIds("""
                    SELECT id FROM snapshots WHERE status <> @pending AND id NOT IN (SELECT id FROM ret_kept) ORDER BY id
                    """);

            var evictions = ComputeQuotaEvictions();
            var orphaned = ReadIds("""
                SELECT id FROM snapshots WHERE status <> @pending AND id NOT IN (SELECT id FROM ret_kept) ORDER BY id
                """);
            orphaned.AddRange(evictions);
            return orphaned.Distinct().Order().ToList();
        }
        finally
        {
            DropClosureTables();
        }
    }

    private void RunClosure(ClosureKind kind)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = kind switch
        {
            ClosureKind.HardWithPullRequests => HardClosureWithPullRequestsSql,
            ClosureKind.Hard => HardClosureSql,
            ClosureKind.Kept => KeptClosureSql,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        BindStatuses(cmd);
        cmd.ExecuteNonQuery();
    }

    private static void BindStatuses(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("@pending", SnapshotStatus.Pending);
        cmd.Parameters.AddWithValue("@complete", SnapshotStatus.Complete);
        cmd.Parameters.AddWithValue("@superseded", SnapshotStatus.Superseded);
        cmd.Parameters.AddWithValue("@abandoned", IndexRunState.Abandoned);
    }

    private List<long> ReadIds(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        BindStatuses(cmd);
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _budget?.Check();
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private void DropClosureTables()
    {
        try
        {
            Exec("DROP TABLE IF EXISTS temp.ret_edges; DROP TABLE IF EXISTS temp.ret_runs; DROP TABLE IF EXISTS temp.ret_hard; DROP TABLE IF EXISTS temp.ret_kept;");
        }
        catch (SqliteException)
        {
            // An interrupted budget can refuse even a drop; the next call drops first, and a reader is disposed anyway.
        }
    }

    /// <summary>
    /// Selects the oldest-over-cap complete snapshots per repository that are safe to evict for the quota:
    /// never a hard-protected (branch/open-PR/overlay/provider) snapshot, and never one a still-retained
    /// snapshot depends on (its base or a provider it consumes). Evicting a snapshot can never strand a kept
    /// snapshot's shared rows: everything reachable from a kept snapshot is un-evicted (a worklist over the edges).
    /// </summary>
    private List<long> ComputeQuotaEvictions()
    {
        _budget?.Check();
        var cap = _policy.MaxSnapshotsPerRepository;
        var retained = ReadIds("SELECT id FROM ret_kept").ToHashSet();
        var hardProtected = ReadIds("SELECT id FROM ret_hard").ToHashSet();
        var snapshots = new SnapshotStore(_connection).GetSnapshotsForRetention();
        var evict = new HashSet<long>();

        foreach (var repoGroup in snapshots
                     .Where(s => s.status == SnapshotStatus.Complete && retained.Contains(s.id) && !hardProtected.Contains(s.id))
                     .GroupBy(s => s.repositoryId))
        {
            _budget?.Check();
            // Newest first (created_at desc, id desc as a stable tiebreak); keep the cap newest, evict the rest.
            var ranked = repoGroup.OrderByDescending(s => s.createdAt).ThenByDescending(s => s.id).ToList();
            for (var i = cap; i < ranked.Count; i++)
                evict.Add(ranked[i].id);
        }

        if (evict.Count == 0) return [];

        var adjacency = new Dictionary<long, List<long>>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT f, t FROM ret_edges;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _budget?.Check();
                var from = reader.GetInt64(0);
                (adjacency.TryGetValue(from, out var list) ? list : adjacency[from] = []).Add(reader.GetInt64(1));
            }
        }

        var frontier = new Queue<long>(retained.Where(r => !evict.Contains(r)));
        var seen = new HashSet<long>(frontier);
        while (frontier.Count > 0)
        {
            _budget?.Check();
            var current = frontier.Dequeue();
            if (!adjacency.TryGetValue(current, out var next)) continue;
            foreach (var n in next)
            {
                evict.Remove(n);
                if (seen.Add(n)) frontier.Enqueue(n);
            }
        }
        return evict.ToList();
    }

    /// <summary>
    /// Deletes up to <see cref="ProjectChunkRowLimit"/> rows of the snapshot's first project version, child tables
    /// first, and the project row itself only once everything under it is gone (issue #252). The cascade of a single
    /// large project held the writer for seconds; a chunk per batch keeps every transaction short and releases the
    /// writer between chunks. The state lives in the database (a half-deleted project is simply the first project
    /// left), so no plan state is needed, and every batch re-verifies the snapshot before calling this.
    /// Returns true when the project row was deleted by this call.
    /// </summary>
    private bool DeleteProjectChunk(long snapshotId, out int rows)
    {
        rows = 0;
        long projectId;
        using (var pick = _connection.CreateCommand())
        {
            pick.CommandText = "SELECT id FROM projects WHERE snapshot_id = @id ORDER BY id LIMIT 1;";
            pick.Parameters.AddWithValue("@id", snapshotId);
            if (pick.ExecuteScalar() is not long found) return false;
            projectId = found;
        }

        var remaining = Math.Max(1, ProjectChunkRowLimit);
        foreach (var sql in ProjectOwnedChunkSql)
        {
            var deleted = ChunkStep(sql, projectId, null, remaining);
            rows += deleted;
            remaining -= deleted;
            if (remaining <= 0) return false;
        }

        while (true)
        {
            var symbolIds = FirstSymbolIds(projectId, Math.Min(SymbolChunkSize, remaining));
            if (symbolIds.Count == 0) break;
            var ids = "[" + string.Join(",", symbolIds) + "]";
            foreach (var sql in SymbolEdgeChunkSql)
            {
                var deleted = ChunkStep(sql, projectId, ids, remaining);
                rows += deleted;
                remaining -= deleted;
                if (remaining <= 0) return false;
            }
            var symbolsDeleted = ChunkStep(DeleteSymbolsChunkSql, projectId, ids, remaining);
            rows += symbolsDeleted;
            remaining -= symbolsDeleted;
            if (remaining <= 0) return false;
        }

        foreach (var sql in ProjectFileChunkSql)
        {
            var deleted = ChunkStep(sql, projectId, null, remaining);
            rows += deleted;
            remaining -= deleted;
            if (remaining <= 0) return false;
        }

        _budget?.Check();
        using var project = _connection.CreateCommand();
        project.CommandText = "DELETE FROM projects WHERE id = @p;";
        project.Parameters.AddWithValue("@p", projectId);
        var removed = project.ExecuteNonQuery();
        rows += removed;
        return removed > 0;
    }

    private int ChunkStep(string sql, long projectId, string? symbolIds, int limit)
    {
        _budget?.Check();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@n", limit);
        if (sql.Contains("@p", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("@p", projectId);
        if (symbolIds is not null) cmd.Parameters.AddWithValue("@ids", symbolIds);
        return cmd.ExecuteNonQuery();
    }

    private List<long> FirstSymbolIds(long projectId, int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM symbols WHERE project_id = @p LIMIT @n;";
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@n", limit);
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private const int SymbolChunkSize = 256;

    private static readonly string[] ProjectOwnedChunkSql =
    [
        "DELETE FROM occurrences WHERE id IN (SELECT id FROM occurrences WHERE in_project_id = @p LIMIT @n);",
        "DELETE FROM comments WHERE id IN (SELECT id FROM comments WHERE project_id = @p LIMIT @n);",
        "DELETE FROM api_surface_snapshots WHERE id IN (SELECT id FROM api_surface_snapshots WHERE project_id = @p LIMIT @n);"
    ];

    // Edges that point at, or start from, a chunk of the project's symbols, from any project (a cross-project usage).
    private static readonly string[] SymbolEdgeChunkSql =
    [
        "DELETE FROM occurrences WHERE id IN (SELECT id FROM occurrences WHERE target_symbol_id IN (SELECT value FROM json_each(@ids)) LIMIT @n);",
        "DELETE FROM occurrences WHERE id IN (SELECT id FROM occurrences WHERE source_symbol_id IS NOT NULL AND source_symbol_id IN (SELECT value FROM json_each(@ids)) LIMIT @n);",
        "DELETE FROM relationships WHERE id IN (SELECT id FROM relationships WHERE from_symbol_id IN (SELECT value FROM json_each(@ids)) LIMIT @n);",
        "DELETE FROM relationships WHERE id IN (SELECT id FROM relationships WHERE to_symbol_id IN (SELECT value FROM json_each(@ids)) LIMIT @n);"
    ];

    private const string DeleteSymbolsChunkSql =
        "DELETE FROM symbols WHERE id IN (SELECT value FROM json_each(@ids));";

    private static readonly string[] ProjectFileChunkSql =
    [
        "DELETE FROM file_versions WHERE id IN (SELECT fv.id FROM file_versions fv JOIN files f ON f.id = fv.file_id WHERE f.project_id = @p LIMIT @n);",
        "DELETE FROM files WHERE id IN (SELECT id FROM files WHERE project_id = @p LIMIT @n);"
    ];

    /// <summary>
    /// Deletes the catalog row only after all of its owned project versions have been collected.
    /// </summary>
    private int DeleteEmptySnapshot(long snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM snapshots WHERE id = @id
              AND NOT EXISTS (SELECT 1 FROM projects WHERE snapshot_id = @id);
            """;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Prunes source blobs referenced by no live symbol/occurrence/comment (bounded, protected-set-aware —
    /// issue #37). Resumable (issue #317): one batch examines the next <see cref="FileSweepSpan"/> ids after the plan's
    /// cursor, so a sweep never rescans the referenced prefix it already passed, and its cost per batch is bounded by
    /// the span rather than by how far the next orphans lie. The orphan test itself is unchanged.
    /// </summary>
    private int PruneOrphanFileVersions(RetentionPlan plan, RetentionProtectionSet protections)
    {
        var ids = new List<long>();
        var examined = 0;
        var span = plan.Tuning.SweepSpan;
        var last = plan.FileVersionCursor;
        var capped = false;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fv.id,
                       NOT EXISTS (SELECT 1 FROM symbols WHERE file_version_id = fv.id)
                       AND NOT EXISTS (SELECT 1 FROM occurrences WHERE file_version_id = fv.id)
                       AND NOT EXISTS (SELECT 1 FROM comments WHERE file_version_id = fv.id)
                FROM (SELECT id FROM file_versions WHERE id > @after ORDER BY id LIMIT @span) fv;
                """;
            cmd.Parameters.AddWithValue("@after", plan.FileVersionCursor);
            cmd.Parameters.AddWithValue("@span", span);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _budget?.Check();
                examined++;
                last = reader.GetInt64(0);
                if (reader.GetInt64(1) == 1 && !protections.IsFileVersionProtected(last)) ids.Add(last);
                if (ids.Count >= BatchRowLimit)
                {
                    capped = true;
                    break;
                }
            }
        }

        var deleted = 0;
        foreach (var chunk in Chunk(ids, 256))
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM file_versions WHERE id IN (SELECT value FROM json_each(@ids));";
            cmd.Parameters.AddWithValue("@ids", "[" + string.Join(",", chunk) + "]");
            deleted += cmd.ExecuteNonQuery();
        }
        plan.AdvanceFileVersions(last, !capped && examined < span);
        return deleted;
    }

    private List<long> OrphanFileVersionIds(RetentionProtectionSet protections, int? limit = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT fv.id FROM file_versions fv
            WHERE NOT EXISTS (SELECT 1 FROM symbols WHERE file_version_id = fv.id)
              AND NOT EXISTS (SELECT 1 FROM occurrences WHERE file_version_id = fv.id)
              AND NOT EXISTS (SELECT 1 FROM comments WHERE file_version_id = fv.id)
            ORDER BY fv.id;
            """;
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _budget?.Check();
            var id = reader.GetInt64(0);
            if (!protections.IsFileVersionProtected(id)) ids.Add(id);
            if (limit.HasValue && ids.Count >= limit.Value) break;
        }
        return ids;
    }

    private enum RetentionCount { ProjectVersions, ApiRows }

    private int CountRows(RetentionCount count, object value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = count switch
        {
            RetentionCount.ProjectVersions => "SELECT COUNT(*) FROM projects WHERE snapshot_id = @value;",
            RetentionCount.ApiRows => "SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = @value;",
            _ => throw new ArgumentOutOfRangeException(nameof(count))
        };
        cmd.Parameters.AddWithValue("@value", value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static IEnumerable<IReadOnlyList<long>> Chunk(List<long> ids, int size)
    {
        for (var i = 0; i < ids.Count; i += size)
            yield return ids.GetRange(i, Math.Min(size, ids.Count - i));
    }

    private long ScalarLong(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

}
