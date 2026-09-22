using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sextant.Core;
using Sextant.Store;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Sextant.Indexer;

public sealed class IndexOrchestrator
{
    private readonly IndexDatabase _db;
    private readonly Action<string>? _log;
    private readonly bool _useDocumentExtractor;
    private readonly ExtractionParallelismOptions _parallelism;
    private readonly IndexProfileDescriptor _profile;

    public IndexOrchestrator(
        IndexDatabase db,
        Action<string>? log = null,
        bool useDocumentExtractor = false,
        ExtractionParallelismOptions? parallelism = null,
        IndexProfileDescriptor? profile = null)
    {
        _db = db;
        _log = log;
        _useDocumentExtractor = useDocumentExtractor;
        _parallelism = parallelism ?? ExtractionParallelismOptions.Default;
        // Default to the "everything on" profile so a construction that does not specify one keeps the
        // pre-profile behavior of building every optional feature (behavior-preserving for tests and
        // any direct caller). Production entry points pass the configured profile explicitly.
        _profile = profile ?? IndexProfileDescriptor.Full;
    }

    /// <summary>One non-generated document paired with its project's compilation, the unit of parallel
    /// per-document extraction. The semantic model is built inside the worker so each model is used on
    /// a single thread; the compilation is immutable and safely shared.</summary>
    private readonly record struct OccurrenceDoc(Compilation Compilation, SyntaxTree Tree);

    public async Task IndexSolutionAsync(
        Solution solution,
        IProgress<IndexingProgress>? progress = null,
        IndexingMetrics? metrics = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? projectCanonicalFilter = null,
        SnapshotContext? snapshotContext = null,
        bool enableSnapshots = true,
        OverlayContext? overlay = null,
        string? workingTreeDelta = null,
        string? fallbackReason = null)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = new Stopwatch();
        PhaseMetric? currentPhase = null;

        void StartPhase(string phaseName)
        {
            // Finish (and stamp Completed) the previous phase *before* honoring cancellation, so a
            // fully-finished phase is never left marked Running when cancellation lands between phases.
            FinishPhase();
            ThrowIfCancelled();
            currentPhase = new PhaseMetric { Name = phaseName };
            metrics?.Phases.Add(currentPhase);
            phaseStopwatch.Restart();
        }

        void FinishPhase()
        {
            if (currentPhase == null) return;
            phaseStopwatch.Stop();
            currentPhase.DurationMs = phaseStopwatch.ElapsedMilliseconds;
            currentPhase.Status = IndexRunStatus.Completed;
            currentPhase = null;
        }

        void EnterProject()
        {
            ThrowIfCancelled();
            if (currentPhase != null) currentPhase.ProjectsProcessed++;
        }

