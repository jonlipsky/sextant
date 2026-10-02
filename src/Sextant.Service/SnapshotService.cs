using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Service.Backup;
using Sextant.Service.Contributions;
using Sextant.Service.Observability;
using Sextant.Service.Rollout;
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
    private readonly SqliteConnection _conn;
    private readonly WriterLease _lease;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly bool _ownsDatabase;
    private readonly IContributionAuthorizer _authorizer;
    private readonly IGitContentProvider _gitContent;
    private readonly ContributionPolicy _contributionPolicy;
    private readonly RemoteDefaultBranchLookup? _remoteDefaults;
    private readonly ServiceMetrics _metrics = new();

    // Issue #148: the service-owned lifetime every ensure operation + worker run is bound to (cancelled only
    // by StopProduction/Dispose), the in-flight production registry keyed by identity hash, and the set of
    // live ensure operations Dispose drains. _inFlightLock guards both collections and _disposed's transition
    // (admission and Dispose's drain snapshot are atomic under it). It never waits for the write gate: under it a
    // control write only QUEUES its turn (TakeWriteTurnLocked, which never blocks), so it cannot deadlock against a
    // production.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _inFlightLock = new();
    private readonly Dictionary<string, InFlightProduction> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _operations = [];
    private volatile bool _leaseReleased;
    private volatile bool _disposed;

    // Issue #158: ensures and retires take their turn on the writer at admission, in submission order (see
    // TakeWriteTurnLocked). _writeAdmissions counts every write admitted to the gate (Interlocked) and _pendingRetire,
    // guarded by _inFlightLock, is the latest retire still waiting for its turn (the only one an identical retire may
    // coalesce into, and only while no other write was admitted after it). Job ids are handed out before their rows
    // exist through _jobIds. _pendingEnsures (guarded by _inFlightLock) counts the ensure operations of each identity
    // that have not finished: an identity's id reservation lives while one of them may still register it, and is
    // dropped once none is left (EndPendingEnsureLocked), so a registration that failed or rolled back never leaves a
    // reserved id reading as queued forever.
    private long _writeAdmissions;
    private PendingRetire? _pendingRetire;
    private readonly JobIdReservations _jobIds;
    private readonly Dictionary<string, int> _pendingEnsures = new(StringComparer.Ordinal);

    private SnapshotService(
        ServiceOptions options, ISnapshotWorker worker, ServicePaths paths,
        IndexDatabase db, WriterLease lease, bool ownsDatabase,
        IContributionAuthorizer authorizer, IGitContentProvider gitContent, ContributionPolicy contributionPolicy,
        IRemoteDefaultBranchResolver? remoteDefaults)
    {
        _options = options;
        _worker = worker;
        _paths = paths;
        _db = db;
        _conn = db.GetConnection();
        _lease = lease;
        _ownsDatabase = ownsDatabase;
        _authorizer = authorizer;
        _gitContent = gitContent;
        _contributionPolicy = contributionPolicy;
        _remoteDefaults = remoteDefaults is null ? null : new RemoteDefaultBranchLookup(remoteDefaults);
        _jobIds = new JobIdReservations(
            JobIdReservations.FloorPathFor(options.CatalogDbPath),
            () => ReadCatalog(conn => new SnapshotJobStore(conn).MaxJobId()));
    }

    public ServicePaths Paths => _paths;

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
        ContributionPolicy? contributionPolicy = null, IRemoteDefaultBranchResolver? remoteDefaults = null)
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
        var db = database ?? new IndexDatabase(options.CatalogDbPath, IndexWriteOptions.Default);
        try
        {
            db.RunMigrations(recover: false);

            var lease = WriterLease.TryAcquire(options.CatalogDbPath, options.Holder, options.LeaseTtl)
                ?? throw new InvalidOperationException(
                    $"Another writer holds the single-writer lease on '{options.CatalogDbPath}'. " +
                    "Retention/publish/GC must not race a live daemon/service (issue #38).");

            try
            {
                // Once the lease is held, make every write session abort between batches if it is ever
                // stolen (issue #38 / criterion 3): a long worker index run consults this probe at each
                // batch boundary and rolls back rather than racing the new owner into a corrupt publish.
                db.SetWriterLostProbe(() => lease.IsLost);
                db.Recover();
                var service = new SnapshotService(
                    options, worker ?? new UnavailableSnapshotWorker(), paths, db, lease, ownsDatabase,
                    effectiveAuthorizer, effectiveGitContent, effectivePolicy, remoteDefaults);
                service._jobIds.Load();
                service.ReconcileOnStartup();
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
    /// Reconciles state a previous worker/service instance left orphaned by a crash (Phase 17 criterion 3),
    /// so worker/service loss at every stage resolves to retryable/failed/complete and NEVER a corrupt
    /// publish. This service holds a fresh lease token, so it: (1) resets every <c>running</c> job not owned
    /// by THIS token to <c>queued</c> for re-attempt; (2) resets any PHANTOM terminal job — <c>complete</c>/
    /// <c>partial</c> with a now-NULL <c>snapshot_id</c> (crash between the status write and the publish
    /// commit, or the snapshot later reclaimed) — back to <c>queued</c> so a later ensure regenerates it
    /// rather than reporting a phantom-complete forever; and (3) sweeps orphaned per-job scratch left by a
    /// crash. Returns the number of jobs reconciled.
    /// </summary>
    public int ReconcileOnStartup()
    {
        var reconciled = WithWrite(() =>
        {
            var jobs = new SnapshotJobStore(_conn);
            var count = jobs.ReconcileOrphanedJobs(_lease.OwnerToken)
                      + jobs.ReconcilePhantomTerminalJobs();
            // Durable audit trail of the recovery action (criterion 5). Service-wide (no repository scope).
            new AuditLogStore(_conn).Append(
                AuditAction.Reconcile, AuditOutcome.Complete, detail: $"reconciled_{count}");
            return count;
        });

        // Best-effort scratch sweep OUTSIDE the write transaction (filesystem, not catalog state); confined
        // to the scratch root so it can never touch a persistent volume.
        _paths.SweepOrphanedScratch();

        RecoveryCompleted = true;
        return reconciled;
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
        var (_, completion, _) = StartEnsureOperation(request, principal);
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
    /// same identity gets the same id. Ensures and retires take the writer in submission order. The only case that
    /// keeps waiting past the bound is a reservation whose id floor cannot be persisted (see
    /// <see cref="JobIdReservations"/>): then no unrecorded id is handed out. <paramref name="cancellationToken"/>
    /// only bounds this caller's wait.
    /// </para>
    /// </summary>
    public async Task<EnsureSnapshotResult> BeginEnsureSnapshotAsync(
        EnsureSnapshotRequest request, AuditCaller principal = default, CancellationToken cancellationToken = default)
    {
        var (accepted, _, hash) = StartEnsureOperation(request, principal);
        if (!accepted.IsCompleted)
        {
            await ((Task)accepted).WaitAsync(_options.ControlWriteWait, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!accepted.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The writer is busy: answer with the identity's job id without it. A registration that settled
                // meanwhile reports its own result (it carries the same id).
                if (QueuedResult(request, hash) is { } queued && !accepted.IsCompleted)
                    return queued;
            }
        }
        return await WaitForCallerAsync(accepted, cancellationToken).ConfigureAwait(false);
    }

    // The wait=false answer for an ensure still waiting for the writer (issue #158): queued, with the job id the
    // ensure will register (or has registered) its identity under. Null when no id can be handed out safely.
    private EnsureSnapshotResult? QueuedResult(EnsureSnapshotRequest request, string hash)
    {
        if (FindInFlight(hash) is { } inFlight)
            return PendingResult(inFlight.JobId, hash);
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
            ? new EnsureSnapshotResult { JobId = job.Id, IdentityHash = hash, Status = SnapshotJobStatus.Queued, Attached = job.Existed }
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
            write = Task.Run(
                () => WithWriteAsync(
                    () =>
                    {
                        new AuditLogStore(_conn).Append(
                            action, AuditOutcome.Denied,
                            actor: principal.Actor,
                            detail: principal.Detail(reason));
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
            await write.WaitAsync(_options.DeniedAuditWait, cancellationToken).ConfigureAwait(false);
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

    // Starts one ensure as a service-owned background operation and returns (accepted, completion, identity hash): the
    // completion is the ensure's final result; accepted settles as soon as the job is registered (wait=false).
    // The operation is tracked so Dispose can drain it, and neither task can ever surface as an unobserved
    // exception when no caller is left waiting (the caller disconnected). Admission is atomic with Dispose's
    // drain snapshot (both under _inFlightLock): an operation either starts before shutdown closes admission
    // and is drained, or is refused, so none can run on against a released lease or a closed catalog.
    private (Task<EnsureSnapshotResult> Accepted, Task<EnsureSnapshotResult> Completion, string Hash) StartEnsureOperation(
        EnsureSnapshotRequest request, AuditCaller principal)
    {
        // SVC-6/7: a malformed branch guard is refused before any job exists. The host validates first and
        // answers 400; this protects direct callers.
        if (request.BranchGuardProblem() is { } problem)
            throw new ArgumentException($"The ensure request's branch guards are invalid ({problem}).", nameof(request));

        // Issue #113: the non-default SDK-pin policy is part of the identity, so flipping
        // SEXTANT_SERVICE_SDK_PIN_OVERRIDE never reuses a snapshot (or failed job) built under the other policy. The
        // remote-default lookup (issue #199) is not part of it, so the hash is known before that lookup runs.
        var hash = request.ToIdentity(
            _options.DefaultConfigHash, _options.DefaultCapabilityFingerprint, _options.SdkPinIdentityComponent).Hash;
        var accepted = new TaskCompletionSource<EnsureSnapshotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EnsureSnapshotResult> completion;
        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfStopping();
            // Issue #158: the ensure takes its turn on the writer now, in submission order. A user ensure that must
            // first look up the remote's default branch (issue #199) takes its turn once that lookup has finished.
            var turn = request.RestrictsImplicitDefault ? null : TakeWriteTurnLocked();
            completion = Task.Run(() => RunEnsureAsync(request, hash, principal, accepted, turn), CancellationToken.None);
            _operations.Add(completion);
            _pendingEnsures[hash] = _pendingEnsures.GetValueOrDefault(hash) + 1;
        }
        completion.ContinueWith(
            t =>
            {
                lock (_inFlightLock)
                {
                    _operations.Remove(t);
                    EndPendingEnsureLocked(hash);
                }
                SettleAccepted(accepted, t);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return (accepted.Task, completion, hash);
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
    // The wait is not tied to the service lifetime, so a write admitted before shutdown still lands during the
    // Dispose drain. The returned turn MUST be awaited and the gate released exactly once on every path, or the
    // writer stalls for good (ReleaseTurn covers a turn abandoned before it was granted).
    private Task TakeWriteTurnLocked() => TakeWriteTurnLocked(out _);

    private Task TakeWriteTurnLocked(out long admission)
    {
        admission = Interlocked.Increment(ref _writeAdmissions);
        return _writeGate.WaitAsync(CancellationToken.None);
    }

    private Task TakeWriteTurn()
    {
        lock (_inFlightLock)
            return TakeWriteTurnLocked();
    }

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
        TaskCompletionSource<EnsureSnapshotResult> accepted, Task? turn)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("ensure_snapshot");
        activity?.SetTag("sextant.repository", request.RepositoryRemoteUrl);
        activity?.SetTag("sextant.commit", request.CommitSha);

        var result = await EnsureSnapshotCoreAsync(request, hash, principal, accepted, turn).ConfigureAwait(false);

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
            var branch = await lookup.ResolveAsync(request.RepositoryRemoteUrl).WaitAsync(_lifetime.Token).ConfigureAwait(false);
            return request with { VerifiedRemoteDefaultBranch = branch };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || _lifetime.IsCancellationRequested)
        {
            return unverified;
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
        TaskCompletionSource<EnsureSnapshotResult> accepted, Task? turn)
    {
        var granted = false;
        var handedOff = false;
        try
        {
            if (turn is null)
            {
                request = await WithVerifiedRemoteDefaultAsync(request).ConfigureAwait(false);
                turn = TakeWriteTurn();
            }

            // The identity is already producing: report its job at once, so a re-ensure — e.g. the retry of a
            // caller that timed out — returns its job id immediately and never starts a second worker. Shutdown
            // began: never attach to (or report as running) a production that is being cancelled.
            var inFlight = FindInFlight(hash);
            if (inFlight is not null)
            {
                ThrowIfStopping();
                accepted.TrySetResult(PendingResult(inFlight.JobId, hash));
            }

            await turn.ConfigureAwait(false);
            granted = true;
            EnsureLeaseHeld();

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
            var (job, existed, attached) = TryAttachTerminal(request, hash, principal);
            if (attached is not null)
                return attached;
            accepted.TrySetResult(PendingResult(job, existed));

            // Shutdown began: the job stays durably queued for the next instance to produce.
            ThrowIfStopping();

            var production = StartProduction(request, hash, job.Id, existed, principal);
            handedOff = true;
            return await production.ConfigureAwait(false);
        }
        finally
        {
            if (!handedOff && turn is not null)
                ReleaseTurn(turn, granted);
        }
    }

    // Terminal-attach step, run under the write gate: registers (or attaches to) the identity's durable job and,
    // when its terminal result is still usable, advances/attaches this request's branch pointer and records the
    // request's audit row in the SAME gate hold. Returns the attach result, or null when production is needed.
    private (SnapshotJobRow Job, bool Existed, EnsureSnapshotResult? Attached) TryAttachTerminal(
        EnsureSnapshotRequest request, string hash, AuditCaller principal)
    {
        var jobs = new SnapshotJobStore(_conn);
        var (row, wasExisting) = RegisterJobLocked(jobs, hash, request.RepositoryRemoteUrl, request.CommitSha, request.BranchName);
        // The row committed (this write is not inside a transaction), so a status read now finds it.
        _jobIds.Release(hash);
        if (!SnapshotJobStatus.IsTerminal(row.Status) || !TerminalResultUsable(row, hash, new SnapshotStore(_conn)))
            return (row, wasExisting, null);
        var advanced = AdvanceOrAttachBranchPointer(request, row.SnapshotId);
        var attached = Attach(row, wasExisting, CoverageFor(row.SnapshotId)) with { BranchAdvanced = advanced };
        RecordEnsureAuditLocked(request, attached, principal);
        return (row, wasExisting, attached);
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

    // Starts (and registers) the identity's production on the write-gate turn the calling ensure holds, which the
    // production takes over and releases. The starter is the OWNER: the production runs with its request (branch,
    // sequence) and principal. Every production runs while holding the gate and deregisters before releasing it, so
    // none can be in flight while the caller holds the gate.
    private Task<EnsureSnapshotResult> StartProduction(
        EnsureSnapshotRequest request, string hash, long jobId, bool existed, AuditCaller principal)
    {
        lock (_inFlightLock)
        {
            if (_inFlight.ContainsKey(hash))
                throw new InvalidOperationException(
                    "An identity's production was in flight while another ensure held the write gate; productions must hold it for their whole run.");

            // Registered under the lock BEFORE the production can deregister itself (it takes the same lock).
            var entry = new InFlightProduction(jobId);
            entry.Production = Task.Run(
                () => RunProductionAsync(request, hash, jobId, existed, principal, entry), CancellationToken.None);
            _inFlight[hash] = entry;
            return entry.Production;
        }
    }

    // A non-terminal snapshot of a job whose production is pending or running (the wait=false response): queued
    // while the production waits for the writer, running once the worker holds it. A stale terminal row about
    // to be requeued is reported queued, never as its phantom terminal status.
    private EnsureSnapshotResult PendingResult(SnapshotJobRow job, bool existed) => new()
    {
        JobId = job.Id,
        IdentityHash = job.IdentityHash,
        Status = job.Status == SnapshotJobStatus.Running ? SnapshotJobStatus.Running : SnapshotJobStatus.Queued,
        Attached = existed
    };

    private EnsureSnapshotResult PendingResult(long jobId, string hash)
    {
        var job = ReadCatalog(conn => new SnapshotJobStore(conn).GetJob(jobId));
        return job is not null
            ? PendingResult(job, existed: true)
            : new EnsureSnapshotResult { JobId = jobId, IdentityHash = hash, Status = SnapshotJobStatus.Queued, Attached = true };
    }

    // One identity's shared production: runs on the write-gate turn of the ensure that started it (never waiting for
    // the writer again, so it applies at that ensure's place in submission order, issue #158) and on the SERVICE
    // lifetime (never a caller's token). It runs the worker, records the result and the owner's audit row, then
    // deregisters itself BEFORE releasing the gate — so any request that can observe the new durable state (which
    // needs the gate) never attaches to this already-finished production.
    private async Task<EnsureSnapshotResult> RunProductionAsync(
        EnsureSnapshotRequest request, string hash, long jobId, bool existed, AuditCaller principal, InFlightProduction entry)
    {
        try
        {
            EnsureLeaseHeld();
            var result = await ProduceLockedAsync(request, hash, jobId, existed).ConfigureAwait(false);
            RecordEnsureAuditLocked(request, result, principal);
            return result;
        }
        finally
        {
            lock (_inFlightLock)
            {
                if (_inFlight.TryGetValue(hash, out var current) && ReferenceEquals(current, entry))
                    _inFlight.Remove(hash);
            }
            _writeGate.Release();
        }
    }

    // The production critical section. The caller holds the write gate (and has checked the lease).
    private async Task<EnsureSnapshotResult> ProduceLockedAsync(
        EnsureSnapshotRequest request, string hash, long jobId, bool existed)
    {
        var jobs = new SnapshotJobStore(_conn);
        var snapshots = new SnapshotStore(_conn);

        var current = jobs.GetJob(jobId)!;
        if (SnapshotJobStatus.IsTerminal(current.Status) && TerminalResultUsable(current, hash, snapshots))
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
        // data-less/unservable one is demoted so the worker below genuinely rebuilds it. An unguarded
        // request (the local/legacy path) keeps today's worker path byte-for-byte (#84 criterion 2).
        if (HasBranchGuard(request) && published is { Status: SnapshotStatus.Superseded })
        {
            var (reselected, reselectedCoverage, reselectedAdvanced) =
                ReselectOrDemoteSupersededSnapshot(request, jobId, published.Id, jobs, snapshots);
            if (reselected)
                return Attach(jobs.GetJob(jobId)!, existed, reselectedCoverage) with { BranchAdvanced = reselectedAdvanced };
        }

        // The requested branch's pointer before the worker runs, so the result can report whether the
        // worker's own branch advance (IndexOrchestrator.AdvanceBranchToSnapshot) moved it (SVC-6). The write
        // gate is held for the whole run, so no other writer can move it in between.
        var pointerBeforeWorker = RequestedBranchPointer(snapshots, request);

        // A STALE terminal result (a complete/partial job whose published snapshot was reclaimed by
        // retention) must be reset to queued before MarkRunning, whose guard only advances a
        // queued/running job — otherwise the job would be stuck reporting a phantom-complete.
        if (SnapshotJobStatus.IsTerminal(current.Status))
            jobs.Requeue(jobId);

        jobs.MarkRunning(jobId, _lease.OwnerToken);

        // Issue #125: on the LAST attempt the bound allows, tell the worker so a clone-mode checkout degrades
        // a persistently-transient SUBMODULE failure to partial coverage instead of failing the whole job.
        var workRequest = jobs.GetJob(jobId)!.Attempts >= _options.MaxProvisioningAttempts
            ? request with { IsFinalProvisioningAttempt = true }
            : request;

        var scratch = _paths.AllocateScratch($"job-{jobId}");
        SnapshotWorkResult result;
        try
        {
            // Issue #148: the worker runs on the SERVICE lifetime, never a caller's token — a caller that
            // disconnects or times out must not throw away a long index run. Only shutdown cancels it.
            result = await _worker.ProduceAsync(workRequest, hash, scratch, _lifetime.Token).ConfigureAwait(false);
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

        jobs.ReplaceDiagnostics(jobId, validated.Projects.Select(p => p.ToDiagnostic(jobId)));
        jobs.MarkResult(jobId, validated.Status, validated.SnapshotId, validated.Error);
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
            payloadConn?.Dispose();
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
        ExecRaw("BEGIN IMMEDIATE;");
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
        if (SnapshotJobStatus.IsTerminal(job.Status))
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
        ExecRaw("BEGIN IMMEDIATE;");
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
        if (!snapshots.BranchHeadMatches(snapshots.GetBranchId(repoId, branchName), expectedHead))
            return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ownsDefault = snapshots.ShouldOwnDefault(
            repoId, branchName, request.ResolveIsDefaultBranch(), request.AllowsImplicitDefault(branchName));
        var branchId = snapshots.EnsureBranch(repoId, branchName, ownsDefault, now);
        if (ownsDefault)
            snapshots.PromoteSoleDefaultBranch(repoId, branchId);
        snapshots.AdvanceBranchPointerIfHeadMatches(branchId, sid, expectedHead, now);
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
        ExecRaw("BEGIN IMMEDIATE;");
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
        cmd.ExecuteNonQuery();
    }

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
    public JobStatusResult? GetStatus(long jobId)
    {
        // Read the reservation BEFORE the catalog: it is released only after its row has committed, so one of the
        // two reads always sees the job.
        var reserved = _jobIds.Find(jobId);
        return ReadCatalog(conn =>
        {
            var jobs = new SnapshotJobStore(conn);
            var job = jobs.GetJob(jobId);
            return job is not null ? StatusOf(conn, jobs, job) : ReservedStatus(reserved);
        });
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
        return new JobStatusResult { Job = job, Diagnostics = jobs.GetDiagnostics(job.Id), Coverage = coverage };
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
    /// snapshot's commit SHA and its durable coverage (SVC-6, <c>/control/resolve</c>). One consistent read
    /// on an independent read connection; null exactly when <see cref="ResolveBranch"/> is.
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
            return new ResolvedBranchHead
            {
                Snapshot = row,
                Branch = branch.Name,
                IsDefault = branch.IsDefault,
                HeadSequence = branch.HeadSequence,
                CommitSha = snapshots.GetCommitSha(row.CommitId),
                Coverage = CoverageOn(conn, row.Id)
            };
        });
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
    /// retirement's result when it applies within <see cref="ServiceOptions.ControlWriteWait"/>, else null — the
    /// retirement stays queued and applies once the writer frees (a running production holds it for its whole run),
    /// with its guards evaluated and its audit row written then. <paramref name="cancellationToken"/> only bounds
    /// this caller's wait; it never cancels the retirement.
    /// </summary>
    public async Task<RetireBranchResult?> BeginRetireBranchAsync(
        RetireBranchRequest request, AuditCaller principal = default, CancellationToken cancellationToken = default)
    {
        var retire = StartRetire(request, principal);
        if (!retire.IsCompleted)
        {
            await ((Task)retire).WaitAsync(_options.ControlWriteWait, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!retire.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }
        return await retire.ConfigureAwait(false);
    }

    // Admits one retirement as a service-owned operation on its own write-gate turn (issue #158), or coalesces it into
    // the identical retirement still waiting when no other write was admitted after it (applying both would change
    // nothing more, so it shares that one's result and audit row). Admission is atomic with Dispose's drain snapshot.
    private Task<RetireBranchResult> StartRetire(RetireBranchRequest request, AuditCaller principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Repository);
        ArgumentException.ThrowIfNullOrEmpty(request.Branch);
        var key = new RetireKey(request.Repository, request.Branch, request.ExpectedHeadCommit, principal.Principal);
        PendingRetire entry;
        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfStopping();
            if (_pendingRetire is { } pending && pending.Key == key && pending.Admission == Interlocked.Read(ref _writeAdmissions))
                return pending.Retire;

            var turn = TakeWriteTurnLocked(out var admission);
            entry = new PendingRetire(key, admission);
            // Published under the lock BEFORE the retirement can clear it (it takes the same lock).
            entry.Retire = Task.Run(() => ApplyRetireAsync(request, principal, entry, turn), CancellationToken.None);
            _pendingRetire = entry;
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
        return entry.Retire;
    }

    private async Task<RetireBranchResult> ApplyRetireAsync(
        RetireBranchRequest request, AuditCaller principal, PendingRetire entry, Task turn)
    {
        await turn.ConfigureAwait(false);
        try
        {
            lock (_inFlightLock)
            {
                if (ReferenceEquals(_pendingRetire, entry))
                    _pendingRetire = null;
            }
            using var activity = ServiceTelemetry.Source.StartActivity("retire_branch");
            EnsureLeaseHeld();
            return RetireBranchLocked(request, principal);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private RetireBranchResult RetireBranchLocked(RetireBranchRequest request, AuditCaller principal)
    {
        var snapshots = new SnapshotStore(_conn);
        ExecRaw("BEGIN IMMEDIATE;");
        try
        {
            var branch = snapshots.GetRepositoryId(request.Repository) is long repoId
                ? snapshots.GetBranch(repoId, request.Branch)
                : null;
            RetireBranchResult result;
            if (branch is null)
                result = new RetireBranchResult { Retired = false };
            else if (branch.IsDefault)
                result = new RetireBranchResult { Retired = false, Reason = BranchGuardReason.DefaultBranch };
            else if (request.ExpectedHeadCommit is { } expected && !snapshots.BranchHeadMatches(branch.Id, expected))
                result = new RetireBranchResult { Retired = false, Reason = BranchGuardReason.HeadMismatch };
            else
                result = new RetireBranchResult { Retired = snapshots.DeleteBranch(branch.Id) };

            new AuditLogStore(_conn).Append(
                AuditAction.Retire,
                result.Reason is null ? AuditOutcome.Complete : AuditOutcome.Denied,
                actor: principal.Actor,
                repositoryScope: request.Repository,
                detail: principal.Detail(result.Reason ?? (result.Retired ? "retired" : "absent")));
            ExecRaw("COMMIT;");
            return result;
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
    }

    /// <summary>
    /// Runs the service-owned retention/GC pass under the single-writer lease (issues #46/#37/#54/#38): it
    /// GCs orphaned snapshot DATA, bounds the source-blob prune, and honors a protected set that spans every
    /// retained consumer's providers. The service is the natural lease owner, so this can never race a live
    /// writer.
    /// </summary>
    public RetentionReport RunRetention(bool execute, AuditCaller principal = default)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("retention");
        activity?.SetTag("sextant.execute", execute);
        return WithWrite(() => RunRetentionLocked(execute, principal));
    }

    /// <summary>
    /// <see cref="RunRetention"/> for request threads (issue #148): waits for the writer asynchronously —
    /// never blocking a thread-pool thread behind a running index — and gives up when
    /// <paramref name="cancellationToken"/> fires before the writer is acquired.
    /// </summary>
    public async Task<RetentionReport> RunRetentionAsync(
        bool execute, AuditCaller principal = default, CancellationToken cancellationToken = default)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("retention");
        activity?.SetTag("sextant.execute", execute);
        return await WithWriteAsync(
            () => Task.FromResult(RunRetentionLocked(execute, principal)), cancellationToken).ConfigureAwait(false);
    }

    private RetentionReport RunRetentionLocked(bool execute, AuditCaller principal)
    {
        var retention = new RetentionService(_conn, _options.Retention);
        var report = execute ? retention.Execute() : retention.Plan();
        // Service-wide audit row (no single repository scope). Records the operator + whether it was a
        // dry-run plan or an executed GC pass (criterion 5, audit).
        new AuditLogStore(_conn).Append(
            AuditAction.Retention, AuditOutcome.Complete,
            actor: principal.Actor,
            detail: principal.Detail(execute ? "execute" : "plan"));
        return report;
    }

    /// <summary>
    /// Collects a point-in-time observability snapshot (criterion 5) — indexing latency, queue delay,
    /// success/completeness, worker capacity, storage, cache reuse, query latency, alerts, and per-
    /// repository cost attribution. Reads through an INDEPENDENT read connection so collecting metrics
    /// never blocks the writer or a running index. OPERATOR-ONLY data — the host exposes it on the control
    /// plane only (criterion-1 leakage guard).
    /// </summary>
    public MetricsSnapshot CollectMetrics(AlertThresholds? thresholds = null)
    {
        using var conn = OpenReadConnection();
        var collector = new MetricsCollector(conn, _paths, _options.CatalogDbPath, _metrics, HasWorkerCapacity);
        return collector.Collect(thresholds);
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
    // Failed/unsupported/cancelled carry no snapshot to verify, so they remain terminal as recorded.
    private static bool TerminalResultUsable(SnapshotJobRow job, string identityHash, SnapshotStore snapshots)
    {
        if (job.Status is not (SnapshotJobStatus.Complete or SnapshotJobStatus.Partial))
            return true;
        if (job.SnapshotId is not long id || snapshots.GetById(id) is not { Status: SnapshotStatus.Complete })
            return false;
        var byIdentity = snapshots.GetByIdentityHash(identityHash);
        return byIdentity is not null && byIdentity.Id == id;
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
    private void RecordEnsureAuditLocked(EnsureSnapshotRequest request, EnsureSnapshotResult result, AuditCaller principal)
    {
        var jobs = new SnapshotJobStore(_conn);
        var job = jobs.GetJob(result.JobId);
        long? costMs = !result.Attached && job is { StartedAt: long s, CompletedAt: long c } && c >= s
            ? c - s
            : null;
        new AuditLogStore(_conn).Append(
            AuditAction.Ensure,
            MapOutcome(result.Status),
            actor: principal.Actor,
            repositoryScope: request.RepositoryRemoteUrl,
            detail: principal.Detail($"job_{result.JobId}{SdkPinAuditSuffix(jobs.GetDiagnostics(result.JobId))}{ForcedAuditSuffix(request)}"),
            costIndexMs: costMs);
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
    // cancelled is recorded as error (it carries no usable result).
    private static string MapOutcome(string status) => status switch
    {
        SnapshotJobStatus.Complete => AuditOutcome.Complete,
        SnapshotJobStatus.Partial => AuditOutcome.Partial,
        SnapshotJobStatus.Failed => AuditOutcome.Failed,
        SnapshotJobStatus.Unsupported => AuditOutcome.Unsupported,
        SnapshotJobStatus.Cancelled => AuditOutcome.Error,
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

    // ReadCatalog that stops when `cancellationToken` is cancelled (issue #196). It is a separate name, not an overload,
    // so the existing control-plane reads keep their deliberate no-cancellation contract. A cancellation interrupts the
    // statement in flight (sqlite3_interrupt; SqliteCommand.Cancel does nothing in Microsoft.Data.Sqlite), `read`
    // checks the token between its statements, and a statement failing while the token is cancelled surfaces as an
    // OperationCanceledException. The registration is disposed (which waits out a callback already running) before the
    // read transaction ends and the connection goes back to the pool, so a late cancellation can never interrupt the
    // COMMIT or a statement of the connection's next user.
    private T ReadCatalogCancellable<T>(Func<SqliteConnection, T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = OpenReadConnection();
        ExecOn(conn, "BEGIN;");
        try
        {
            using (cancellationToken.Register(static handle => SQLitePCL.raw.sqlite3_interrupt((SQLitePCL.sqlite3)handle!), conn.Handle))
                return read(conn);
        }
        catch (SqliteException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The catalog read was cancelled.", ex, cancellationToken);
        }
        finally
        {
            // An interrupted statement can end the transaction itself; COMMIT only one still open, so the connection
            // returns to the pool outside any transaction and a failing COMMIT never masks the cancellation.
            if (SQLitePCL.raw.sqlite3_get_autocommit(conn.Handle) == 0)
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
        _writeGate.Wait(CancellationToken.None);
        try
        {
            EnsureLeaseHeld();
            return work();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<T> WithWriteAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        // Counted like a control write's turn, so a retirement is never coalesced across another write (issue #158).
        Interlocked.Increment(ref _writeAdmissions);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLeaseHeld();
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
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
        if (_ownsDatabase)
            _db.Dispose();
        _writeGate.Dispose();
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

    // What makes two retirements identical (issue #158): same repository and branch spelling, same CAS guard, same
    // audit actor. Spellings are compared exactly, so two spellings of one repository simply are not coalesced.
    private readonly record struct RetireKey(string Repository, string Branch, string? ExpectedHeadCommit, string? Actor);

    // A retirement waiting for (or holding) its write-gate turn (issue #158). Admission is the write-admission count
    // its own turn produced; Retire is assigned under _inFlightLock before the entry is published.
    private sealed class PendingRetire(RetireKey key, long admission)
    {
        public RetireKey Key { get; } = key;
        public long Admission { get; } = admission;
        public Task<RetireBranchResult> Retire { get; set; } = null!;
    }
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
