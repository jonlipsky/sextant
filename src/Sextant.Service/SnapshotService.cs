using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Service.Backup;
using Sextant.Service.Contributions;
using Sextant.Service.Grants;
using Sextant.Service.Observability;
using Sextant.Service.Rollout;
using Sextant.Service.Sandbox;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>
/// The standalone Sextant index service's DATA PLANE (Phase 13). It owns the durable Phase-9 snapshot
/// catalog + semantic store on a single writer connection, guarded by a cross-process single-writer lease
/// (issue #38), and exposes the control operations a client (or, in Phase 14, ProcessStack) needs:
/// idempotent ensure-snapshot, job status with per-project diagnostics, branch resolution, and a
/// service-owned retention/GC pass. It NEVER couples to ProcessStack — the dependency points outward — and
/// the local stdio MCP path stays entirely independent of it (criterion 6).
///
/// Ownership + concurrency: all catalog WRITES go through the single writer connection serialized by an
/// async gate (a raw <see cref="SqliteConnection"/> is not thread-safe), so concurrent ensure requests
/// attach to ONE durable job (criterion 1) and never corrupt the connection. QUERY reads use a SEPARATE
/// connection (the HTTP MCP host's own <c>DatabaseProvider</c>), so low-latency queries never block behind
/// a running index; control-plane READS (job status, coverage, branch resolution) likewise use an
/// independent read connection rather than the write gate (issue #148). On construction the service
/// acquires the lease, runs recovery, and reconciles any job a dead worker left <c>running</c> back to
/// <c>queued</c> (criterion 2).
///
/// Ensure lifecycle (issue #148): an ensure runs on a SERVICE-OWNED lifetime, never on the caller's
/// cancellation token. The caller only awaits it; a caller disconnect/timeout ends that wait and nothing
/// else — production continues, the job stays <c>running</c>, and it later publishes normally. Productions
/// are tracked in an in-flight registry keyed by identity hash, so a later ensure of the same identity
/// attaches to the running production (the worker runs once). Only service shutdown
/// (<see cref="StopProduction"/> / <see cref="Dispose"/>) cancels a worker, and that requeues the job.
/// </summary>
public sealed partial class SnapshotService : IDisposable
{
    private readonly ServiceOptions _options;
    private readonly ISnapshotWorker _worker;
    private readonly ServicePaths _paths;
    private readonly IndexDatabase _db;
    /// <summary>The write-session options of the catalog database this service (and its worker) writes through.</summary>
    internal IndexWriteOptions WriteOptions => _db.WriteOptions;
    private readonly SqliteConnection _writerConnection;
    private SqliteConnection _conn
    {
        get
        {
            AssertWriterTurn();
            return _writerConnection;
        }
    }
    private readonly AsyncLocal<Task?> _writerTurn = new();
    private bool _guardWriterAccess;
    private int _writerTraceViolations;
    private readonly WriterLease _lease;
    private readonly WriteGate _writeGate;
    private readonly WriteLockTracker _writeLocks;
    // Persisted across maintenance passes so each pass starts at the step size the last one converged on.
    private int _vacuumStepPages = IncrementalVacuum.InitialStepPages;
    private WriteLockHold? _rawHold;
    private readonly bool _ownsDatabase;
    private readonly IContributionAuthorizer _authorizer;
    private readonly IGitContentProvider _gitContent;
    private readonly ContributionPolicy _contributionPolicy;
    private readonly RemoteDefaultBranchLookup? _remoteDefaults;
    private readonly IRemoteBranchLister? _remoteBranches;
    private readonly TimeProvider _timeProvider;
    private readonly DeniedAuditLimiter _deniedLimiter;
    private readonly ServiceMetrics _metrics = new();

    // Issue #148: the service-owned lifetime every ensure operation + worker run is bound to (cancelled only
    // by StopProduction/Dispose), the in-flight production registry keyed by identity hash, and the set of
    // live ensure operations Dispose drains. _inFlightLock guards both collections and _disposed's transition
    // (admission and Dispose's drain snapshot are atomic under it). It never waits for the write gate: under it a
    // control write only QUEUES its turn (TakeWriteTurnLocked, which never blocks), so it cannot deadlock against a
    // production.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _inFlightLock = new();
    private readonly Dictionary<(long RepositoryId, string Branch), TaskCompletionSource> _branchIntentWakeups = [];
    private readonly Dictionary<string, string> _ensureRepositorySpellings = new(StringComparer.Ordinal);
    // Replay must finish admitting the durable queue before its first turn inspects linked successors.
    private readonly TaskCompletionSource _replayAdmissions = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, InFlightProduction> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _operations = [];
    private volatile bool _leaseReleased;
    private volatile bool _disposed;

    // Issue #158: ensures and retires take their turn on the writer at admission, in submission order (see
    // TakeWriteTurnLocked). _writeAdmissions counts every write admitted to the gate (Interlocked). Job ids are
    // handed out before their rows exist through _jobIds. _pendingEnsures (guarded by _inFlightLock) counts the ensure operations of each identity
    // that have not finished: an identity's id reservation lives while one of them may still register it, and is
    // dropped once none is left (EndPendingEnsureLocked), so a registration that failed or rolled back never leaves a
    // reserved id reading as queued forever.
    private long _writeAdmissions;

    // Idle-branch retirements admitted and not yet applied (issue #333), guarded by _inFlightLock: the next maintenance
    // tick leaves these branches out, so a step that stopped waiting never grows the queue with duplicates.
    private readonly HashSet<string> _pendingIdleRetires = new(StringComparer.Ordinal);

    private static string IdleRetireKey(string repository, string branch) => repository + "\n" + branch;

    private void ForgetPendingIdleRetire(RetireBranchRequest request, IdleRetireGuard? idle)
    {
        if (idle is null)
            return;
        lock (_inFlightLock)
            _pendingIdleRetires.Remove(IdleRetireKey(request.Repository, request.Branch));
    }

    internal bool HasPendingIdleRetire(string repository, string branch)
    {
        lock (_inFlightLock)
            return _pendingIdleRetires.Contains(IdleRetireKey(repository, branch));
    }

    // The latest admitted feature-branch advance ensure per (repository, branch).
    private readonly Dictionary<string, AdvanceTicket> _latestAdvance = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingDefaultEnsures = new(StringComparer.Ordinal);
    private readonly JobIdReservations _jobIds;
    private readonly Dictionary<string, int> _pendingEnsures = new(StringComparer.Ordinal);

    private SnapshotService(
        ServiceOptions options, ISnapshotWorker worker, ServicePaths paths,
        IndexDatabase db, WriterLease lease, bool ownsDatabase,
        IContributionAuthorizer authorizer, IGitContentProvider gitContent, ContributionPolicy contributionPolicy,
        IRemoteDefaultBranchResolver? remoteDefaults, TimeProvider? timeProvider, IRemoteBranchLister? remoteBranches,
        WriteLockTracker writeLocks)
    {
        _writeLocks = writeLocks;
        _options = options;
        _worker = worker;
        _paths = paths;
        SourceTexts = new SourceTextStore(paths.SourceTextRoot);
        _db = db;
        _writerConnection = db.GetConnection();
        _lease = lease;
        _ownsDatabase = ownsDatabase;
        _authorizer = authorizer;
        _gitContent = gitContent;
        _contributionPolicy = contributionPolicy;
        _remoteDefaults = remoteDefaults is null ? null : new RemoteDefaultBranchLookup(
            remoteDefaults, timeProvider ?? TimeProvider.System, remoteDefaults as IRemoteBranchHeadResolver, remoteDefaults as IRemoteCommitBranchChecker);
        _remoteBranches = remoteBranches;
        _headResolver = remoteDefaults as IRemoteBranchHeadResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deniedLimiter = new DeniedAuditLimiter(_timeProvider);
        _freshness = new DefaultBranchFreshnessTracker(
            _timeProvider, 3 * _options.FreshnessCheckInterval + TimeSpan.FromMinutes(1));
        _writeGate = new WriteGate(_timeProvider, expeditedStreakLimit: options.ExpeditedStreakLimit);
        _jobIds = new JobIdReservations(
            JobIdReservations.FloorPathFor(options.CatalogDbPath),
            () => ReadCatalog(conn => new SnapshotJobStore(conn).MaxJobId()));
    }

    public ServicePaths Paths => _paths;

    /// <summary>
    /// The content-addressed source text published snapshots serve their snippets and <c>include_source</c> from
    /// (issue #244), on the artifact volume. The worker writes it (it must be built over
    /// <see cref="ServicePaths.SourceTextRoot"/> too), the query surface reads it, and an executed retention pass
    /// deletes the blobs no indexed file version references any more.
    /// </summary>
    public SourceTextStore SourceTexts { get; }

    /// <summary>
    /// False once <see cref="Dispose"/> gave up waiting for an in-flight production that ignored cancellation
    /// past <see cref="ServiceOptions.ShutdownDrainTimeout"/> (issue #148). The straggler may still be using
    /// the shared catalog <see cref="IndexDatabase"/>, so a host that passed one in must leave it open (process
    /// exit reclaims it) rather than dispose it under the straggler. True while running and after a clean drain.
    /// </summary>
    public bool ProductionDrained { get; private set; } = true;

    /// <summary>The live in-process metric counters (criterion 5), shared with the host query-timing middleware.</summary>
    public ServiceMetrics Metrics => _metrics;

    /// <summary>
    /// True once startup recovery + reconciliation completed on this instance (set by
    /// <see cref="ReconcileOnStartup"/>). The pilot-readiness gate uses it as the criterion-3 signal.
    /// </summary>
    public bool RecoveryCompleted { get; private set; }

    /// <summary>
    /// Whether OS-hard, out-of-process worker isolation (issue #76) is available on this build. It is a
    /// SERVICE capability, never a request parameter — the pilot gate reads it here so a caller cannot
    /// assert the #76 hard precondition into existence. Currently always false: #76 is open and this build
    /// ships only the in-process defense-in-depth sandbox. When #76 lands, this reflects the worker's real
    /// isolation capability.
    /// </summary>
    public bool HardOsIsolationAvailable => false;

    /// <summary>The writer-lease token this service instance holds (identifies jobs it owns).</summary>
    public string OwnerToken => _lease.OwnerToken;