        // Cooperative cancellation point. Before unwinding it records the interrupted phase's
        // partial duration + Cancelled status and the run-level Cancelled outcome, so per-phase and
        // per-run metrics recorded up to this point survive the cancellation.
        void ThrowIfCancelled()
        {
            if (!cancellationToken.IsCancellationRequested) return;
            if (currentPhase is { Status: IndexRunStatus.Running })
            {
                phaseStopwatch.Stop();
                currentPhase.DurationMs = phaseStopwatch.ElapsedMilliseconds;
                currentPhase.Status = IndexRunStatus.Cancelled;
            }
            if (metrics != null)
            {
                totalStopwatch.Stop();
                metrics.TotalDurationMs = totalStopwatch.ElapsedMilliseconds;
                metrics.Status = IndexRunStatus.Cancelled;
                metrics.FailureReason = "cancelled";
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        ThrowIfCancelled();

        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var symbolStore = new SymbolStore(conn);
        var referenceStore = new ReferenceStore(conn);
        var callGraphStore = new CallGraphStore(conn);
        var relationshipStore = new RelationshipStore(conn);
        var dependencyStore = new ProjectDependencyStore(conn);
        var apiSurfaceStore = new ApiSurfaceStore(conn);

        // One shared file/version engine for the whole run. All stores that resolve a source path to a
        // file_version_id (symbols, occurrences, comments) route through it so path→id resolution is
        // single-sourced, cached, and — because it is only touched by the sequential symbol phase and
        // the single occurrence-persistence consumer — deterministic (Phase 6).
        var fileStore = new FileStore(conn);
        symbolStore.Files = fileStore;
        referenceStore.Files = fileStore;
        callGraphStore.Files = fileStore;
        relationshipStore.Files = fileStore;

        var solutionStore = new SolutionStore(conn);
        var runStore = new IndexRunStore(conn);

        // Map each Roslyn project instance to its stored logical-project row. Keyed by the Roslyn
        // ProjectId (unique per instance) rather than the csproj file path, so the multiple
        // evaluated-TFM instances of one multi-targeted csproj map to distinct logical projects
        // instead of collapsing onto a single id (which would make DeleteByProject cross-TFM).
        var projectRoslynToId = new Dictionary<ProjectId, long>();
        // Logical-project rows this run is responsible for (re)building. For a full index it is every
        // registered project; for an incremental rebuild the orchestrator is handed a canonical-id
        // filter naming the invalidated project closure, and only those projects are reset and
        // re-extracted. Registration and dependency recording always cover every project so the
        // cross-project reference/usage mapping and the dependency graph stay complete.
        var processSet = new HashSet<ProjectId>();
        var catalog = new SymbolCatalog();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Feature gates (Phase 8). Calls, references, and relationships are core and always built; the
        // profile only gates the optional add-ons. Dataflow rows (argument/return flow) are the deep
        // profile's extended evidence; comments are their own phase. When a feature is off the indexer
        // does not persist its rows so the optional tables stay empty and the run is cheaper.
        var persistDataflow = _profile.Has(IndexFeature.Dataflow);
        var extractComments = _profile.Has(IndexFeature.Comments);

        // Open a staging generation and a single bounded, batched write session for the whole run.
        // The session replaces per-row implicit transactions with a small number of explicit,
        // project-bounded batches; the run scope keeps the previous complete generation visible until
        // this one is published, and abandons the staging generation if the run fails or is cancelled.
        using var runScope = runStore.BeginScope(
            metrics?.Mode ?? "full", now,
            _profile.ConfigurationHash, _profile.Profile, (long)_profile.Features);
        using var session = _db.BeginWriteSession();
        using var symbolInsert = symbolStore.CreateInsertCommand();
        using var referenceInsert = referenceStore.CreateInsertCommand();
        using var relationshipInsert = relationshipStore.CreateInsertCommand();
        using var callGraphInsert = callGraphStore.CreateInsertCommand();
        var argumentFlowStore = new ArgumentFlowStore(conn);
        var returnFlowStore = new ReturnFlowStore(conn);
        using var argumentFlowInsert = argumentFlowStore.CreateInsertCommand();
        using var returnFlowInsert = returnFlowStore.CreateInsertCommand();
        session.Begin();

        // Discover submodules from the repo root
        var submodules = new List<SubmoduleInfo>();
        string? repoRoot = null;
        var firstProjectPath = solution.Projects.FirstOrDefault(p => p.FilePath != null)?.FilePath;
        if (firstProjectPath != null)
        {
            repoRoot = GitRemoteResolver.ResolveGitRoot(firstProjectPath);
            if (repoRoot != null)
            {
                submodules = await SubmoduleDiscovery.DiscoverAsync(repoRoot);
                if (submodules.Count > 0)
                    _log?.Invoke($"Discovered {submodules.Count} submodule(s)");
            }
        }

        // Phase 9 snapshot coordination. Resolve the repository/commit coordinates (git, or injected for
        // tests); when none can be resolved this stays the pre-Phase-9 mutable-row path (legacy DB, or a
        // no-git temp workspace), so existing behavior and the temp-dir test suite are unchanged.
        // enableSnapshots is a caller override: a multi-solution repo shares ONE snapshot identity across
        // its solutions (commit+tree+schema+analyzer+config+toolchain — no solution component), so the
        // daemon disables snapshots there to avoid solution 2+ idempotently attaching to solution 1's
        // snapshot and silently skipping its own indexing. An explicitly injected snapshotContext always
        // wins (tests), so this only gates the auto-resolved git path.
        var isFullIndex = projectCanonicalFilter == null;
        var effectiveCtx = snapshotContext ?? (enableSnapshots ? TryResolveSnapshotContext(repoRoot) : null);
        SnapshotStore? snapshotStore = null;
        long? repositoryId = null;
        long? commitId = null;
        long? activeSnapshotId = null;
        var isOverlayRun = false;
        if (effectiveCtx != null)
        {
            snapshotStore = new SnapshotStore(conn);
            repositoryId = snapshotStore.EnsureRepository(effectiveCtx.RepositoryRemoteUrl, now);
            commitId = snapshotStore.EnsureCommit(repositoryId.Value, effectiveCtx.CommitSha, effectiveCtx.TreeSha, now);

            if (isFullIndex)
            {
                var identity = new SnapshotIdentity
                {
                    RepositoryRemoteUrl = effectiveCtx.RepositoryRemoteUrl,
                    CommitSha = effectiveCtx.CommitSha,
                    TreeSha = effectiveCtx.TreeSha,
                    SchemaVersion = IndexDatabase.LatestSchemaVersion,
                    AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
                    ConfigHash = _profile.ConfigurationHash,
                    ToolchainFingerprint = ToolchainFingerprint.Current,
                    // A full LOCAL index over a DIRTY working tree (Phase-10 fallback: no compatible base)
                    // carries the working-tree delta so it never claims identity with the clean base
                    // commit's snapshot (issue #43). Null for a genuine clean-HEAD full index.
                    WorkingTreeDelta = workingTreeDelta,
                    // A full index is never an overlay; for a dirty fallback this keeps its identity
                    // distinct from an overlay of the same dirty tree (issue #47). Ignored when the tree
                    // is clean (delta null → discriminator not folded).
                    IsOverlay = false
                };
                var (snapId, existed, status) = snapshotStore.BeginPending(
                    identity, repositoryId.Value, commitId, runScope.RunId, now, fallbackReason: fallbackReason);
                if (existed && (status == SnapshotStatus.Complete || status == SnapshotStatus.Superseded))
                {
                    // A snapshot for this exact identity (commit + tree + schema + analyzer + config +
                    // toolchain) already holds valid, immutable data. NEVER rebuild it: for a Complete one
                    // this is the idempotent duplicate-publish case (criterion 3); for a Superseded one this
                    // is a branch rollback / re-checkout of an older commit (criterion 2), where rebuilding
                    // would both mutate an immutable snapshot AND fail the guarded pending→complete publish.
                    // Instead re-select it — (re)point the branch at it and restore Complete status — inside
                    // this still-open write transaction, commit, and abandon the (empty) staging run on
                    // dispose so the reused snapshot keeps its original generation.
                    SelectExistingSnapshot(snapshotStore, repositoryId.Value, effectiveCtx, snapId, now);
                    session.Complete();
                    _log?.Invoke($"Snapshot {snapId} for commit {effectiveCtx.CommitSha} already indexed; " +
                                 $"re-selected for branch '{effectiveCtx.BranchName}' without rebuild (idempotent).");
                    return;
                }
                activeSnapshotId = snapId;
                // A retried snapshot that exists but is NOT complete/superseded (a prior Partial or Failed
                // generation for this exact identity, or a Pending one abandoned by a crash) is about to be
                // rebuilt from scratch below. Reset it to pending so the guarded pending→complete publish
                // succeeds instead of tripping the "was not pending at publish" guard.
                if (existed && status != SnapshotStatus.Pending)
                    snapshotStore.MarkStatus(snapId, SnapshotStatus.Pending);
            }
            else
            {
                if (overlay != null)
                {
                    // Phase-10 overlay (issue #44): stage a FRESH snapshot layered on the committed base
                    // rather than mutating the base in place. Its identity = the base commit identity +
                    // the working-tree delta digest (issue #43), so a dirty tree never collides with the
                    // clean base commit's snapshot, and the same dirty state re-selects idempotently.
                    var overlayIdentity = new SnapshotIdentity
                    {
                        RepositoryRemoteUrl = effectiveCtx.RepositoryRemoteUrl,
                        CommitSha = effectiveCtx.CommitSha,
                        TreeSha = effectiveCtx.TreeSha,
                        SchemaVersion = IndexDatabase.LatestSchemaVersion,
                        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
                        ConfigHash = _profile.ConfigurationHash,
                        ToolchainFingerprint = ToolchainFingerprint.Current,
                        WorkingTreeDelta = overlay.WorkingTreeDelta,
                        // An overlay is distinct from a full-local fallback for the same dirty tree (#47).
                        IsOverlay = true
                    };
                    var (overlayId, overlayExisted, overlayStatus) = snapshotStore.BeginPending(
                        overlayIdentity, repositoryId.Value, commitId, runScope.RunId, now,
                        baseSnapshotId: overlay.BaseSnapshotId);
                    if (overlayExisted && (overlayStatus == SnapshotStatus.Complete || overlayStatus == SnapshotStatus.Superseded))
                    {
                        // This exact dirty state was already built as an overlay: re-select it without
                        // rebuilding (criterion 3 idempotent restart), never touching the base. Use the
                        // overlay-aware re-select so re-pointing the branch supersedes only a stale
                        // OVERLAY it may currently target — never the committed base (issue #44): the
                        // branch can legitimately point at the base here (e.g. built overlay-X, reverted
                        // to clean so the branch rolled back to the base, then re-applied the identical
                        // edit), and superseding it would break every later base lookup.
                        SelectExistingOverlay(snapshotStore, repositoryId.Value, effectiveCtx, overlayId, now);
                        session.Complete();
                        _log?.Invoke($"Overlay {overlayId} for the current working-tree delta already exists; " +
                                     $"re-selected for branch '{effectiveCtx.BranchName}' without rebuild (idempotent).");
                        return;
                    }
                    activeSnapshotId = overlayId;
                    isOverlayRun = true;
                    if (overlayExisted && overlayStatus != SnapshotStatus.Pending)
                        snapshotStore.MarkStatus(overlayId, SnapshotStatus.Pending);
                }
                else
                {
                    // Legacy (pre-Phase-10) incremental: mutate the selected working-head snapshot in
                    // place. Only reachable when a caller drives incremental WITHOUT an overlay context
                    // (e.g. the direct IncrementalIndexer test path); the Phase-10 reconciler always
                    // supplies an overlay so a published base snapshot is never mutated.
                    activeSnapshotId = snapshotStore.GetSelectedSnapshotId();
                }
            }
        }

        // Phase 1: Register all projects (using submodule remote URL when applicable)
        var projectList = solution.Projects.ToList();
        var totalProjects = projectList.Count;
        var projectIndex = 0;
        if (metrics != null) metrics.ProjectCount = totalProjects;

        StartPhase("registering_projects");
        _log?.Invoke("Registering projects...");
        progress?.Report(new IndexingProgress
        {
            Phase = "registering_projects",
            Description = $"Registering {totalProjects} projects",
            ProjectIndex = 0,
            ProjectCount = totalProjects
        });
        foreach (var project in solution.Projects)
        {
            EnterProject();
            if (project.FilePath == null) continue;

            var identity = ProjectIdentityFactory.Create(project, submodules, repoRoot);
            var inFilter = projectCanonicalFilter == null || projectCanonicalFilter.Contains(identity.CanonicalId);

            long projectId;
            var extractThisProject = inFilter;
            if (activeSnapshotId is long snapId && snapshotStore != null && repositoryId is long repoId)
            {
                var logicalId = snapshotStore.EnsureLogicalProject(
                    repoId, identity.CanonicalId, identity.RepoRelativePath, identity.TargetFramework, now);

                if (overlay != null && !inFilter
                    && projectStore.GetSnapshotProjectRow(overlay.BaseSnapshotId, logicalId) is long baseRowId)
                {
                    // Out-of-closure project in an overlay run: SHARE the base snapshot's existing,
                    // unchanged project-version row by mapping it into the overlay's snapshot_projects.
                    // No new row and no re-extraction, so the base snapshot stays byte-identical (issue
                    // #44). Safe because the undirected closure guarantees no reference edge crosses the
                    // boundary between shared and re-extracted projects (Phase-4 invariant).
                    projectId = baseRowId;
                    snapshotStore.MapProject(snapId, projectId);
                }
                else
                {
                    // Snapshot-tagged project version: a fresh (per-snapshot) row keyed by the commit-
                    // invariant logical identity, so distinct commits' versions coexist without
                    // overwriting (criterion 1) and the branch pointer stays out of the row key
                    // (criterion 2). In an overlay run this covers every closure project, plus the
                    // defensive case of an out-of-closure project with no base row to share (then force
                    // its extraction so the fresh row is not left empty).
                    projectId = projectStore.UpsertSnapshotProject(identity, snapId, logicalId, now);
                    snapshotStore.MapProject(snapId, projectId);
                    if (overlay != null && !inFilter)
                        extractThisProject = true;
                }
            }
            else
            {
                projectId = projectStore.Insert(identity, now);
            }
            projectRoslynToId[project.Id] = projectId;
            if (extractThisProject)
                processSet.Add(project.Id);
            _log?.Invoke($"  Project: {project.Name} (id={projectId}, tfm={identity.TargetFramework}, test={identity.IsTestProject})");
        }

        // Record solution → project mappings
        if (solution.FilePath != null)
        {
            var solutionId = solutionStore.Upsert(solution.FilePath, Path.GetFileNameWithoutExtension(solution.FilePath), now);
            foreach (var pid in projectRoslynToId.Values)
                solutionStore.AddProjectMapping(solutionId, pid);
        }
        session.CommitBatch();

        // Phase 2: Extract symbols from all projects
        StartPhase("extracting_symbols");
        _log?.Invoke("Extracting symbols...");
        projectIndex = 0;
        // Completeness gate (criterion 3): count processed projects whose Roslyn compilation could not be
        // produced (missing SDK/reference, broken evaluation). Such a project silently contributes zero
        // rows; publishing that as a complete branch head would serve an incomplete API surface. A full
        // index that hits any such failure is marked partial (diagnosable) and NOT selected below.
        var compilationFailures = 0;
        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null || !projectRoslynToId.TryGetValue(project.Id, out var projectId)
                || !processSet.Contains(project.Id))
                continue;

            EnterProject();
            projectIndex++;
            _log?.Invoke($"  {project.Name}...");
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_symbols",
                Description = $"Extracting symbols from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });

            // Reset this logical (per-TFM) project's contributions before re-extracting. Deleting the
            // project's symbols cascades its relationships, its outbound call occurrences, and every
            // inbound occurrence that targets them (target_symbol_id cascade); deleting the project's
            // files cascades its file_versions and, through them, the pure-reference occurrences and
            // comments located in this project's files (the outbound usages this project owns) and
            // clears fingerprints for files deleted/renamed away since the last run. Together the two
            // deletes clear exactly this project's owned rows — the usage-site production domain — so
            // they are all rebuilt from scratch below.
            symbolStore.DeleteByProject(projectId);
            fileStore.DeleteByProject(projectId);

            var (symbols, compilationAvailable) = await SymbolExtractor.ExtractFromProjectWithStatusAsync(
                project, projectId, includeDocComments: _profile.Has(IndexFeature.DocumentationSearch));
            if (!compilationAvailable)
            {
                compilationFailures++;
                _log?.Invoke($"    WARNING: {project.Name} produced no compilation; generation will be marked partial.");
            }

            // Issue #35 (TOCTOU): capture each analyzed on-disk source file's raw-disk hash NOW, before
            // any symbol/occurrence resolves its file_version row, so the persisted content hash is the
            // bytes analyzed rather than whatever is on disk later at persist time.
            foreach (var document in project.Documents)
            {
                var docPath = document.FilePath;
                if (!string.IsNullOrEmpty(docPath) && !SymbolExtractor.IsGeneratedFile(docPath) && File.Exists(docPath))
                    fileStore.CaptureAnalyzedHash(projectId, docPath);
            }

            foreach (var symbol in symbols)
            {
                var id = symbolStore.Insert(symbolInsert, symbol);
                catalog.Add(projectId, symbol.SymbolKey, id);
                session.RowsWritten();
            }

            // Fingerprint every indexed (non-generated, on-disk) file so an unchanged daemon restart
            // short-circuits to zero work, and record the project's evaluation fingerprint so a later
            // catch-up can detect config/props/global.json/assets changes.
            await SeedFileIndexAsync(project, projectId, fileStore, session, now, cancellationToken);
            projectStore.SetEvaluationFingerprint(projectId, EvaluationFingerprint.Compute(project.FilePath, repoRoot));

            session.CommitBatch();

            _log?.Invoke($"    {symbols.Count} symbols extracted");
        }

