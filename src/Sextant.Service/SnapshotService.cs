using Microsoft.Data.Sqlite;
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
/// a running index. On construction the service acquires the lease, runs recovery, and reconciles any job
/// a dead worker left <c>running</c> back to <c>queued</c> (criterion 2).
/// </summary>
public sealed class SnapshotService : IDisposable
{
    private readonly ServiceOptions _options;
    private readonly ISnapshotWorker _worker;
    private readonly ServicePaths _paths;
    private readonly IndexDatabase _db;
    private readonly SqliteConnection _conn;
    private readonly WriterLease _lease;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly bool _ownsDatabase;
    private bool _disposed;

    private SnapshotService(
        ServiceOptions options, ISnapshotWorker worker, ServicePaths paths,
        IndexDatabase db, WriterLease lease, bool ownsDatabase)
    {
        _options = options;
        _worker = worker;
        _paths = paths;
        _db = db;
        _conn = db.GetConnection();
        _lease = lease;
        _ownsDatabase = ownsDatabase;
    }

    public ServicePaths Paths => _paths;

    /// <summary>The writer-lease token this service instance holds (identifies jobs it owns).</summary>
    public string OwnerToken => _lease.OwnerToken;

    /// <summary>
    /// Starts the data plane: opens the catalog database, runs migrations WITHOUT recovery, acquires the
    /// single-writer lease (fail-closed — throws if a live writer already holds it), then recovers a valid
    /// WAL / sweeps abandoned staging generations and reconciles orphaned jobs. Ordering matters: migrate
    /// then lease then recover, so recovery never abandons a live writer's staging generation (issue #38).
    /// </summary>
    public static SnapshotService Start(ServiceOptions options, ISnapshotWorker? worker = null, IndexDatabase? database = null)
    {
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
                db.Recover();
                var service = new SnapshotService(
                    options, worker ?? new UnavailableSnapshotWorker(), paths, db, lease, ownsDatabase);
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
    /// Reconciles jobs a previous worker left <c>running</c> (criterion 2). This service holds a fresh lease
    /// token, so every <c>running</c> job not owned by THIS token is reset to <c>queued</c> for re-attempt.
    /// </summary>
    public int ReconcileOnStartup()
    {
        return WithWrite(() =>
        {
            var jobs = new SnapshotJobStore(_conn);
            return jobs.ReconcileOrphanedJobs(_lease.OwnerToken);
        });
    }

    /// <summary>
    /// Idempotently ensures a committed-branch snapshot exists (acceptance criterion 1). Repeated requests
    /// for the same identity attach to the ONE durable job. If a compatible complete snapshot already
    /// exists it is returned immediately; otherwise the pluggable worker produces one under a per-job
    /// scratch directory, whose output is validated and published through the catalog, with a terminal job
    /// status + per-project diagnostics recorded (criterion 5).
    /// </summary>
    public async Task<EnsureSnapshotResult> EnsureSnapshotAsync(
        EnsureSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        var identity = request.ToIdentity(_options.DefaultConfigHash);
        var hash = identity.Hash;

        // Idempotent attach: create-or-return the ONE durable job for this identity (criterion 1), and in
        // the SAME write check whether a terminal result is still backed by durable data (a complete job
        // whose snapshot retention has since reclaimed must NOT be reported complete forever).
        var (job, existed, terminalUsable) = await WithWriteAsync(() =>
        {
            var jobs = new SnapshotJobStore(_conn);
            var (row, wasExisting) = jobs.EnsureJob(hash, request.RepositoryRemoteUrl, request.CommitSha, request.BranchName);
            var usable = SnapshotJobStatus.IsTerminal(row.Status)
                && TerminalResultUsable(row, hash, new SnapshotStore(_conn));
            return Task.FromResult((row, wasExisting, usable));
        }).ConfigureAwait(false);

        if (SnapshotJobStatus.IsTerminal(job.Status) && terminalUsable)
            return Attach(job, existed);

        // Serialize production so only ONE worker runs per identity; concurrent callers attach.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLeaseHeld();
            var jobs = new SnapshotJobStore(_conn);
            var snapshots = new SnapshotStore(_conn);

            var current = jobs.GetJob(job.Id)!;
            if (SnapshotJobStatus.IsTerminal(current.Status) && TerminalResultUsable(current, hash, snapshots))
                return Attach(current, existed);

            // A complete snapshot may already be published for this identity (produced by an earlier run
            // whose job row predates migration 016, or a race we lost). Attach to it without re-indexing.
            var published = snapshots.GetByIdentityHash(hash);
            if (published is { Status: SnapshotStatus.Complete })
            {
                jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, published.Id);
                return Attach(jobs.GetJob(job.Id)!, existed);
            }

            // A STALE terminal result (a complete/partial job whose published snapshot was reclaimed by
            // retention) must be reset to queued before MarkRunning, whose guard only advances a
            // queued/running job — otherwise the job would be stuck reporting a phantom-complete.
            if (SnapshotJobStatus.IsTerminal(current.Status))
                jobs.Requeue(job.Id);

            jobs.MarkRunning(job.Id, _lease.OwnerToken);

            var scratch = _paths.AllocateScratch($"job-{job.Id}");
            SnapshotWorkResult result;
            try
            {
                result = await _worker.ProduceAsync(request, hash, scratch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not a durable failure: requeue so a later ensure re-attempts this
                // identity rather than attaching to a permanent failed/cancelled terminal state.
                jobs.Requeue(job.Id);
                throw;
            }
            catch (Exception ex)
            {
                jobs.MarkResult(job.Id, SnapshotJobStatus.Failed, null, ex.Message);
                jobs.ReplaceDiagnostics(job.Id, [FailureDiagnostic(job.Id, ex.Message)]);
                return Attach(jobs.GetJob(job.Id)!, existed);
            }
            finally
            {
                _paths.ReleaseScratch(scratch);
            }

            var validated = ValidateWorkerResult(result, hash, snapshots);
            jobs.ReplaceDiagnostics(job.Id, validated.Projects.Select(p => p.ToDiagnostic(job.Id)));
            jobs.MarkResult(job.Id, validated.Status, validated.SnapshotId, validated.Error);
            return Attach(jobs.GetJob(job.Id)!, existed);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The full status of a job (with diagnostics) by durable job id — null when unknown.</summary>
    public JobStatusResult? GetStatus(long jobId)
    {
        return WithWrite(() =>
        {
            var jobs = new SnapshotJobStore(_conn);
            var job = jobs.GetJob(jobId);
            return job is null ? null : new JobStatusResult { Job = job, Diagnostics = jobs.GetDiagnostics(jobId) };
        });
    }

    /// <summary>The full status of a job (with diagnostics) by snapshot identity hash — null when unknown.</summary>
    public JobStatusResult? GetStatusByIdentity(string identityHash)
    {
        return WithWrite(() =>
        {
            var jobs = new SnapshotJobStore(_conn);
            var job = jobs.GetJobByIdentity(identityHash);
            return job is null ? null : new JobStatusResult { Job = job, Diagnostics = jobs.GetDiagnostics(job.Id) };
        });
    }

    /// <summary>
    /// Resolves a repository branch (or its default branch when <paramref name="branchName"/> is null) to
    /// the complete snapshot it currently points at — null when the repo/branch/pointer is absent or the
    /// pointed snapshot is not complete. Read-only; never creates catalog rows.
    /// </summary>
    public SnapshotRow? ResolveBranch(string repositoryRemoteUrl, string? branchName)
    {
        return WithWrite(() =>
        {
            var snapshots = new SnapshotStore(_conn);
            if (snapshots.GetRepositoryId(repositoryRemoteUrl) is not long repoId)
                return null;

            var branchId = branchName is null
                ? snapshots.GetDefaultBranchId(repoId)
                : snapshots.GetBranchId(repoId, branchName);
            if (branchId is not long bid || snapshots.GetBranchSnapshotId(bid) is not long snapId)
                return null;

            var row = snapshots.GetById(snapId);
            return row is { Status: SnapshotStatus.Complete } ? row : null;
        });
    }

    /// <summary>
    /// Runs the service-owned retention/GC pass under the single-writer lease (issues #46/#37/#54/#38): it
    /// GCs orphaned snapshot DATA, bounds the source-blob prune, and honors a protected set that spans every
    /// retained consumer's providers. The service is the natural lease owner, so this can never race a live
    /// writer.
    /// </summary>
    public RetentionReport RunRetention(bool execute)
    {
        return WithWrite(() =>
        {
            var retention = new RetentionService(_conn, _options.Retention);
            return execute ? retention.Execute() : retention.Plan();
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

    private EnsureSnapshotResult Attach(SnapshotJobRow job, bool existed) => new()
    {
        JobId = job.Id,
        IdentityHash = job.IdentityHash,
        Status = job.Status,
        SnapshotId = job.SnapshotId,
        Attached = existed
    };

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

    private T WithWrite<T>(Func<T> work)
    {
        _writeGate.Wait();
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

    private async Task<T> WithWriteAsync<T>(Func<Task<T>> work)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
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

    // Fail closed if this instance lost the single-writer lease (it expired and another writer stole it):
    // writing anyway would race the new owner on one SQLite database (issue #38).
    private void EnsureLeaseHeld()
    {
        if (_lease.IsLost)
            throw new InvalidOperationException(
                "This service lost the single-writer lease (it expired and was stolen by another writer); " +
                "refusing to write to avoid racing the new owner (issue #38).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease.Dispose();
        if (_ownsDatabase)
            _db.Dispose();
        _writeGate.Dispose();
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