    /// <summary>
    /// Starts the data plane: opens the catalog database, runs migrations WITHOUT recovery, acquires the
    /// single-writer lease (fail-closed — throws if a live writer already holds it), then recovers a valid
    /// WAL / sweeps abandoned staging generations and reconciles orphaned jobs. Ordering matters: migrate
    /// then lease then recover, so recovery never abandons a live writer's staging generation (issue #38).
    /// </summary>
    public static SnapshotService Start(
        ServiceOptions options, ISnapshotWorker? worker = null, IndexDatabase? database = null,
        IContributionAuthorizer? authorizer = null, IGitContentProvider? gitContent = null,
        ContributionPolicy? contributionPolicy = null, IRemoteDefaultBranchResolver? remoteDefaults = null,
        TimeProvider? timeProvider = null, IRemoteBranchLister? remoteBranches = null)
    {
        var effectivePolicy = contributionPolicy ?? options.Contribution;
        var effectiveAuthorizer = authorizer ?? OpenContributionAuthorizer.Instance;
        var effectiveGitContent = gitContent ?? UnavailableGitContentProvider.Instance;

        // Fail-closed startup guard (CRITICAL 1): a deployment that OPTED INTO required authorization or
        // required Git-content verification MUST have a real provider wired. Refusing to start — rather than
        // silently running with the dev-open default — prevents a fail-OPEN supply-chain posture where
        // RequireAuthorization is set but every contribution is waved through by the open dev authorizer.
        if (effectivePolicy.RequireAuthorization && effectiveAuthorizer is OpenContributionAuthorizer)
            throw new InvalidOperationException(
                "ContributionPolicy.RequireAuthorization is set but no real IContributionAuthorizer is wired; " +
                "refusing to start with the dev-open authorizer (fail-closed).");
        if (effectivePolicy.RequireGitContentVerification && effectiveGitContent is UnavailableGitContentProvider)
            throw new InvalidOperationException(
                "ContributionPolicy.RequireGitContentVerification is set but no real IGitContentProvider is wired; " +
                "refusing to start with the unavailable provider (fail-closed).");

        var paths = new ServicePaths(options.Volumes);
        var ownsDatabase = database is null;
        var db = database ?? new IndexDatabase(options.CatalogDbPath, options.ApplyTo(IndexWriteOptions.Default));
        if (options.GrantWriteBusyTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "GrantWriteBusyTimeout must be positive.");
        if (options.WriteLockWarnThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "WriteLockWarnThreshold must be positive.");
        if (options.VacuumTargetHold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "VacuumTargetHold must be positive.");
        var writeLocks = new WriteLockTracker(
            options.WriteLockWarnThreshold, e => Console.Error.WriteLine("warn: " + e.Describe()), timeProvider);
        db.WriteLockTracker = writeLocks;
        try
        {
            db.RunMigrations(recover: false);

            var lease = WriterLease.TryAcquire(options.CatalogDbPath, options.Holder, options.LeaseTtl, kind: WriterKind.Service, tracker: writeLocks)
                ?? throw new InvalidOperationException(
                    $"Another writer holds the single-writer lease on '{options.CatalogDbPath}'. " +
                    "Retention/publish/GC must not race a live daemon/service (issue #38). A lease whose holder is " +
                    "provably dead on this host is taken over at once; otherwise it must expire first (issue #320).");

            try
            {
                LogLeaseAcquisition(lease.Acquisition);
                // Once the lease is held, make every write session abort between batches if it is ever
                // stolen (issue #38 / criterion 3): a long worker index run consults this probe at each
                // batch boundary and rolls back rather than racing the new owner into a corrupt publish.
                db.SetWriterLostProbe(() => lease.IsLost);
                db.Recover();
                var service = new SnapshotService(
                    options, worker ?? new UnavailableSnapshotWorker(), paths, db, lease, ownsDatabase,
                    effectiveAuthorizer, effectiveGitContent, effectivePolicy, remoteDefaults, timeProvider, remoteBranches, writeLocks);
                service._jobIds.Load();
                service.LogTenantGrantLease();
                service.ReconcileOnStartup();
                try
                {
                    service.ReplayDurableAdmissions();
                }
                finally
                {
                    service._replayAdmissions.TrySetResult();
                }
                service.StartIntentScheduling();
                service.StartMaintenanceLoop();
                service.StartObservabilityLoops();
                return service;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch
        {
            if (ownsDatabase) db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Makes a crash of the previous run visible (issue #320): one line when a dead holder's lease was taken over
    /// without waiting for its TTL, one WARNING when the previous run never released the lease (clean-shutdown
    /// marker missing). The durable counters behind the metrics are written by <see cref="WriterLease"/> itself.
    /// </summary>
    internal static void LogLeaseAcquisition(WriterLeaseAcquisition acquisition, TextWriter? output = null)
    {
        var writer = output ?? Console.Error;
        if (acquisition.TakenOverReason is { } reason)
            writer.WriteLine(
                $"Took over the stale single-writer lease of '{acquisition.TakenOverHolder ?? "unknown holder"}' " +
                $"(last heartbeat {TimeSpan.FromMilliseconds(acquisition.TakenOverAgeMs).TotalSeconds:0} s ago): {reason}.");
        if (acquisition.PreviousRunUnclean)
        {
            var last = acquisition.PreviousRunHeartbeatAt is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("u", System.Globalization.CultureInfo.InvariantCulture)
                : "an unknown time";
            var kind = acquisition.PreviousRunKind ?? "writer";
            writer.WriteLine(acquisition.PreviousRunEndReason switch
            {
                WriterEndReason.Abandoned =>
                    $"Note: previous {kind} run abandoned the lease at ~{last} (it could not confirm it had stopped " +
                    "writing, e.g. a shutdown drain timeout); not a crash.",
                WriterEndReason.ReleaseFailed =>
                    $"Note: previous {kind} run could not release the lease at ~{last} (a database error during " +
                    "shutdown); not a crash.",
                _ => $"WARNING: previous {kind} run ended without a clean shutdown at ~{last}; " +
                     "possible crash or kill (e.g. OOM)."
            });
        }
    }

    /// <summary>
    /// Reconciles state a previous worker/service instance left orphaned by a crash (Phase 17 criterion 3),
    /// so worker/service loss at every stage resolves to retryable/failed/complete and NEVER a corrupt
    /// publish. This service holds a fresh lease token, so it: (1) resets every <c>running</c> job not owned
    /// by THIS token to <c>queued</c> for re-attempt; (2) resets any PHANTOM terminal job — <c>complete</c>/
    /// <c>partial</c> with a now-NULL <c>snapshot_id</c> (crash between the status write and the publish
    /// commit, or the snapshot later reclaimed) — back to <c>queued</c> so a later ensure regenerates it
    /// rather than reporting a phantom-complete forever; and (3) sweeps orphaned per-job scratch left by a
    /// crash. Returns the number of jobs reconciled. Startup only: it must run before admission opens (it resets every
    /// non-owned running job and settles every queued one).
    /// </summary>
    internal int ReconcileOnStartup()
    {
        var reconciled = WithWrite(() =>
        {
            var jobs = new SnapshotJobStore(_conn);
            var count = jobs.ReconcileOrphanedJobs(_lease.OwnerToken)
                      + jobs.ReconcilePhantomTerminalJobs();
            var releasedRefreshes = new PendingRefreshStore(_conn).ReleaseDeadClaims(_refreshTurnId);
            var releasedIntents = new BranchIntentStore(_conn).ReleaseClaims();
            // Durable audit trail of the recovery action (criterion 5). Service-wide (no repository scope).
            new AuditLogStore(_conn).Append(
                AuditAction.Reconcile, AuditOutcome.Complete,
                detail: $"reconciled_{count};refresh_claims_released={releasedRefreshes};branch_intents_released={releasedIntents}");
            SettleOrphanedQueuedJobsLocked(jobs);
            return count;
        });

        // Best-effort scratch sweep OUTSIDE the write transaction (filesystem, not catalog state); confined
        // to the scratch root so it can never touch a persistent volume.
        _paths.SweepOrphanedScratch();

        RecoveryCompleted = true;
        return reconciled;
    }

    // Issue #327: at startup no job has a live in-process turn, so every queued row is an orphan of an earlier run (its
    // turn was lost to a restart, a cancel or a retention requeue). Settle it with the existing statuses, in bounded
    // transactions: coalesced when a newer job of its branch already ran, cancelled otherwise. The durable ensure
    // admission is replayed separately and applies the current branch-specific behavior (default refresh or feature
    // handover). A later ensure of either identity produces it again. Timestamps are not touched.
    // MUST only run before admission opens (it takes the write gate and treats every queued row as orphaned).
    private const int StartupSettleBatchSize = 500;

    private void SettleOrphanedQueuedJobsLocked(SnapshotJobStore jobs)
    {
        var queued = jobs.FindQueuedJobs();
        if (queued.Count == 0)
            return;

        int coalesced = 0, cancelled = 0;
        for (var offset = 0; offset < queued.Count; offset += StartupSettleBatchSize)
        {
            BeginRaw("settle-orphaned-queued");
            try
            {
                var last = offset + StartupSettleBatchSize >= queued.Count;
                foreach (var (jobId, superseded) in queued.Skip(offset).Take(StartupSettleBatchSize))
                {
                    var settled = superseded
                        ? jobs.SettleQueued(jobId, SnapshotJobStatus.Coalesced,
                            SnapshotJobStore.StartupSettledReasonPrefix + " superseded by a newer job of its branch that already ran; a later ensure produces it")
                        : jobs.SettleQueued(jobId, SnapshotJobStatus.Cancelled,
                            SnapshotJobStore.StartupSettledReasonPrefix + " its service run ended before its turn (restart); a later ensure produces it");
                    if (!settled)
                        continue;
                    if (superseded) coalesced++;
                    else cancelled++;
                }
                // The settlement time lives only here (the job rows keep their timestamps), and the audit row commits
                // with the last batch, so a settlement is never unaudited.
                if (last)
                    new AuditLogStore(_conn).Append(
                        AuditAction.Reconcile, AuditOutcome.Complete,
                        detail: $"settled_queued_coalesced_{coalesced}_cancelled_{cancelled}");
                ExecRaw("COMMIT;");
            }
            catch
            {
                ExecRaw("ROLLBACK;");
                throw;
            }
        }

        _metrics.RecordStartupJobsSettled(coalesced, cancelled);
        Console.Error.WriteLine(
            $"Startup settled {coalesced + cancelled} orphaned queued job(s) with no live turn: " +
            $"{coalesced} coalesced (superseded), {cancelled} cancelled (not produced; a later ensure produces them).");
    }

    /// <summary>
    /// Idempotently ensures a committed-branch snapshot exists (acceptance criterion 1), recording the
    /// observability signals around it: an ensure-latency trace span, the idempotent-reuse counter
    /// (criterion 5, cache reuse), and a durable audit row attributing the outcome and worker cost to the
    /// requested repository scope (criterion 5, audit + cost attribution). <paramref name="principal"/> is
    /// the control-plane bearer the host authenticated; it is stored ONLY as a non-reversible hash.
    ///
    /// Issue #148: the ensure itself runs on the service-owned lifetime; <paramref name="cancellationToken"/>
    /// only bounds how long THIS caller waits. Cancelling it throws <see cref="OperationCanceledException"/>
    /// to the caller while the ensure (job registration, worker run, publish, audit) carries on in the
    /// background — so a caller whose timeout is shorter than the index still gets a snapshot, and a
    /// re-ensure attaches to the running production. <see cref="BeginEnsureSnapshotAsync"/> is the
    /// non-blocking variant.
    /// </summary>
    public async Task<EnsureSnapshotResult> EnsureSnapshotAsync(
        EnsureSnapshotRequest request, CancellationToken cancellationToken = default, AuditCaller principal = default)
    {
        var refresh = await ResolveRefreshRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (refresh.IsRefresh)
        {
            if (!refresh.IsDefaultConfirmed)
            {
                await RecordEnsureDeniedAsync(BranchGuardReason.RefreshDefaultUnconfirmed, principal, cancellationToken)
                    .ConfigureAwait(false);
                return RejectedRefreshResult();
            }
            var admissionId = Guid.NewGuid().ToString("N");
            var (_, refreshCompletion) = await AdmitRefreshAsync(request, principal, admissionId, cancellationToken).ConfigureAwait(false);
            return await WaitForCallerAsync(refreshCompletion, cancellationToken).ConfigureAwait(false);
        }
        var (_, completion, _, persisted, _, _) = StartEnsureOperation(request, principal);
        await persisted.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await WaitForCallerAsync(completion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The non-blocking ensure (issue #148, <c>POST /control/ensure?wait=false</c>): starts the same
    /// service-owned ensure as <see cref="EnsureSnapshotAsync"/> but returns as soon as the job is registered —
    /// a <c>queued</c>/<c>running</c> result carrying the <c>job_id</c> to poll, or the full result when the
    /// identity was already terminal. An identity that is already producing is attached at once.
    /// <para>
    /// Registering a NEW identity needs the single writer, which another identity's production holds for its whole
    /// run. When the ensure has not registered within <see cref="ServiceOptions.ControlWriteWait"/> (issue #158), this
    /// returns a <c>queued</c> result anyway, without the writer: its <c>job_id</c> is the identity's durable job id
    /// when the row exists, else an id RESERVED for it, which <see cref="GetStatus"/> reports as <c>queued</c> at
    /// once and which the row is inserted with when the ensure's turn on the writer comes. The ensure itself stays
    /// queued and service-owned, so it registers and produces even after this caller has gone; a second ensure of the
    /// same identity gets the same id. For remote branch-moving ensures, the response bound includes recording a
    /// durable admission; a catalog lock past the bound produces <see cref="GrantStoreUnavailableException"/>.
    /// Other ensures await their best-effort admission outside the bound; a failed write can leave queued work
    /// without a replay record. The service-owned ensure remains active in either case.
    /// <paramref name="cancellationToken"/> only bounds this caller's wait.
    /// </para>
    /// </summary>
    public async Task<EnsureSnapshotResult> BeginEnsureSnapshotAsync(
        EnsureSnapshotRequest request, AuditCaller principal = default, CancellationToken cancellationToken = default)
    {
        var refresh = await ResolveRefreshRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (refresh.IsRefresh)
        {
            if (!refresh.IsDefaultConfirmed)
            {
                await RecordEnsureDeniedAsync(BranchGuardReason.RefreshDefaultUnconfirmed, principal, cancellationToken)
                    .ConfigureAwait(false);
                return RejectedRefreshResult();
            }
            using var refreshResponseBound = new CancellationTokenSource(_options.ControlWriteWait, _timeProvider);
            using var refreshWait = CancellationTokenSource.CreateLinkedTokenSource(
                refreshResponseBound.Token, cancellationToken);
            var refreshAdmissionId = Guid.NewGuid().ToString("N");
            try
            {
                var (admission, _) = await AdmitRefreshAsync(request, principal, refreshAdmissionId, refreshWait.Token).ConfigureAwait(false);
                return admission;
            }
            catch (OperationCanceledException) when (
                refreshResponseBound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new GrantStoreUnavailableException(
                    "The refresh admission could not be durably recorded within the control-write response bound; retry the request.")
                { AdmissionId = refreshAdmissionId };
            }
        }
        var (accepted, _, hash, persisted, _, admissionId) = StartEnsureOperation(request, principal);
        if (!NeedsBranchConvergence(request))
        {
            if (!persisted.IsCompleted)
                await persisted.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!accepted.IsCompleted)
            {
                using var legacyBound = new CancellationTokenSource(_options.ControlWriteWait, _timeProvider);
                using var legacyWait = CancellationTokenSource.CreateLinkedTokenSource(legacyBound.Token, cancellationToken);
                ControlWaitStartedForTesting?.Invoke();
                try
                {
                    await ((Task)accepted).WaitAsync(legacyWait.Token)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                catch (OperationCanceledException) when (legacyBound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                }
                if (!accepted.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (QueuedResult(request, hash, null) is { } queued && !accepted.IsCompleted)
                        return queued;
                }
            }
            return await WaitForCallerAsync(accepted, cancellationToken).ConfigureAwait(false);
        }

        using var responseBound = new CancellationTokenSource(_options.ControlWriteWait, _timeProvider);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            responseBound.Token, cancellationToken);
        // Issue #406: the 202 is only given once the admitted ensure is durable (a short catalog write that does not
        // take the write gate; it can wait out an open publish transaction, bounded by the grant busy timeout).
        try
        {
            await persisted.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (responseBound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new GrantStoreUnavailableException(
                "The queued ensure could not be durably admitted within the control-write response bound; retry the request.")
            { AdmissionId = admissionId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GrantStoreUnavailableException(
                "The queued ensure could not be durably admitted; retry the request.", ex)
            { AdmissionId = admissionId };
        }
        // Issue #273 (D2): from here on the answer carries the admission the transaction resolved: this request's own
        // intent, or the identical earlier ensure it shares. The candidate id is only for a 503 before that decision.
        admissionId = persisted.Result.AdmissionId ?? admissionId;
        if (!accepted.IsCompleted)
        {
            ControlWaitStartedForTesting?.Invoke();
            try
            {
                await ((Task)accepted).WaitAsync(waitCancellation.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            catch (OperationCanceledException) when (responseBound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The durable admission is already recorded; the reserved id can safely be returned as queued.
            }
            if (!accepted.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The writer is busy: answer with the identity's job id without it. A registration that settled
                // meanwhile reports its own result (it carries the same id).
                if (QueuedResult(request, hash, admissionId) is { } queued && !accepted.IsCompleted)
                    return queued;
                if (!accepted.IsCompleted && responseBound.IsCancellationRequested)
                    throw new GrantStoreUnavailableException(
                        "The queued ensure exceeded the control-write response bound before a safe job id could be reserved; retry the request.")
                    { AdmissionId = admissionId };
            }
        }
        return await WaitForCallerAsync(accepted, cancellationToken).ConfigureAwait(false);
    }

    // The wait=false answer for an ensure still waiting for the writer (issue #158): queued, with the job id the
    // ensure will register (or has registered) its identity under. Null when no id can be handed out safely.
    private EnsureSnapshotResult? QueuedResult(EnsureSnapshotRequest request, string hash, string? admissionId)
    {
        if (FindInFlight(hash) is { } inFlight)
            return PendingResult(inFlight.JobId, hash, admissionId);
        var handed = _jobIds.ForCaller(
            hash, request.RepositoryRemoteUrl, request.CommitSha, request.BranchName,
            identity => ReadCatalog(conn => new SnapshotJobStore(conn).GetJobByIdentity(identity)?.Id));
        lock (_inFlightLock)
        {
            // The ensure finished meanwhile (its result is about to settle): nothing is left to register a reservation
            // made just now, so drop it and let the caller take the ensure's own result.
            if (!_pendingEnsures.ContainsKey(hash))
            {
                _jobIds.Release(hash);
                return null;
            }
        }
        return handed is { } job
            ? new EnsureSnapshotResult
            {
                JobId = job.Id, IdentityHash = hash, Status = SnapshotJobStatus.Queued,
                Attached = job.Existed, AdmissionId = admissionId
            }
            : null;
    }

    /// <summary>
    /// Records the durable <c>ensure</c>/<c>denied</c> audit row for an ensure the host refused at intake,
    /// before any job row exists (SVC-5: <see cref="RepositoryUrlPolicy"/>). <paramref name="reason"/> is the
    /// refusal code only: the submitted URL is NEVER stored (it is untrusted and may carry credentials), so the
    /// row has no repository scope. Like the ensure audit, the write is a service-owned operation: it survives
    /// the caller disconnecting (its <paramref name="cancellationToken"/> only bounds this wait) and is drained by
    /// <see cref="Dispose"/>. The caller waits at most <see cref="ServiceOptions.DeniedAuditWait"/> for it, so a
    /// refusal is never held behind a running production (which holds the single writer for its whole run); a
    /// write still queued past that bound lands once the writer frees. Its gate wait is deliberately NOT tied to
    /// the service lifetime: the caller may already have its 400, so shutdown must not drop the row (it lands
    /// during the <see cref="Dispose"/> drain, and fails closed if the lease was abandoned). A refusal admitted
    /// after shutdown began throws <see cref="OperationCanceledException"/> on the service lifetime (503).
    /// </summary>
    public Task RecordEnsureDeniedAsync(
        string reason, AuditCaller principal = default, CancellationToken cancellationToken = default) =>
        RecordDeniedAsync(AuditAction.Ensure, reason, principal, cancellationToken);

    /// <summary>
    /// Records the durable <c>retire</c>/<c>denied</c> audit row for a branch retirement the host refused at
    /// intake (SVC-6: the <see cref="RepositoryUrlPolicy"/>, or a missing branch name), before any catalog read.
    /// Same contract as <see cref="RecordEnsureDeniedAsync"/>: the reason code only, never the submitted URL.
    /// </summary>
    public Task RecordRetireDeniedAsync(
        string reason, AuditCaller principal = default, CancellationToken cancellationToken = default) =>
        RecordDeniedAsync(AuditAction.Retire, reason, principal, cancellationToken);

    /// <summary>
    /// Records the durable <paramref name="action"/>/<c>denied</c> audit row for a control call the host refused
    /// by caller before reading anything (issue #193: a user caller on an application/operator-only route such
    /// as retire, retention or backup). The row has no repository scope, because the refusal precedes reading
    /// the request. Same contract as <see cref="RecordEnsureDeniedAsync"/>.
    /// </summary>
    public Task RecordControlDeniedAsync(
        string action, string reason, AuditCaller principal = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);
        return RecordDeniedAsync(action, reason, principal, cancellationToken);
    }

    private async Task RecordDeniedAsync(
        string action, string reason, AuditCaller principal, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        Task<bool> write;
        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfStopping();
            if (!_deniedLimiter.TryAdmit(principal.Actor, $"{action};{reason}", out var suppressed))
                return;
            write = Task.Run(
                () => WithWriteAsync(
                    () =>
                    {
                        new AuditLogStore(_conn).Append(
                            action, AuditOutcome.Denied,
                            actor: principal.Actor,
                            detail: DeniedAuditLimiter.WithSuppressed(principal.Detail(reason), suppressed));
                        return Task.FromResult(true);
                    },
                    CancellationToken.None),
                CancellationToken.None);
            _operations.Add(write);
        }
        _ = write.ContinueWith(
            t =>
            {
                lock (_inFlightLock)
                    _operations.Remove(t);
                _ = t.Exception;
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await write.WaitAsync(_options.DeniedAuditWait, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The writer is busy (a running production); the tracked write lands once it frees. If it finished
            // after the timer fired, surface its real outcome instead of the timeout.
            if (write.IsCompleted)
                await write.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Begins service shutdown (issue #148): cancels the service-owned production lifetime, so every in-flight
    /// worker run is cancelled and its job requeued (never recorded as a failure) and any later ensure fails
    /// fast. The host calls this on <c>ApplicationStopping</c>; <see cref="Dispose"/> calls it too. Idempotent.
    /// </summary>
    public void StopProduction()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed after a full drain: nothing is left to cancel.
        }
    }

    // The snapshot identity hash an ensure of `request` computes on this node NOW: the request's own fields folded with
    // this node's schema/analyzer/toolchain, its default profile configuration and capability, and its non-default
    // SDK-pin (issue #113) and restore policies, so flipping SEXTANT_SERVICE_SDK_PIN_OVERRIDE or
    // SEXTANT_SERVICE_PACKAGE_RESTORE never reuses a snapshot (or failed job) built under the other policy. The ONE
    // place the service computes a request identity: the ensure path and /control/resolve's identity_current share it.
    private string IdentityHashFor(EnsureSnapshotRequest request) => request.ToIdentity(
        _options.DefaultConfigHash, _options.DefaultCapabilityFingerprint, _options.SdkPinIdentityComponent,
        _options.RestoreIdentityComponent).Hash;

    private (EnsureSnapshotRequest Request, string RepositoryKey) PinEnsureRepositorySpelling(
        EnsureSnapshotRequest request)
    {
        var repositoryKey = RemoteUrlIdentity.Normalize(request.RepositoryRemoteUrl);
        var (catalogUrl, existingUrl) = ReadCatalog(conn => CatalogSpellings(conn, request, repositoryKey));

        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfStopping();
            if (repositoryKey.Length == 0)
                return (request, repositoryKey);

            var spelling = existingUrl
                ?? catalogUrl
                ?? _ensureRepositorySpellings.GetValueOrDefault(repositoryKey)
                ?? request.RepositoryRemoteUrl;
            _ensureRepositorySpellings[repositoryKey] = spelling;
            return (request with { RepositoryRemoteUrl = spelling }, repositoryKey);
        }
    }

    // The repository spellings an ensure of `request` is pinned to: the catalog's spelling, and the spelling of an
    // existing job or snapshot of the same identity (which wins). /control/resolve reads the same pair, so its
    // current_identity_hash is the identity the resolving caller's ensure builds.
    private (string? CatalogUrl, string? ExistingUrl) CatalogSpellings(
        SqliteConnection conn, EnsureSnapshotRequest request, string repositoryKey)
    {
        var snapshots = new SnapshotStore(conn);
        var repositoryId = snapshots.GetRepositoryId(request.RepositoryRemoteUrl);
        var catalog = repositoryId is long id ? snapshots.GetRepositoryRemoteUrl(id) : null;
        var jobs = new SnapshotJobStore(conn);
        bool Exists(string spelling)
        {
            var hash = IdentityHashFor(request with { RepositoryRemoteUrl = spelling });
            return jobs.GetJobByIdentity(hash) is not null
                || snapshots.GetByIdentityHash(hash) is { } snapshot && snapshot.RepositoryId == repositoryId;
        }
        if (Exists(request.RepositoryRemoteUrl))
            return (catalog, request.RepositoryRemoteUrl);
        if (catalog is not null && Exists(catalog))
            return (catalog, catalog);
        foreach (var spelling in jobs.RepositorySpellings(repositoryKey, request.CommitSha))
            if (Exists(spelling))
                return (catalog, spelling);
        if (repositoryId is long repository)
            foreach (var spelling in snapshots.ProjectRepositorySpellings(repository, request.CommitSha))
                if (RemoteUrlIdentity.Normalize(spelling) == repositoryKey && Exists(spelling))
                    return (catalog, spelling);
        return (catalog, (string?)null);
    }

    // Starts one ensure as a service-owned background operation and returns (accepted, completion, identity hash): the
    // completion is the ensure's final result; accepted settles as soon as the job is registered (wait=false).
    // The operation is tracked so Dispose can drain it, and neither task can ever surface as an unobserved
    // exception when no caller is left waiting (the caller disconnected). Admission is atomic with Dispose's
    // drain snapshot (both under _inFlightLock): an operation either starts before shutdown closes admission
    // and is drained, or is refused, so none can run on against a released lease or a closed catalog.
    private (Task<EnsureSnapshotResult> Accepted, Task<EnsureSnapshotResult> Completion, string Hash,
        Task<EnsureAdmissionOutcome> Persisted, long Sequence, string? AdmissionId) StartEnsureOperation(
        EnsureSnapshotRequest request, AuditCaller principal, long? replaySeq = null, long? replayAdmittedAt = null)
    {
        // SVC-6/7: a malformed branch guard is refused before any job exists. The host validates first and
        // answers 400; this protects direct callers.
        if (request.BranchGuardProblem() is { } problem)
            throw new ArgumentException($"The ensure request's branch guards are invalid ({problem}).", nameof(request));
        // The remote-default lookup (issue #199) is not part of the identity, so the hash is known before that lookup runs.
        var pinned = PinEnsureRepositorySpelling(request);
        request = pinned.Request;
        var repositoryKey = pinned.RepositoryKey;
        var hash = IdentityHashFor(request);
        var convergence = NeedsBranchConvergence(request);
        var unorderedAttach = replaySeq is null && CanAttachWithoutAdmission(request, hash);
        var persist = replaySeq is null ? !unorderedAttach : convergence;
        var candidateAdmissionId = convergence && !unorderedAttach && replaySeq is null
            ? Guid.NewGuid().ToString("N")
            : null;
        var accepted = new TaskCompletionSource<EnsureSnapshotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EnsureSnapshotResult> completion;
        Task<EnsureAdmissionOutcome> persisted;
        AdvanceTicket? ticket = null;
        string? defaultEnsureKey = null;
        var admissionKey = EnsureAdmissionKey(request);
        // Issue #273 (D2): a branch-moving remote ensure decides whether to share an identical earlier ensure in its own
        // admission transaction, from branch_intents ids. Every other ensure (no intent) keeps the in-process barrier.
        // Only a live request can share: a replayed or rescheduled queued ensure is durable, and anything durable takes its
        // own turn, so that decision is never re-derived from neighbouring rows that retention may have pruned.
        var durableShare = persist && convergence && replaySeq is null;
        long seq;
        EnsureShareCandidate? shareCandidate = null;
        Task<EnsureSnapshotResult>? shareCompletion = null;
        Task<EnsureSnapshotResult>? shareAccepted = null;
        Task<EnsureAdmissionOutcome> admission;
        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfStopping();
            var barrier = Interlocked.Read(ref _ensureAdmissionBarrier);
            if (_ensureAdmissions.TryGetValue(admissionKey, out var duplicate) && duplicate.Request == request
                && duplicate.Principal.Actor == principal.Actor
                && duplicate.Principal.UserTenant == principal.UserTenant && duplicate.Principal.UserSubject == principal.UserSubject
                && !duplicate.Completion.IsCompleted)
            {
                if (durableShare)
                {
                    var candidateCompletion = duplicate.Completion;
                    shareCandidate = new EnsureShareCandidate(duplicate.Admission, () => !candidateCompletion.IsCompleted);
                    shareCompletion = duplicate.Completion;
                    shareAccepted = duplicate.Accepted;
                }
                // A durable (intent-backed) replayed ensure never shares; only ensures without an intent use the barrier.
                else if (!(persist && convergence) && duplicate.WriteAdmission == barrier)
                {
                    _duplicateEnsureAdmissions++;
                    if (replaySeq is long replay)
                    {
                        _metrics.RecordQueuedEnsureDuplicate(replay, hash, IsDefaultBranchWork(request),
                            replayAdmittedAt ?? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                        _liveQueuedEnsures.Add(replay);
                        _ = duplicate.Completion.ContinueWith(t =>
                            {
                                lock (_inFlightLock)
                                    _liveQueuedEnsures.Remove(replay);
                                ForgetQueuedEnsure(replay, t, finishMetricAfterCleanup: true);
                            },
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    Log($"queued_ensure_duplicate seq={replaySeq?.ToString() ?? "live"} retained_seq={duplicate.Seq}");
                    var duplicateAccepted = _inFlight.TryGetValue(hash, out var producing)
                        ? Task.FromResult(PendingResult(producing.JobId, hash, duplicate.AdmissionId))
                        : AsAttached(duplicate.Accepted);
                    return (duplicateAccepted, AsAttached(duplicate.Completion), hash, duplicate.Admission,
                        duplicate.Seq, duplicate.AdmissionId);
                }
            }
            // Issue #158: the ensure takes its turn on the writer now, in submission order. A user ensure that must
            // first look up the remote's default branch (issue #199) takes its turn once that lookup has finished.
            // Issue #403: an ensure that claims the default branch (only the application may; a user ensure with
            // default_branch is refused) takes the expedited lane, so it never waits behind feature-branch work.
            var expedited = IsDefaultBranchWork(request);
            var needsDefaultLookup = request.RestrictsImplicitDefault && !request.SuppressesBranchUpdate
                && !RepositoryHasDefaultBranch(request.RepositoryRemoteUrl);
            // Issue #273: a replayed intent-backed ensure reserves no turn at admission. Its order is its branch intent,
            // and its admission may retry indefinitely, which must never hold a granted writer turn; it takes its turn
            // once admitted (as the #199 lookup path does).
            var turn = needsDefaultLookup || (replaySeq is not null && persist)
                ? null
                : TakeWriteTurnLocked(out _, expedited: expedited,
                    ensureAdmission: true);
            if (turn is not null)
                ticket = AdmitAdvanceLocked(request, hash, expedited: expedited);
            if (!request.SuppressesBranchUpdate)
            {
                defaultEnsureKey = RemoteUrlIdentity.Normalize(request.RepositoryRemoteUrl) + "\n" + (request.BranchName ?? "main");
                _pendingDefaultEnsures[defaultEnsureKey] = _pendingDefaultEnsures.GetValueOrDefault(defaultEnsureKey) + 1;
            }
            // Issue #406: the admission sequence orders a restart's replay; a replayed ensure keeps its persisted row.
            // The row is written on the grant connection (never the write gate) as part of admission: the wait=false
            // answer is held until it is durable, and the ensure's completion always follows it.
            seq = replaySeq ?? ++_ensureSeq;
            _ensureSeq = Math.Max(_ensureSeq, seq);
            // Issue #273 (B1): the rescan never starts a second operation for a queued ensure that is live here.
            _liveQueuedEnsures.Add(seq);
            var admittedSeq = seq;
            var admittedAt = replayAdmittedAt ?? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            // A possible share is not counted as queued until its transaction admits it on its own.
            if (shareCandidate is null)
                _metrics.RecordQueuedEnsureAdmission(seq, hash, expedited, admittedAt);
            else
                _metrics.RecordQueuedEnsureDuplicate(seq, hash, expedited, admittedAt);
            Action onAdmitted = shareCandidate is null
                ? () => { }
                : () => _metrics.RecordQueuedEnsureAdmission(admittedSeq, hash, expedited, admittedAt);
            ProgressForTesting?.Invoke();
            if (!persist)
                admission = Task.FromResult(new EnsureAdmissionOutcome(seq, candidateAdmissionId));
            else if (replaySeq is not null)
                // A replayed queued ensure is started only by its owner, after its admission committed.
                admission = Task.FromResult(new EnsureAdmissionOutcome(seq, null));
            else
                admission = Task.Run(async () =>
                {
                    var shareWithSeq = await ShareTargetAsync(shareCandidate).ConfigureAwait(false);
                    return PersistQueuedEnsure(admittedSeq, request, hash, principal, admittedAt,
                        bestEffort: !convergence, admissionId: candidateAdmissionId,
                        shareWithSeq: shareWithSeq, shareLive: shareCandidate?.Live, onAdmitted: onAdmitted);
                }, CancellationToken.None);
            persisted = admission;
            if (ticket is not null)
            {
                ticket.Persisted = persisted;
            }
            var rowWritten = admission;
            completion = Task.Run(async () =>
            {
                EnsureAdmissionOutcome admitted;
                try
                {
                    admitted = await rowWritten.ConfigureAwait(false);
                }
                catch
                {
                    if (turn is not null)
                        ReleaseTurn(turn, granted: false);
                    throw;
                }
                if (admitted.SharedWith(admittedSeq))
                {
                    // The admission transaction found the identical ensure still the branch's newest intent: this
                    // request shares its operation and result, with no intent, worker run or writer turn of its own.
                    if (turn is not null)
                        ReleaseTurn(turn, granted: false);
                    Interlocked.Increment(ref _duplicateEnsureAdmissions);
                    Log($"queued_ensure_duplicate seq=live retained_seq={admitted.Seq}");
                    ForwardSharedAcceptance(accepted, hash, admitted.AdmissionId, shareAccepted!);
                    return await AsAttached(shareCompletion!).ConfigureAwait(false);
                }
                try
                {
                    var result = await RunEnsureAsync(request, hash, principal, accepted, turn, ticket, admittedSeq).ConfigureAwait(false);
                    if (!SnapshotJobStatus.IsSettled(result.Status) && !_lifetime.IsCancellationRequested && !LeaseLost)
                        DeferQueuedEnsure(admittedSeq, $"ensure_{result.Status}");
                    return result;
                }
                catch (Exception ex)
                {
                    if (!_lifetime.IsCancellationRequested && !LeaseLost)
                        DeferQueuedEnsure(admittedSeq, $"turn_{ex.GetType().Name}");
                    throw;
                }
            }, CancellationToken.None);
            _operations.Add(completion);
            _pendingEnsures[hash] = _pendingEnsures.GetValueOrDefault(hash) + 1;
            _ensureAdmissions[admissionKey] = new(
                request, principal, seq, barrier, candidateAdmissionId, accepted.Task, completion, admission);
        }
        completion.ContinueWith(
            t =>
            {
                lock (_inFlightLock)
                {
                    _metrics.RecordQueuedEnsureFinished(seq);
                    _operations.Remove(t);
                    _liveQueuedEnsures.Remove(seq);
                    if (_ensureAdmissions.TryGetValue(admissionKey, out var pending) && pending.Completion == t)
                        _ensureAdmissions.Remove(admissionKey);
                    if (repositoryKey.Length > 0
                        && _ensureRepositorySpellings.TryGetValue(repositoryKey, out var spelling)
                        && string.Equals(spelling, request.RepositoryRemoteUrl, StringComparison.Ordinal))
                        _ensureRepositorySpellings.Remove(repositoryKey);
                    EndPendingEnsureLocked(hash);
                    if (defaultEnsureKey is not null)
                        EndPendingDefaultEnsureLocked(defaultEnsureKey);
                    if (ticket is not null)
                    {
                        ticket.Ended = true;
                        ticket.Produced = t.IsCompletedSuccessfully && !IsUnbuiltOutcome(t.Result.Status);
                    }
                    if (ticket is not null
                        && _latestAdvance.TryGetValue(ticket.BranchKey, out var latest) && ReferenceEquals(latest, ticket))
                        _latestAdvance.Remove(ticket.BranchKey);
                }
                if (!t.IsCompletedSuccessfully)
                    ForgetQueuedEnsure(seq, t);
                SettleAccepted(accepted, t);
                ProgressForTesting?.Invoke();
                if (defaultEnsureKey is not null)
                    SchedulePendingRefreshes();
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return (accepted.Task, completion, hash, persisted, seq, candidateAdmissionId);
    }

    // A request that shares an identical ensure answers its wait=false acceptance from that ensure's: the running
    // production's job at once, or the shared acceptance once it settles (its final result settles it otherwise).
    private void ForwardSharedAcceptance(
        TaskCompletionSource<EnsureSnapshotResult> accepted, string hash, string? admissionId,
        Task<EnsureSnapshotResult> shared)
    {
        InFlightProduction? producing;
        lock (_inFlightLock)
            _inFlight.TryGetValue(hash, out producing);
        if (producing is not null)
        {
            accepted.TrySetResult(PendingResult(producing.JobId, hash, admissionId));
            return;
        }
        _ = shared.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    accepted.TrySetResult(t.Result with { Attached = true, AdmissionId = t.Result.AdmissionId ?? admissionId });
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task<EnsureSnapshotResult> AsAttached(Task<EnsureSnapshotResult> result) =>
        (await result.ConfigureAwait(false)) with { Attached = true };

    private AdvanceTicket? AdmitAdvanceLocked(
        EnsureSnapshotRequest request, string hash, bool expedited)
    {
        if (!IsBranchAdvance(request))
            return null;

        var key = RemoteUrlIdentity.Normalize(request.RepositoryRemoteUrl) + "\n" + request.BranchName;
        var ticket = new AdvanceTicket
        {
            BranchKey = key,
            Hash = hash,
            CommitSha = request.CommitSha,
            ExpectedHeadCommit = request.ExpectedHeadCommit!,
            Expedited = expedited
        };
        if (!NeedsBranchConvergence(request)
            && _latestAdvance.TryGetValue(key, out var previous) && !previous.Started
            && !string.Equals(previous.Hash, hash, StringComparison.Ordinal)
            && string.Equals(previous.CommitSha, ticket.ExpectedHeadCommit, StringComparison.OrdinalIgnoreCase))
            previous.SupersededBy = ticket;
        _latestAdvance[key] = ticket;
        return ticket;
    }

    private bool WillBuildFeatureBranchHead(AdvanceTicket predecessor, AdvanceTicket successor)
    {
        if (_lifetime.IsCancellationRequested || (successor.Ended && !successor.Produced))
            return false;
        return successor.Expedited || !predecessor.Expedited;
    }

    // An ensure that ended without a snapshot serving its branch head (it threw, was cancelled, or its job failed).
    private static bool IsUnbuiltOutcome(string status) =>
        status is SnapshotJobStatus.Failed or SnapshotJobStatus.Unsupported or SnapshotJobStatus.Cancelled;

    private bool IsCatalogDefaultBranch(EnsureSnapshotRequest request, SnapshotStore snapshots)
    {
        if (request.SuppressesBranchUpdate || request.BranchName is not { Length: > 0 })
            return false;
        var repositoryId = snapshots.GetRepositoryId(request.RepositoryRemoteUrl);
        if (repositoryId is not long id || snapshots.GetDefaultBranchId(id) is not long defaultBranchId)
            return false;
        return snapshots.GetBranchById(defaultBranchId) is { } branch
            && string.Equals(branch.Name, request.BranchName, StringComparison.Ordinal);
    }

    // An ensure that advances its named branch under an expected_head_commit compare-and-swap. Default-branch
    // refreshes and feature-branch linked successors both begin with this request shape.
    private static bool IsBranchAdvance(EnsureSnapshotRequest request) =>
        request.BranchName is { Length: > 0 } && !request.SuppressesBranchUpdate && request.ExpectedHeadCommit is not null
        && request.BranchHeadSequence is null && request.RebuildGeneration is null;

    private const int MaxHandoverHops = 64;

    private EnsureSnapshotRequest ResolveLinkedSuccessor(EnsureSnapshotRequest request)
    {
        if (!IsBranchAdvance(request))
            return request;
        var snapshots = new SnapshotStore(_conn);
        var jobs = new SnapshotJobStore(_conn);
        var repositoryId = snapshots.GetRepositoryId(request.RepositoryRemoteUrl);
        var branchId = repositoryId is long repoId ? snapshots.GetBranchId(repoId, request.BranchName!) : null;
        var repositoryKey = RemoteUrlIdentity.Normalize(request.RepositoryRemoteUrl);
        var expected = request.ExpectedHeadCommit!;
        var successor = request.CommitSha;
        for (var hop = 0;
             hop < MaxHandoverHops && !snapshots.BranchHeadMatches(branchId, expected, repositoryId, request.BranchName);
             hop++)
        {
            if (jobs.GetHandover(repositoryKey, request.BranchName!, expected, successor) is not { } handedOver)
                break;
            successor = expected;
            expected = handedOver;
        }
        return string.Equals(expected, request.ExpectedHeadCommit, StringComparison.Ordinal)
            ? request
            : request with { ExpectedHeadCommit = expected };
    }

    // One ensure of the identity has finished (the caller holds _inFlightLock). When it was the identity's last, no
    // ensure is left to register a reserved id, so the reservation is dropped: after a successful registration it is
    // already gone, and after a failure (the writer lease lost, a failed insert) its id then reads 404, the documented
    // signal to ensure again, instead of queued forever. A reserved id is never reused (JobIdReservations).
    private void EndPendingEnsureLocked(string hash)
    {
        if (_pendingEnsures.GetValueOrDefault(hash) > 1)
        {
            _pendingEnsures[hash]--;
            return;
        }
        _pendingEnsures.Remove(hash);
        _jobIds.Release(hash);
    }

    private void EndPendingDefaultEnsureLocked(string key)
    {
        if (_pendingDefaultEnsures.GetValueOrDefault(key) > 1)
            _pendingDefaultEnsures[key]--;
        else
            _pendingDefaultEnsures.Remove(key);
    }

    // Drops an identity's id reservation unless an ensure that may still register it is pending (issue #158).
    private void ReleaseUnlessPending(string hash)
    {
        lock (_inFlightLock)
        {
            if (!_pendingEnsures.ContainsKey(hash))
                _jobIds.Release(hash);
        }
    }

    // Issue #158: queues a turn on the single writer for a control write (an ensure or a retire). The caller holds
    // _inFlightLock, so turns are taken in admission order, and the write gate grants waiting turns first-in,
    // first-out (SemaphoreSlim releases its asynchronous waiters in the order they queued), so writes apply in
    // submission order: a branch delete's retire is never overtaken by the re-create push's ensure that followed it.
    // The one exception is the idle-branch retirement (issue #333), which takes the priority lane and so can run before an
    // earlier-admitted ensure; ApplyRetireAsync therefore answers it BranchBusy while a branch-advance ensure of the same
    // repository and branch is still admitted (_latestAdvance), and the idle rule never deletes a branch with such work.
    // The wait is not tied to the service lifetime, so a write admitted before shutdown still lands during the
    // Dispose drain. The returned turn MUST be awaited and the gate released exactly once on every path, or the
    // writer stalls for good (ReleaseTurn covers a turn abandoned before it was granted).
    private Task TakeWriteTurnLocked() => TakeWriteTurnLocked(out _);

    private Task TakeWriteTurnLocked(out long admission, bool priority = false, bool expedited = false, bool ensureAdmission = false)
    {
        if (!ensureAdmission)
            Interlocked.Increment(ref _ensureAdmissionBarrier);
        admission = Interlocked.Increment(ref _writeAdmissions);
        var turn = _writeGate.WaitAsync(CancellationToken.None, priority, expedited);
        ProgressForTesting?.Invoke();
        return turn;
    }

    private bool IsDefaultBranchWork(EnsureSnapshotRequest request) =>
        !request.SuppressesBranchUpdate && (request.IsDefaultBranch == true || ReadCatalog(conn =>
        {
            var snapshots = new SnapshotStore(conn);
            return snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is long repository
                && snapshots.GetBranch(repository, request.BranchName ?? "main") is { IsDefault: true };
        }));

    private static bool IsAutomaticDefaultRepair(EnsureSnapshotRequest request, AuditCaller principal) =>
        principal.Actor == UpstreamPolicyCaller.Actor && request.IsDefaultBranch == true;

    // Releases a turn whether or not it has been granted yet.
    private void ReleaseTurn(Task turn, bool granted)
    {
        if (granted)
            _writeGate.Release();
        else
            turn.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully)
                        _writeGate.Release();
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForBranchIntentHeadAsync(
        long repositoryId, string branch, long intentId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = ReadCatalog(conn =>
            {
                var intents = new BranchIntentStore(conn);
                var intent = intents.Get(intentId);
                return (Intent: intent, Head: intent is null ? null : intents.Head(repositoryId, branch));
            });
            if (state.Intent is null || state.Intent.State == "settled")
                return;
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (state.Head?.Id == intentId)
            {
                if (!RetryPending(state.Intent.NextAttemptAt, now))
                    return;
                await Task.Delay(TimeSpan.FromMilliseconds(state.Intent.NextAttemptAt!.Value - now),
                    _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Task wakeup;
            lock (_inFlightLock)
            {
                if (!_branchIntentWakeups.TryGetValue((repositoryId, branch), out var signal))
                    _branchIntentWakeups[(repositoryId, branch)] = signal =
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wakeup = signal.Task;
            }

            state = ReadCatalog(conn =>
            {
                var intents = new BranchIntentStore(conn);
                var intent = intents.Get(intentId);
                return (Intent: intent, Head: intent is null ? null : intents.Head(repositoryId, branch));
            });
            if (state.Intent is null || state.Intent.State == "settled")
                return;
            if (state.Head?.Id == intentId)
                continue;
            await wakeup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void WakeBranchIntent(long repositoryId, string branch)
    {
        TaskCompletionSource? signal;
        lock (_inFlightLock)
            _branchIntentWakeups.Remove((repositoryId, branch), out signal);
        signal?.TrySetResult();
    }

    // Service shutdown refuses new work with an OperationCanceledException on the SERVICE lifetime token, which
    // the host maps to 503 (the caller's own token is not cancelled).
    private void ThrowIfStopping()
    {
        if (_lifetime.IsCancellationRequested)
            throw new OperationCanceledException(ShutdownMessage, _lifetime.Token);
    }

    private const string ShutdownMessage =
        "The index service is shutting down; retry the ensure against the restarted service.";

    private static void SettleAccepted(
        TaskCompletionSource<EnsureSnapshotResult> accepted, Task<EnsureSnapshotResult> completion)
    {
        if (completion.IsCompletedSuccessfully)
            accepted.TrySetResult(completion.Result);
        else if (completion.IsCanceled)
            accepted.TrySetCanceled();
        else
            accepted.TrySetException(completion.Exception!.InnerExceptions);

        // Observe both: the job's outcome is already durable (MarkResult/Requeue), and a disconnected caller
        // must never turn it into an UnobservedTaskException.
        _ = completion.Exception;
        _ = accepted.Task.Exception;
    }

    // Awaits a service-owned task on behalf of ONE caller. Cancelling the caller's token ends only this wait
    // (throwing OperationCanceledException) and never the task itself; once the task has completed its own
    // outcome — result or original exception — is returned/rethrown unchanged.
    private static async Task<T> WaitForCallerAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!task.IsCompleted)
        {
            await ((Task)task).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!task.IsCompleted)
                cancellationToken.ThrowIfCancellationRequested();
        }
        return await task.ConfigureAwait(false);
    }

    private async Task<EnsureSnapshotResult> RunEnsureAsync(
        EnsureSnapshotRequest request, string hash, AuditCaller principal,
        TaskCompletionSource<EnsureSnapshotResult> accepted, Task? turn, AdvanceTicket? ticket, long seq)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("ensure_snapshot");
        activity?.SetTag("sextant.repository", request.RepositoryRemoteUrl);
        activity?.SetTag("sextant.commit", request.CommitSha);

        var result = await EnsureSnapshotCoreAsync(request, hash, principal, accepted, turn, ticket, seq).ConfigureAwait(false);
        if (NeedsBranchConvergence(request))
        {
            var admissionId = ReadCatalog(conn => new BranchIntentStore(conn).GetByQueuedEnsureId(seq)?.AdmissionId);
            result = result with { AdmissionId = admissionId ?? result.AdmissionId };
        }

        _metrics.RecordEnsure(result.Attached);
        activity?.SetTag("sextant.status", result.Status);
        activity?.SetTag("sextant.attached", result.Attached);
        return result;
    }

    // Issue #199: a caller that may not pick its repository's default (RestrictsImplicitDefault) gets the #104
    // first-branch default only for the branch the REMOTE names as its default. The service looks that up itself,
    // outside the write gate, and only when the answer can matter: the repository has no default branch yet (the only
    // time the safety net can apply) and the ensure may move a pointer (not `branch_update: none`). The lookup is shared,
    // remembered briefly and capped (RemoteDefaultBranchLookup). Any value a direct caller supplied is discarded. No
    // resolver (locate mode), a failed lookup or a full cap leaves it unset, so the branch is created without the
    // default (fail closed). So does shutdown, which stops waiting for the lookup: the ensure still registers its job
    // (issue #158), because a wait=false caller may already hold its id.
    private async Task<EnsureSnapshotRequest> WithVerifiedRemoteDefaultAsync(EnsureSnapshotRequest request)
    {
        if (!request.RestrictsImplicitDefault)
            return request;
        var unverified = request with { VerifiedRemoteDefaultBranch = null };
        if (request.SuppressesBranchUpdate || _remoteDefaults is not { } lookup || RepositoryHasDefaultBranch(request.RepositoryRemoteUrl))
            return unverified;
        try
        {
            var branch = await lookup.ResolveAsync(
                request.RepositoryRemoteUrl, request.LookupTenant, _lifetime.Token).WaitAsync(_lifetime.Token).ConfigureAwait(false);
            return request with { VerifiedRemoteDefaultBranch = branch };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || _lifetime.IsCancellationRequested)
        {
            return unverified;
        }
    }

    /// <summary>
    /// Issue #203: the intake reason code when a user caller's branch advance targets a commit that is not the
    /// remote's current <c>refs/heads/&lt;branch&gt;</c>, or <c>null</c> when it may proceed. The check is the
    /// service's own <c>git ls-remote</c> (never the request), so a user CAS cannot move a branch to an older commit or a
    /// <c>refs/pull/*</c> head. It applies only in clone mode (an <see cref="IRemoteBranchHeadResolver"/> exists) and
    /// only to an ensure that may move a pointer; an unavailable lookup or a full slot returns the retryable
    /// <see cref="BranchGuardReason.LookupBusy"/>, never a verdict on the commit. Like the #199
    /// lookup, it can make the ensure wait up to the lookup timeout (30 s), a <c>wait=false</c> caller included; the
    /// <c>branch_update: none</c> reachability check (#209) can hold it up to 2 minutes, because it runs before the job is
    /// registered. Cancelling the caller's token stops the remote call.
    /// </summary>
    public async Task<string?> UserAdvanceProblemAsync(EnsureSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Refresh == true && !request.SuppressesBranchUpdate)
            return null;
        if (request.SuppressesBranchUpdate)
            return await UserPinnedCommitProblemAsync(request, cancellationToken).ConfigureAwait(false);
        if (_remoteDefaults is not { HasBranchHeadResolver: true } lookup)
            return null;
        if (string.IsNullOrWhiteSpace(request.BranchName) || string.IsNullOrWhiteSpace(request.CommitSha))
            return BranchGuardReason.CommitNotRemoteHead;
        try
        {
            var head = await lookup.ObserveBranchHeadForIntakeAsync(request.RepositoryRemoteUrl, request.BranchName, request.LookupTenant, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (head is null)
                return BranchGuardReason.LookupBusy;
            return string.Equals(head, request.CommitSha, StringComparison.OrdinalIgnoreCase)
                ? null
                : BranchGuardReason.CommitNotRemoteHead;
        }
        catch (RemoteLookupBusyException)
        {
            return BranchGuardReason.LookupBusy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BranchGuardReason.LookupBusy;
        }
    }

    // Issue #209: a user's `branch_update: none` ensure pins a caller-chosen commit that the worker fetches and
    // evaluates. On GitHub a base repository serves fork-network commits (refs/pull/*, shared objects), so the commit
    // must be reachable from a refs/heads/* branch as the service itself fetches the branches (clone mode only; in
    // locate mode nothing is fetched). A blank commit is refused; an unavailable check is retryable.
    private async Task<string?> UserPinnedCommitProblemAsync(EnsureSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (_remoteDefaults is not { HasCommitBranchChecker: true } lookup)
            return null;
        if (string.IsNullOrWhiteSpace(request.CommitSha))
            return BranchGuardReason.CommitNotOnBranch;
        try
        {
            return await lookup.IsCommitOnBranchAsync(request.RepositoryRemoteUrl, request.CommitSha.Trim(), request.LookupTenant, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false)
                ? null
                : BranchGuardReason.CommitNotOnBranch;
        }
        catch (RemoteLookupBusyException)
        {
            return BranchGuardReason.LookupBusy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BranchGuardReason.LookupBusy;
        }
    }

    private bool RepositoryHasDefaultBranch(string repositoryRemoteUrl) => ReadCatalog(conn =>
    {
        var snapshots = new SnapshotStore(conn);
        return snapshots.GetRepositoryId(repositoryRemoteUrl) is long repoId && snapshots.GetDefaultBranchId(repoId) is not null;
    });

    // The idempotent-ensure core. Every durable step (and the request's audit row) runs under the single write gate,
    // on this ensure's turn (issue #158: turns are granted in submission order); the only long step — the worker — is
    // a shared, service-owned production per identity, which runs on the turn of the ensure that started it.
    private async Task<EnsureSnapshotResult> EnsureSnapshotCoreAsync(
        EnsureSnapshotRequest request, string hash, AuditCaller principal,
        TaskCompletionSource<EnsureSnapshotResult> accepted, Task? turn, AdvanceTicket? ticket, long seq)
    {
        var granted = false;
        try
        {
            return await ExecuteAsync().ConfigureAwait(false);
        }
        finally
        {
            if (turn is not null)
                ReleaseTurn(turn, granted);
        }

        async Task<EnsureSnapshotResult> ExecuteAsync()
        {
            if (turn is null)
            {
                request = await WithVerifiedRemoteDefaultAsync(request).ConfigureAwait(false);
                lock (_inFlightLock)
                {
                    var expedited = IsDefaultBranchWork(request);
                    turn = TakeWriteTurnLocked(out _, expedited: expedited, ensureAdmission: true);
                }
            }

            var ensureIntent = NeedsBranchConvergence(request)
                ? ReadCatalog(conn => new BranchIntentStore(conn).GetByQueuedEnsureId(seq))
                : null;
            if (ensureIntent is not null)
            {
                var atHead = ReadCatalog(conn =>
                {
                    var intents = new BranchIntentStore(conn);
                    return intents.Head(ensureIntent.RepositoryId, ensureIntent.Branch)?.Id == ensureIntent.Id
                        && !RetryPending(intents.Get(ensureIntent.Id)?.NextAttemptAt,
                            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                });
                if (!atHead)
                {
                    ReleaseTurn(turn, granted: false);
                    turn = null;
                    await WaitForBranchIntentHeadAsync(
                        ensureIntent.RepositoryId, ensureIntent.Branch, ensureIntent.Id, _lifetime.Token).ConfigureAwait(false);
                    lock (_inFlightLock)
                        turn = TakeWriteTurnLocked(out _, expedited: IsDefaultBranchWork(request), ensureAdmission: true);
                }
            }

            // The identity is already producing: report its job at once, so a re-ensure — e.g. the retry of a
            // caller that timed out — returns its job id immediately and never starts a second worker. Shutdown
            // began: never attach to (or report as running) a production that is being cancelled.
            var inFlight = FindInFlight(hash);
            if (inFlight is not null)
            {
                ThrowIfStopping();
                accepted.TrySetResult(PendingResult(inFlight.JobId, hash, ensureIntent?.AdmissionId));
            }

            await turn.ConfigureAwait(false);
            granted = true;
            using var owner = EnterWriterTurn(turn);
            await _replayAdmissions.Task.ConfigureAwait(false);
            _metrics.RecordQueuedEnsureStarted(seq);
            EnsureLeaseHeld();

            // From its turn on, this ensure can no longer be superseded. Locate mode retains linked handovers
            // on every branch; remote mode converges through its durable refresh marker instead.
            AdvanceTicket? supersededBy = null;
            if (ticket is not null)
            {
                lock (_inFlightLock)
                {
                    ticket.Started = true;
                    if (_pendingEnsures.GetValueOrDefault(hash) == 1
                        && ticket.SupersededBy is { } successor
                        && WillBuildFeatureBranchHead(ticket, successor))
                        supersededBy = successor;
                }
            }
            if (ticket is not null)
                await ticket.Persisted.ConfigureAwait(false);
            if (!NeedsBranchConvergence(request))
                request = ResolveLinkedSuccessor(request);
            if (inFlight is not null)
            {
                // A production holds the writer for its whole run and deregisters before releasing it, so it has
                // finished by the time this turn is granted. A terminal outcome is re-attached through the terminal
                // path below, so THIS request's own branch pointer is advanced/attached (the identity excludes the
                // branch, so the producing request's branch may differ). A non-terminal (transient requeue) outcome is
                // shared as-is rather than immediately re-running the worker.
                var produced = await inFlight.Production.ConfigureAwait(false);
                if (!SnapshotJobStatus.IsTerminal(produced.Status))
                {
                    // The branch decision in `produced` was the producing request's, not this one's.
                    var shared = produced with { Attached = true, BranchAdvanced = null };
                    RecordEnsureAuditLocked(request, shared, principal);
                    return shared;
                }
            }

            // Idempotent attach: create-or-return the ONE durable job for this identity (criterion 1), and in the
            // SAME write check whether a terminal result is still backed by durable data (a complete job whose
            // snapshot retention has since reclaimed must NOT be reported complete forever). A usable terminal result
            // is attached — branch pointer advanced — in that SAME gate hold: releasing the gate between the check
            // and the advance would let a concurrent ensure supersede the snapshot in between, leaving the branch
            // head on a Superseded snapshot (issue #85).
            if (NeedsBranchConvergence(request) && ensureIntent is null)
            {
                var terminal = new SnapshotJobStore(_conn).GetJobByIdentity(hash);
                if (terminal is null || terminal.SnapshotId is not null
                    || !SnapshotJobStatus.IsTerminal(terminal.Status)
                    || !TerminalResultUsable(terminal, hash, new SnapshotStore(_conn), new SnapshotJobStore(_conn)))
                    throw new InvalidOperationException("An unadmitted remote ensure cannot attach a snapshot or produce.");
            }
            var (job, existed, attached) = TryAttachTerminal(request, hash);
            if (attached is not null)
            {
                var (repaired, refusal) = ensureIntent is null
                    ? (attached, null)
                    : await RepairVerifiedDefaultHeadAsync(request, attached).ConfigureAwait(false);
                attached = repaired;
                RecordEnsureAuditLocked(request, attached, principal, refusal);
                DeleteSettledQueuedEnsureLocked(seq, attached);
                return attached with { AdmissionId = ensureIntent?.AdmissionId };
            }

            accepted.TrySetResult(PendingResult(job, existed, ensureIntent?.AdmissionId));

            if (!NeedsBranchConvergence(request) && supersededBy is not null && inFlight is null
                && string.Equals(supersededBy.ExpectedHeadCommit, request.CommitSha, StringComparison.OrdinalIgnoreCase))
            {
                var coalesced = CoalesceFeatureBranchLocked(request, job, existed, supersededBy, principal);
                DeleteSettledQueuedEnsureLocked(seq, coalesced);
                return coalesced;
            }

            // Shutdown began: the job stays durably queued for the next instance to produce.
            ThrowIfStopping();

            var production = StartProduction(request, hash, job.Id, existed, principal, seq, isRefresh: false);
            return await production.ConfigureAwait(false);
        }
    }

    // Terminal-attach step, run under the write gate: registers (or attaches to) the identity's durable job and,
    // when its terminal result is still usable, advances/attaches this request's branch pointer and records the
    // request's audit row in the SAME gate hold. Returns the attach result, or null when production is needed.
    private (SnapshotJobRow Job, bool Existed, EnsureSnapshotResult? Attached) TryAttachTerminal(
        EnsureSnapshotRequest request, string hash)
    {
        var jobs = new SnapshotJobStore(_conn);
        var (row, wasExisting) = RegisterJobLocked(jobs, hash, request.RepositoryRemoteUrl, request.CommitSha, request.BranchName);
        // The row committed (this write is not inside a transaction), so a status read now finds it.
        _jobIds.Release(hash);
        if (!SnapshotJobStatus.IsTerminal(row.Status) || !TerminalResultUsable(row, hash, new SnapshotStore(_conn), jobs))
            return (row, wasExisting, null);
        var advanced = AdvanceOrAttachBranchPointer(request, row.SnapshotId);
        var attached = Attach(row, wasExisting, CoverageFor(row.SnapshotId)) with { BranchAdvanced = advanced };
        return (row, wasExisting, attached);
    }

    private EnsureSnapshotResult CoalesceFeatureBranchLocked(
        EnsureSnapshotRequest request, SnapshotJobRow job, bool existed,
        AdvanceTicket successor, AuditCaller principal)
    {
        var jobs = new SnapshotJobStore(_conn);
        BeginRaw("coalesce-feature-branch");
        try
        {
            jobs.RecordHandover(
                RemoteUrlIdentity.Normalize(request.RepositoryRemoteUrl), request.BranchName!,
                request.CommitSha, request.ExpectedHeadCommit!, successor.CommitSha);
            jobs.MarkResult(job.Id, SnapshotJobStatus.Coalesced, snapshotId: null,
                $"superseded by linked feature-branch successor {successor.CommitSha}");
            ExecRaw("COMMIT;");
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
        var result = Attach(jobs.GetJob(job.Id)!, existed);
        RecordEnsureAuditLocked(request, result, principal);
        return result;
    }

    // Creates or returns the ONE job row for an identity, under the write gate. A new row takes its id from _jobIds
    // (issue #158), so it is the id a wait=false caller may already be polling, and no reserved id is ever taken by
    // another identity's row. The caller releases the reservation once the row has committed.
    private (SnapshotJobRow Row, bool Existed) RegisterJobLocked(
        SnapshotJobStore jobs, string hash, string repositoryUrl, string commitSha, string? branchName)
    {
        long? id = jobs.GetJobByIdentity(hash) is null
            ? _jobIds.ForRegistration(hash, repositoryUrl, commitSha, branchName)
            : null;
        return jobs.EnsureJob(hash, repositoryUrl, commitSha, branchName, id);
    }

    private InFlightProduction? FindInFlight(string hash)
    {
        lock (_inFlightLock)
            return _inFlight.GetValueOrDefault(hash);
    }

    // Starts (and registers) the identity's production on the write-gate turn the calling ensure holds. The
    // starter is the OWNER: the production runs with its request (branch,
    // sequence) and principal. Every production runs while holding the gate and deregisters before its release, so
    // none can be in flight while the caller holds the gate.
    private Task<EnsureSnapshotResult> StartProduction(
        EnsureSnapshotRequest request, string hash, long jobId, bool existed, AuditCaller principal, long? seq,
        bool isRefresh)
    {
        lock (_inFlightLock)
        {
            if (_inFlight.ContainsKey(hash))
                throw new InvalidOperationException(
                    "An identity's production was in flight while another ensure held the write gate; productions must hold it for their whole run.");

            // Registered under the lock BEFORE the production can deregister itself (it takes the same lock).
            var entry = new InFlightProduction(jobId);
            entry.Production = Task.Run(
                () => RunProductionAsync(request, hash, jobId, existed, principal, entry, seq, isRefresh), CancellationToken.None);
            _inFlight[hash] = entry;
            // After the production Task completed (not from inside its frame, which still roots the job's state) and,
            // issue #273, after the turn that started it released the writer: that ensure or refresh turn records its
            // result and releases the gate only after the production, so an earlier look would always find it busy.
            var startersHoldReleased = _writeGate.CurrentHoldReleased;
            ScheduleJobMemoryReclaim(Task.WhenAll(entry.Production, startersHoldReleased), jobId);
            return entry.Production;
        }
    }

    // A non-terminal snapshot of a job whose production is pending or running (the wait=false response): queued
    // while the production waits for the writer, running once the worker holds it. A stale terminal row about
    // to be requeued is reported queued, never as its phantom terminal status.
    private EnsureSnapshotResult PendingResult(SnapshotJobRow job, bool existed, string? admissionId = null) => new()
    {
        JobId = job.Id,
        IdentityHash = job.IdentityHash,
        Status = job.Status == SnapshotJobStatus.Running ? SnapshotJobStatus.Running : SnapshotJobStatus.Queued,
        Attached = existed,
        AdmissionId = admissionId
    };

    private EnsureSnapshotResult PendingResult(long jobId, string hash, string? admissionId = null)
    {
        var job = ReadCatalog(conn => new SnapshotJobStore(conn).GetJob(jobId));
        return job is not null
            ? PendingResult(job, existed: true, admissionId)
            : new EnsureSnapshotResult
            {
                JobId = jobId, IdentityHash = hash, Status = SnapshotJobStatus.Queued,
                Attached = true, AdmissionId = admissionId
            };
    }

    // One identity's shared production: runs on the write-gate turn of the ensure that started it (never waiting for
    // the writer again, so it applies at that ensure's place in submission order, issue #158) and on the SERVICE
    // lifetime (never a caller's token). It runs the worker, records the result and the owner's audit row, then
    // deregisters itself BEFORE the ensure releases the gate — so any request that can observe the new durable state (which
    // needs the gate) never attaches to this already-finished production.
    private async Task<EnsureSnapshotResult> RunProductionAsync(
        EnsureSnapshotRequest request, string hash, long jobId, bool existed, AuditCaller principal, InFlightProduction entry,
        long? seq, bool isRefresh)
    {
        try
        {
            EnsureLeaseHeld();
            var workRequest = IsAutomaticDefaultRepair(request, principal) || isRefresh
                ? request with { BranchUpdate = "none" }
                : request;
            var produced = await ProduceLockedAsync(workRequest, hash, jobId, existed).ConfigureAwait(false);
            var (result, refusal) = isRefresh || !NeedsBranchConvergence(request)
                ? (produced, null)
                : await RepairVerifiedDefaultHeadAsync(request, produced).ConfigureAwait(false);
            RecordEnsureAuditLocked(request, result, principal, refusal);
            if (seq is long admissionSeq)
                DeleteSettledQueuedEnsureLocked(admissionSeq, result);
            return result;
        }
        finally
        {
            lock (_inFlightLock)
            {
                if (_inFlight.TryGetValue(hash, out var current) && ReferenceEquals(current, entry))
                    _inFlight.Remove(hash);
            }
        }
    }

    // Issue #330: a worker's async chain completes INLINE, each frame's continuation running inside the completing
    // frame of the one below it, so the thread that finished the last extraction step goes on to run the rest of the
    // production (and the post-job reclaim's yield) with every one of those state machines still on its stack. The
    // reclaim, started on another pool thread at once, then found the job's whole Solution reachable (a GC root in
    // that stack) and kept 1.85 GB committed. The service continues on a fresh pool work item instead, so the worker's
    // frames unwind on their own thread and nothing of the job is on the stack the production continues on.
    private static async Task<T> ContinueOffWorkerStack<T>(Task<T> work)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = work.ContinueWith(
            static (_, state) => ((TaskCompletionSource)state!).TrySetResult(), finished,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await finished.Task.ConfigureAwait(false);
        // Awaiting the finished task rethrows the worker's own exception, exactly as awaiting it directly did.
        return await work.ConfigureAwait(false);
    }

    // The production critical section. The caller holds the write gate (and has checked the lease).
    private async Task<EnsureSnapshotResult> ProduceLockedAsync(
        EnsureSnapshotRequest request, string hash, long jobId, bool existed)
    {
        var jobs = new SnapshotJobStore(_conn);
        var snapshots = new SnapshotStore(_conn);

        var current = jobs.GetJob(jobId)!;
        if (SnapshotJobStatus.IsTerminal(current.Status) && TerminalResultUsable(current, hash, snapshots, jobs))
        {
            var terminalAdvanced = AdvanceOrAttachBranchPointer(request, current.SnapshotId);
            return Attach(current, existed, CoverageFor(current.SnapshotId)) with { BranchAdvanced = terminalAdvanced };
        }

        // A complete snapshot may already be published for this identity (produced by an earlier run
        // whose job row predates migration 016, or a race we lost). Attach to it without re-indexing —
        // but take the job's verdict from the snapshot's DURABLE coverage record (issue #119): a
        // published snapshot whose recorded coverage is partial must never be reported complete.
        var published = snapshots.GetByIdentityHash(hash);
        if (published is { Status: SnapshotStatus.Complete })
        {
            var publishedCoverage = CoverageFor(published.Id);
            RecordPublishedVerdict(jobs, jobId, published.Id, publishedCoverage);
            var publishedAdvanced = AdvanceOrAttachBranchPointer(request, published.Id);
            return Attach(jobs.GetJob(jobId)!, existed, publishedCoverage) with { BranchAdvanced = publishedAdvanced };
        }

        // Branch reset / force-push A→B→A (issue #85): the identity's snapshot was published but a later
        // advance superseded it. When the request carries a branch guard (a head sequence, SVC-6's
        // expected_head_commit, or SVC-7's branch_update: none), re-select it WITHOUT running the worker —
        // mirroring the orchestrator's SelectExistingSnapshot — provided it is still intact; a
        // data-less/unservable one is demoted so the worker below genuinely rebuilds it. Issue #273: a remote
        // branch-moving ensure (an ordered intent with its following refresh) re-selects it guarded or not, so the
        // unguarded retry of a request whose snapshot a refresh has since superseded never runs the worker again;
        // its own branch decision applies, and its refresh then converges the branch. An unguarded locate/local
        // request keeps today's worker path byte-for-byte (#84 criterion 2).
        if ((HasBranchGuard(request) || NeedsBranchConvergence(request)) && published is { Status: SnapshotStatus.Superseded })
        {
            var (reselected, reselectedCoverage, reselectedAdvanced) =
                ReselectOrDemoteSupersededSnapshot(request, jobId, published.Id, jobs, snapshots);
            if (reselected)
            {
                return Attach(jobs.GetJob(jobId)!, existed, reselectedCoverage) with { BranchAdvanced = reselectedAdvanced };
            }
        }

        // The requested branch's pointer before the worker runs, so the result can report whether the
        // worker's own branch advance (IndexOrchestrator.AdvanceBranchToSnapshot) moved it (SVC-6). The write
        // gate is held for the whole run, so no other writer can move it in between.
        var pointerBeforeWorker = RequestedBranchPointer(snapshots, request);

        // A STALE terminal result (a complete/partial job whose published snapshot was reclaimed by
        // retention) must be reset to queued before MarkRunning, whose guard only advances a
        // queued/running job — otherwise the job would be stuck reporting a phantom-complete.
        if (SnapshotJobStatus.IsSettled(current.Status))
            jobs.Requeue(jobId);

        jobs.MarkRunning(jobId, _lease.OwnerToken);

        // Issue #125: on the LAST attempt the bound allows, tell the worker so a clone-mode checkout degrades
        // a persistently-transient SUBMODULE failure to partial coverage instead of failing the whole job.
        var workRequest = jobs.GetJob(jobId)!.Attempts >= _options.MaxProvisioningAttempts
            ? request with { IsFinalProvisioningAttempt = true }
            : request;

        // Issue #325: nothing of the previous job is reachable here, so its memory can be returned before restore/load.
        // The debt is taken only by a production that reaches its worker (attach/reuse/reselect paths returned above),
        // and a previous job's unreturned memory is carried when its post-job reclaim did not run to completion.
        if (TakeCarriedJobMemory())
            ReclaimBeforeJob(jobId);

        var scratch = _paths.AllocateScratch($"job-{jobId}");
        SnapshotWorkResult result;
        try
        {
            // Issue #148: the worker runs on the SERVICE lifetime, never a caller's token — a caller that
            // disconnects or times out must not throw away a long index run. Only shutdown cancels it.
            result = await ContinueOffWorkerStack(_worker.ProduceAsync(workRequest, hash, scratch, _lifetime.Token)).ConfigureAwait(false);
        }
        // Every catch below writes the job row, so each is skipped once the lease is lost (issue #38): the
        // exception propagates and the job stays running for the NEW owner's startup reconcile to requeue.
        catch (Exception ex) when (!LeaseLost && (ex is OperationCanceledException || _lifetime.IsCancellationRequested))
        {
            // Cancellation (service shutdown) is not a durable failure: requeue so a later ensure re-attempts
            // this identity rather than attaching to a permanent failed/cancelled terminal state. Once shutdown
            // began, ANY worker failure is treated the same way — cancellation often surfaces as another
            // exception (a killed build process, a torn-down pipe) that must not be cached as terminal.
            jobs.Requeue(jobId);
            if (ex is OperationCanceledException)
                throw;
            throw new OperationCanceledException(ShutdownMessage, ex, _lifetime.Token);
        }
        catch (TransientProvisioningException ex) when (!LeaseLost)
        {
            // A TRANSIENT provisioning/clone failure (network blip, fetch timeout, remote 5xx) carries no
            // durable snapshot and is EXPECTED to recover. Do NOT record a cached terminal that would
            // suppress every later ensure for this identity (the idempotency-poisoning hole). Instead
            // requeue — bounded by the job-wide attempt counter (incremented by MarkRunning ABOVE) — so
            // the next ensure re-attempts; only once the bound is exhausted does it settle to terminal
            // Failed. Distinct from cancellation: we return a QUEUED result rather than rethrowing.
            var attempts = jobs.GetJob(jobId)!.Attempts;
            if (attempts < _options.MaxProvisioningAttempts)
            {
                jobs.ReplaceDiagnostics(
                    jobId, [ProvisioningDiagnostic(jobId, ex.DiagnosticCode ?? "provisioning_transient", ex.Message)]);
                jobs.Requeue(jobId);
                return Produced(jobs.GetJob(jobId)!);
            }
            var exhausted = $"provisioning failed after {attempts} attempt(s): {ex.Message}";
            jobs.MarkResult(jobId, SnapshotJobStatus.Failed, null, exhausted);
            SnapshotJobDiagnostic[] exhaustedDiagnostics = ex.DiagnosticCode is { } code
                ? [ProvisioningDiagnostic(jobId, code, ex.Message),
                   ProvisioningDiagnostic(jobId, "provisioning_attempts_exhausted", exhausted)]
                : [ProvisioningDiagnostic(jobId, "provisioning_attempts_exhausted", exhausted)];
            jobs.ReplaceDiagnostics(jobId, exhaustedDiagnostics);
            return Produced(jobs.GetJob(jobId)!);
        }
        catch (Exception ex) when (!LeaseLost)
        {
            jobs.MarkResult(jobId, SnapshotJobStatus.Failed, null, ex.Message);
            jobs.ReplaceDiagnostics(jobId, [FailureDiagnostic(jobId, ex.Message)]);
            return Attach(jobs.GetJob(jobId)!, existed);
        }
        finally
        {
            _paths.ReleaseScratch(scratch);
        }

        // The worker may have run for a long time: re-check the lease before recording its result.
        EnsureLeaseHeld();

        // A worker that returned after shutdown cancelled it (it ignored or swallowed the cancellation) may
        // report a failure that is only an artifact of the shutdown: requeue rather than cache it. Nothing is
        // lost — a snapshot it did publish is attached, with its durable coverage verdict, by the next ensure.
        if (_lifetime.IsCancellationRequested)
        {
            jobs.Requeue(jobId);
            throw new OperationCanceledException(ShutdownMessage, _lifetime.Token);
        }

        var validated = ValidateWorkerResult(result, hash, snapshots);

        // Defense in depth (issue #119): the job verdict never contradicts the snapshot's durable
        // coverage record — a worker that reports Complete over a snapshot recorded as partial is
        // downgraded to Partial with the recorded reasons.
        var producedCoverage = CoverageFor(validated.SnapshotId);
        if (validated.Status == SnapshotJobStatus.Complete && producedCoverage is { IsPartial: true })
            validated = validated with { Status = SnapshotJobStatus.Partial, Error = PartialCoverageReason(producedCoverage) };

        BeginRaw("production-result");
        try
        {
            jobs.ReplaceDiagnostics(jobId, validated.Projects.Select(p => p.ToDiagnostic(jobId)));
            jobs.MarkResult(jobId, validated.Status, validated.SnapshotId, validated.Error);
            ExecRaw("COMMIT;");
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
        AfterProductionResultCommitForTesting?.Invoke();
        // Issue #267: timings are telemetry, recorded after the result so they can never decide the job's outcome.
        if (validated.Timings is { } timings)
        {
            try
            {
                jobs.ReplaceTimings(jobId, JobTimingsJson.Serialize(timings));
            }
            catch (Exception ex) when (ex is SqliteException or NotSupportedException or InvalidOperationException)
            {
                // This production still owns the writer turn until it returns.
                await Console.Error.WriteLineAsync(
                    $"Job {jobId}: its phase timings could not be recorded: {ex.Message}".AsMemory(),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        bool? workerAdvanced = validated.SnapshotId is long producedId
                               && validated.Status is SnapshotJobStatus.Complete or SnapshotJobStatus.Partial
            ? BranchAdvancedTo(producedId, pointerBeforeWorker, RequestedBranchPointer(snapshots, request))
            : null;
        return Attach(jobs.GetJob(jobId)!, existed, producedCoverage) with { BranchAdvanced = workerAdvanced };
    }

    /// <summary>
    /// Ingests a client/CI contribution artifact (Phase 16). This is the SUPPLY-CHAIN entrypoint: the
    /// artifact is an UNTRUSTED external input, so it is size-bounded, authenticated, authorized, and
    /// hash/capability-verified BEFORE a single row is imported or published (CRITICAL 1). It is
    /// content-addressed — re-uploading the same artifact is a no-op (acceptance criterion 2) — and attaches
    /// to the ONE capability-less assembly snapshot for the committed state, so multiple capability-specific
    /// contributions (Windows + macOS) assemble into one repository snapshot (criterion 4). A validation
    /// failure records structured per-project diagnostics and is NEVER published (criterion 3). This path is
    /// entirely OPT-IN: a service with no contributor never calls it, and a normal local build is unaffected
    /// (CRITICAL 2 / criterion 5).
    /// </summary>
    public async Task<IngestContributionResult> IngestContributionAsync(
        IngestContributionRequest request, CancellationToken cancellationToken = default)
    {
        // Parse + size-bound the artifact OUTSIDE the write gate (cheap, and rejects an oversized/garbage
        // upload without touching the catalog).
        Contributions.ContributionArtifact artifact;
        try
        {
            artifact = Contributions.ContributionArtifact.ReadFrom(request.Artifact, _contributionPolicy.MaxArtifactBytes);
        }
        catch (Contributions.ContributionTooLargeException ex)
        {
            return Reject(Contributions.ContributionRejectionCode.SizeLimit, ex.Message, null, string.Empty);
        }
        catch (Contributions.ContributionFormatException ex)
        {
            return Reject(Contributions.ContributionRejectionCode.MalformedArtifact, ex.Message, null, string.Empty);
        }

        return await IngestParsedAsync(artifact, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streaming ingest entry: enforces <see cref="ContributionPolicy.MaxArtifactBytes"/> INCREMENTALLY as
    /// the artifact is read, so an oversized upload is rejected without ever buffering it whole. The HTTP host
    /// uses this so the SERVICE's own artifact-size cap governs the untrusted upload (not the transport's
    /// default request-body limit), keeping memory bounded on the supply-chain boundary.
    /// </summary>
    public async Task<IngestContributionResult> IngestContributionAsync(
        Stream artifactStream, string? token, bool finalize, string? branchName, bool isDefaultBranch,
        CancellationToken cancellationToken = default)
    {
        Contributions.ContributionArtifact artifact;
        try
        {
            artifact = await Contributions.ContributionArtifact
                .ReadFromAsync(artifactStream, _contributionPolicy.MaxArtifactBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Contributions.ContributionTooLargeException ex)
        {
            return Reject(Contributions.ContributionRejectionCode.SizeLimit, ex.Message, null, string.Empty);
        }
        catch (Contributions.ContributionFormatException ex)
        {
            return Reject(Contributions.ContributionRejectionCode.MalformedArtifact, ex.Message, null, string.Empty);
        }

        var request = new IngestContributionRequest
        {
            Artifact = [],
            Token = token,
            Finalize = finalize,
            BranchName = branchName,
            IsDefaultBranch = isDefaultBranch
        };
        return await IngestParsedAsync(artifact, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestContributionResult> IngestParsedAsync(
        Contributions.ContributionArtifact artifact, IngestContributionRequest request, CancellationToken cancellationToken)
    {
        var manifest = artifact.Manifest;
        var assemblyIdentity = manifest.ToSnapshotIdentity();
        var identityHash = assemblyIdentity.Hash;
        var contentHash = artifact.ContentAddress;

        // Materialize the payload catalog to scratch (quarantined from persistent volumes) and open it
        // READ-ONLY so the importer/validator can never mutate the untrusted payload. The scratch handle is
        // allocated BEFORE the try so the finally always releases it, and the payload write is INSIDE the try
        // so a write fault or cancellation (disk-full, client disconnect) cannot leak the scratch directory.
        var scratch = _paths.AllocateScratch($"contrib-{Guid.NewGuid():N}");

        SqliteConnection? payloadConn = null;
        try
        {
            var payloadPath = Path.Combine(scratch, "payload.db");
            await File.WriteAllBytesAsync(payloadPath, artifact.Payload.ToArray(), cancellationToken).ConfigureAwait(false);
            payloadConn = OpenReadOnly(payloadPath);

            return await WithWriteAsync(() =>
                Task.FromResult(IngestUnderWriteLock(artifact, manifest, assemblyIdentity, identityHash, contentHash, payloadConn, request)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            return Reject(Contributions.ContributionRejectionCode.MalformedArtifact,
                $"the contribution payload is not a readable catalog: {ex.Message}", null, identityHash);
        }
        catch (Exception ex) when (!_lease.IsLost &&
            ex is ArgumentException or InvalidOperationException or InvalidCastException or FormatException or OverflowException)
        {
            // Defense-in-depth on the UNTRUSTED boundary: a crafted payload can make a typed column read or a
            // structural assumption (e.g. a NULL where a value is required) throw a non-Sqlite exception. The
            // write transaction has already rolled back cleanly (IngestUnderWriteLock's ROLLBACK), so translate
            // it into a structured MalformedArtifact rejection instead of surfacing an opaque 500 (criterion 3).
            // The !_lease.IsLost guard deliberately lets a lost-writer-lease InvalidOperationException (issue
            // #38) propagate as a server fault rather than be mislabeled a client error.
            return Reject(Contributions.ContributionRejectionCode.MalformedArtifact,
                $"the contribution payload is malformed: {ex.Message}", null, identityHash);
        }
        finally
        {
            if (payloadConn != null) await payloadConn.DisposeAsync().ConfigureAwait(false);
            _paths.ReleaseScratch(scratch);
        }
    }

    // Wraps the ingest in ONE atomic transaction (raw BEGIN IMMEDIATE / COMMIT so existing store commands
    // enrol in the connection's ambient transaction — do NOT use SqliteConnection.BeginTransaction()), so a
    // contribution is imported + published + job-recorded atomically or rolled back entirely (no half-imported
    // pending snapshot survives a mid-ingest fault).
    private IngestContributionResult IngestUnderWriteLock(
        Contributions.ContributionArtifact artifact, Core.Platform.ContributionManifest manifest,
        Core.SnapshotIdentity assemblyIdentity, string identityHash, string contentHash,
        SqliteConnection payloadConn, IngestContributionRequest request)
    {
        EnsureLeaseHeld();
        BeginRaw("ingest-contribution");
        try
        {
            var result = IngestBody(artifact, manifest, assemblyIdentity, identityHash, contentHash, payloadConn, request);
            ExecRaw("COMMIT;");
            // The job row (if this ingest registered one) has committed: drop its id reservation (issue #158).
            _jobIds.Release(identityHash);
            return result;
        }
        catch
        {
            // No row commits: drop an id reservation this ingest made, unless a queued ensure will register it.
            ReleaseUnlessPending(identityHash);
            ExecRaw("ROLLBACK;");
            throw;
        }
    }

    // The full ingest critical section, serialized on the writer gate. Ordered: idempotency → job attach →
    // validate → begin/attach pending assembly snapshot → import → record provenance → finalize/publish.
    private IngestContributionResult IngestBody(
        Contributions.ContributionArtifact artifact, Core.Platform.ContributionManifest manifest,
        Core.SnapshotIdentity assemblyIdentity, string identityHash, string contentHash,
        SqliteConnection payloadConn, IngestContributionRequest request)
    {
        var jobs = new SnapshotJobStore(_conn);
        var snapshots = new SnapshotStore(_conn);
        var contributions = new ContributionStore(_conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // (2) Content-addressed idempotency: an identical artifact already accepted is a NO-OP (criterion 2).
        if (contributions.GetByContentHash(contentHash) is { } already)
        {
            var priorJob = jobs.GetJobByIdentity(identityHash);
            return new IngestContributionResult
            {
                Accepted = true,
                Status = ContributionIngestStatus.Duplicate,
                JobId = priorJob?.Id,
                SnapshotId = already.SnapshotId,
                ContentHash = contentHash,
                IdentityHash = identityHash
            };
        }

        // Attach to the ONE durable job for this assembly identity (criterion 1), and take it running under
        // this instance's lease. A stale terminal job is requeued first so MarkRunning's guard advances it.
        var (job, _) = RegisterJobLocked(jobs, identityHash, manifest.RepositoryRemoteUrl, manifest.CommitSha, request.BranchName);
        if (SnapshotJobStatus.IsSettled(job.Status))
            jobs.Requeue(job.Id);
        jobs.MarkRunning(job.Id, _lease.OwnerToken);

        // (3) SUPPLY-CHAIN validation — authz + schema/analyzer + payload + capability + git-content + graph.
        var validator = Contributions.ContributionValidator.ForService(_contributionPolicy, _gitContent, _authorizer);
        var validation = validator.Validate(artifact, payloadConn, request.Token);
        if (!validation.Ok)
        {
            jobs.ReplaceDiagnostics(job.Id, validation.Diagnostics.Select(d => d.ToDiagnostic(job.Id)));
            jobs.MarkResult(job.Id, SnapshotJobStatus.Failed, null, $"{validation.Code}: {validation.Message}");
            return new IngestContributionResult
            {
                Accepted = false,
                Status = ContributionIngestStatus.Rejected,
                RejectionCode = validation.Code,
                Message = validation.Message,
                JobId = job.Id,
                IdentityHash = identityHash,
                ContentHash = contentHash,
                Diagnostics = jobs.GetDiagnostics(job.Id)
            };
        }

        // (4) Begin (or attach to) the PENDING assembly snapshot for this committed state. Idempotent by
        // identity hash, so a second (compatible) contribution accumulates into the SAME pending snapshot.
        var repoId = snapshots.EnsureRepository(manifest.RepositoryRemoteUrl, now);
        var commitId = snapshots.EnsureCommit(repoId, manifest.CommitSha, manifest.TreeSha, now);
        var (snapshotId, _, status) = snapshots.BeginPending(assemblyIdentity, repoId, commitId, runId: null, now);

        // Immutability: if the assembly snapshot is NOT pending it is already published (complete) — or was
        // published and later superseded on a branch. Either way it is IMMUTABLE: never import into it and
        // never re-complete it. Record this distinct contribution's provenance only and no-op (the committed
        // state's data already exists). A Superseded snapshot must NOT be resurrected or re-advanced here.
        if (status != SnapshotStatus.Pending)
        {
            contributions.Record(snapshotId, contentHash, manifest.Tenant, manifest.RepositoryRemoteUrl,
                manifest.CommitSha, manifest.CapabilityFingerprint, manifest.Producer,
                manifest.ToolchainFingerprint, manifest.ManifestHash);
            jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, snapshotId);
            return Accepted(ContributionIngestStatus.Complete, job.Id, snapshotId, contentHash, identityHash);
        }

        var importer = new Contributions.ContributionImporter(_conn);

        // (5-pre) Disjoint-assembly guard: a second contribution accumulating into the SAME pending snapshot
        // must import project versions DISJOINT from those already assembled (each environment contributes its
        // own capability-specific projects — criterion 4). Re-importing an already-mapped logical project is
        // rejected with a structured reason rather than silently reusing/overwriting the earlier row.
        var conflicts = importer.FindConflictingLogicalProjects(payloadConn, validation.PayloadSnapshotId!.Value, snapshotId);
        if (conflicts.Count > 0)
        {
            var overlapDiagnostics = conflicts.Select(c => new ProjectOutcome
            {
                ProjectCanonicalId = c,
                Severity = JobDiagnosticSeverity.Error,
                Code = Contributions.ContributionRejectionCode.ProjectGraphMismatch,
                Message = $"logical project '{c}' is already assembled into this pending snapshot; contributions must be disjoint."
            }.ToDiagnostic(job.Id));
            jobs.ReplaceDiagnostics(job.Id, overlapDiagnostics);
            jobs.MarkResult(job.Id, SnapshotJobStatus.Failed, null,
                $"{Contributions.ContributionRejectionCode.ProjectGraphMismatch}: overlapping contribution");
            return new IngestContributionResult
            {
                Accepted = false,
                Status = ContributionIngestStatus.Rejected,
                RejectionCode = Contributions.ContributionRejectionCode.ProjectGraphMismatch,
                Message = "the contribution overlaps project versions already assembled into this snapshot; contributions must be disjoint.",
                JobId = job.Id,
                IdentityHash = identityHash,
                ContentHash = contentHash,
                Diagnostics = jobs.GetDiagnostics(job.Id)
            };
        }

        // (5) Import the validated payload's project versions + compact rows into the pending snapshot,
        // stamping each project's producing capability (migration 018). A payload whose logical-project
        // tuple diverges from shared catalog metadata is rejected here rather than perturbing shared state
        // (issue #69) — EnsureLogicalProject verifies instead of overwriting.
        try
        {
            importer.Import(payloadConn, validation.PayloadSnapshotId!.Value, snapshotId, repoId, manifest, now);
        }
        catch (LogicalProjectConflictException ex)
        {
            jobs.ReplaceDiagnostics(job.Id, [new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Error,
                Code = Contributions.ContributionRejectionCode.ProjectGraphMismatch,
                Message = ex.Message
            }.ToDiagnostic(job.Id)]);
            jobs.MarkResult(job.Id, SnapshotJobStatus.Failed, null,
                $"{Contributions.ContributionRejectionCode.ProjectGraphMismatch}: {ex.Message}");
            return new IngestContributionResult
            {
                Accepted = false,
                Status = ContributionIngestStatus.Rejected,
                RejectionCode = Contributions.ContributionRejectionCode.ProjectGraphMismatch,
                Message = ex.Message,
                JobId = job.Id,
                IdentityHash = identityHash,
                ContentHash = contentHash,
                Diagnostics = jobs.GetDiagnostics(job.Id)
            };
        }

        // (6) Record contribution provenance keyed by content address (idempotency + assembled-snapshot lineage).
        // The contribution's declared completeness is recorded durably so the finalize gate can publish the
        // assembled snapshot Partial if ANY contribution that fed it was non-complete (issue #70).
        contributions.Record(snapshotId, contentHash, manifest.Tenant, manifest.RepositoryRemoteUrl,
            manifest.CommitSha, manifest.CapabilityFingerprint, manifest.Producer,
            manifest.ToolchainFingerprint, manifest.ManifestHash, ManifestCompleteness(manifest));

        // (7) Finalize → publish + advance branch, or leave assembling (pending) for more contributions.
        if (!request.Finalize)
            return Accepted(ContributionIngestStatus.Assembling, job.Id, snapshotId, contentHash, identityHash);

        // (7a) Completeness/topology gate (issue #70): an assembled snapshot may only publish COMPLETE when
        // it is provably complete for its identity. If any contribution declared itself non-complete, or the
        // assembled graph is missing an intra-repository project version it references, publish PARTIAL — a
        // materially-incomplete assembly is NEVER silently Complete (criterion 3). Cross-repo provider
        // references are resolved via snapshot_dependencies (#72) and are not counted as incompleteness.
        var gate = Contributions.AssemblyFinalizeGate.Evaluate(
            manifest,
            contributions.HasIncompleteContribution(snapshotId),
            snapshots.GetSnapshotLogicalCanonicalIds(snapshotId),
            snapshots.GetKnownLogicalCanonicalIds(repoId));
        if (!gate.IsComplete)
        {
            var reason = string.Join(" ", gate.Reasons);
            if (snapshots.MarkPartial(snapshotId, now) == 1)
            {
                jobs.ReplaceDiagnostics(job.Id, gate.Reasons.Select(r => new ProjectOutcome
                {
                    Severity = JobDiagnosticSeverity.Error,
                    Code = "assembly_incomplete",
                    Message = r
                }.ToDiagnostic(job.Id)));
                jobs.MarkResult(job.Id, SnapshotJobStatus.Partial, snapshotId, $"assembly incomplete: {reason}");
            }
            return Accepted(ContributionIngestStatus.Assembling, job.Id, snapshotId, contentHash, identityHash);
        }

        // Publish is guarded on status = pending inside MarkComplete; a zero-row result means the snapshot was
        // NOT pending at publish time. That is only safe to report Complete when the snapshot is ALREADY
        // complete (a concurrent finalize won the pending→complete race / immutability). "Not pending" also
        // covers PARTIAL (an earlier topology/contribution gate marked it) and other non-complete states —
        // reporting Complete for a partial snapshot would silently publish a materially-incomplete assembly,
        // so we re-read the real status and never claim Complete for a snapshot we did not complete
        // (criterion 3). Never advance the branch against a snapshot we did not transition to complete.
        if (snapshots.MarkComplete(snapshotId, now) != 1)
        {
            // Not pending at publish time. A COMPLETE or SUPERSEDED snapshot is fully-published immutable
            // data — safe to report Complete (a concurrent finalize won the pending→complete race, or a
            // branch has since moved past it). But "not pending" ALSO covers PARTIAL (an earlier
            // topology/contribution gate marked it): reporting Complete for a partial snapshot would
            // silently publish a materially-incomplete assembly, so we re-read the real status and report
            // the true terminal state, never claiming Complete for a snapshot we did not complete
            // (criterion 3).
            if (snapshots.GetById(snapshotId) is { Status: SnapshotStatus.Complete or SnapshotStatus.Superseded })
            {
                jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, snapshotId);
                return Accepted(ContributionIngestStatus.Complete, job.Id, snapshotId, contentHash, identityHash);
            }
            jobs.MarkResult(job.Id, SnapshotJobStatus.Partial, snapshotId, "assembly not complete at finalize");
            return Accepted(ContributionIngestStatus.Assembling, job.Id, snapshotId, contentHash, identityHash);
        }
        if (!string.IsNullOrWhiteSpace(request.BranchName))
            AdvanceBranch(snapshots, repoId, request.BranchName, request.IsDefaultBranch, snapshotId, now);
        jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, snapshotId);
        return Accepted(ContributionIngestStatus.Complete, job.Id, snapshotId, contentHash, identityHash);
    }

    // Reduces a contribution's per-project declared completeness to one contribution-level value: unsupported
    // dominates partial, which dominates complete (issue #70).
    private static string ManifestCompleteness(ContributionManifest manifest)
    {
        var worst = "complete";
        foreach (var p in manifest.Projects)
        {
            if (string.Equals(p.Completeness, "unsupported", StringComparison.OrdinalIgnoreCase))
                return "unsupported";
            if (string.Equals(p.Completeness, "partial", StringComparison.OrdinalIgnoreCase))
                worst = "partial";
        }
        return worst;
    }

    private static IngestContributionResult Accepted(string status, long jobId, long snapshotId, string contentHash, string identityHash) => new()
    {
        Accepted = true,
        Status = status,
        JobId = jobId,
        SnapshotId = snapshotId,
        ContentHash = contentHash,
        IdentityHash = identityHash
    };

    private static IngestContributionResult Reject(string code, string message, long? jobId, string identityHash) => new()
    {
        Accepted = false,
        Status = ContributionIngestStatus.Rejected,
        RejectionCode = code,
        Message = message,
        JobId = jobId,
        IdentityHash = identityHash
    };

    // Ensures the requesting branch owns a pointer to the snapshot it attached to (issue #62), so a second
    // branch at the same commit both resolves and protects the shared snapshot. For a NULL-sequence request
    // an EXISTING pointer is re-pointed only when that cannot regress commit history (issue #162): the
    // current target is at the same commit (a pure identity change, e.g. an AnalyzerVersion bump or a
    // Phase-12 provider snapshot reused by a direct ensure) or it is no longer usable. Anything else stays
    // attach-if-unset. Raw DB write — callers must already hold the write gate AND an open write transaction
    // (AdvanceOrAttachBranchPointer). A request with no branch targets "main", exactly as the worker path
    // (CreateSnapshotContext) and the sequence-bearing reuse path resolve it, so a branchless ensure reaches
    // the same branch state whether or not its identity was pre-built. No-op when the request names an empty
    // branch, has no resolved snapshot, or an unknown repository.
    private void EnsureAttachBranchPointer(EnsureSnapshotRequest request, long? snapshotId)
    {
        if ((request.BranchName ?? "main") is not { Length: > 0 } branch || snapshotId is not long sid)
            return;
        var snapshots = new SnapshotStore(_conn);
        if (snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is not long repoId)
            return;
        // A repository ensured in its own right is a consumer, exactly as the worker path's EnsureRepository
        // makes it, so a provider-only repository whose provider snapshot this ensure reuses stays selectable.
        snapshots.MarkConsumerRepository(repoId);
        var branchId = snapshots.AttachOrUpgradeBranchPointer(repoId, branch, sid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // Safety net (issue #104): AttachBranchPointer inserts the branch NON-default (issue #62 — a second
        // branch must never demote the real default). But when THIS ensure's branch should own the default
        // — the coordinator flagged it, it already is the default, or the repo has NO default yet (the
        // first/sole consumer) — promote it so the multi-tenant read selector (GetSelectedSnapshotIdFor-
        // Repository, which requires is_default = 1) can resolve a snapshot instead of failing closed. Uses
        // SetSoleDefaultBranch (not PromoteSoleDefaultBranch) because the attached row is is_default = 0 and
        // must be SET, not merely have siblings demoted; it preserves the single-default invariant. A caller that
        // may not pick the default (issue #199) gets the no-default clause only for the remote's own default.
        if (snapshots.ShouldOwnDefault(repoId, branch, request.ResolveIsDefaultBranch(), request.AllowsImplicitDefault(branch)))
            snapshots.SetSoleDefaultBranch(repoId, branchId);
    }

    // The branch-pointer decision for a REUSE/terminal-attach ensure — a snapshot the service attaches
    // WITHOUT running the worker (an already-terminal job or an already-published snapshot), so the
    // orchestrator's gated AdvanceBranchToSnapshot never runs on these paths. Issue #84: when the request
    // carries a monotonic head sequence, apply the SAME forward-only gate the orchestrator would have, so a
    // higher-sequence re-ensure of an already-published commit still advances the pointer (e.g. a
    // reset/force-push A@10 → B@20 → A@30) and a lower/equal one still declines — never regressing the
    // branch head and always persisting the advanced sequence. When the request carries NO sequence (the
    // local/legacy path) the pointer stays attach-if-unset except for the non-regressing same-commit /
    // unusable-target re-point of issue #162 (EnsureAttachBranchPointer), so identical requests reach the
    // same branch state whether or not the identity was pre-built. The mutation runs in ONE raw
    // BEGIN IMMEDIATE / COMMIT transaction — this path, unlike
    // the worker's AdvanceBranchToSnapshot and the contribution ingest, is NOT already inside a write
    // transaction — so a concurrent reader never observes an intermediate state where the branch pointer
    // advanced but its default was not yet set, or a re-ensured default was momentarily demoted (issue
    // #104). Raw DB write — callers must already hold the write gate.
    private bool? AdvanceOrAttachBranchPointer(EnsureSnapshotRequest request, long? snapshotId)
    {
        BeginRaw("advance-branch-pointer");
        try
        {
            var advanced = AdvanceOrAttachBranchPointerCore(request, snapshotId);
            ExecRaw("COMMIT;");
            return advanced;
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
    }

    // Applies this request's branch decision to an attached snapshot and reports whether it moved the requested
    // branch's pointer onto it (SVC-6 branch_advanced): null when there is no snapshot to decide for (a failed or
    // unsupported terminal job). Precedence (SVC-6/7), evaluated inside the caller's write transaction:
    //  0. branch_update: none — no branch row is created and no pointer moves;
    //  1. both expected_head_commit and branch_head_sequence — refused at intake, never reaches here;
    //  2. expected_head_commit — the CAS (ApplyHeadCommitGuard), identical to the worker's AdvanceBranchToSnapshot;
    //  3. branch_head_sequence — the #84 forward-only advance;
    //  4. neither — the #162 attach-or-upgrade (EnsureAttachBranchPointer).
    private bool? AdvanceOrAttachBranchPointerCore(EnsureSnapshotRequest request, long? snapshotId)
    {
        if (snapshotId is not long sid)
            return null;
        var snapshots = new SnapshotStore(_conn);
        var before = RequestedBranchPointer(snapshots, request);
        ApplyReuseBranchDecision(request, sid, snapshots);
        return BranchAdvancedTo(sid, before, RequestedBranchPointer(snapshots, request));
    }

    private void ApplyReuseBranchDecision(EnsureSnapshotRequest request, long sid, SnapshotStore snapshots)
    {
        if (request.SuppressesBranchUpdate)
        {
            // Parity with the worker path, whose EnsureRepository makes a directly ensured repository a consumer.
            if (snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is long consumerId)
                snapshots.MarkConsumerRepository(consumerId);
            return;
        }
        if (snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is long selectedRepo
            && snapshots.PreserveRebuildSelection(snapshots.GetBranchId(selectedRepo, request.BranchName ?? "main"),
                sid, request.BranchHeadSequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            return;
        if (request.ExpectedHeadCommit is { } expectedHead)
        {
            ApplyHeadCommitGuard(request, sid, expectedHead, snapshots);
            return;
        }
        if (request.BranchHeadSequence is not long seq)
        {
            EnsureAttachBranchPointer(request, sid);
            return;
        }
        if (snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is not long repoId)
            return;
        // Mirror CreateSnapshotContext's branch/default resolution so the reuse path and the worker path
        // advance the SAME branch under the SAME default semantics. Default ownership is resolved BEFORE
        // EnsureBranch (issue #104): the request's explicit flag (ResolveIsDefaultBranch), or the
        // first/sole-consumer safety net when the repo has no default yet — so a coordinator that names the
        // branch it advances still leaves a selectable default, and the is_default transition stays
        // monotonic (a re-ensured default is never demoted-then-re-promoted).
        var branchName = request.BranchName ?? "main";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshots.MarkConsumerRepository(repoId);
        var ownsDefault = snapshots.ShouldOwnDefault(
            repoId, branchName, request.ResolveIsDefaultBranch(), request.AllowsImplicitDefault(branchName));
        var branchId = snapshots.EnsureBranch(repoId, branchName, ownsDefault, now);
        if (ownsDefault)
            snapshots.PromoteSoleDefaultBranch(repoId, branchId);
        snapshots.AdvanceBranchPointerForwardOnly(branchId, sid, seq, now);
    }

    // SVC-6: the expected_head_commit CAS on the reuse paths — the same decision, in the same order, as the
    // worker's IndexOrchestrator.AdvanceBranchToSnapshot, so a pre-built identity and a freshly built one
    // converge. A mismatch attaches only: no branch row is created and the default designation is untouched,
    // so a stale push can neither move the head nor re-create a retired branch.
    private static void ApplyHeadCommitGuard(
        EnsureSnapshotRequest request, long sid, string expectedHead, SnapshotStore snapshots)
    {
        if (snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is not long repoId)
            return;
        snapshots.MarkConsumerRepository(repoId);
        var branchName = request.BranchName ?? "main";
        var currentBranchId = snapshots.GetBranchId(repoId, branchName);
        if (!snapshots.BranchHeadMatches(currentBranchId, expectedHead, repoId, branchName))
            return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ownsDefault = snapshots.ShouldOwnDefault(
            repoId, branchName, request.ResolveIsDefaultBranch(), request.AllowsImplicitDefault(branchName));
        var branchId = snapshots.EnsureBranch(repoId, branchName, ownsDefault, now);
        if (ownsDefault)
            snapshots.PromoteSoleDefaultBranch(repoId, branchId);
        snapshots.AdvanceBranchPointerIfHeadMatches(branchId, sid, expectedHead, now,
            preservePrevious: request.RebuildGeneration is not null);
    }

    // True when any SVC-6/7 or #84 branch guard is present; such a request re-selects an intact superseded
    // snapshot on the reuse path instead of handing it to the worker.
    private static bool HasBranchGuard(EnsureSnapshotRequest request) =>
        request.BranchHeadSequence is not null || request.ExpectedHeadCommit is not null || request.SuppressesBranchUpdate;

    // The snapshot the request's branch (BranchName, else "main", as every advance path resolves it) currently
    // points at, or null when the repository, the branch or its pointer is absent.
    private static long? RequestedBranchPointer(SnapshotStore snapshots, EnsureSnapshotRequest request) =>
        snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is long repoId
        && snapshots.GetBranchId(repoId, request.BranchName ?? "main") is long branchId
            ? snapshots.GetBranchSnapshotId(branchId)
            : null;

    // SVC-6 branch_advanced: the decision moved the pointer onto the snapshot (it targeted something else, or
    // nothing, before).
    private static bool BranchAdvancedTo(long snapshotId, long? before, long? after) =>
        after == snapshotId && before != snapshotId;

    // Issue #85 (generalized by SVC-6/7): re-selects an already-published but SUPERSEDED snapshot for a guarded
    // ensure — the branch reset / force-push A@10 → B@20 → A@30 case — mirroring the orchestrator's
    // SelectExistingSnapshot so the no-worker reuse path converges on the same state the worker path would.
    // Runs in ONE raw BEGIN IMMEDIATE / COMMIT transaction (callers hold the write gate; WithWrite is not
    // reentrant):
    //  * INTACT (SnapshotStore.IsReselectable): restore it to Complete, then run the SAME guarded decision as
    //    every other reuse path (AdvanceOrAttachBranchPointerCore). The branch is re-pointed at the snapshot only
    //    when its guard allows it: a higher #84 sequence, or a passing SVC-6 head CAS; `branch_update: none`
    //    never moves it. A declined guard (a lower/equal sequence, a head mismatch, none) leaves the pointer +
    //    stored sequence untouched while (exactly like SelectExistingSnapshot) the snapshot stays Complete,
    //    attached and resolvable by commit. The job verdict comes from the snapshot's durable coverage row,
    //    which is read but never written. Returns (true, coverage, branch_advanced).
    //  * NOT intact (data reclaimed, an unpublished provider, never published): demote it to Failed
    //    so the worker's orchestrator genuinely rebuilds the identity (its Failed → Pending retry reset, which
    //    also drops stale coverage) instead of resurrecting a data-less snapshot via SelectExistingSnapshot.
    //    Returns (false, null, null); the caller falls through to the requeue/worker path.
    private (bool Reselected, SnapshotCoverage? Coverage, bool? BranchAdvanced) ReselectOrDemoteSupersededSnapshot(
        EnsureSnapshotRequest request, long jobId, long snapshotId, SnapshotJobStore jobs, SnapshotStore snapshots)
    {
        BeginRaw("reselect-snapshot");
        try
        {
            if (!snapshots.IsReselectable(snapshotId))
            {
                if (snapshots.GetById(snapshotId) is { Status: SnapshotStatus.Superseded })
                    snapshots.MarkStatus(snapshotId, SnapshotStatus.Failed);
                ExecRaw("COMMIT;");
                return (false, null, null);
            }

            snapshots.MarkStatus(snapshotId, SnapshotStatus.Complete);
            var advanced = AdvanceOrAttachBranchPointerCore(request, snapshotId);
            var coverage = CoverageFor(snapshotId);
            RecordPublishedVerdict(jobs, jobId, snapshotId, coverage);
            ExecRaw("COMMIT;");
            return (true, coverage, advanced);
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
    }

    // Advances a branch pointer to a published snapshot and supersedes the previous target — the same
    // atomic branch advance the orchestrator performs, run inside the ingest write transaction.
    private static void AdvanceBranch(
        SnapshotStore snapshots, long repositoryId, string branchName, bool isDefaultBranch, long snapshotId, long now)
    {
        // Resolve default ownership BEFORE EnsureBranch (issue #104): the contribution's explicit flag, or
        // the first/sole-consumer safety net when the repo has no default yet, so a contributed branch still
        // yields a selectable default for the multi-tenant read selector. Monotonic is_default transition.
        var ownsDefault = snapshots.ShouldOwnDefault(repositoryId, branchName, isDefaultBranch);
        var branchId = snapshots.EnsureBranch(repositoryId, branchName, ownsDefault, now);
        if (ownsDefault)
            snapshots.PromoteSoleDefaultBranch(repositoryId, branchId);
        var previous = snapshots.GetBranchSnapshotId(branchId);
        snapshots.SetBranchPointer(branchId, snapshotId, now);
        if (previous is long prev && prev != snapshotId)
            snapshots.MarkStatus(prev, SnapshotStatus.Superseded);
    }

    private void ExecRaw(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            cmd.ExecuteNonQuery();
        }
        finally
        {
            // COMMIT/ROLLBACK ends the raw write transaction BeginRaw opened (one at a time: the single writer connection).
            if (_rawHold is { } hold && sql is "COMMIT;" or "ROLLBACK;"
                && SQLitePCL.raw.sqlite3_get_autocommit(_conn.Handle) != 0)
            {
                _rawHold = null;
                _writeLocks.Released(hold);
            }
        }
    }

    // BEGIN IMMEDIATE on the writer connection, timing the wait and starting the hold that the matching COMMIT/ROLLBACK ends.
    private void BeginRaw(string operation)
    {
        // Raw transactions run under the write gate or single-threaded startup, so _rawHold has one owner at a time.
        Debug.Assert(_rawHold is null);
        var owner = "snapshot-service:" + operation;
        var waitStarted = _writeLocks.BeginAcquire();
        try
        {
            ExecRaw("BEGIN IMMEDIATE;");
        }
        catch
        {
            _writeLocks.AcquireFailed(owner, null, waitStarted);
            throw;
        }
        _rawHold = _writeLocks.Acquired(owner, null, waitStarted);
    }

    /// <summary>The writer-lock holds and waits this service recorded (issue #342).</summary>
    public WriteLockMetrics WriteLockMetrics => _writeLocks.Snapshot();

    private static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>
    /// The full status of a job (with diagnostics) by durable job id — null when unknown. Served from an
    /// independent read connection (issue #148), so it returns promptly while a worker holds the writer. A job id
    /// handed out by a <c>wait=false</c> ensure whose registration is still waiting for the writer (issue #158)
    /// resolves as <c>queued</c>, with no diagnostics or coverage, until its row is written.
    /// </summary>
    public JobStatusResult? GetStatus(long? jobId)
    {
        if (jobId is not long id)
            return null;
        // Read the reservation BEFORE the catalog: it is released only after its row has committed, so one of the
        // two reads always sees the job.
        var reserved = _jobIds.Find(id);
        return ReadCatalog(conn =>
        {
            var jobs = new SnapshotJobStore(conn);
            var job = jobs.GetJob(id);
            return job is not null ? StatusOf(conn, jobs, job) : ReservedStatus(reserved);
        });
    }

    /// <summary>Returns the durable status of a default-branch refresh admission, or null when it is unknown.</summary>
    public RefreshAdmissionStatus? GetRefreshAdmissionStatus(string admissionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(admissionId);
        return ReadCatalog(conn => new PendingRefreshStore(conn).GetStatus(admissionId));
    }

    /// <summary>
    /// The full status of a job (with diagnostics) by snapshot identity hash — null when unknown. Served from
    /// an independent read connection (issue #148). Like <see cref="GetStatus"/>, a reserved job id resolves as
    /// <c>queued</c> (issue #158).
    /// </summary>
    public JobStatusResult? GetStatusByIdentity(string identityHash)
    {
        var reserved = _jobIds.Find(identityHash);
        return ReadCatalog(conn =>
        {
            var jobs = new SnapshotJobStore(conn);
            var job = jobs.GetJobByIdentity(identityHash);
            return job is not null ? StatusOf(conn, jobs, job) : ReservedStatus(reserved);
        });
    }

    // A job id handed out before its row exists (issue #158): queued, described by what the ensure submitted.
    private static JobStatusResult? ReservedStatus(JobIdReservations.Reservation? reserved) => reserved is null
        ? null
        : new JobStatusResult
        {
            Job = new SnapshotJobRow
            {
                Id = reserved.Id,
                IdentityHash = reserved.IdentityHash,
                RepositoryUrl = reserved.RepositoryUrl,
                CommitSha = reserved.CommitSha,
                BranchName = reserved.BranchName,
                Status = SnapshotJobStatus.Queued,
                CreatedAt = reserved.CreatedAt,
                UpdatedAt = reserved.CreatedAt
            },
            Diagnostics = [],
            Coverage = null
        };

    // Like Attach, the reported verdict never contradicts the durable coverage.
    private static JobStatusResult StatusOf(SqliteConnection conn, SnapshotJobStore jobs, SnapshotJobRow job)
    {
        var coverage = CoverageOn(conn, job.SnapshotId);
        if (job.Status == SnapshotJobStatus.Complete && coverage is { IsPartial: true })
            job = job with { Status = SnapshotJobStatus.Partial, LastError = PartialCoverageReason(coverage) };
        return new JobStatusResult
        {
            Job = job, Diagnostics = jobs.GetDiagnostics(job.Id), Coverage = coverage,
            Timings = JobTimingsJson.Parse(jobs.GetTimings(job.Id))
        };
    }

    /// <summary>
    /// The durable checkout coverage recorded for a snapshot (issue #119), or null when none was recorded.
    /// Served from an independent read connection (issue #148).
    /// </summary>
    public SnapshotCoverage? GetCoverage(long snapshotId) => ReadCatalog(conn => CoverageOn(conn, snapshotId));

    /// <summary>
    /// Resolves a repository branch (or its default branch when <paramref name="branchName"/> is null) to
    /// the complete snapshot it currently points at — null when the repo/branch/pointer is absent or the
    /// pointed snapshot is not complete. Read-only; never creates catalog rows. Served from an independent
    /// read connection (issue #148), so branch resolution never waits behind a running index.
    /// </summary>
    public SnapshotRow? ResolveBranch(string repositoryRemoteUrl, string? branchName) =>
        ResolveBranchHead(repositoryRemoteUrl, branchName)?.Snapshot;

    /// <summary>
    /// <see cref="ResolveBranch"/> plus the branch it resolved through — its name (the default branch's own
    /// name when <paramref name="branchName"/> is null), default designation and #84 head sequence — the
    /// snapshot's commit SHA, its durable coverage (SVC-6, <c>/control/resolve</c>) and whether the snapshot is
    /// current under this node's present identity (<see cref="ResolvedBranchHead.CurrentIdentityHash"/>). One
    /// consistent read on an independent read connection; null exactly when <see cref="ResolveBranch"/> is.
    /// </summary>
    public ResolvedBranchHead? ResolveBranchHead(string repositoryRemoteUrl, string? branchName)
    {
        return ReadCatalog(conn =>
        {
            var snapshots = new SnapshotStore(conn);
            if (snapshots.GetRepositoryId(repositoryRemoteUrl) is not long repoId)
                return null;

            var branchId = branchName is null
                ? snapshots.GetDefaultBranchId(repoId)
                : snapshots.GetBranchId(repoId, branchName);
            if (branchId is not long bid || snapshots.GetBranchById(bid) is not { SnapshotId: long snapId } branch)
                return null;

            var row = snapshots.GetById(snapId);
            if (row is not { Status: SnapshotStatus.Complete })
                return null;
            var commitSha = snapshots.GetCommitSha(row.CommitId);
            return new ResolvedBranchHead
            {
                Snapshot = row,
                Branch = branch.Name,
                IsDefault = branch.IsDefault,
                HeadSequence = branch.HeadSequence,
                CommitSha = commitSha,
                Coverage = CoverageOn(conn, row.Id),
                // A contributed head is its contributors' to refresh: no service ensure reproduces a contribution's
                // identity (its own tree sha, no toolchain), and rebuilding it here could replace client-built
                // (e.g. Windows-only) projects with a narrower server snapshot. Its currency is not reported.
                CurrentIdentityHash = commitSha is null || new ContributionStore(conn).HasContributions(row.Id)
                    ? null
                    : CurrentIdentityHash(conn, snapshots, row, repositoryRemoteUrl, commitSha)
            };
        });
    }

    // The identity hash an ensure of the pointed snapshot's commit computes NOW (default profile, no tree sha), for
    // /control/resolve's identity_current. The identity folds the RAW repository URL spelling, and one repository is
    // ensured under several spellings (a push names GitHub's clone_url `…/repo.git`, a reconcile the grant's stored
    // spelling), so a spelling difference alone must never read as stale: the snapshot is current when an ensure under
    // ANY known spelling of its repository — the one the snapshot's own job submitted, this request's, or the catalog
    // row's — reproduces its identity, and that hash is returned. Otherwise the hash under this request's spelling is
    // returned: the identity the caller's own ensure of the commit will produce.
    private string CurrentIdentityHash(
        SqliteConnection conn, SnapshotStore snapshots, SnapshotRow row, string requestedUrl, string commitSha)
    {
        var job = new SnapshotJobStore(conn).GetJobByIdentity(row.IdentityHash);
        (string Url, string Commit)?[] spellings =
        [
            job is null ? null : (job.RepositoryUrl, job.CommitSha),
            (requestedUrl, commitSha),
            snapshots.GetRepositoryRemoteUrl(row.RepositoryId) is { } catalogUrl ? (catalogUrl, commitSha) : null
        ];
        var rebuildGeneration = CoverageOn(conn, row.Id)?.Rebuild?.Generation;
        foreach (var (url, commit) in spellings.OfType<(string Url, string Commit)>().Distinct())
        {
            var hash = IdentityHashFor(new EnsureSnapshotRequest
            {
                RepositoryRemoteUrl = url, CommitSha = commit,
                RebuildGeneration = rebuildGeneration
            });
            if (string.Equals(hash, row.IdentityHash, StringComparison.Ordinal))
                return hash;
        }
        // No spelling reproduces the snapshot (its identity changed): the hash the caller's ensure would build, under the
        // spelling that ensure is pinned to.
        var request = new EnsureSnapshotRequest { RepositoryRemoteUrl = requestedUrl, CommitSha = commitSha };
        var (catalog, existing) = CatalogSpellings(conn, request, RemoteUrlIdentity.Normalize(requestedUrl));
        return IdentityHashFor(request with { RepositoryRemoteUrl = existing ?? catalog ?? requestedUrl });
    }

    /// <summary>
    /// Retires (deletes) a repository branch's pointer row (SVC-6, <c>POST /control/branches/retire</c>).
    /// The snapshots it pointed at stay in the catalog for retention to reclaim once nothing protects them.
    /// Idempotent: an unknown repository or branch is <c>retired: false</c> with no reason. The repository's
    /// default branch is never retired (<see cref="BranchGuardReason.DefaultBranch"/>), and when
    /// <see cref="RetireBranchRequest.ExpectedHeadCommit"/> is present the branch is retired only while
    /// <see cref="SnapshotStore.BranchHeadMatches"/> passes, so a newer push that re-created the branch is
    /// never retired (<see cref="BranchGuardReason.HeadMismatch"/>). The decision, the delete and the
    /// <c>retire</c> audit row commit in ONE write transaction under the single writer, so both guards are evaluated
    /// when the retirement APPLIES, never when it was submitted.
    /// <para>
    /// Issue #158: the retirement is a service-owned operation. It takes its turn on the writer when submitted
    /// (ensures and retires apply in submission order) and applies once the writer frees, even after this caller has
    /// gone: <paramref name="cancellationToken"/> only bounds this caller's wait. An identical retirement (same
    /// repository, branch, guard and caller) submitted while one is still waiting, with no other write admitted in
    /// between, is coalesced into it and shares its result and its single audit row. A retirement submitted after
    /// shutdown began throws <see cref="OperationCanceledException"/> on the service lifetime. The host applies the
    /// repository URL policy before calling this; it uses <see cref="BeginRetireBranchAsync"/> to bound its wait.
    /// </para>
    /// </summary>
    public Task<RetireBranchResult> RetireBranchAsync(
        RetireBranchRequest request, AuditCaller principal = default, CancellationToken cancellationToken = default) =>
        WaitForCallerAsync(StartRetire(request, principal), cancellationToken);

    /// <summary>
    /// <see cref="RetireBranchAsync"/> with a bounded wait (issue #158, <c>POST /control/branches/retire</c>): the
    /// retirement's result when it applies within <see cref="ServiceOptions.ControlWriteWait"/>, else a result with
    /// <see cref="RetireBranchResult.Pending"/> set — the retirement stays queued and applies once the writer frees
    /// (a running production holds it for its whole run), with its guards evaluated and its audit row written then.
    /// <paramref name="cancellationToken"/> only bounds this caller's wait; it never cancels the retirement.
    /// </summary>
    public async Task<RetireBranchResult> BeginRetireBranchAsync(
        RetireBranchRequest request, AuditCaller principal = default, CancellationToken cancellationToken = default)
    {
        var started = _timeProvider.GetTimestamp();
        var entry = StartRetireEntry(request, principal);
        if (!entry.IntentAdmission.IsCompleted)
        {
            var admissionWait = entry.IntentAdmission.WaitAsync(_options.ControlWriteWait, _timeProvider, cancellationToken);
            RetireWaitStartedForTesting?.Invoke();
            await ((Task)admissionWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!entry.IntentAdmission.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entry.CancelAdmission();
                throw new RetireAdmissionUnavailableException(entry.AdmissionId);
            }
        }
        await entry.IntentAdmission.ConfigureAwait(false);
        if (entry.IntentId is long intentId)
            entry.AdmissionId ??= ReadCatalog(conn => new BranchIntentStore(conn).Get(intentId)?.AdmissionId);
        var retire = entry.Retire;
        if (!retire.IsCompleted)
        {
            var remaining = _options.ControlWriteWait - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return new RetireBranchResult { Retired = false, Pending = true, AdmissionId = entry.AdmissionId };
            var bounded = ((Task)retire).WaitAsync(remaining, _timeProvider, cancellationToken);
            RetireWaitStartedForTesting?.Invoke();
            await bounded.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!retire.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new RetireBranchResult { Retired = false, Pending = true, AdmissionId = entry.AdmissionId };
            }
        }
        var result = await retire.ConfigureAwait(false);
        return result with { AdmissionId = result.AdmissionId ?? entry.AdmissionId };
    }

    // Admits one retirement as a service-owned operation on its own write-gate turn (issue #158), or coalesces it into
    // the identical retirement still waiting when no other write was admitted after it (applying both would change
    // nothing more, so it shares that one's result and audit row). Admission is atomic with Dispose's drain snapshot.
    // An idle-branch retirement (issue #299) passes the pointer it selected as `idle`, so the retirement applies only while
    // the pointer is unchanged and no work is pending against the branch.
    private Task<RetireBranchResult> StartRetire(
        RetireBranchRequest request, AuditCaller principal, IdleRetireGuard? idle = null) =>
        StartRetireEntry(request, principal, idle).Retire;

    private bool NeedsBranchConvergenceForRetire() =>
        _remoteDefaults is { HasBranchHeadResolver: true };

    private PendingRetire StartRetireEntry(
        RetireBranchRequest request, AuditCaller principal, IdleRetireGuard? idle = null, long? existingIntentId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Repository);
        ArgumentException.ThrowIfNullOrEmpty(request.Branch);
        var admissionId = existingIntentId is null && NeedsBranchConvergenceForRetire()
            ? Guid.NewGuid().ToString("N")
            : null;
        PendingRetire entry;
        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfStopping();
            // An idle-branch retirement is maintenance (issue #333): it takes the priority lane, so it is the next turn
            // after the current holder instead of waiting behind every production admitted before it.
            entry = new PendingRetire(Task.FromResult(0L)) { AdmissionId = admissionId };
            // An ensure with no branch intent never shares an identical ensure admitted before this retirement, even
            // while the retirement still waits for its turn (intent-backed ensures decide from branch_intents ids).
            Interlocked.Increment(ref _ensureAdmissionBarrier);
            if (existingIntentId is long existing)
            {
                entry.IntentAdmission = Task.FromResult(existing);
                _liveIntentRuns.Add(existing);
            }
            else if (NeedsBranchConvergenceForRetire())
                entry.IntentAdmission = Task.Run(async () =>
                {
                    var intent = GrantWrite(conn =>
                    {
                        if (entry.AdmissionCancelled)
                            throw new OperationCanceledException("Retirement admission was not durable before the response bound.");
                        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                        var snapshots = new SnapshotStore(conn);
                        var repositoryId = snapshots.GetRepositoryId(request.Repository)
                            ?? snapshots.EnsureRepository(request.Repository, now);
                        var kind = idle is { Upstream: true } ? "delete" : "retire";
                        var admitted = new BranchIntentStore(conn).Admit(repositoryId, request.Branch, kind, null, admissionId,
                            System.Text.Json.JsonSerializer.Serialize(
                                new RetireIntentPayload(request, principal, idle), ServiceJson.Options), now);
                        // Live before it commits, so the rescan never starts a second executor for it (issue #273, B1).
                        lock (_inFlightLock)
                            _liveIntentRuns.Add(admitted.Id);
                        entry.AdmittedIntentId = admitted.Id;
                        entry.AdmissionId = admitted.AdmissionId;
                        return admitted;
                    });
                    if (idle is { Upstream: true })
                        AfterUpstreamDeleteIntentAdmissionForTesting?.Invoke();
                    AfterRetireAdmissionCommitForTesting?.Invoke();
                    WakeBranchIntent(intent.RepositoryId, intent.Branch);
                    return intent.Id;
                }, CancellationToken.None);
            var asked = System.Diagnostics.Stopwatch.GetTimestamp();
            entry.Retire = Task.Run(
                () => ApplyRetireAsync(request, principal, entry, idle, asked),
                CancellationToken.None);
            if (idle is not null)
                _pendingIdleRetires.Add(IdleRetireKey(request.Repository, request.Branch));
            _operations.Add(entry.Retire);
        }
        _ = entry.Retire.ContinueWith(
            t =>
            {
                lock (_inFlightLock)
                    _operations.Remove(t);
                _ = t.Exception;
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return entry;
    }

    // Re-admits one unsettled retire/delete intent at startup (ReplayDurableAdmissions); it waits for its branch head, and
    // a persisted retry deadline is honoured.
    private void ReplayRetirement(BranchIntent intent)
    {
        try
        {
            if (TryDropStaleRetireIntent(intent))
                return;
        }
        catch (Exception ex) when (ex is not (ObjectDisposedException or OperationCanceledException))
        {
            // The rescan retries the decision.
            Log($"branch_intent_drop_check_failed intent_id={intent.Id} error={ex.GetType().Name}");
        }
        if (RetryPending(intent.NextAttemptAt, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()))
        {
            ScheduleIntentRetryAt(intent.NextAttemptAt!.Value);
            return;
        }
        try
        {
            var payload = intent.PayloadJson is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<RetireIntentPayload>(intent.PayloadJson, ServiceJson.Options);
            if (payload is null || payload.Request.Repository is null || payload.Request.Branch is null)
                throw new InvalidOperationException($"Branch intent {intent.Id} has no usable retirement payload.");
            _ = StartRetireEntry(
                payload.Request, payload.Principal, payload.Idle, intent.Id);
        }
        catch (Exception ex)
        {
            Log($"branch_intent_replay_failed intent_id={intent.Id} error={ex.GetType().Name}");
        }
    }

    private async Task<RetireBranchResult> ApplyRetireAsync(
        RetireBranchRequest request, AuditCaller principal, PendingRetire entry, IdleRetireGuard? idle,
        long asked)
    {
        Task turn;
        try
        {
            entry.IntentId = await entry.IntentAdmission.ConfigureAwait(false);
            while (true)
            {
                if (entry.IntentId is > 0
                    && ReadCatalog(conn => new BranchIntentStore(conn).Get(entry.IntentId.Value)) is { } intent)
                {
                    entry.AdmissionId ??= intent.AdmissionId;
                    await WaitForBranchIntentHeadAsync(
                        intent.RepositoryId, intent.Branch, intent.Id, _lifetime.Token).ConfigureAwait(false);
                }
                lock (_inFlightLock)
                {
                    turn = TakeWriteTurnLocked(out _, priority: idle is not null);
                }
                await turn.ConfigureAwait(false);
                bool deferred;
                try
                {
                    deferred = entry.IntentId is > 0
                        && ReadCatalog(conn => RetryPending(
                            new BranchIntentStore(conn).Get(entry.IntentId.Value)?.NextAttemptAt,
                            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
                }
                catch
                {
                    _writeGate.Release();
                    throw;
                }
                if (!deferred)
                    break;
                _writeGate.Release();
            }
        }
        catch
        {
            lock (_inFlightLock)
            {
                _pendingIdleRetires.Remove(IdleRetireKey(request.Repository, request.Branch));
            }
            ForgetPendingIdleRetire(request, idle);
            // A durable intent whose executor stopped (shutdown, or a fault after its admission committed) is left to
            // the rescan or the next start.
            ForgetLiveIntentRun(entry.IntentId ?? entry.AdmittedIntentId);
            throw;
        }
        try
        {
            using var owner = EnterWriterTurn(turn);
            if (idle is not null)
                _metrics.RecordMaintenanceWriterWait(System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds);
            var advanceAdmitted = false;
            lock (_inFlightLock)
            {
                if (idle is not null)
                {
                    _pendingIdleRetires.Remove(IdleRetireKey(request.Repository, request.Branch));
                    // The priority lane lets this retirement overtake an earlier-admitted ensure; an admitted advance
                    // of this very branch makes it busy instead.
                    advanceAdmitted = _latestAdvance.ContainsKey(RemoteUrlIdentity.Normalize(request.Repository) + "\n" + request.Branch);
                }
            }
            using var activity = ServiceTelemetry.Source.StartActivity("retire_branch");
            EnsureLeaseHeld();
            RetireBranchResult result;
            try
            {
                result = RetireBranchLocked(request, principal, idle, advanceAdmitted, intentId: entry.IntentId);
            }
            catch (Exception ex) when (entry.IntentId is > 0)
            {
                // Issue #273 (B1): the turn ended without settling its intent, which stays the branch head. Record its
                // retry time on this same turn; the rescan runs it again then, before any later intent of the branch.
                DeferIntentAfterFailedTurn(entry.IntentId.Value, $"turn_{ex.GetType().Name}");
                throw;
            }
            // Counted when it applies, so a retirement the maintenance step stopped waiting for still shows (issue #333).
            if (idle is { Upstream: true })
                _metrics.RecordUpstreamBranchRetireApplied(result.Retired);
            else if (idle is not null)
                _metrics.RecordIdleBranchRetireApplied(result.Retired);
            return result with { AdmissionId = entry.AdmissionId ?? result.AdmissionId };
        }
        finally
        {
            _writeGate.Release();
            ForgetLiveIntentRun(entry.IntentId);
        }
    }

    private RetireBranchResult RetireBranchLocked(
        RetireBranchRequest request, AuditCaller principal, IdleRetireGuard? idle, bool advanceAdmitted = false,
        long? intentId = null)
    {
        var snapshots = new SnapshotStore(_conn);
        RetireBranchResult result;
        string? intentAdmissionId = null;
        BeginRaw("retire-branch");
        try
        {
            var admittedAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var repositoryId = snapshots.GetRepositoryId(request.Repository)
                ?? snapshots.EnsureRepository(request.Repository, admittedAt);
            var intentKind = idle is { Upstream: true } ? "delete" : "retire";
            var intents = new BranchIntentStore(_conn);
            BranchIntent? intent = null;
            if (intentId is > 0)
            {
                intent = intents.Get(intentId.Value)
                    ?? throw new InvalidOperationException($"Retirement intent {intentId} is missing.");
                intentAdmissionId = intent.AdmissionId;
                if (intent.State == "settled")
                {
                    // A second executor of an intent that already ran (issue #273): it must change nothing.
                    ExecRaw("ROLLBACK;");
                    return new RetireBranchResult { Retired = intent.Outcome is "retired" or "deleted" };
                }
            }
            else if (intentId is null && NeedsBranchConvergenceForRetire())
            {
                intent = intents.Admit(
                    repositoryId, request.Branch, intentKind, null, null,
                    System.Text.Json.JsonSerializer.Serialize(request, ServiceJson.Options), admittedAt);
                intentAdmissionId = intent.AdmissionId;
            }
            BeforeIntentWriteForTesting?.Invoke("retire-branch:" + (intent?.Kind ?? intentKind));
            var branch = snapshots.GetBranch(repositoryId, request.Branch);
            var detail = (string?)null;
            // Rule 5 (#273): an upstream deletion yields to any push admitted after the listing that called the branch
            // deleted (an intent admitted since, or a job), since that listing no longer speaks for the branch.
            bool UpstreamActivitySince(IdleRetireGuard listed) =>
                intents.HasAdmissionSince(repositoryId, request.Branch, listed.ListedAtUnixMs, intent?.Id)
                || new SnapshotJobStore(_conn).HasBranchActivitySince(
                    RemoteUrlIdentity.Normalize(request.Repository), request.Branch, listed.ListedAtUnixMs);
            if (branch is null)
            {
                result = new RetireBranchResult
                {
                    Retired = false,
                    Reason = idle is { Upstream: true } absentListing && UpstreamActivitySince(absentListing)
                        ? BranchGuardReason.UpstreamActivity : null
                };
                // Issue #299: a branch the idle rule retired is gone from the catalog but still in git. When it is then
                // deleted upstream (a manual/delete-push retire whose CAS names the retired head, or no CAS), its
                // retired head is cleared, so no later push expecting that head can resurrect it.
                if (idle is null && snapshots.GetRetiredHead(repositoryId, request.Branch) is { } retiredHead
                    && (request.ExpectedHeadCommit is null
                        || string.Equals(retiredHead, request.ExpectedHeadCommit, StringComparison.OrdinalIgnoreCase))
                    && snapshots.ConsumeRetiredHead(repositoryId, request.Branch))
                    detail = "absent;retired_head_cleared";
            }
            else if (branch.IsDefault)
                result = new RetireBranchResult { Retired = false, Reason = BranchGuardReason.DefaultBranch };
            else if (idle is { } selected && (branch.SnapshotId != selected.SnapshotId || branch.UpdatedAt != selected.UpdatedAt))
                result = new RetireBranchResult { Retired = false, Reason = BranchGuardReason.PointerMoved };
            else if (idle is { Upstream: true } listed
                     && (branch.UpdatedAt >= listed.ListedAtUnixMs || UpstreamActivitySince(listed)))
                // Issue #338: the branch was touched after the listing that called it deleted (re-created upstream and
                // ensured), so that listing no longer speaks for it.
                result = new RetireBranchResult { Retired = false, Reason = BranchGuardReason.UpstreamActivity };
            else if (idle is not null && (advanceAdmitted || new SnapshotJobStore(_conn).BranchesWithPendingWork()
                         .Contains((RemoteUrlIdentity.Normalize(request.Repository), branch.Name))))
                result = new RetireBranchResult { Retired = false, Reason = BranchGuardReason.BranchBusy };
            else if (request.ExpectedHeadCommit is { } expected && !snapshots.BranchHeadMatches(branch.Id, expected))
                result = new RetireBranchResult { Retired = false, Reason = BranchGuardReason.HeadMismatch };
            else if (idle is { Upstream: true })
                // Issue #338: the branch is gone from git too, so no head is recorded (like a delete-push retire); a
                // retired head would let a later push resurrect it.
                result = new RetireBranchResult { Retired = snapshots.DeleteBranch(branch.Id) };
            else if (idle is not null)
            {
                // Issue #299: the branch still exists in git, so its next push expects this head; record it so that push
                // re-creates the branch. A branch with no known head commit is never retired by the idle rule.
                if (snapshots.GetBranchHeadCommit(branch.Id) is not { } head)
                    result = new RetireBranchResult { Retired = false, Reason = BranchGuardReason.NoHeadCommit };
                else
                {
                    result = new RetireBranchResult { Retired = snapshots.DeleteBranch(branch.Id) };
                    snapshots.RecordRetiredHead(branch.RepositoryId, branch.Name, head,
                        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), SnapshotStore.RetiredHeadCauseIdle);
                }
            }
            else
                result = new RetireBranchResult { Retired = snapshots.DeleteBranch(branch.Id) };

            if (intent is not null)
                intents.Settle(
                    intent.Id,
                    intentKind == "delete"
                        ? result.Reason ?? (result.Retired || branch is null ? "deleted" : "already_gone")
                        : result.Retired ? "retired" : branch is null ? "absent" : result.Reason ?? "already_gone",
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            new AuditLogStore(_conn).Append(
                AuditAction.Retire,
                result.Reason is null ? AuditOutcome.Complete : AuditOutcome.Denied,
                actor: principal.Actor,
                repositoryScope: request.Repository,
                detail: principal.Detail(result.Reason ?? detail ?? (result.Retired ? "retired" : "absent")));
            ExecRaw("COMMIT;");
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
        if (intentId is > 0 || NeedsBranchConvergenceForRetire())
            WakeBranchIntent(snapshots.GetRepositoryId(request.Repository)!.Value, request.Branch);
        SchedulePendingRefreshes();
        return result with { AdmissionId = intentAdmissionId };
    }

    /// <summary>
    /// Plans on an independent reader, or executes bounded, revalidated batches under the writer lease.
    /// </summary>
    public RetentionReport RunRetention(bool execute, AuditCaller principal = default)
        => RunRetentionAsync(execute, principal, _lifetime.Token).GetAwaiter().GetResult();

    /// <summary>
    /// The budget of an interactive retention run (issue #303): <c>seconds</c> when given, else
    /// <see cref="RetentionService.PassTimeLimit"/>, never above the configured pass seconds
    /// (<c>SEXTANT_SERVICE_RETENTION_PASS_SECONDS</c>, itself capped at 3600) and at least one second.
    /// </summary>
    public TimeSpan InteractiveRetentionLimit(int? seconds, bool dryRun = false)
        => ClampInteractiveRetention(seconds, _options.Maintenance.RetentionPassSeconds, dryRun);

    // A dry run holds no writer, so its default is the longer RetentionService.DryRunTimeLimit (issue #335); the same
    // ceiling applies to both.
    internal static TimeSpan ClampInteractiveRetention(int? seconds, int configuredSeconds, bool dryRun = false)
    {
        var ceiling = Math.Clamp(configuredSeconds, 1, MaintenancePolicy.MaxRetentionPassSeconds);
        var asked = seconds ?? (int)(dryRun ? RetentionService.DryRunTimeLimit : RetentionService.PassTimeLimit).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(asked, 1, ceiling));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> _oversizedToday = new();

    private long _tailTuningDayTicks;

    private HashSet<long> OversizedToDeferToday()
    {
        var today = _timeProvider.GetUtcNow().UtcDateTime.Date;
        // Tail steps cut down or stepped over (issue #329) get the same one-day memory. Only the caller that moves
        // the day resets the tuning.
        if (Interlocked.Exchange(ref _tailTuningDayTicks, today.Ticks) != today.Ticks)
        {
            _fileSweepCursor.ResetTuning();
            _scheduledFileCursor.ResetTuning();
        }
        var ids = new HashSet<long>();
        foreach (var (id, day) in _oversizedToday)
        {
            if (day == today) ids.Add(id);
            else _oversizedToday.TryRemove(id, out _);
        }
        return ids;
    }

    /// <summary>
    /// A dry run plans on an independent reader. An executed pass computes its plan on a reader too (no writer held),
    /// then runs short batches under the writer, each re-verifying its candidates. An interactive pass releases the gate
    /// between batches so publishes interleave, and its whole time, writer-queue waits included, is bounded by
    /// <paramref name="passLimit"/> (default <see cref="RetentionService.PassTimeLimit"/>). The scheduled
    /// (<paramref name="maintenancePriority"/>) pass runs its batches, tail and vacuum steps back to back inside ONE
    /// priority gate hold (issue #333), releases at <see cref="MaintenancePolicy.RetentionBurstSeconds"/> or when it is
    /// done and queues again on the priority lane; its budget counts only the time it HELD the writer, and
    /// <paramref name="passBound"/> (the tick deadline) ends a wait for a turn. A pass that runs out of time reports what
    /// it computed and deleted so far; cancellation interrupts the current batch, and committed batches stay applied.
    /// <paramref name="auditWait"/> caps the wait for the writer of the closing audit row (default
    /// <see cref="InteractiveRetentionAuditWait"/>; the scheduled pass passes its short cap).
    /// </summary>
    public async Task<RetentionReport> RunRetentionAsync(
        bool execute, AuditCaller principal = default, CancellationToken cancellationToken = default,
        TimeSpan? passLimit = null, TimeSpan? auditWait = null, bool maintenancePriority = false,
        CancellationToken passBound = default)
    {
        if (!execute)
        {
            if (Interlocked.CompareExchange(ref _dryRunRunning, 1, 0) != 0)
                throw new RetentionDryRunBusyException();
            try
            {
                return await RunRetentionCoreAsync(false, principal, cancellationToken, passLimit, auditWait, maintenancePriority, passBound)
                    .ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _dryRunRunning, 0);
            }
        }
        return await RunRetentionCoreAsync(true, principal, cancellationToken, passLimit, auditWait, maintenancePriority, passBound)
            .ConfigureAwait(false);
    }

    private async Task<RetentionReport> RunRetentionCoreAsync(
        bool execute, AuditCaller principal, CancellationToken cancellationToken,
        TimeSpan? passLimit, TimeSpan? auditWait, bool maintenancePriority, CancellationToken passBound)
    {
        var passStartedMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var timer = new RetentionPassTimer
        {
            Priority = maintenancePriority,
            Time = _timeProvider,
            Burst = TimeSpan.FromSeconds(Math.Clamp(
                _options.Maintenance.RetentionBurstSeconds, 0, MaintenancePolicy.MaxRetentionBurstSeconds))
        };
        try
        {
            var report = await RunRetentionPassAsync(execute, principal, cancellationToken, passLimit, timer, passBound).ConfigureAwait(false);
            if (!execute) return report;
            // A priority pass that waited for the writer until the tick deadline and never held it did not run at all.
            if (report.MoreRemaining && report.Batches == 0 && timer.Priority && timer.HeldTime == TimeSpan.Zero
                && timer.WriterWaitMs > 0 && passBound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                report = report with { StopReason = StarvedStopReason };
            // A pass that was cut short without deleting anything asks for a continuation (capped by the scheduler).
            if (report.MoreRemaining && !MadeDeletionProgress(report) && report.StopReason is "time_budget" or "unit_budget")
                report = report with { StopReason = "unit_budget" };
            // Never "more remaining" without a reason (issue #329).
            if (report.MoreRemaining && report.StopReason == null)
                report = report with { StopReason = "work_remaining" };
            if (report.Batches > 0)
            {
                var skipped = await AuditRetentionPassAsync(
                    report, principal, passStartedMs, timer, auditWait ?? InteractiveRetentionAuditWait).ConfigureAwait(false);
                if (skipped)
                {
                    report = report with { AuditSkipped = true };
                    _metrics.RecordRetentionAuditSkipped();
                }
            }
            return report with { Timings = timer.ToTimings(report.Batches) };
        }
        finally
        {
            // Whatever ended the pass, a held burst never outlives it.
            timer.EndTurn(this);
        }
    }

    /// <summary>Stop reason of a scheduled pass that never got the writer before the tick deadline (issue #333).</summary>
    internal const string StarvedStopReason = "starved";

    // How long past its own budget a dry run's pass token waits (a backstop); settable so a test can make it fire first.
    internal TimeSpan DryRunGrace { get; set; } = TimeSpan.FromSeconds(2);

    // A dry run holds one read transaction for up to its whole budget, which pins the WAL: one at a time (issue #335).
    private int _dryRunRunning;

    /// <summary>Test seam: runs after each planning stage of a retention plan (dry run or executed pass), with its planner.</summary>
    internal Action<RetentionService, string>? RetentionPlanStageHook { get; set; }

    /// <summary>Test seam: runs once an executed pass's plan finished, with the pass's cancellation source.</summary>
    internal Action<CancellationTokenSource>? AfterRetentionPlanHook { get; set; }

    internal static bool MadeRetentionProgress(RetentionReport report) =>
        MadeDeletionProgress(report) || report.PagesVacuumed > 0;

    // Rows or blobs actually removed. Vacuumed pages are left out: they only return space and say nothing about whether
    // the work behind a stop reason moved (issue #329).
    internal static bool MadeDeletionProgress(RetentionReport report) =>
        (report.SnapshotsDeleted + report.SnapshotProjectVersionsDeleted + report.ApiSnapshotsDeleted +
            report.FileVersionsDeleted + report.Deleted.Count + (report.SourceTextsDeleted ?? 0) +
            report.ProjectChunkRowsDeleted) > 0;

    // Accumulates the step timings of one executed pass (issue #321). The pass is sequential, so no locking.
    // A priority (scheduled) pass holds the gate across consecutive writes: a "turn" starts at the first write after the
    // gate was released and ends (EndTurn) once its hold reaches Burst, or when the pass ends or a write fails
    // (issue #333). Batches still commit one by one inside the hold; only the gate is kept.
    internal sealed class RetentionPassTimer
    {
        public double PlanMs, WriterWaitMs, DeleteMs, TailMs, VacuumMs, AuditMs;

        private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        private bool _holding;
        private Task? _turn;
        private DateTimeOffset _holdStart;
        private TimeSpan _held;

        // The scheduled pass's writer turns take the priority lane (issue #333).
        public bool Priority { get; init; }

        /// <summary>How long one priority turn may keep the gate before it is released (zero: after every write).</summary>
        public TimeSpan Burst { get; init; }

        /// <summary>The clock the held time is read on (the service's, so tests drive it).</summary>
        public TimeProvider Time { get; init; } = TimeProvider.System;

        /// <summary>
        /// The time the pass has spent against its budget: for a priority pass only the time it HELD the writer (the wait
        /// for turns is not spent); otherwise the wall time since it started.
        /// </summary>
        public TimeSpan BudgetElapsed() => !Priority
            ? System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt)
            : _held + (_holding ? Time.GetUtcNow() - _holdStart : TimeSpan.Zero);

        /// <summary>The total time this pass held the gate (priority passes), including a turn still open.</summary>
        public TimeSpan HeldTime => _held + (_holding ? Time.GetUtcNow() - _holdStart : TimeSpan.Zero);

        /// <summary>Releases the gate if this pass holds it; a no-op otherwise.</summary>
        public void EndTurn(SnapshotService service)
        {
            if (!_holding)
                return;
            _held += Time.GetUtcNow() - _holdStart;
            _holding = false;
            _turn = null;
            service._writeGate.Release();
        }

        public RetentionPassTimings ToTimings(int batches) => new()
        {
            PlanMs = PlanMs, WriterWaitMs = WriterWaitMs, DeleteMs = DeleteMs, TailMs = TailMs,
            VacuumMs = VacuumMs, AuditMs = AuditMs, Batches = batches
        };

        // Runs a writer turn, splitting the time into the wait for the gate and the work under it.
        public async Task<T> WriteAsync<T>(SnapshotService service, Func<Task<T>> work, Action<double> addWork, CancellationToken ct)
        {
            if (Priority)
                return await WriteHeldAsync(service, work, addWork, ct).ConfigureAwait(false);

            var asked = System.Diagnostics.Stopwatch.GetTimestamp();
            long entered = 0;
            try
            {
                return await service.WithWriteAsync(async () =>
                {
                    entered = System.Diagnostics.Stopwatch.GetTimestamp();
                    return await work().ConfigureAwait(false);
                }, ct, Priority).ConfigureAwait(false);
            }
            finally
            {
                var end = System.Diagnostics.Stopwatch.GetTimestamp();
                if (entered == 0)
                    WriterWaitMs += System.Diagnostics.Stopwatch.GetElapsedTime(asked, end).TotalMilliseconds;
                else
                {
                    WriterWaitMs += System.Diagnostics.Stopwatch.GetElapsedTime(asked, entered).TotalMilliseconds;
                    addWork(System.Diagnostics.Stopwatch.GetElapsedTime(entered, end).TotalMilliseconds);
                }
            }
        }

        private async Task<T> WriteHeldAsync<T>(SnapshotService service, Func<Task<T>> work, Action<double> addWork, CancellationToken ct)
        {
            if (!_holding)
            {
                // Counted like any write's turn, so a retirement is never coalesced across it (issue #158).
                Interlocked.Increment(ref service._writeAdmissions);
                Interlocked.Increment(ref service._ensureAdmissionBarrier);
                var asked = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    var turn = service._writeGate.WaitAsync(ct, priority: true);
                    await turn.ConfigureAwait(false);
                    // Set before anything that could throw, so EndTurn always releases a granted turn.
                    _holding = true;
                    _turn = turn;
                    _holdStart = Time.GetUtcNow();
                }
                finally
                {
                    var waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
                    WriterWaitMs += waitMs;
                    service._metrics.RecordMaintenanceWriterWait(waitMs);
                }
            }

            var entered = System.Diagnostics.Stopwatch.GetTimestamp();
            var completed = false;
            try
            {
                using var owner = service.EnterWriterTurn(_turn!);
                service.EnsureLeaseHeld();
                var result = await work().ConfigureAwait(false);
                completed = true;
                return result;
            }
            finally
            {
                addWork(System.Diagnostics.Stopwatch.GetElapsedTime(entered).TotalMilliseconds);
                if (!completed || Burst <= TimeSpan.Zero || Time.GetUtcNow() - _holdStart >= Burst)
                    EndTurn(service);
            }
        }
    }

    internal const string RetentionPassDoneDetail = "execute_pass;done";

    // How far past its budget a scheduled pass may run (issue #321): the closing audit row waits at most
    // RetentionAuditWait for the writer, the post-vacuum checkpoint at most VacuumCheckpointWait, and a batch in
    // flight at the limit rolls back within RollbackSlack.
    internal static TimeSpan RetentionPassMargin => RetentionAuditWait + VacuumCheckpointWait + RollbackSlack;
    internal static readonly TimeSpan RetentionAuditWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RollbackSlack = TimeSpan.FromSeconds(2);

    /// <summary>The audit-row writer wait of an interactive pass: a human caller is waiting on the answer anyway.</summary>
    internal static readonly TimeSpan InteractiveRetentionAuditWait = TimeSpan.FromSeconds(30);

    /// <summary>The audit-row writer wait of the scheduled pass (settable for tests).</summary>
    internal TimeSpan ScheduledRetentionAuditWait { get; set; } = RetentionAuditWait;
    internal const string RetentionPassContinueDetail = "execute_pass;continue";

    // One audit row per executed pass, written after it ends. It records whether the pass asks for a continuation, which
    // is what a restart reads to tell a finished day from one cut short (issue #317). A pass that never reaches this
    // row (a crash, shutdown) leaves no "done" row, so the next tick continues it.
    // The row is stamped with the pass's START, so a pass that straddles UTC midnight closes the day it began on.
    // Returns true when the row was skipped (writer wait cap or failure).
    private async Task<bool> AuditRetentionPassAsync(
        RetentionReport report, AuditCaller principal, long passStartedMs, RetentionPassTimer timer, TimeSpan auditWait)
    {
        // Bounded: a production holding the writer must not hold a finished pass (issue #321). Without the row the
        // next tick continues the day, as after a crash.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (auditWait <= TimeSpan.Zero) await wait.CancelAsync();
        else wait.CancelAfter(auditWait);
        try
        {
            wait.Token.ThrowIfCancellationRequested();
            await timer.WriteAsync(this, () =>
            {
                EnsureLeaseHeld();
                new AuditLogStore(_conn).Append(AuditAction.Retention, AuditOutcome.Complete, actor: principal.Actor,
                    detail: principal.Detail(ScheduledRetentionContinues(report) ? RetentionPassContinueDetail : RetentionPassDoneDetail)
                        + (report.ProjectChunkRowsDeleted > 0 ? $";chunk_rows={report.ProjectChunkRowsDeleted}" : ""),
                    costBytes: report.ReclaimedBytes, timestampMs: passStartedMs);
                return Task.FromResult(0);
            }, ms => timer.AuditMs += ms, wait.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync(
                "maintenance: retention pass audit waited too long for the writer; it is skipped.".AsMemory(),
                _lifetime.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                $"maintenance: retention pass audit failed: {ex.GetType().Name}.".AsMemory(),
                _lifetime.Token).ConfigureAwait(false);
            return true;
        }
    }

    private readonly FileSweepCursor _fileSweepCursor = new();
    // Like the file-version cursor: held for the service lifetime, so each pass continues the source-text walk (issue #336).
    private readonly SourceTextSweepCursor _sourceTextCursor = new();
    // The scheduled pass and its continuations have cursors of their own, so a manual pass moving the shared ones can never
    // make a scheduled slice "complete" a cycle it only walked the tail of: the scheduled day's cycle is a full one.
    private readonly FileSweepCursor _scheduledFileCursor = new();
    private readonly SourceTextSweepCursor _scheduledTextCursor = new();
    internal SourceTextSweepCursor ScheduledSourceTextCursor => _scheduledTextCursor;
    internal SourceTextSweepCursor ManualSourceTextCursor => _sourceTextCursor;

    // One cycle per sweep per scheduled retention day (issue #336). The scheduled pass and its continuations of a UTC day
    // run each sweep (file versions, source texts) through at most one full cycle that STARTED that day; once a cycle
    // is done the later passes skip the sweep and report it finished, so steady churn cannot keep two out-of-phase
    // sweeps open for ever. The next day's scheduled pass starts both over. Manual passes never read or write this state
    // (they only move the shared cursors); a restart forgets it, which costs at most one more cycle.
    private readonly object _sweepDayGate = new();
    private DateOnly _sweepDay;
    private bool _sweepDayFilesDone;
    private bool _sweepDayTextsDone;

    // The scheduled retention day whose chain of passes is running (set when its first pass starts, cleared when the chain
    // ends), so a chain that crosses 00:00 stays one day; null outside the maintenance loop (the pass's own UTC date).
    private DateOnly? _scheduledChainDay;

    private (bool FilesDone, bool TextsDone) BeginScheduledSweepDay()
    {
        lock (_sweepDayGate)
        {
            var today = _scheduledChainDay ?? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
            if (_sweepDay != today)
            {
                _sweepDay = today;
                _sweepDayFilesDone = _sweepDayTextsDone = false;
                _scheduledFileCursor.Reset();
                _scheduledTextCursor.Reset();
            }
            return (_sweepDayFilesDone, _sweepDayTextsDone);
        }
    }

    private void NoteScheduledSweepDone(bool files, bool texts)
    {
        lock (_sweepDayGate)
        {
            _sweepDayFilesDone |= files;
            _sweepDayTextsDone |= texts;
        }
    }

    private async Task<RetentionReport> RunRetentionPassAsync(
        bool execute, AuditCaller principal, CancellationToken cancellationToken, TimeSpan? passLimit,
        RetentionPassTimer timer, CancellationToken passBound = default)
    {
        var scope = new RetentionPassScope();
        try
        {
            var report = await RunRetentionPassCoreAsync(execute, principal, cancellationToken, passLimit, timer, scope, passBound)
                .ConfigureAwait(false);
            return scope.Plan is { } plan
                ? report with { FileSweepAdvanced = plan.FileSweepAdvanced, PlanMs = plan.Report.PlanMs, PlanComplete = plan.Report.PlanComplete }
                : report;
        }
        finally
        {
            if (timer.Priority && execute && scope.Plan is { FileSweepCompleted: true })
                NoteScheduledSweepDone(files: true, texts: false);
        }
    }

    /// <summary>Test seam: the file-version spans one retention plan sweeps (null = the default allowance).</summary>
    internal int? RetentionSweepSpansPerPass { get; set; }

    private sealed class RetentionPassScope
    {
        public RetentionPlan? Plan;
    }

    private async Task<RetentionReport> RunRetentionPassCoreAsync(
        bool execute, AuditCaller principal, CancellationToken cancellationToken, TimeSpan? passLimit,
        RetentionPassTimer timer, RetentionPassScope scope, CancellationToken passBound = default)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("retention");
        activity?.SetTag("sextant.execute", execute);
        var limit = passLimit ?? RetentionService.PassTimeLimit;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var pass = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, passBound);
        // A priority pass spends only its writer-HELD time (timer.BudgetElapsed), never the wall time it waited for turns;
        // passBound (the tick deadline) is what ends such a wait.
        // A dry run's own planning budget reports first (with its partial counts); the pass token only backstops it.
        if (!timer.Priority)
            pass.CancelAfter(execute ? limit : limit + DryRunGrace);
        var report = new RetentionReport { DryRun = !execute };
        var planning = true;
        var passStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!execute)
            {
                // No writer admission, lease acquisition, audit write, or speculative delete.
                using var dryReader = OpenReadConnection();
                var dryPlanner = new RetentionService(dryReader, _options.Retention);
                dryPlanner.PlanStageHook = stage => RetentionPlanStageHook?.Invoke(dryPlanner, stage);
                return dryPlanner.Plan(pass.Token, limit);
            }

            RetentionPlan plan;
            var scheduledDay = timer.Priority ? BeginScheduledSweepDay() : (FilesDone: false, TextsDone: false);
            var planStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                using var reader = OpenReadConnection();
                var planner = new RetentionService(reader, _options.Retention, fileSweep: timer.Priority ? _scheduledFileCursor : _fileSweepCursor,
                    sweepFileVersions: !scheduledDay.FilesDone);
                if (RetentionSweepSpansPerPass is { } spans) planner.SweepSpansPerPass = spans;
                planner.PlanStageHook = stage => RetentionPlanStageHook?.Invoke(planner, stage);
                plan = planner.CreatePlan(pass.Token, limit);
            }
            finally
            {
                timer.PlanMs = System.Diagnostics.Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds;
            }
            if (plan.StopReason != null)
                return plan.Report with { DryRun = false, MoreRemaining = true, StopReason = plan.StopReason };
            planning = false;
            scope.Plan = plan;
            AfterRetentionPlanHook?.Invoke(pass);
            var deferred = OversizedToDeferToday();
            var deferredNow = deferred.Count == 0 ? [] : plan.ExcludeSnapshots(deferred);
            report = plan.Report with { OversizedDeferredSnapshotIds = deferredNow };

            var batchPassBound = false;
            do
            {
                var batch = await timer.WriteAsync(this, () =>
                {
                    pass.Token.ThrowIfCancellationRequested();
                    var remaining = limit - timer.BudgetElapsed();
                    batchPassBound = remaining < RetentionService.BatchTimeLimit;
                    var batchLimit = batchPassBound ? remaining : RetentionService.BatchTimeLimit;
                    var retention = new RetentionService(_conn, _options.Retention) { WriteLockTracker = _writeLocks };
                    return Task.FromResult(retention.ExecuteBatch(plan, pass.Token, batchLimit, () =>
                    {
                        EnsureLeaseHeld();
                        // One row per project or snapshot actually deleted; chunk-only batches and tail batches are
                        // covered by the pass row (which carries the chunk counts).
                        if (retention.ProjectOrSnapshotDeletedInBatch)
                            new AuditLogStore(_conn).Append(AuditAction.Retention, AuditOutcome.Complete,
                                actor: principal.Actor, detail: principal.Detail("execute_batch"));
                    }, passRemaining: remaining, passLimit: limit,
                    // Until the pass has deleted something its first unit always starts, however long planning took.
                    enforceUnitStart: MadeRetentionProgress(report) || report.Oversized > 0));
                }, ms => timer.DeleteMs += ms, pass.Token).ConfigureAwait(false);
                // A batch cut at its own cap while the pass still had time is not "the budget was spent" (issue #329).
                if (batch.StopReason == "time_budget" && !batchPassBound)
                    batch = batch with { StopReason = "batch_budget" };
                report = RetentionService.Combine(report, batch);
                foreach (var oversizedId in batch.OversizedSnapshotIds)
                {
                    _oversizedToday[oversizedId] = _timeProvider.GetUtcNow().UtcDateTime.Date;
                    await Console.Error.WriteLineAsync(
                        $"Retention rolled back the deletion unit of snapshot {oversizedId}: it ran past {_options.Retention.Normalized().UnitMaxSeconds}s. It is skipped for the rest of the UTC day."
                            .AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                foreach (var line in batch.TailSkipped)
                    await Console.Error.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (batch.StopReason != null) return report with { LeftWork = plan.DescribeLeft() };
                // The writer-held budget is spent: stop here (a wall-clock pass is cut by its token instead).
                if (report.MoreRemaining && timer.Priority && timer.BudgetElapsed() >= limit)
                    return report with { MoreRemaining = true, StopReason = "time_budget", LeftWork = plan.DescribeLeft() };
            } while (report.MoreRemaining);

            var sweepPassBound = false;
            var sweep = scheduledDay.TextsDone ? new RetentionReport { DryRun = false, SourceTextsDeleted = 0 }
                : await timer.WriteAsync(this, () =>
            {
                pass.Token.ThrowIfCancellationRequested();
                var remaining = limit - timer.BudgetElapsed();
                sweepPassBound = remaining < RetentionService.BatchTimeLimit;
                var sweepLimit = sweepPassBound ? remaining : RetentionService.BatchTimeLimit;
                // The key build (it reads every referenced hash) has its own budget, so it never eats the walk's slice.
                // It never has less than the floor, and never more than the pass budget plus the floor.
                var keyLimit = TimeSpan.FromTicks(Math.Min(Math.Max(remaining.Ticks, RetentionService.SourceTextKeyBuildFloor.Ticks),
                    (limit + RetentionService.SourceTextKeyBuildFloor).Ticks));
                return Task.FromResult(new RetentionService(_conn, _options.Retention)
                    .SweepSourceTexts(SourceTexts, pass.Token, sweepLimit, keyLimit,
                        timer.Priority ? _scheduledTextCursor : _sourceTextCursor));
            }, ms => timer.TailMs += ms, pass.Token).ConfigureAwait(false);
            if (timer.Priority && sweep.SourceTextCycleCompleted)
                NoteScheduledSweepDone(files: false, texts: true);
            // A scheduled day stays open until the file-version sweep's one cycle is done, even when this pass used up its span allowance.
            var fileSweepOpen = timer.Priority && plan.FileSweepOpen;
            var sweepReason = sweep.StopReason == "time_budget" && !sweepPassBound ? "sweep_budget" : sweep.StopReason;
            report = report with { SourceTextsDeleted = sweep.SourceTextsDeleted,
                SourceTextSweepAdvanced = sweep.SourceTextSweepAdvanced,
                SourceTextCycleCompleted = sweep.SourceTextCycleCompleted,
                MoreRemaining = sweep.MoreRemaining || plan.ApiDeferred || fileSweepOpen,
                StopReason = sweepReason ?? (plan.ApiDeferred ? "api_deferred" : fileSweepOpen ? "sweep_budget" : null),
                LeftWork = plan.DescribeLeft() + (sweep.MoreRemaining ? " texts=more" : " texts=0") };

            // Give disk back (issue #252): bounded incremental_vacuum steps, the writer released between them.
            // A no-op on a catalog created before auto_vacuum=INCREMENTAL (it needs `sextant catalog compact`).
            // Vacuuming stops a safety margin before the pass limit, so the final checkpoint and the report never run
            // on a cancelled pass token; whatever happens, the pages vacuumed so far stay in the report.
            long pagesVacuumed = 0;
            var vacuumPages = _vacuumStepPages;
            var vacuumLeft = false;
            var vacuumStop = limit - TimeSpan.FromTicks(Math.Min(limit.Ticks / 4, VacuumSafetyMargin.Ticks));
            try
            {
                while (true)
                {
                    if (timer.BudgetElapsed() >= vacuumStop)
                    {
                        using var reader = OpenReadConnection();
                        vacuumLeft = IncrementalVacuum.IsIncremental(reader) && IncrementalVacuum.FreelistPages(reader) > 0;
                        break;
                    }
                    var step = await timer.WriteAsync(this, () =>
                    {
                        pass.Token.ThrowIfCancellationRequested();
                        EnsureLeaseHeld();
                        var result = IncrementalVacuum.RunStep(_conn, vacuumPages, _writeLocks);
                        return Task.FromResult((Freed: result.Freed, result.Elapsed, Left: IncrementalVacuum.FreelistPages(_conn)));
                    }, ms => timer.VacuumMs += ms, pass.Token).ConfigureAwait(false);
                    pagesVacuumed += step.Freed;
                    // Aim each step's lock hold at the target: a catalog's per-page cost is only known by measuring.
                    vacuumPages = IncrementalVacuum.NextStepPages(vacuumPages, step.Elapsed, _options.VacuumTargetHold);
                    _vacuumStepPages = vacuumPages;
                    if (step.Freed == 0 || step.Left == 0) break;
                }
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && pass.IsCancellationRequested)
            {
                // Same gate as the time check: only an incremental catalog with free pages has anything left.
                try
                {
                    using var reader = OpenReadConnection();
                    vacuumLeft = IncrementalVacuum.IsIncremental(reader) && IncrementalVacuum.FreelistPages(reader) > 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    vacuumLeft = false;
                }
            }
            report = report with
            {
                PagesVacuumed = pagesVacuumed,
                MoreRemaining = report.MoreRemaining || vacuumLeft,
                VacuumLeft = vacuumLeft,
                // Vacuum progress wins over a reason that only says "run again"; a stuck reason stays visible.
                StopReason = vacuumLeft && report.StopReason is null or "api_deferred" or "sweep_budget" or "work_remaining"
                    ? "vacuum_budget" : report.StopReason
            };

            if (pagesVacuumed > 0)
                await TruncateWalAfterVacuumAsync(lifetime.Token, timer).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && pass.IsCancellationRequested)
        {
            // The budget ended while still planning (issue #335): nothing is known to be eligible or not. The pass token is
            // only a backstop here: the planner's own budget normally reports first, with its partial counts; when this
            // token fires first (a dry run's grace window lost) the report is empty and says so.
            return planning
                ? report with
                {
                    MoreRemaining = true,
                    StopReason = RetentionService.PlanBudgetStopReason,
                    PlanComplete = false,
                    PlanMs = System.Diagnostics.Stopwatch.GetElapsedTime(passStarted).TotalMilliseconds
                }
                : report with { MoreRemaining = true, StopReason = "time_budget" };
        }
        return report;
    }

    private static readonly TimeSpan VacuumSafetyMargin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VacuumCheckpointWait = TimeSpan.FromSeconds(5);

    // Never blocks the writer on a reader: the checkpoint itself waits for nothing, and a busy or timed-out attempt is
    // logged once and left for the next pass. The wait for the writer turn is bounded and uses the service lifetime,
    // not the (possibly spent) pass token.
    private async Task TruncateWalAfterVacuumAsync(CancellationToken lifetimeToken, RetentionPassTimer timer)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        wait.CancelAfter(VacuumCheckpointWait);
        try
        {
            var complete = await timer.WriteAsync(
                this, () => Task.FromResult(IncrementalVacuum.CheckpointTruncate(_conn, _writeLocks)),
                ms => timer.VacuumMs += ms, wait.Token).ConfigureAwait(false);
            if (!complete)
                await Console.Error.WriteLineAsync(
                    "maintenance: WAL checkpoint after vacuum was busy; it is retried next pass.".AsMemory(),
                    lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!lifetimeToken.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync(
                "maintenance: WAL checkpoint after vacuum waited too long for the writer; it is retried next pass.".AsMemory(),
                lifetimeToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                $"maintenance: WAL checkpoint after vacuum failed: {ex.GetType().Name}.".AsMemory(),
                lifetimeToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Collects a point-in-time observability snapshot (criterion 5) — indexing latency, queue delay,
    /// success/completeness, worker capacity, storage, cache reuse, query latency, alerts, and per-
    /// repository cost attribution. Reads through an INDEPENDENT read connection so collecting metrics
    /// never blocks the writer or a running index. OPERATOR-ONLY data — the host exposes it on the control
    /// plane only (criterion-1 leakage guard).
    /// </summary>
    public MetricsSnapshot CollectMetrics(AlertThresholds? thresholds = null, IReadOnlySet<string>? visibleRepositoryKeys = null)
    {
        using var conn = OpenReadConnection();
        var collector = new MetricsCollector(
            conn, _paths, _options.CatalogDbPath, _metrics, HasWorkerCapacity, _timeProvider, _writeLocks,
            _freshness, _sizeCache);
        return collector.Collect(thresholds, visibleRepositoryKeys);
    }

    /// <summary>
    /// Returns the most recent audit rows (criterion 5, security audit trail). OPERATOR-ONLY — the host
    /// gates this behind the CONTROL token, never the query token, so a query-plane tenant can never read
    /// another tenant's audit rows (criterion-1 leakage guard).
    /// </summary>
    public IReadOnlyList<AuditEntry> RecentAudit(
        int limit = 100, string? action = null, string? repositoryScope = null)
    {
        using var conn = OpenReadConnection();
        return new AuditLogStore(conn).Recent(limit, action, repositoryScope);
    }

    /// <summary>Per-repository cost attribution rolled up from the audit log (criterion 5). OPERATOR-ONLY.</summary>
    public IReadOnlyList<AuditCostAttribution> CostAttribution()
    {
        using var conn = OpenReadConnection();
        return new AuditLogStore(conn).CostByRepository();
    }

    /// <summary>
    /// Writes a consistent backup of the catalog + immutable artifact volume into
    /// <paramref name="destinationDir"/> (criterion 6). Runs under the writer gate so the online catalog
    /// copy races no concurrent write, and records a durable audit row. The backup NEVER contains secrets;
    /// the manifest documents the credentials boundary an operator re-provides on restore.
    /// </summary>
    public BackupManifest CreateBackup(string destinationDir, AuditCaller principal = default)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("backup");
        return WithWrite(() => CreateBackupLocked(destinationDir, principal));
    }

    /// <summary>
    /// <see cref="CreateBackup"/> for request threads (issue #148): waits for the writer asynchronously and
    /// gives up when <paramref name="cancellationToken"/> fires before the writer is acquired.
    /// </summary>
    public async Task<BackupManifest> CreateBackupAsync(
        string destinationDir, AuditCaller principal = default, CancellationToken cancellationToken = default)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("backup");
        return await WithWriteAsync(
            () => Task.FromResult(CreateBackupLocked(destinationDir, principal)), cancellationToken).ConfigureAwait(false);
    }

    private BackupManifest CreateBackupLocked(string destinationDir, AuditCaller principal)
    {
        var manifest = ServiceBackup.Create(
            _conn, IndexDatabase.LatestSchemaVersion, _paths, destinationDir,
            configFingerprint: _options.DefaultConfigHash ?? "none");
        new AuditLogStore(_conn).Append(
            AuditAction.Backup, AuditOutcome.Complete,
            actor: principal.Actor,
            detail: principal.Detail($"schema_{manifest.SchemaVersion}"));
        return manifest;
    }

    /// <summary>
    /// Evaluates whether this service meets its documented pilot exit criteria + rollback preconditions for
    /// the given workload class (criterion 7), so the go/no-go decision is EXERCISED rather than only
    /// written down. Every capability is derived from the service's ACTUAL state — never a request
    /// parameter: the issue-#76 hard precondition (an UNTRUSTED multi-tenant pilot is not ready until
    /// out-of-process OS-hard worker isolation exists — the current sandbox is in-process defense-in-depth)
    /// reads <see cref="HardOsIsolationAvailable"/>; the DR-proven signal reads a durable successful backup
    /// row from the audit log; the control-plane-secured signal reads whether a control token is configured.
    /// </summary>
    public PilotReadinessReport EvaluatePilotReadiness(PilotWorkloadClass workloadClass)
    {
        var metrics = CollectMetrics();
        bool recentBackup;
        using (var conn = OpenReadConnection())
            recentBackup = new AuditLogStore(conn).HasAction(AuditAction.Backup, AuditOutcome.Complete);

        return PilotReadiness.Evaluate(new PilotReadinessInput
        {
            WorkloadClass = workloadClass,
            AuthorizationEnabled = _options.ReadPolicy.Enabled,
            ControlPlaneSecured = _options.ControlToken is { Length: > 0 },
            SandboxEnforced = _options.Sandbox.Enabled,
            HardOsIsolationAvailable = HardOsIsolationAvailable,
            RecentBackupAvailable = recentBackup,
            CatalogRecovered = RecoveryCompleted,
            WorkerCapacityAvailable = HasWorkerCapacity,
            RecentJobs = metrics.RecentJobs,
            Alerts = metrics.Alerts
        });
    }

    /// <summary>
    /// Registers (or updates) an OPEN pull-request snapshot retention root (Phase 17 criterion 4): the
    /// snapshot indexed for a PR head must not be reclaimed by retention while the PR is open, so an open-PR
    /// reviewer's cross-repo/find-references queries keep resolving. Idempotent per <c>(repository, pr)</c>.
    /// When <paramref name="snapshotId"/> is omitted the PR head commit is resolved to its complete snapshot.
    /// Fails closed — returns false without writing a root — when the repository is unknown OR (no snapshot id
    /// supplied AND no complete snapshot exists for the head commit); a NULL-snapshot root protects nothing,
    /// so it is never persisted.
    /// </summary>
    public bool RegisterPullRequestSnapshot(
        string repositoryRemoteUrl, int prNumber, string headCommitSha, long? snapshotId = null)
    {
        return WithWrite(() =>
        {
            var snapshots = new SnapshotStore(_conn);
            if (snapshots.GetRepositoryId(repositoryRemoteUrl) is not long repoId)
                return false;

            long? resolved;
            if (snapshotId is long supplied)
            {
                // A caller-supplied id must be a COMPLETE snapshot that belongs to THIS repository and was
                // indexed at the PR head commit — otherwise the "protection" would pin the wrong (or a
                // partial) snapshot while the real open-PR head goes unprotected. Validate, else fail closed.
                if (snapshots.GetById(supplied) is not { Status: SnapshotStatus.Complete } row
                    || row.RepositoryId != repoId
                    || snapshots.GetCommitSha(row.CommitId) != headCommitSha)
                    return false;
                resolved = supplied;
            }
            else
            {
                resolved = snapshots.ResolveCompleteSnapshotByCommit(repoId, headCommitSha);
            }
            if (resolved is null)
                return false;

            var prStore = new PullRequestSnapshotStore(_conn);
            prStore.Register(repoId, prNumber, resolved, headCommitSha,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return true;
        });
    }

    /// <summary>
    /// Marks a pull-request retention root CLOSED (Phase 17 criterion 4): once closed the snapshot is no
    /// longer protected as an open-PR root and becomes eligible for the normal retention window/quota.
    /// Idempotent; returns false when the repository is unknown.
    /// </summary>
    public bool ClosePullRequestSnapshot(string repositoryRemoteUrl, int prNumber)
    {
        return WithWrite(() =>
        {
            var snapshots = new SnapshotStore(_conn);
            if (snapshots.GetRepositoryId(repositoryRemoteUrl) is not long repoId)
                return false;

            var prStore = new PullRequestSnapshotStore(_conn);
            prStore.Close(repoId, prNumber, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return true;
        });
    }

    /// <summary>Service AVAILABILITY: the catalog is reachable and this instance holds the writer lease.</summary>
    public bool IsAvailable => !_disposed;
    /// <summary>
    /// Worker CAPACITY, distinct from availability (health vs readiness): whether this node can currently
    /// accept production work. A node whose worker is the unavailable placeholder is available for QUERIES
    /// but reports no capacity, so an operator can distinguish "service up" from "no worker".
    /// </summary>
    public bool HasWorkerCapacity => _worker is not UnavailableSnapshotWorker;

    private EnsureSnapshotResult Attach(SnapshotJobRow job, bool existed, SnapshotCoverage? coverage = null)
    {
        // The reported verdict never contradicts the snapshot's durable coverage (issue #119), even for a
        // terminal job recorded before the coverage was reconciled.
        var partialByCoverage = job.Status == SnapshotJobStatus.Complete && coverage is { IsPartial: true };
        var status = partialByCoverage ? SnapshotJobStatus.Partial : job.Status;
        return new EnsureSnapshotResult
        {
            JobId = job.Id,
            IdentityHash = job.IdentityHash,
            Status = status,
            SnapshotId = job.SnapshotId,
            Attached = existed,
            Reason = status == SnapshotJobStatus.Complete
                ? null
                : partialByCoverage ? PartialCoverageReason(coverage!) : job.LastError,
            Coverage = coverage
        };
    }

    // The durable coverage record for a snapshot (issue #119), or null when none was recorded. Reads the
    // writer connection, so the caller MUST hold the write gate.
    private SnapshotCoverage? CoverageFor(long? snapshotId) => CoverageOn(_conn, snapshotId);

    private static SnapshotCoverage? CoverageOn(SqliteConnection conn, long? snapshotId) =>
        snapshotId is long id ? new SnapshotCoverageStore(conn).Get(id) : null;

    private static string PartialCoverageReason(SnapshotCoverage coverage) =>
        "snapshot coverage is partial: " + string.Join(" ", coverage.Reasons);

    // Records the verdict of a job attached to an already-PUBLISHED snapshot (no worker run), taken from the
    // snapshot's DURABLE coverage record (issue #119): a snapshot recorded as partial is never reported
    // complete. Coverage is only read here — reuse never backfills or rewrites it.
    private static void RecordPublishedVerdict(
        SnapshotJobStore jobs, long jobId, long snapshotId, SnapshotCoverage? coverage)
    {
        if (coverage is { IsPartial: true })
            jobs.MarkResult(jobId, SnapshotJobStatus.Partial, snapshotId, PartialCoverageReason(coverage));
        else
            jobs.MarkResult(jobId, SnapshotJobStatus.Complete, snapshotId);
    }

    // A terminal job is only safe to attach to when its recorded outcome is still backed by durable state:
    // a complete/partial job MUST still point at a published COMPLETE snapshot that carries this exact
    // identity. If retention has since reclaimed that snapshot (its snapshot_id NULLed via ON DELETE SET
    // NULL, or the row replaced), the "complete" result is a phantom and the job must be regenerated.
    // Unsupported carries no snapshot to verify, so it remains terminal as recorded. A cancelled job is never reused (a
    // later ensure produces it), and so is a failure, except a sandbox budget abort under an older policy (issue #245): the worker has since learned
    // to publish a partial snapshot inside the budget, so the identity is produced again, once per policy.
    private bool TerminalResultUsable(
        SnapshotJobRow job, string identityHash, SnapshotStore snapshots, SnapshotJobStore jobs)
    {
        if (job.Status == SnapshotJobStatus.Failed)
            return !IsStaleBudgetAbort(job, jobs);
        if (job.Status == SnapshotJobStatus.Cancelled)
            return false;
        if (job.Status is not (SnapshotJobStatus.Complete or SnapshotJobStatus.Partial))
            return true;
        if (job.SnapshotId is not long id || snapshots.GetById(id) is not { Status: SnapshotStatus.Complete })
            return false;
        var byIdentity = snapshots.GetByIdentityHash(identityHash);
        return byIdentity is not null && byIdentity.Id == id;
    }

    // Whether a failed job is a budget abort recorded under a policy other than this service's (or before the
    // policy was recorded at all), and so should be produced again rather than reused (issue #245).
    private bool IsStaleBudgetAbort(SnapshotJobRow job, SnapshotJobStore jobs)
    {
        var diagnostics = jobs.GetDiagnostics(job.Id);
        var exceeded = diagnostics.Any(d => d.Code == EvaluationBudgetPolicy.ExceededCode);
        var recorded = diagnostics.FirstOrDefault(d => d.Code == EvaluationBudgetPolicy.PolicyCode)?.Message;
        return EvaluationBudgetPolicy.IsStaleAbort(
            exceeded, recorded, job.LastError, EvaluationBudgetPolicy.Token(_options.Sandbox));
    }

    // Validation before we trust a worker's terminal status: a worker that claims Complete/Partial MUST
    // have actually published a complete snapshot whose identity_hash equals the REQUESTED identity;
    // otherwise we downgrade to Failed so the catalog never records a "complete" job pointing at data that
    // is not really servable, or at a snapshot for a different identity (a worker/config mismatch).
    private static SnapshotWorkResult ValidateWorkerResult(SnapshotWorkResult result, string expectedHash, SnapshotStore snapshots)
    {
        if (result.Status is SnapshotJobStatus.Complete or SnapshotJobStatus.Partial)
        {
            var published = result.SnapshotId is long id ? snapshots.GetById(id) : null;
            var byIdentity = snapshots.GetByIdentityHash(expectedHash);
            if (published is not { Status: SnapshotStatus.Complete }
                || byIdentity is null || byIdentity.Id != result.SnapshotId)
            {
                return result with
                {
                    Status = SnapshotJobStatus.Failed,
                    SnapshotId = null,
                    Error = "worker reported success but published no complete snapshot for the requested identity"
                };
            }
        }
        return result;
    }

    private static SnapshotJobDiagnostic FailureDiagnostic(long jobId, string message) => new()
    {
        JobId = jobId,
        Severity = JobDiagnosticSeverity.Error,
        Code = "worker_exception",
        Message = message
    };

    // A structured diagnostic for a provisioning-classified outcome. Distinct CODE from worker_exception so
    // an operator can tell "clone will retry" (provisioning_transient) and "clone gave up after N tries"
    // (provisioning_attempts_exhausted) apart from a generic worker crash. Message is pre-redacted.
    private static SnapshotJobDiagnostic ProvisioningDiagnostic(long jobId, string code, string message) => new()
    {
        JobId = jobId,
        Severity = JobDiagnosticSeverity.Error,
        Code = code,
        Message = message
    };

    // The result of an ensure that actually RAN the worker this call (as opposed to Attach, which reuses a
    // prior terminal). Attached=false so cost is attributed and cache-reuse metrics stay honest even when
    // the run ended in a transient requeue (status=queued) rather than a terminal outcome.
    private EnsureSnapshotResult Produced(SnapshotJobRow job) => new()
    {
        JobId = job.Id,
        IdentityHash = job.IdentityHash,
        Status = job.Status,
        SnapshotId = job.SnapshotId,
        Attached = false,
        Reason = job.Status == SnapshotJobStatus.Complete ? null : job.LastError
    };

    // Records the durable audit row for an ensure request (criterion 5): outcome + repository scope +
    // worker cost (index milliseconds), attributed to the requesting principal's non-reversible hash. Cost
    // is recorded only for a job that actually RAN (an attach reuses prior work and has no new cost). The
    // caller holds the write gate — the row is written in the same hold that settled the result, so it is
    // durable before any caller observes the result, even one that already disconnected (issue #148).
    private void RecordEnsureAuditLocked(EnsureSnapshotRequest request, EnsureSnapshotResult result, AuditCaller principal,
        DefaultRepairRefusal? repairRefusal = null)
    {
        var jobs = new SnapshotJobStore(_conn);
        var jobId = result.JobId!.Value;
        var job = jobs.GetJob(jobId);
        long? costMs = !result.Attached && job is { StartedAt: long s, CompletedAt: long c } && c >= s
            ? c - s
            : null;
        var audit = new AuditLogStore(_conn);
        audit.Append(
            AuditAction.Ensure,
            MapOutcome(result.Status),
            actor: principal.Actor,
            repositoryScope: request.RepositoryRemoteUrl,
            detail: principal.Detail($"job_{jobId}{SdkPinAuditSuffix(jobs.GetDiagnostics(jobId))}{ForcedAuditSuffix(request)}"
                + (request.RebuildGeneration is { } generation ? $";rebuild={generation};identity={result.IdentityHash}" : "")),
            costIndexMs: costMs);
        var refusal = BranchAdvanceRefusal(request, result);
        if (refusal is not null)
        {
            audit.Append(AuditAction.BranchAdvanceRefused, AuditOutcome.Denied,
                actor: principal.Actor, repositoryScope: request.RepositoryRemoteUrl,
                detail: principal.Detail($"job_{jobId};branch_advance_refused={refusal}"));
            Log(System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = AuditAction.BranchAdvanceRefused,
                job_id = result.JobId,
                repository = RemoteUrlIdentity.Normalize(request.RepositoryRemoteUrl),
                branch = request.BranchName ?? "main",
                snapshot_id = result.SnapshotId,
                expected_head = request.ExpectedHeadCommit,
                observed_head = repairRefusal?.ObservedHead,
                fresh_head = repairRefusal?.FreshHead,
                reason = repairRefusal?.Reason ?? refusal,
                detail = repairRefusal?.Reason == AncestryUnknown ? repairRefusal.Detail : null
            }));
        }
    }

    private string? BranchAdvanceRefusal(EnsureSnapshotRequest request, EnsureSnapshotResult result)
    {
        if (result.BranchAdvanced != false || result.SnapshotId is not long target || request.SuppressesBranchUpdate)
            return null;
        var snapshots = new SnapshotStore(_conn);
        var name = request.BranchName ?? "main";
        var repo = snapshots.GetRepositoryId(request.RepositoryRemoteUrl);
        var branch = repo is long id ? snapshots.GetBranch(id, name) : null;
        if (branch?.SnapshotId == target)
            return null;
        if (request.ExpectedHeadCommit is { } expected && !snapshots.BranchHeadMatches(branch?.Id, expected, repo, name))
            return "expected_head_mismatch";
        if (request.BranchHeadSequence is long sequence && branch?.HeadSequence >= sequence)
            return "head_sequence_not_newer";
        return "pointer_not_selected";
    }

    // SVC-6: `forced` is informational only (it never bypasses the head CAS); the audit trail records it.
    private static string ForcedAuditSuffix(EnsureSnapshotRequest request) => request.Forced == true ? ";forced" : "";

    // Issue #113: the audit row flags a job whose snapshot was built with a substituted SDK (or that failed
    // SDK resolution / could not restore a neutralized global.json), e.g. "job_42;sdk_pin_overridden", so the
    // audit trail — not just the job's diagnostics — shows it. Empty for every other job (detail unchanged).
    internal static string SdkPinAuditSuffix(IReadOnlyList<SnapshotJobDiagnostic> diagnostics)
    {
        string[] flagged =
        [
            LocalIndexerSnapshotWorker.SdkPinOverriddenCode,
            LocalIndexerSnapshotWorker.SdkResolutionFailedCode,
            LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode
        ];
        var present = flagged.Where(code => diagnostics.Any(d => string.Equals(d.Code, code, StringComparison.Ordinal)));
        return string.Concat(present.Select(code => ";" + code));
    }

    // Maps a job status to an audit outcome. Non-terminal (queued/running) is recorded as accepted;
    // cancelled is recorded as error (it carries no usable result); coalesced (issue #273) as coalesced.
    private static string MapOutcome(string status) => status switch
    {
        SnapshotJobStatus.Complete => AuditOutcome.Complete,
        SnapshotJobStatus.Partial => AuditOutcome.Partial,
        SnapshotJobStatus.Failed => AuditOutcome.Failed,
        SnapshotJobStatus.Unsupported => AuditOutcome.Unsupported,
        SnapshotJobStatus.Cancelled => AuditOutcome.Error,
        SnapshotJobStatus.Coalesced => AuditOutcome.Coalesced,
        _ => AuditOutcome.Accepted
    };

    // An independent, short-lived READ connection to the catalog for metrics/audit/control-plane reads, so
    // operator observability and job-status polling never contend with the writer (WAL supports concurrent
    // readers). Read-only mode so a read can never mutate the catalog.
    private SqliteConnection OpenReadConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.CatalogDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true
        }.ToString();
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        ReadInterrupt.Attach(conn);
        return conn;
    }

    // Runs a control-plane read on an independent read connection inside ONE read transaction (issue #148):
    // a consistent WAL snapshot of the last COMMITTED catalog state, never waiting on the write gate — so
    // status/coverage/resolve stay prompt while a worker holds the writer for a long index. Every durable
    // ensure step commits before its result is returned, so a read after an ensure returns sees that result
    // (read-your-writes).
    private T ReadCatalog<T>(Func<SqliteConnection, T> read)
    {
        using var conn = OpenReadConnection();
        ExecOn(conn, "BEGIN;");
        try
        {
            return read(conn);
        }
        finally
        {
            ExecOn(conn, "COMMIT;");
        }
    }

    private static void ExecOn(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private T WithWrite<T>(Func<T> work)
    {
        // Synchronous callers only (startup reconcile, the sync retention/backup/PR-root APIs): request threads
        // use WithWriteAsync with their own token (issue #148), so this explicitly opts out of cancellation.
        Interlocked.Increment(ref _writeAdmissions);
        Interlocked.Increment(ref _ensureAdmissionBarrier);
        var turn = _writeGate.WaitAsync(CancellationToken.None);
        turn.GetAwaiter().GetResult();
        try
        {
            using var owner = EnterWriterTurn(turn);
            EnsureLeaseHeld();
            return work();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<T> WithWriteAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default, bool priority = false)
    {
        // Counted like a control write's turn, so a retirement is never coalesced across another write (issue #158).
        Interlocked.Increment(ref _writeAdmissions);
        Interlocked.Increment(ref _ensureAdmissionBarrier);
        // A maintenance write (issue #333) takes the priority lane and records how long it waited, so starvation shows.
        var asked = System.Diagnostics.Stopwatch.GetTimestamp();
        var turn = _writeGate.WaitAsync(cancellationToken, priority);
        await turn.ConfigureAwait(false);
        if (priority)
            _metrics.RecordMaintenanceWriterWait(System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds);
        try
        {
            using var owner = EnterWriterTurn(turn);
            EnsureLeaseHeld();
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // Test-only diagnostic: a token identifies the actual granted turn, not merely a held gate.
    // AsyncLocal follows the worker across awaits; an inherited token is rejected after its turn ends.
    internal void EnableWriterTurnGuardForTesting()
    {
        _guardWriterAccess = true;
        _db.SetWriterAccessGuard(AssertWriterTurn, () => Interlocked.Increment(ref _writerTraceViolations));
    }

    internal Action? ControlWaitStartedForTesting { get; set; }
    internal Action? RetireWaitStartedForTesting { get; set; }
    internal Action? AfterRetireAdmissionCommitForTesting { get; set; }
    internal Action? AfterUpstreamDeleteIntentAdmissionForTesting { get; set; }
    internal int WriterTraceViolationsForTesting => Volatile.Read(ref _writerTraceViolations);

    private void AssertWriterTurn()
    {
        if (_guardWriterAccess && !_writeGate.Owns(_writerTurn.Value))
            throw new InvalidOperationException("The shared writer connection requires the current writer turn.");
    }

    private IDisposable EnterWriterTurn(Task turn)
    {
        if (_guardWriterAccess && !_writeGate.Owns(turn))
            throw new InvalidOperationException("Cannot enter a writer turn that is not currently granted.");
        var previous = _writerTurn.Value;
        _writerTurn.Value = turn;
        return new WriterTurnScope(_writerTurn, previous);
    }

    private sealed class WriterTurnScope(AsyncLocal<Task?> current, Task? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }

    // True once this instance may no longer write: the lease was stolen (issue #38) or released by Dispose.
    internal bool LeaseLost => _lease.IsLost || _leaseReleased;

    // How many writes have been admitted to the write gate so far (issue #158); lets a test tell that a control write
    // has been submitted before its caller goes away.
    internal long WriteAdmissions => Interlocked.Read(ref _writeAdmissions);

    // Fail closed if this instance lost the single-writer lease (it expired and another writer stole it):
    // writing anyway would race the new owner on one SQLite database (issue #38).
    private void EnsureLeaseHeld()
    {
        if (LeaseLost)
            throw new InvalidOperationException(
                "This service lost the single-writer lease (it expired and was stolen by another writer); " +
                "refusing to write to avoid racing the new owner (issue #38).");
    }

    /// <summary>
    /// Shuts the data plane down (issue #148 ordering): close admission, stop production (cancel the service
    /// lifetime, so each in-flight worker is cancelled and its job requeued), drain the in-flight ensure
    /// operations — bounded by <see cref="ServiceOptions.ShutdownDrainTimeout"/> — so their requeue/result
    /// writes land while the lease is still held, THEN release the lease and close the catalog. A worker that
    /// ignores cancellation past the bound may still be writing, so the lease is then ABANDONED rather than
    /// released (see <see cref="ProductionDrained"/>).
    /// </summary>
    public void Dispose()
    {
        lock (_inFlightLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        StopProduction();
        if (!DrainOperations(_options.ShutdownDrainTimeout))
        {
            // A straggler outlived the drain and may still commit through the shared writer connection.
            // Releasing the lease row now would let another writer start while it can: abandon it instead —
            // every write probe fails closed (its write session aborts at the next batch boundary) and the row
            // expires by its TTL, as for a crashed holder, after which the next owner's startup reconcile
            // requeues the still-running job. The catalog, write gate and lifetime stay open for the straggler
            // (process exit reclaims them); the host reports the timeout via ProductionDrained.
            ProductionDrained = false;
            _lease.Abandon();
            _lease.Dispose();
            return;
        }
        _leaseReleased = true;
        _lease.Dispose();
        _db.SetWriterAccessGuard(null);
        if (_ownsDatabase)
            _db.Dispose();
        _maintenanceTimer?.Dispose();
        DisposeObservability();
        _maintenanceTick.Dispose();
        _maintenanceTicked.Dispose();
        _lifetime.Dispose();
    }

    private bool DrainOperations(TimeSpan timeout)
    {
        Task[] pending;
        lock (_inFlightLock)
            pending = [.. _operations];
        if (pending.Length == 0)
            return true;
        try
        {
            return Task.WhenAll(pending).Wait(timeout, CancellationToken.None);
        }
        catch (AggregateException)
        {
            // Faulted/cancelled operations have finished — their outcome is already durable.
            return true;
        }
    }

    // One identity's in-flight production (issue #148). Production is assigned under _inFlightLock before the
    // entry is published to the registry, so readers (who take the same lock) always see it set.
    private sealed class InFlightProduction(long jobId)
    {
        public long JobId { get; } = jobId;
        public Task<EnsureSnapshotResult> Production { get; set; } = null!;
    }

    // The pointer an idle-branch retirement selected (issue #299): it applies only while the branch still has exactly it.
    private readonly record struct IdleRetireGuard(long? SnapshotId, long UpdatedAt, bool Upstream = false, long ListedAtUnixMs = 0);
    private sealed record RetireIntentPayload(RetireBranchRequest Request, AuditCaller Principal, IdleRetireGuard? Idle);

    // A retirement waiting for (or holding) its write-gate turn.
    private sealed class PendingRetire(Task<long> intentAdmission)
    {
        public Task<long> IntentAdmission { get; set; } = intentAdmission;
        public long? IntentId { get; set; }
        public long? AdmittedIntentId { get; set; }
        public string? AdmissionId { get; set; }
        private int _admissionCancelled;
        public bool AdmissionCancelled => Volatile.Read(ref _admissionCancelled) != 0;
        public void CancelAdmission() => Volatile.Write(ref _admissionCancelled, 1);
        public Task<RetireBranchResult> Retire { get; set; } = null!;
    }
}

public sealed class RetireAdmissionUnavailableException(string? admissionId)
    : Exception("The branch retirement was not durably admitted before the response bound.")
{
    public string? AdmissionId { get; } = admissionId;
}

/// <summary>
/// The default worker for a query-only / not-yet-provisioned node: it supports no production. A node with
/// this worker is AVAILABLE for queries but reports no worker capacity (health vs readiness), and any
/// ensure request resolves to an <see cref="SnapshotJobStatus.Unsupported"/> job rather than silently
/// hanging queued forever. Production deployments register a real worker (a local in-process indexer, or a
/// ProcessStack-scheduled worker in Phase 14).
/// </summary>
public sealed class UnavailableSnapshotWorker : ISnapshotWorker
{
    public Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken) =>
        Task.FromResult(SnapshotWorkResult.Unsupported(
            "no snapshot worker is configured on this node; it serves published snapshots for query only"));
}

/// <summary>
/// One admitted branch-advance ensure (issue #273), tracked per branch for default refresh deduplication and feature
/// linked-successor handovers. Mutable fields are guarded by the service's in-flight lock.
/// </summary>
internal sealed class AdvanceTicket
{
    public required string BranchKey { get; init; }
    public required string Hash { get; init; }
    public required string CommitSha { get; init; }
    /// <summary>The compare-and-swap admitted with this request, before any earlier handover is applied.</summary>
    public required string ExpectedHeadCommit { get; init; }

    public Task Persisted { get; set; } = Task.CompletedTask;

    /// <summary>Set once its turn is granted: from then on it is produced (or attached), never superseded.</summary>
    public bool Started { get; set; }
    public AdvanceTicket? SupersededBy { get; set; }

    /// <summary>Whether its turn was taken in the expedited (default-branch) lane.</summary>
    public bool Expedited { get; init; }

    /// <summary>Set when its ensure operation has finished, whatever the outcome.</summary>
    public bool Ended { get; set; }

    /// <summary>Whether the finished ensure left a job that is, or produced, a snapshot (not a failure or a throw).</summary>
    public bool Produced { get; set; }
}
