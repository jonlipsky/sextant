using Microsoft.Data.Sqlite;
using Sextant.Core.Platform;
using Sextant.Service.Contributions;
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
            return jobs.ReconcileOrphanedJobs(_lease.OwnerToken)
                 + jobs.ReconcilePhantomTerminalJobs();
        });

        // Best-effort scratch sweep OUTSIDE the write transaction (filesystem, not catalog state); confined
        // to the scratch root so it can never touch a persistent volume.
        _paths.SweepOrphanedScratch();

        return reconciled;
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
            WithWrite(() => { EnsureAttachBranchPointer(request, job.SnapshotId); return 0; });
            return Attach(job, existed);
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
                EnsureAttachBranchPointer(request, current.SnapshotId);
                return Attach(current, existed);
            }

            // A complete snapshot may already be published for this identity (produced by an earlier run
            // whose job row predates migration 016, or a race we lost). Attach to it without re-indexing.
            var published = snapshots.GetByIdentityHash(hash);
            if (published is { Status: SnapshotStatus.Complete })
            {
                jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, published.Id);
                EnsureAttachBranchPointer(request, published.Id);
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
        // NOT pending at publish time (concurrent completion / immutability). Never advance the branch or mark
        // the job complete against a snapshot we did not ourselves transition to complete.
        if (snapshots.MarkComplete(snapshotId, now) != 1)
        {
            jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, snapshotId);
            return Accepted(ContributionIngestStatus.Complete, job.Id, snapshotId, contentHash, identityHash);
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
    // already hold the write gate. No-op when the request carries no branch, no resolved snapshot, or an
    // unknown repository.
    private void EnsureAttachBranchPointer(EnsureSnapshotRequest request, long? snapshotId)
    {
        if (request.BranchName is not { Length: > 0 } branch || snapshotId is not long sid)
            return;
        var snapshots = new SnapshotStore(_conn);
        if (snapshots.GetRepositoryId(request.RepositoryRemoteUrl) is not long repoId)
            return;
        snapshots.AttachBranchPointer(repoId, branch, sid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // Advances a branch pointer to a published snapshot and supersedes the previous target — the same
    // atomic branch advance the orchestrator performs, run inside the ingest write transaction.
    private static void AdvanceBranch(
        SnapshotStore snapshots, long repositoryId, string branchName, bool isDefaultBranch, long snapshotId, long now)
    {
        var branchId = snapshots.EnsureBranch(repositoryId, branchName, isDefaultBranch, now);
        if (isDefaultBranch)
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

    /// <summary>
    /// Registers (or updates) an OPEN pull-request snapshot retention root (Phase 17 criterion 4): the
    /// snapshot indexed for a PR head must not be reclaimed by retention while the PR is open, so an open-PR
    /// reviewer's cross-repo/find-references queries keep resolving. Idempotent per <c>(repository, pr)</c>;
    /// resolving the branch pointer for the PR head commit when a snapshot id is not supplied. Returns false
    /// when the repository/commit is unknown to the catalog.
    /// </summary>
    public bool RegisterPullRequestSnapshot(
        string repositoryRemoteUrl, int prNumber, string headCommitSha, long? snapshotId = null)
    {
        return WithWrite(() =>
        {
            var snapshots = new SnapshotStore(_conn);
            if (snapshots.GetRepositoryId(repositoryRemoteUrl) is not long repoId)
                return false;

            var prStore = new PullRequestSnapshotStore(_conn);
            prStore.Register(repoId, prNumber, snapshotId, headCommitSha,
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