        // Phase 3-5 (document-oriented): one pass over each processed project's documents emits
        // relationships, references, and call/dataflow contributions from a single cached semantic
        // model + root per document, replacing the legacy declaration-driven FindReferencesAsync
        // passes below. Occurrence ownership flips to the using document; cross-project references
        // still persist in_project_id = the using project and symbol_id = the target declaration, so
        // the Phase-4 incremental closure's cross-project connectivity
        // (ReferenceStore.GetCrossProjectPairs) — and therefore its correctness — is preserved because
        // every cross-project call/inheritance also emits a reference edge.
        if (_useDocumentExtractor)
        {
            StartPhase("extracting_occurrences");
            _log?.Invoke($"Extracting occurrences (document-oriented, parallelism={_parallelism.MaxParallelism})...");

            // Compilation-scoped exact resolution: map a bound occurrence target's containing assembly
            // to its exact indexed (per-TFM) project id. This restores parity with the legacy
            // declaration-anchored reference path — which bound each reference to the exact declared
            // symbol — for keys that are ambiguous across projects (a multi-targeted dependency several
            // of whose TFMs are indexed, or an extern-alias-duplicated assembly). Solution.GetProject
            // maps a source assembly symbol back to its originating project; results are memoized since
            // one assembly is the target of many occurrences. A null result (metadata / out-of-solution
            // assembly, or a project outside the indexed map) leaves the contribution to fall back to
            // key-only resolution in the persistence loops below.
            //
            // The cache is a ConcurrentDictionary because ResolveTargetProject is invoked from the
            // parallel per-document workers (it stamps each occurrence's exact TargetProjectId). Its
            // factory — Solution.GetProject over the immutable solution plus a read-only
            // projectRoslynToId lookup — is deterministic and side-effect-free, so a benign racing
            // double-computation yields the identical value: thread-safe and determinism-preserving.
            // It is cleared at each project boundary (below) so it only ever retains assembly symbols
            // referenced by the current project — all of which are already rooted by that project's
            // live compilation — keeping the "one compilation live at a time" memory bound intact
            // (criteria 2 & 6) rather than accumulating every processed project's assemblies.
            var assemblyProjectCache = new ConcurrentDictionary<IAssemblySymbol, long?>(SymbolEqualityComparer.Default);
            long? ResolveTargetProject(IAssemblySymbol assembly) =>
                assemblyProjectCache.GetOrAdd(assembly, a =>
                    solution.GetProject(a) is { } targetProject
                    && projectRoslynToId.TryGetValue(targetProject.Id, out var targetPid)
                        ? targetPid
                        : null);

            // Pure, CPU-bound per-document extraction run in parallel. Builds the document's semantic
            // model on the calling worker thread (one model per thread) and walks it once into a
            // fresh per-document contribution set; the catalog and SQLite are never touched here. The
            // linked token (not the outer request token) is threaded through so a consumer/worker
            // fault cancels in-flight analysis, not just externally-requested cancellation.
            DocumentContributionSet ExtractOne(OccurrenceDoc doc, CancellationToken ct)
            {
                var model = doc.Compilation.GetSemanticModel(doc.Tree);
                var root = doc.Tree.GetRoot(ct);
                var text = doc.Tree.GetText(ct);
                var set = new DocumentContributionSet();
                DocumentSemanticExtractor.ExtractDocument(
                    root, model, doc.Tree.FilePath, text, set, ResolveTargetProject,
                    includeDataflow: persistDataflow);
                return set;
            }

            // Project descriptors in deterministic solution order. Each materializes its compilation and
            // non-generated documents just-in-time (one project at a time) so completed projects'
            // compilations/models are released and peak memory stays bounded (criteria 2 & 6).
            var projectDescriptors =
                new List<Func<CancellationToken, Task<ProjectExtraction<OccurrenceDoc>>>>();
            foreach (var project in solution.Projects)
            {
                if (!processSet.Contains(project.Id)) continue;
                if (!projectRoslynToId.TryGetValue(project.Id, out var ownerProjectId)) continue;

                var captured = project;
                var owner = ownerProjectId;
                projectDescriptors.Add(async ct =>
                {
                    // Release the previous project's cached assembly symbols before extracting this
                    // one. The producer invokes descriptors sequentially with no worker active (the
                    // prior project's Parallel.ForEachAsync has completed and this project's has not
                    // started), and the consumer never calls ResolveTargetProject (targets are already
                    // stamped into the contribution records), so clearing here races nothing.
                    assemblyProjectCache.Clear();
                    var compilation = await captured.GetCompilationAsync(ct);
                    var docs = new List<OccurrenceDoc>();
                    if (compilation != null)
                    {
                        foreach (var syntaxTree in compilation.SyntaxTrees)
                        {
                            if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                                continue;
                            docs.Add(new OccurrenceDoc(compilation, syntaxTree));
                        }
                    }
                    return new ProjectExtraction<OccurrenceDoc>(owner, captured.Name, docs);
                });
            }

            // Single-consumer persistence: the only stage that touches the catalog and the SQLite
            // writer. Runs one project at a time, in the deterministic order the producer enqueued
            // them, so target resolution (and AmbiguousEdgeBindings counting) and rowid assignment stay
            // single-threaded and deterministic.
            var occurrenceProjectIndex = 0;
            Task PersistProject(ProjectContributions project, CancellationToken persistToken)
            {
                persistToken.ThrowIfCancellationRequested();
                EnterProject();
                occurrenceProjectIndex++;
                progress?.Report(new IndexingProgress
                {
                    Phase = "extracting_occurrences",
                    Description = $"Persisting occurrences from {project.Name}",
                    CurrentProject = project.Name,
                    ProjectIndex = occurrenceProjectIndex,
                    ProjectCount = totalProjects
                });

                long ownerProjectId = project.OwnerProjectId;
                var contributions = project.Contributions;

                foreach (var rel in contributions.Relationships)
                {
                    if (catalog.TryResolveEdge(rel.FromKey, ownerProjectId, out var fromId) &&
                        catalog.TryResolveEdge(rel.ToKey, ownerProjectId, out var toId))
                    {
                        relationshipStore.Insert(relationshipInsert, new RelationshipInfo
                        {
                            FromSymbolId = fromId,
                            ToSymbolId = toId,
                            Kind = rel.Kind,
                            LastIndexedAt = now
                        });
                        session.RowsWritten();
                    }
                }

                foreach (var reference in contributions.References)
                {
                    // Prefer the exact per-TFM target row when the extractor resolved the target's real
                    // owning project (compilation-scoped); fall back to key-only resolution (which
                    // deterministically picks and counts an ambiguity) only for targets outside the
                    // indexed set.
                    if (!TryResolveTarget(catalog, reference.TargetKey, reference.TargetProjectId,
                            ownerProjectId, out var targetId))
                        continue;

                    referenceStore.Insert(referenceInsert, new ReferenceInfo
                    {
                        SymbolId = targetId,
                        InProjectId = ownerProjectId,
                        FilePath = reference.FilePath,
                        Line = reference.Line,
                        ContextSnippet = reference.Snippet,
                        ReferenceKind = reference.Kind,
                        AccessKind = reference.Access
                    });
                    session.RowsWritten();
                }

                foreach (var call in contributions.Calls)
                {
                    // The caller is the enclosing member of this document, so it resolves exactly in the
                    // owner project; the callee uses compilation-scoped exact resolution with key-only
                    // fallback (parity with the legacy call path for in-solution unique targets).
                    if (!catalog.TryResolveEdge(call.CallerKey, ownerProjectId, out var callerId) ||
                        !TryResolveTarget(catalog, call.CalleeKey, call.CalleeProjectId, ownerProjectId, out var calleeId))
                        continue;

                    var edgeId = callGraphStore.Insert(callGraphInsert, new CallGraphEdge
                    {
                        CallerSymbolId = callerId,
                        CalleeSymbolId = calleeId,
                        CallSiteFile = call.CallSiteFile,
                        CallSiteLine = call.CallSiteLine,
                        CallSiteColumn = call.CallSiteColumn,
                        LastIndexedAt = now
                    }, ownerProjectId);
                    session.RowsWritten();

                    var dfResult = call.Dataflow;
                    if (persistDataflow)
                    {
                        foreach (var arg in dfResult.Arguments)
                        {
                            argumentFlowStore.Insert(argumentFlowInsert, edgeId, arg.ParameterOrdinal, arg.ParameterName,
                                arg.ArgumentExpression, arg.ArgumentKind, arg.SourceSymbolFqn, now);
                            session.RowsWritten();
                        }

                        if (dfResult.ReturnDestination != null)
                        {
                            returnFlowStore.Insert(returnFlowInsert, edgeId, dfResult.ReturnDestination.DestinationKind,
                                dfResult.ReturnDestination.DestinationVariable,
                                dfResult.ReturnDestination.DestinationSymbolFqn, now);
                            session.RowsWritten();
                        }
                    }
                }

                if (contributions.CompletenessDiagnostics > 0)
                    _log?.Invoke($"  {project.Name}: {contributions.CompletenessDiagnostics} unresolved " +
                                 "region(s) (extraction completeness diagnostic)");

                _log?.Invoke($"  {project.Name}: occurrences extracted");
                // Honour cancellation before publishing this project's batch so a mid-run cancel drops
                // the uncommitted rows (rolled back on session dispose) instead of committing them.
                persistToken.ThrowIfCancellationRequested();
                session.CommitBatch();
                return Task.CompletedTask;
            }

            await ParallelExtractionPipeline.RunAsync(
                projectDescriptors, ExtractOne, PersistProject, _parallelism, cancellationToken);
        }

