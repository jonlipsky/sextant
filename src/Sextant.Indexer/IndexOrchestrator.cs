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

    public IndexOrchestrator(IndexDatabase db, Action<string>? log = null, bool useDocumentExtractor = false)
    {
        _db = db;
        _log = log;
        _useDocumentExtractor = useDocumentExtractor;
    }

    public async Task IndexSolutionAsync(
        Solution solution,
        IProgress<IndexingProgress>? progress = null,
        IndexingMetrics? metrics = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? projectCanonicalFilter = null)
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
        var fileIndexStore = new FileIndexStore(conn);

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

        // Open a staging generation and a single bounded, batched write session for the whole run.
        // The session replaces per-row implicit transactions with a small number of explicit,
        // project-bounded batches; the run scope keeps the previous complete generation visible until
        // this one is published, and abandons the staging generation if the run fails or is cancelled.
        using var runScope = runStore.BeginScope(metrics?.Mode ?? "full", now);
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

            var projectId = projectStore.Insert(identity, now);
            projectRoslynToId[project.Id] = projectId;
            if (projectCanonicalFilter == null || projectCanonicalFilter.Contains(identity.CanonicalId))
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
            // project's symbols cascades its references/relationships/call edges/dataflow AND the
            // inbound edges that point into them, so those are all rebuilt from scratch below; the
            // file_index rows are cleared here too so files that were deleted/renamed away since the
            // last run leave no stale fingerprint.
            symbolStore.DeleteByProject(projectId);
            fileIndexStore.DeleteByProject(projectId);

            var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, projectId);
            foreach (var symbol in symbols)
            {
                var id = symbolStore.Insert(symbolInsert, symbol);
                catalog.Add(projectId, symbol.SymbolKey, id);
                session.RowsWritten();
            }

            // Fingerprint every indexed (non-generated, on-disk) file so an unchanged daemon restart
            // short-circuits to zero work, and record the project's evaluation fingerprint so a later
            // catch-up can detect config/props/global.json/assets changes.
            await SeedFileIndexAsync(project, projectId, fileIndexStore, session, now, cancellationToken);
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
            _log?.Invoke("Extracting occurrences (document-oriented)...");

            // Compilation-scoped exact resolution: map a bound occurrence target's containing assembly
            // to its exact indexed (per-TFM) project id. This restores parity with the legacy
            // declaration-anchored reference path — which bound each reference to the exact declared
            // symbol — for keys that are ambiguous across projects (a multi-targeted dependency several
            // of whose TFMs are indexed, or an extern-alias-duplicated assembly). Solution.GetProject
            // maps a source assembly symbol back to its originating project; results are memoized since
            // one assembly is the target of many occurrences. A null result (metadata / out-of-solution
            // assembly, or a project outside the indexed map) leaves the contribution to fall back to
            // key-only resolution in the persistence loops below.
            var assemblyProjectCache = new Dictionary<IAssemblySymbol, long?>(SymbolEqualityComparer.Default);
            long? ResolveTargetProject(IAssemblySymbol assembly)
            {
                if (assemblyProjectCache.TryGetValue(assembly, out var cached))
                    return cached;
                long? resolved = solution.GetProject(assembly) is { } targetProject
                                 && projectRoslynToId.TryGetValue(targetProject.Id, out var targetPid)
                    ? targetPid
                    : null;
                assemblyProjectCache[assembly] = resolved;
                return resolved;
            }

            projectIndex = 0;
            foreach (var project in solution.Projects)
            {
                if (!processSet.Contains(project.Id)) continue;
                EnterProject();
                projectIndex++;
                progress?.Report(new IndexingProgress
                {
                    Phase = "extracting_occurrences",
                    Description = $"Extracting occurrences from {project.Name}",
                    CurrentProject = project.Name,
                    ProjectIndex = projectIndex,
                    ProjectCount = totalProjects
                });
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) continue;

                long? ownerProjectId = projectRoslynToId.TryGetValue(project.Id, out var ownerPid)
                    ? ownerPid
                    : null;
                if (ownerProjectId == null) continue;

                // One contribution set per project so partial-type relationships across files coalesce
                // and repeated same-line occurrences dedup (acceptance criterion 5). All processed
                // projects' symbols are already in the catalog (Phase 2), so cross-project targets
                // resolve regardless of the order projects are visited here.
                var contributions = new DocumentContributionSet();
                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    ThrowIfCancelled();
                    if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                        continue;

                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    var root = await syntaxTree.GetRootAsync(cancellationToken);
                    var text = await syntaxTree.GetTextAsync(cancellationToken);
                    DocumentSemanticExtractor.ExtractDocument(
                        root, semanticModel, syntaxTree.FilePath, text, contributions, ResolveTargetProject);
                }

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
                        InProjectId = ownerProjectId.Value,
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
                        LastIndexedAt = now
                    });
                    session.RowsWritten();

                    var dfResult = call.Dataflow;
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

                if (contributions.CompletenessDiagnostics > 0)
                    _log?.Invoke($"  {project.Name}: {contributions.CompletenessDiagnostics} unresolved " +
                                 "region(s) (extraction completeness diagnostic)");

                _log?.Invoke($"  {project.Name}: occurrences extracted");
                session.CommitBatch();
            }
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

        // Phase 4.5: Extract tagged comments
        StartPhase("extracting_comments");
        _log?.Invoke("Extracting tagged comments...");
        projectIndex = 0;
        var commentStore = new CommentStore(conn);
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

            // Comments are not cascade-deleted by the symbol reset, so clear the whole project's
            // comments once (covering files deleted/renamed away since the last run) before re-extract.
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
                            if (edge.InvocationSyntax != null && edge.SemanticModel != null)
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
            // Replace the entire dependency set rather than only upserting: the extractor recomputes
            // every edge in the solution each run and registration covers every project, so clearing
            // each consumer's edges first purges any that were removed since the last run (e.g. a
            // dropped project reference) instead of leaving a stale project_dependencies row behind.
            foreach (var pid in projectRoslynToId.Values)
                dependencyStore.DeleteByConsumer(pid);
            foreach (var dep in deps)
            {
                if (dep.DependencyProjectId == 0)
                    continue; // Skip NuGet refs with no indexed project
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
        if (runStore.MarkComplete(runScope.RunId, completedAt, projectRoslynToId.Count) != 1)
            throw new InvalidOperationException(
                $"Index run {runScope.RunId} was not in staging state at publish; aborting to avoid a false completion.");
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
        FileIndexStore fileIndexStore,
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

            fileIndexStore.Upsert(new FileIndexEntry
            {
                ProjectId = projectId,
                FilePath = filePath,
                ContentHash = IncrementalIndexer.ComputeFileHash(filePath),
                LastIndexedAt = now
            });
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
