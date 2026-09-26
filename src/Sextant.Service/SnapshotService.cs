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
    private readonly IContributionAuthorizer _authorizer;
    private readonly IGitContentProvider _gitContent;
    private readonly ContributionPolicy _contributionPolicy;
    private readonly ServiceMetrics _metrics = new();
    private bool _disposed;

    private SnapshotService(
        ServiceOptions options, ISnapshotWorker worker, ServicePaths paths,
        IndexDatabase db, WriterLease lease, bool ownsDatabase,
        IContributionAuthorizer authorizer, IGitContentProvider gitContent, ContributionPolicy contributionPolicy)
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
    }

    public ServicePaths Paths => _paths;

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
        ContributionPolicy? contributionPolicy = null)
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
                    effectiveAuthorizer, effectiveGitContent, effectivePolicy);
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
    /// </summary>
    public async Task<EnsureSnapshotResult> EnsureSnapshotAsync(
        EnsureSnapshotRequest request, CancellationToken cancellationToken = default, string? principal = null)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("ensure_snapshot");
        activity?.SetTag("sextant.repository", request.RepositoryRemoteUrl);
        activity?.SetTag("sextant.commit", request.CommitSha);

        var result = await EnsureSnapshotCoreAsync(request, cancellationToken).ConfigureAwait(false);

        _metrics.RecordEnsure(result.Attached);
        activity?.SetTag("sextant.status", result.Status);
        activity?.SetTag("sextant.attached", result.Attached);
        RecordEnsureAudit(request, result, principal);
        return result;
    }

    // The idempotent-ensure core (unchanged behavior). Wrapped by EnsureSnapshotAsync for observability.
    private async Task<EnsureSnapshotResult> EnsureSnapshotCoreAsync(
        EnsureSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        var identity = request.ToIdentity(_options.DefaultConfigHash, _options.DefaultCapabilityFingerprint);
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
        {
            var attachedCoverage = WithWrite(() =>
            {
                AdvanceOrAttachBranchPointer(request, job.SnapshotId);
                return CoverageFor(job.SnapshotId);
            });
            return Attach(job, existed, attachedCoverage);
        }

        // Serialize production so only ONE worker runs per identity; concurrent callers attach.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLeaseHeld();
            var jobs = new SnapshotJobStore(_conn);
            var snapshots = new SnapshotStore(_conn);

            var current = jobs.GetJob(job.Id)!;
            if (SnapshotJobStatus.IsTerminal(current.Status) && TerminalResultUsable(current, hash, snapshots))
            {
                AdvanceOrAttachBranchPointer(request, current.SnapshotId);
                return Attach(current, existed, CoverageFor(current.SnapshotId));
            }

            // A complete snapshot may already be published for this identity (produced by an earlier run
            // whose job row predates migration 016, or a race we lost). Attach to it without re-indexing —
            // but take the job's verdict from the snapshot's DURABLE coverage record (issue #119): a
            // published snapshot whose recorded coverage is partial must never be reported complete.
            var published = snapshots.GetByIdentityHash(hash);
            if (published is { Status: SnapshotStatus.Complete })
            {
                var publishedCoverage = CoverageFor(published.Id);
                if (publishedCoverage is { IsPartial: true })
                {
                    jobs.MarkResult(
                        job.Id, SnapshotJobStatus.Partial, published.Id, PartialCoverageReason(publishedCoverage));
                }
                else
                {
                    jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, published.Id);
                }
                AdvanceOrAttachBranchPointer(request, published.Id);
                return Attach(jobs.GetJob(job.Id)!, existed, publishedCoverage);
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
            catch (TransientProvisioningException ex)
            {
                // A TRANSIENT provisioning/clone failure (network blip, fetch timeout, remote 5xx) carries no
                // durable snapshot and is EXPECTED to recover. Do NOT record a cached terminal that would
                // suppress every later ensure for this identity (the idempotency-poisoning hole). Instead
                // requeue — bounded by the job-wide attempt counter (incremented by MarkRunning ABOVE) — so
                // the next ensure re-attempts; only once the bound is exhausted does it settle to terminal
                // Failed. Distinct from cancellation: we return a QUEUED result rather than rethrowing.
                var attempts = jobs.GetJob(job.Id)!.Attempts;
                if (attempts < _options.MaxProvisioningAttempts)
                {
                    jobs.ReplaceDiagnostics(
                        job.Id, [ProvisioningDiagnostic(job.Id, "provisioning_transient", ex.Message)]);
                    jobs.Requeue(job.Id);
                    return Produced(jobs.GetJob(job.Id)!);
                }
                var exhausted = $"provisioning failed after {attempts} attempt(s): {ex.Message}";
                jobs.MarkResult(job.Id, SnapshotJobStatus.Failed, null, exhausted);
                jobs.ReplaceDiagnostics(
                    job.Id, [ProvisioningDiagnostic(job.Id, "provisioning_attempts_exhausted", exhausted)]);
                return Produced(jobs.GetJob(job.Id)!);
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

            // Defense in depth (issue #119): the job verdict never contradicts the snapshot's durable
            // coverage record — a worker that reports Complete over a snapshot recorded as partial is
            // downgraded to Partial with the recorded reasons.
            var producedCoverage = CoverageFor(validated.SnapshotId);
            if (validated.Status == SnapshotJobStatus.Complete && producedCoverage is { IsPartial: true })
                validated = validated with { Status = SnapshotJobStatus.Partial, Error = PartialCoverageReason(producedCoverage) };

            jobs.ReplaceDiagnostics(job.Id, validated.Projects.Select(p => p.ToDiagnostic(job.Id)));
            jobs.MarkResult(job.Id, validated.Status, validated.SnapshotId, validated.Error);
            return Attach(jobs.GetJob(job.Id)!, existed, producedCoverage);
        }
        finally
        {
            _writeGate.Release();
        }
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
                Task.FromResult(IngestUnderWriteLock(artifact, manifest, assemblyIdentity, identityHash, contentHash, payloadConn, request)))
                .ConfigureAwait(false);
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
            return result;
        }
        catch
        {
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
        var (job, _) = jobs.EnsureJob(identityHash, manifest.RepositoryRemoteUrl, manifest.CommitSha, request.BranchName);
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
    // branch at the same commit both resolves and protects the shared snapshot. Raw DB write — callers must
    // already hold the write gate AND an open write transaction (AdvanceOrAttachBranchPointer). No-op when
    // the request carries no branch, no resolved snapshot, or an unknown repository.
    private void EnsureAttachBranchPointer(EnsureSnapshotRequest request, long? snapshotId)
    {
        if (request.BranchName is not { Length: > 0 } branch || snapshotId is not long sid)
            return;
        var snapshots = new SnapshotStore(_conn);
        if (snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is not long repoId)
            return;
        var branchId = snapshots.AttachBranchPointer(repoId, branch, sid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // Safety net (issue #104): AttachBranchPointer inserts the branch NON-default (issue #62 — a second
        // branch must never demote the real default). But when THIS ensure's branch should own the default
        // — the coordinator flagged it, it already is the default, or the repo has NO default yet (the
        // first/sole consumer) — promote it so the multi-tenant read selector (GetSelectedSnapshotIdFor-
        // Repository, which requires is_default = 1) can resolve a snapshot instead of failing closed. Uses
        // SetSoleDefaultBranch (not PromoteSoleDefaultBranch) because the attached row is is_default = 0 and
        // must be SET, not merely have siblings demoted; it preserves the single-default invariant.
        if (snapshots.ShouldOwnDefault(repoId, branch, request.ResolveIsDefaultBranch()))
            snapshots.SetSoleDefaultBranch(repoId, branchId);
    }

    // The branch-pointer decision for a REUSE/terminal-attach ensure — a snapshot the service attaches
    // WITHOUT running the worker (an already-terminal job or an already-published snapshot), so the
    // orchestrator's gated AdvanceBranchToSnapshot never runs on these paths. Issue #84: when the request
    // carries a monotonic head sequence, apply the SAME forward-only gate the orchestrator would have, so a
    // higher-sequence re-ensure of an already-published commit still advances the pointer (e.g. a
    // reset/force-push A@10 → B@20 → A@30) and a lower/equal one still declines — never regressing the
    // branch head and always persisting the advanced sequence. When the request carries NO sequence (the
    // local/legacy path) this preserves today's forward-only attach-if-unset behavior byte-for-byte
    // (criterion 2). The mutation runs in ONE raw BEGIN IMMEDIATE / COMMIT transaction — this path, unlike
    // the worker's AdvanceBranchToSnapshot and the contribution ingest, is NOT already inside a write
    // transaction — so a concurrent reader never observes an intermediate state where the branch pointer
    // advanced but its default was not yet set, or a re-ensured default was momentarily demoted (issue
    // #104). Raw DB write — callers must already hold the write gate.
    private void AdvanceOrAttachBranchPointer(EnsureSnapshotRequest request, long? snapshotId)
    {
        ExecRaw("BEGIN IMMEDIATE;");
        try
        {
            AdvanceOrAttachBranchPointerCore(request, snapshotId);
            ExecRaw("COMMIT;");
        }
        catch
        {
            ExecRaw("ROLLBACK;");
            throw;
        }
    }

    private void AdvanceOrAttachBranchPointerCore(EnsureSnapshotRequest request, long? snapshotId)
    {
        if (request.BranchHeadSequence is not long seq)
        {
            EnsureAttachBranchPointer(request, snapshotId);
            return;
        }
        if (snapshotId is not long sid)
            return;
        var snapshots = new SnapshotStore(_conn);
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
        var ownsDefault = snapshots.ShouldOwnDefault(repoId, branchName, request.ResolveIsDefaultBranch());
        var branchId = snapshots.EnsureBranch(repoId, branchName, ownsDefault, now);
        if (ownsDefault)
            snapshots.PromoteSoleDefaultBranch(repoId, branchId);
        snapshots.AdvanceBranchPointerForwardOnly(branchId, sid, seq, now);
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

    /// <summary>The full status of a job (with diagnostics) by durable job id — null when unknown.</summary>
    public JobStatusResult? GetStatus(long jobId)
    {
        return WithWrite(() =>
        {
            var jobs = new SnapshotJobStore(_conn);
            var job = jobs.GetJob(jobId);
            return job is null ? null : StatusOf(jobs, job);
        });
    }

    /// <summary>The full status of a job (with diagnostics) by snapshot identity hash — null when unknown.</summary>
    public JobStatusResult? GetStatusByIdentity(string identityHash)
    {
        return WithWrite(() =>
        {
            var jobs = new SnapshotJobStore(_conn);
            var job = jobs.GetJobByIdentity(identityHash);
            return job is null ? null : StatusOf(jobs, job);
        });
    }

    // Caller holds the write gate. Like Attach, the reported verdict never contradicts the durable coverage.
    private JobStatusResult StatusOf(SnapshotJobStore jobs, SnapshotJobRow job)
    {
        var coverage = CoverageFor(job.SnapshotId);
        if (job.Status == SnapshotJobStatus.Complete && coverage is { IsPartial: true })
            job = job with { Status = SnapshotJobStatus.Partial, LastError = PartialCoverageReason(coverage) };
        return new JobStatusResult { Job = job, Diagnostics = jobs.GetDiagnostics(job.Id), Coverage = coverage };
    }

    /// <summary>
    /// The durable checkout coverage recorded for a snapshot (issue #119), or null when none was recorded.
    /// </summary>
    public SnapshotCoverage? GetCoverage(long snapshotId) => WithWrite(() => CoverageFor(snapshotId));

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
    public RetentionReport RunRetention(bool execute, string? principal = null)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("retention");
        activity?.SetTag("sextant.execute", execute);
        return WithWrite(() =>
        {
            var retention = new RetentionService(_conn, _options.Retention);
            var report = execute ? retention.Execute() : retention.Plan();
            // Service-wide audit row (no single repository scope). Records the operator + whether it was a
            // dry-run plan or an executed GC pass (criterion 5, audit).
            new AuditLogStore(_conn).Append(
                AuditAction.Retention, AuditOutcome.Complete,
                actor: AuditLogStore.HashActor(principal),
                detail: execute ? "execute" : "plan");
            return report;
        });
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
    public BackupManifest CreateBackup(string destinationDir, string? principal = null)
    {
        using var activity = ServiceTelemetry.Source.StartActivity("backup");
        return WithWrite(() =>
        {
            var manifest = ServiceBackup.Create(
                _conn, IndexDatabase.LatestSchemaVersion, _paths, destinationDir,
                configFingerprint: _options.DefaultConfigHash ?? "none");
            new AuditLogStore(_conn).Append(
                AuditAction.Backup, AuditOutcome.Complete,
                actor: AuditLogStore.HashActor(principal),
                detail: $"schema_{manifest.SchemaVersion}");
            return manifest;
        });
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
    private SnapshotCoverage? CoverageFor(long? snapshotId) =>
        snapshotId is long id ? new SnapshotCoverageStore(_conn).Get(id) : null;

    private static string PartialCoverageReason(SnapshotCoverage coverage) =>
        "snapshot coverage is partial: " + string.Join(" ", coverage.Reasons);

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
    // is recorded only for a job that actually RAN (an attach reuses prior work and has no new cost).
    private void RecordEnsureAudit(EnsureSnapshotRequest request, EnsureSnapshotResult result, string? principal)
    {
        WithWrite(() =>
        {
            var job = new SnapshotJobStore(_conn).GetJob(result.JobId);
            long? costMs = !result.Attached && job is { StartedAt: long s, CompletedAt: long c } && c >= s
                ? c - s
                : null;
            new AuditLogStore(_conn).Append(
                AuditAction.Ensure,
                MapOutcome(result.Status),
                actor: AuditLogStore.HashActor(principal),
                repositoryScope: request.RepositoryRemoteUrl,
                detail: $"job_{result.JobId}",
                costIndexMs: costMs);
            return 0;
        });
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

    // An independent, short-lived READ connection to the catalog for metrics/audit reads, so operator
    // observability never contends with the writer (WAL supports concurrent readers). Read-only mode so a
    // metrics read can never mutate the catalog.
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