        // Phase 3: Extract relationships (legacy declaration-driven path; skipped when the
        // document-oriented extractor above has already produced relationships/references/calls)
        if (!_useDocumentExtractor)
        {
        StartPhase("extracting_relationships");
        _log?.Invoke("Extracting relationships...");
        projectIndex = 0;
        progress?.Report(new IndexingProgress
        {
            Phase = "extracting_relationships",
            Description = "Extracting type relationships",
            ProjectIndex = 0,
            ProjectCount = totalProjects
        });
        foreach (var project in solution.Projects)
        {
            if (!processSet.Contains(project.Id)) continue;
            EnterProject();
            projectIndex++;
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_relationships",
                Description = $"Extracting relationships from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            long? relProjectId = projectRoslynToId.TryGetValue(project.Id, out var relPid)
                ? relPid
                : null;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                ThrowIfCancelled();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                foreach (var node in root.DescendantNodes())
                {
                    if (semanticModel.GetDeclaredSymbol(node) is INamedTypeSymbol typeSymbol &&
                        !typeSymbol.IsImplicitlyDeclared &&
                        !SemanticSymbolKeyFactory.IsExcludedArtifact(typeSymbol))
                    {
                        var rels = RelationshipExtractor.ExtractRelationships(typeSymbol);
                        var instantiates = RelationshipExtractor.ExtractInstantiates(typeSymbol, compilation);
                        foreach (var (fromKey, toKey, kind) in rels.Concat(instantiates))
                        {
                            if (catalog.TryResolveEdge(fromKey, relProjectId, out var fromId) &&
                                catalog.TryResolveEdge(toKey, relProjectId, out var toId))
                            {
                                relationshipStore.Insert(relationshipInsert, new RelationshipInfo
                                {
                                    FromSymbolId = fromId,
                                    ToSymbolId = toId,
                                    Kind = kind,
                                    LastIndexedAt = now
                                });
                                session.RowsWritten();
                            }
                        }
                    }
                }
            }
            session.CommitBatch();
        }

        // Phase 4: Extract references
        StartPhase("extracting_references");
        _log?.Invoke("Extracting references...");
        projectIndex = 0;
        foreach (var project in solution.Projects)
        {
            if (!processSet.Contains(project.Id)) continue;
            EnterProject();
            projectIndex++;
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_references",
                Description = $"Extracting references from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            long? refProjectId = projectRoslynToId.TryGetValue(project.Id, out var refPid)
                ? refPid
                : null;

            // No per-file reference clear here: the symbol phase already reset every processed
            // project (DeleteByProject), which cascade-deleted the references those symbols owned —
            // including cross-project usages that point INTO this project's declarations. Re-deleting
            // by usage file after inserts have begun is what dropped freshly-inserted cross-project
            // rows when a dependency was processed before its consumer (acceptance criterion 4).

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                ThrowIfCancelled();
                if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                    continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                foreach (var node in root.DescendantNodes())
                {
                    var declaredSymbol = semanticModel.GetDeclaredSymbol(node);
                    if (declaredSymbol == null || declaredSymbol.IsImplicitlyDeclared)
                        continue;

                    // Only extract references for types and type members
                    if (SymbolExtractor.MapSymbolKind(declaredSymbol) == null ||
                        SemanticSymbolKeyFactory.IsExcludedArtifact(declaredSymbol))
                        continue;

                    var declKey = SemanticSymbolKeyFactory.DeclarationKey(declaredSymbol);
                    if (!catalog.TryResolveEdge(declKey, refProjectId, out var symbolId))
                        continue;

                    var refs = await ReferenceExtractor.ExtractReferencesAsync(
                        declaredSymbol, symbolId, solution, projectRoslynToId);

                    foreach (var refInfo in refs)
                    {
                        referenceStore.Insert(referenceInsert, refInfo);
                        session.RowsWritten();
                    }
                }
            }

            _log?.Invoke($"  {project.Name}: references extracted");
            session.CommitBatch();
        }
        }

        // Phase 4.5: Extract tagged comments (gated: standard+ profiles only)
        if (extractComments)
        {
        StartPhase("extracting_comments");
        _log?.Invoke("Extracting tagged comments...");
        projectIndex = 0;
        var commentStore = new CommentStore(conn);
        commentStore.Files = fileStore;
        using var commentInsert = commentStore.CreateInsertCommand();
        foreach (var project in solution.Projects)
        {
            if (!processSet.Contains(project.Id)) continue;
            EnterProject();
            projectIndex++;
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_comments",
                Description = $"Extracting comments from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            if (project.FilePath == null || !projectRoslynToId.TryGetValue(project.Id, out var commentProjectId))
                continue;

            // The symbol-phase reset already cascade-deleted this project's comments (comments FK to
            // file_version, and the project's file_versions were deleted there). Re-clear defensively
            // so a comment-only re-run still drops stale rows (files deleted/renamed away) before
            // re-extract; it is project-scoped (file_version → file → project_id), never cross-project.
            commentStore.DeleteByProject(commentProjectId);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                ThrowIfCancelled();
                if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                    continue;

                var comments = CommentExtractor.ExtractComments(syntaxTree);
                foreach (var comment in comments)
                {
                    var enclosingSymbolId = ResolveEnclosingSymbol(comment.Line, comment.FilePath, commentProjectId, symbolStore);
                    commentStore.Insert(commentInsert, commentProjectId, comment.FilePath, comment.Line, comment.Tag,
                                       comment.Text, enclosingSymbolId, now);
                    session.RowsWritten();
                }
            }

            _log?.Invoke($"  {project.Name}: comments extracted");
            session.CommitBatch();
        }
        }

        // Phase 5: Extract call graph and dataflow (legacy path; skipped when the document-oriented
        // extractor above has already produced call edges + dataflow)
        if (!_useDocumentExtractor)
        {
        StartPhase("extracting_call_graph");
        _log?.Invoke("Extracting call graph...");
        projectIndex = 0;
        foreach (var project in solution.Projects)
        {
            if (!processSet.Contains(project.Id)) continue;
            EnterProject();
            projectIndex++;
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_call_graph",
                Description = $"Extracting call graph from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            long? callProjectId = projectRoslynToId.TryGetValue(project.Id, out var callPid)
                ? callPid
                : null;

            // No per-file call-edge clear here: the symbol phase's DeleteByProject already cascaded
            // this project's outbound call edges and the inbound edges targeting its methods. Deleting
            // by call-site file after inserts began would drop freshly-inserted cross-project edges.

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                ThrowIfCancelled();
                if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                    continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                foreach (var node in root.DescendantNodes())
                {
                    var declaredSymbol = semanticModel.GetDeclaredSymbol(node);
                    if (declaredSymbol is not IMethodSymbol methodSymbol || declaredSymbol.IsImplicitlyDeclared)
                        continue;

                    var callerKey = SemanticSymbolKeyFactory.DeclarationKey(methodSymbol);
                    if (!catalog.TryResolveEdge(callerKey, callProjectId, out var callerSymbolId))
                        continue;

                    var edges = await CallGraphBuilder.BuildCallGraphAsync(methodSymbol, project);

                    foreach (var edge in edges)
                    {
                        if (catalog.TryResolveEdge(edge.CalleeKey, callProjectId, out var calleeSymbolId))
                        {
                            var edgeId = callGraphStore.Insert(callGraphInsert, new CallGraphEdge
                            {
                                CallerSymbolId = callerSymbolId,
                                CalleeSymbolId = calleeSymbolId,
                                CallSiteFile = edge.CallSiteFile,
                                CallSiteLine = edge.CallSiteLine,
                                LastIndexedAt = now
                            });
                            session.RowsWritten();

                            // Extract and store dataflow for this call site
                            if (persistDataflow && edge.InvocationSyntax != null && edge.SemanticModel != null)
                            {
                                var dfResult = DataflowExtractor.ExtractFromInvocation(
                                    edge.InvocationSyntax, edge.SemanticModel);

                                foreach (var arg in dfResult.Arguments)
                                {
                                    argumentFlowStore.Insert(argumentFlowInsert, edgeId, arg.ParameterOrdinal, arg.ParameterName,
                                        arg.ArgumentExpression, arg.ArgumentKind, arg.SourceSymbolFqn, now);
                                    session.RowsWritten();
                                }

                                if (dfResult.ReturnDestination != null)
                                {
                                    returnFlowStore.Insert(returnFlowInsert, edgeId, dfResult.ReturnDestination.DestinationKind,
                                        dfResult.ReturnDestination.DestinationVariable,
                                        dfResult.ReturnDestination.DestinationSymbolFqn, now);
                                    session.RowsWritten();
                                }
                            }
                        }
                    }
                }
            }

            _log?.Invoke($"  {project.Name}: call graph extracted");
            session.CommitBatch();
        }
        }

        // Phase 6: Record project dependencies
        if (repoRoot != null)
        {
            StartPhase("recording_dependencies");
            _log?.Invoke("Recording project dependencies...");
            progress?.Report(new IndexingProgress
            {
                Phase = "recording_dependencies",
                Description = "Recording project dependencies",
                ProjectIndex = totalProjects,
                ProjectCount = totalProjects
            });
            var deps = DependencyExtractor.ExtractDependencies(solution, projectRoslynToId, submodules, repoRoot);
            // Replace the dependency set for the projects this run actually (re)built. Registration
            // covers every project so the extractor can resolve every edge's endpoints, but only the
            // processed projects' consumer edges are cleared and re-inserted: for a full index that is
            // every project (identical to before); for an incremental/overlay run the out-of-closure
            // (shared base) projects' edges are left untouched, so an overlay never mutates a published
            // base snapshot's project_dependencies (issue #44). By the undirected-closure property a
            // processed consumer's dependencies are themselves processed, so no processed edge is lost.
            var processedProjectIds = new HashSet<long>();
            foreach (var project in solution.Projects)
                if (processSet.Contains(project.Id) && projectRoslynToId.TryGetValue(project.Id, out var procId))
                    processedProjectIds.Add(procId);

            foreach (var pid in processedProjectIds)
                dependencyStore.DeleteByConsumer(pid);
            foreach (var dep in deps)
            {
                if (dep.DependencyProjectId == 0)
                    continue; // Skip NuGet refs with no indexed project
                if (!processedProjectIds.Contains(dep.ConsumerProjectId))
                    continue; // Only (re)write edges for the processed consumers (issue #44 for overlays)
                dependencyStore.Insert(dep);
            }
            _log?.Invoke($"  {deps.Count} dependencies recorded");
            session.CommitBatch();
        }

        // Phase 7: Capture API surface snapshots for projects with inbound dependencies
        StartPhase("capturing_api_surface");
        _log?.Invoke("Capturing API surface snapshots...");
        progress?.Report(new IndexingProgress
        {
            Phase = "capturing_api_surface",
            Description = "Capturing API surface snapshots",
            ProjectIndex = totalProjects,
            ProjectCount = totalProjects
        });
        var gitCommit = GetHeadCommit(repoRoot);
        if (gitCommit != null)
        {
            // Partition the projects this run rebuilt into those that still have inbound dependencies
            // and those that don't. Every rebuilt project's current-commit snapshot is purged first so
            // a project that lost its last consumer since the previous run does not keep a stale
            // current-commit snapshot (a fresh full index would not create one); snapshots captured at
            // other commits are historical and left untouched (criterion 7).
            var rebuiltProjectIds = new List<long>();
            var projectsWithConsumers = new HashSet<long>();
            foreach (var project in solution.Projects)
            {
                if (!processSet.Contains(project.Id))
                    continue;
                if (projectRoslynToId.TryGetValue(project.Id, out var pid))
                {
                    rebuiltProjectIds.Add(pid);
                    var consumers = dependencyStore.GetByDependency(pid);
                    if (consumers.Count > 0)
                        projectsWithConsumers.Add(pid);
                }
            }

            foreach (var pid in rebuiltProjectIds)
                apiSurfaceStore.DeleteByProjectAndCommit(pid, gitCommit);

            foreach (var projectId in projectsWithConsumers)
            {
                // Get all public/protected symbols for this project
                var publicSymbols = symbolStore.GetByProjectAndAccessibility(projectId, ["public", "protected"]);
                foreach (var sym in publicSymbols)
                {
                    var sigHash = sym.SignatureHash ?? ComputeSignatureHash(sym.Signature ?? sym.FullyQualifiedName);
                    apiSurfaceStore.Insert(new ApiSurfaceSnapshot
                    {
                        ProjectId = projectId,
                        SymbolId = sym.Id,
                        SymbolKey = sym.SymbolKey,
                        FullyQualifiedName = sym.FullyQualifiedName,
                        Accessibility = SymbolStore.FormatAccessibility(sym.Accessibility),
                        SignatureHash = sigHash,
                        CapturedAt = now,
                        GitCommit = gitCommit
                    });
                    session.RowsWritten();
                }
            }
            _log?.Invoke($"  API surface captured for {projectsWithConsumers.Count} project(s)");
        }

        FinishPhase();

        // Publish the generation atomically with the final batch of data: the completion pointer flip
        // enrols in the still-open write transaction, so a reader either sees the previous complete run
        // or this one — never a "complete" pointer without its committed data. If the guarded update
        // publishes no row (e.g. recovery abandoned this run under an unsupported second writer), abort
        // before committing so disposal rolls back the final batch and leaves the ledger consistent.
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Completeness gate (criterion 3): if any processed project failed to produce a compilation, this
        // full-index generation is incomplete. Persist it as a diagnosable PARTIAL snapshot but do NOT
        // publish the run pointer or advance the branch — the previous complete generation stays selected
        // and readable, so a failed/partial snapshot is never served as a branch head. The (unpublished)
        // staging run is abandoned on dispose; a later re-index of the same commit resets the snapshot to
        // pending and retries. Only the snapshot path gates here; the legacy mutable-row path is unchanged.
        if ((isFullIndex || isOverlayRun) && compilationFailures > 0 && activeSnapshotId is long partialId && snapshotStore != null)
        {
            snapshotStore.MarkStatus(partialId, SnapshotStatus.Partial);
            session.Complete();
            if (metrics != null)
            {
                totalStopwatch.Stop();
                metrics.TotalDurationMs = totalStopwatch.ElapsedMilliseconds;
                metrics.Status = IndexRunStatus.Failed;
            }
            _log?.Invoke($"Index produced a PARTIAL snapshot: {compilationFailures} project(s) failed to compile. " +
                         "It is retained for diagnosis but was NOT selected; the previous complete generation " +
                         "remains the current head (criterion 3).");
            progress?.Report(new IndexingProgress
            {
                Phase = "partial",
                Description = "Index incomplete — partial snapshot not selected",
                ProjectIndex = totalProjects,
                ProjectCount = totalProjects
            });
            return;
        }

        if (runStore.MarkComplete(runScope.RunId, completedAt, projectRoslynToId.Count) != 1)
            throw new InvalidOperationException(
                $"Index run {runScope.RunId} was not in staging state at publish; aborting to avoid a false completion.");

        // Publish + select the snapshot in the SAME transaction as the data and the run pointer (full
        // index only; incremental mutates the working head in place). The branch pointer is advanced to
        // the snapshot ONLY after its status flips to complete, and both happen inside this still-open
        // write transaction, so a concurrent reader resolving the default branch sees exactly one
        // complete, selected snapshot — the previous one until this commit lands, then this one — never a
        // half-built pending generation and never a torn mix (criterion 7 / whole-generation isolation).
        if (isFullIndex && activeSnapshotId is long publishId && snapshotStore != null && repositoryId is long publishRepoId
            && effectiveCtx != null)
        {
            if (snapshotStore.MarkComplete(publishId, completedAt) != 1)
                throw new InvalidOperationException(
                    $"Snapshot {publishId} was not pending at publish; aborting to avoid a false completion.");

            AdvanceBranchToSnapshot(snapshotStore, publishRepoId, effectiveCtx, publishId, completedAt);

            // Reclaim the now-superseded pre-Phase-9 mutable rows (snapshot_id IS NULL) so the database
            // returns to ~1x, first re-pointing their historical api-surface snapshots onto the new
            // snapshot's project rows so those comparisons survive the sweep (criterion 5 + Condition E).
            ReconcileLegacyRows(conn, publishId);
        }

        // Phase-10 overlay publish (issue #44): publish the overlay generation and advance the branch to
        // it in the SAME transaction as the data and the run pointer, mirroring the full-index atomicity
        // so MCP never reads a half-updated overlay. CRUCIALLY, the base snapshot is NEVER superseded:
        // AdvanceBranchToOverlay only supersedes the branch's previous target when that target was itself
        // an overlay (a prior working-tree delta being replaced by a newer one), so the committed base
        // stays Complete and its shared rows remain readable.
        if (isOverlayRun && activeSnapshotId is long overlayPublishId && snapshotStore != null
            && repositoryId is long overlayRepoId && effectiveCtx != null)
        {
            if (snapshotStore.MarkComplete(overlayPublishId, completedAt) != 1)
                throw new InvalidOperationException(
                    $"Overlay snapshot {overlayPublishId} was not pending at publish; aborting to avoid a false completion.");

            AdvanceBranchToOverlay(snapshotStore, overlayRepoId, effectiveCtx, overlayPublishId, completedAt);
        }

        session.Complete();
        runScope.Detach();

        // Post-publish maintenance/provenance only: the generation is already durably published, so a
        // checkpoint or footprint failure must not turn a successful index into a reported failure.
        var finalWalBytes = _db.WalBytes;
        var finalShmBytes = _db.ShmBytes;
        try
        {
            _db.Checkpoint();
            runStore.RecordFootprint(runScope.RunId, _db.MainDbBytes, finalWalBytes, finalShmBytes);
        }
        catch (SqliteException ex)
        {
            _log?.Invoke($"  Post-publish maintenance (checkpoint/footprint) failed, index already published: {ex.Message}");
        }

        if (metrics != null)
        {
            // Stop the clock before collecting row metrics so the post-index aggregate queries
            // (a DISTINCT scan over every reference row) never inflate total_duration_ms.
            totalStopwatch.Stop();
            metrics.TotalDurationMs = totalStopwatch.ElapsedMilliseconds;
            metrics.Rows = new IndexMetricsStore(conn).Collect();
            metrics.Status = IndexRunStatus.Completed;
        }

        if (catalog.AmbiguousEdgeBindings > 0)
        {
            _log?.Invoke($"  {catalog.AmbiguousEdgeBindings} edge(s) bound to a deterministic pick " +
                         "due to a key defined in multiple projects. References and call callees use " +
                         "compilation-scoped exact resolution, so these are residual key-only bindings: " +
                         "relationship endpoints, or a target that mapped to no indexed project.");
        }

        _log?.Invoke("Indexing complete.");
        progress?.Report(new IndexingProgress
        {
            Phase = "complete",
            Description = "Indexing complete",
            ProjectIndex = totalProjects,
            ProjectCount = totalProjects
        });
    }

    /// <summary>
    /// Records a content fingerprint in <c>file_index</c> for every non-generated, on-disk source file
    /// owned by this logical (per-TFM) project. Seeding this during a full index is what lets a later
    /// daemon restart short-circuit unchanged files to zero work, and gives the incremental path a
    /// per-file baseline to diff against. Files that are not present on disk (e.g. an in-memory
    /// ad-hoc workspace used in tests) are skipped — no stable content hash can be taken for them.
    /// </summary>
    private static async Task SeedFileIndexAsync(
        Project project,
        long projectId,
        FileStore fileStore,
        IndexWriteSession session,
        long now,
        CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var filePath = syntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath) || SymbolExtractor.IsGeneratedFile(filePath))
                continue;
            if (!File.Exists(filePath) || !seen.Add(filePath))
                continue;

            // Ensure a file_version exists for every on-disk source file, including those with no
            // top-level symbols. The symbol phase already seeded versions for files it touched;
            // select-existing-first reuses those rows (no re-hash), so this only adds the gaps and the
            // fingerprint hash is a raw SHA-256 taken from disk — the same bytes the daemon compares.
            fileStore.ResolveFileVersionId(projectId, filePath, contentHash: null, lastIndexedAt: now);
            session.RowsWritten();
        }
    }

    private static string? GetHeadCommit(string? repoRoot)
    {
        if (repoRoot == null) return null;
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static string? RunGit(string repoRoot, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolves the repository/commit coordinates for the indexed working tree from git. Returns null
    /// when there is no git root or no HEAD commit (a legacy/no-git workspace), which keeps the
    /// orchestrator on the pre-Phase-9 mutable-row path.
    /// </summary>
    /// <summary>
    /// Advances a branch pointer to a just-published snapshot inside the caller's write transaction. The
    /// just-indexed branch is the working head, so when it is the default it becomes the SOLE default
    /// (demoting any sibling a prior full index left marked default; otherwise two is_default=1 rows would
    /// coexist and a scope-less query would resolve the older branch — criterion 6). Whatever the branch
    /// previously pointed at is superseded (never mutated — criterion 2), leaving exactly one complete,
    /// selected snapshot for the branch.
    /// </summary>
    private static void AdvanceBranchToSnapshot(
        SnapshotStore snapshotStore, long repositoryId, SnapshotContext ctx, long snapshotId, long completedAt)
    {
        var branchId = snapshotStore.EnsureBranch(repositoryId, ctx.BranchName, ctx.IsDefaultBranch, completedAt);
        if (ctx.IsDefaultBranch)
            snapshotStore.PromoteSoleDefaultBranch(repositoryId, branchId);
        var previousSnapshot = snapshotStore.GetBranchSnapshotId(branchId);
        snapshotStore.SetBranchPointer(branchId, snapshotId, completedAt);
        if (previousSnapshot is long prev && prev != snapshotId)
            snapshotStore.MarkStatus(prev, SnapshotStatus.Superseded);
    }

    /// <summary>
    /// Advances a branch pointer to a just-published OVERLAY (Phase 10, issue #44). Identical to
    /// <see cref="AdvanceBranchToSnapshot"/> EXCEPT it supersedes the branch's previous target only when
    /// that target was itself an overlay — a committed base snapshot is NEVER superseded, so it stays
    /// Complete and keeps serving the unchanged (shared) project rows the overlay layers on. Advancing
    /// from base→overlay therefore leaves the base Complete; advancing overlay→overlay supersedes the
    /// stale overlay so exactly one working-tree delta is selected at a time.
    /// </summary>
    private static void AdvanceBranchToOverlay(
        SnapshotStore snapshotStore, long repositoryId, SnapshotContext ctx, long overlaySnapshotId, long completedAt)
    {
        var branchId = snapshotStore.EnsureBranch(repositoryId, ctx.BranchName, ctx.IsDefaultBranch, completedAt);
        if (ctx.IsDefaultBranch)
            snapshotStore.PromoteSoleDefaultBranch(repositoryId, branchId);
        var previousSnapshot = snapshotStore.GetBranchSnapshotId(branchId);
        snapshotStore.SetBranchPointer(branchId, overlaySnapshotId, completedAt);
        if (previousSnapshot is long prev && prev != overlaySnapshotId
            && snapshotStore.GetById(prev)?.IsOverlay == true)
            snapshotStore.MarkStatus(prev, SnapshotStatus.Superseded);
    }

    /// <summary>
    /// Re-selects an existing, already-built immutable snapshot for a branch without rebuilding it — the
    /// idempotent duplicate-publish path (a Complete snapshot) and the branch-rollback / older-commit
    /// re-checkout path (a Superseded snapshot). Restores the snapshot to Complete (un-supersedes it) and
    /// (re)points the branch at it via <see cref="AdvanceBranchToSnapshot"/>. Never touches the snapshot's
    /// data rows, so the immutable snapshot stays byte-identical (criteria 1 &amp; 2).
    /// </summary>
    private static void SelectExistingSnapshot(
        SnapshotStore snapshotStore, long repositoryId, SnapshotContext ctx, long snapshotId, long completedAt)
    {
        snapshotStore.MarkStatus(snapshotId, SnapshotStatus.Complete);
        AdvanceBranchToSnapshot(snapshotStore, repositoryId, ctx, snapshotId, completedAt);
    }

    /// <summary>
    /// Re-selects an existing, already-built OVERLAY for a branch without rebuilding it — the Phase-10
    /// idempotent overlay path (restart with the same dirty state, or re-applying a working-tree delta
    /// that was previously built then superseded). Mirrors <see cref="SelectExistingSnapshot"/> but
    /// re-points the branch through <see cref="AdvanceBranchToOverlay"/>, so it supersedes only a stale
    /// OVERLAY the branch currently targets and NEVER the committed base (issue #44) — the branch may
    /// legitimately point at the base at this moment (delta built, reverted to clean, then re-applied).
    /// Never touches the overlay's data rows, so the immutable overlay stays byte-identical.
    /// </summary>
    private static void SelectExistingOverlay(
        SnapshotStore snapshotStore, long repositoryId, SnapshotContext ctx, long overlaySnapshotId, long completedAt)
    {
        snapshotStore.MarkStatus(overlaySnapshotId, SnapshotStatus.Complete);
        AdvanceBranchToOverlay(snapshotStore, repositoryId, ctx, overlaySnapshotId, completedAt);
    }

    internal static SnapshotContext? TryResolveSnapshotContext(string? repoRoot)
    {
        if (repoRoot == null) return null;
        var commit = GetHeadCommit(repoRoot);
        if (commit == null) return null;

        var rawRemote = GitRemoteResolver.ReadOriginRemote(repoRoot);
        var remote = rawRemote != null
            ? GitRemoteNormalizer.Normalize(rawRemote)
            : $"local://{Environment.MachineName}";
        var branch = RunGit(repoRoot, "rev-parse --abbrev-ref HEAD");
        if (string.IsNullOrEmpty(branch) || branch == "HEAD")
            branch = "HEAD"; // detached: a stable pointer name for the current checkout.

        return new SnapshotContext
        {
            RepositoryRemoteUrl = remote,
            CommitSha = commit,
            TreeSha = RunGit(repoRoot, "rev-parse HEAD^{tree}"),
            BranchName = branch,
            IsDefaultBranch = true
        };
    }

    /// <summary>
    /// One-time transition maintenance run inside the first snapshot's publish transaction: the
    /// pre-Phase-9 mutable rows (<c>snapshot_id IS NULL</c>) are now superseded by immutable snapshot
    /// rows, so reclaim them to keep the database at ~1x (Condition E). Their historical
    /// <c>api_surface_snapshots</c> are first re-pointed onto the newly-published snapshot's project rows
    /// (matched by logical canonical id) so API/semantic history survives the sweep (criterion 5); a
    /// legacy row still referenced by an un-re-pointable api snapshot is intentionally left in place
    /// rather than cascade-deleting that history. Idempotent: a no-op once no legacy rows remain.
    /// </summary>
    private static void ReconcileLegacyRows(SqliteConnection conn, long publishedSnapshotId)
    {
        using (var repoint = conn.CreateCommand())
        {
            repoint.CommandText = """
                UPDATE api_surface_snapshots
                   SET project_id = (
                       SELECT np.id FROM projects np
                       JOIN logical_projects lp ON lp.id = np.logical_project_id
                       WHERE np.snapshot_id = @snap
                         AND lp.canonical_id = (SELECT lg.canonical_id FROM projects lg
                                                 WHERE lg.id = api_surface_snapshots.project_id)
                       LIMIT 1)
                 WHERE project_id IN (SELECT id FROM projects WHERE snapshot_id IS NULL)
                   AND EXISTS (
                       SELECT 1 FROM projects np
                       JOIN logical_projects lp ON lp.id = np.logical_project_id
                       WHERE np.snapshot_id = @snap
                         AND lp.canonical_id = (SELECT lg.canonical_id FROM projects lg
                                                 WHERE lg.id = api_surface_snapshots.project_id));
                """;
            repoint.Parameters.AddWithValue("@snap", publishedSnapshotId);
            repoint.ExecuteNonQuery();
        }

        using var sweep = conn.CreateCommand();
        sweep.CommandText = """
            DELETE FROM projects
             WHERE snapshot_id IS NULL
               AND id NOT IN (SELECT project_id FROM api_surface_snapshots);
            """;
        sweep.ExecuteNonQuery();
    }

    private static string ComputeSignatureHash(string signature)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Resolves an occurrence target (reference/call) to a stored symbol row. Prefers compilation-scoped
    /// exact resolution when the document extractor supplied the target's exact owning project
    /// (<paramref name="exactProjectId"/>) — restoring parity with the legacy declaration-anchored path
    /// and never counting an ambiguity — and falls back to key-only resolution (<see cref="SymbolCatalog.TryResolveEdge"/>,
    /// a deterministic pick that counts the ambiguity) for targets outside the indexed set.
    /// </summary>
    private static bool TryResolveTarget(SymbolCatalog catalog, string targetKey, long? exactProjectId,
        long? ownerProjectId, out long symbolId)
    {
        if (exactProjectId is { } exact && catalog.TryResolveExact(targetKey, exact, out symbolId))
            return true;
        return catalog.TryResolveEdge(targetKey, ownerProjectId, out symbolId);
    }

    private static long? ResolveEnclosingSymbol(int line, string filePath, long projectId, SymbolStore symbolStore)
    {
        // Scope to the comment's own logical (per-TFM) project so a shared source file does not attach
        // the comment to the sibling framework's identically-positioned symbol.
        var fileSymbols = symbolStore.GetByFile(filePath, projectId);
        var enclosing = fileSymbols
            .Where(s => s.LineStart <= line && s.LineEnd >= line)
            .OrderByDescending(s => s.LineStart)
            .FirstOrDefault();
        return enclosing?.Id;
    }
}
