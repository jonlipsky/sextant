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

    public IndexOrchestrator(IndexDatabase db, Action<string>? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task IndexSolutionAsync(
        Solution solution,
        IProgress<IndexingProgress>? progress = null,
        IndexingMetrics? metrics = null,
        CancellationToken cancellationToken = default)
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

        var solutionStore = new SolutionStore(conn);
        var runStore = new IndexRunStore(conn);

        // Map each Roslyn project instance to its stored logical-project row. Keyed by the Roslyn
        // ProjectId (unique per instance) rather than the csproj file path, so the multiple
        // evaluated-TFM instances of one multi-targeted csproj map to distinct logical projects
        // instead of collapsing onto a single id (which would make DeleteByProject cross-TFM).
        var projectRoslynToId = new Dictionary<ProjectId, long>();
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
            if (project.FilePath == null || !projectRoslynToId.TryGetValue(project.Id, out var projectId))
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

            // Clear existing data for this project (full re-index)
            symbolStore.DeleteByProject(projectId);

            var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, projectId);
            foreach (var symbol in symbols)
            {
                var id = symbolStore.Insert(symbolInsert, symbol);
                catalog.Add(projectId, symbol.SymbolKey, id);
                session.RowsWritten();
            }
            session.CommitBatch();

            _log?.Invoke($"    {symbols.Count} symbols extracted");
        }

        // Phase 3: Extract relationships
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

            if (refProjectId is { } refClearId)
            {
                // Clear existing references for files in THIS logical (per-TFM) project only, so a
                // sibling framework's references for a shared source file survive this phase.
                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    if (!string.IsNullOrEmpty(syntaxTree.FilePath))
                        referenceStore.DeleteByFile(syntaxTree.FilePath, refClearId);
                }
            }

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

        // Phase 4.5: Extract tagged comments
        StartPhase("extracting_comments");
        _log?.Invoke("Extracting tagged comments...");
        projectIndex = 0;
        var commentStore = new CommentStore(conn);
        using var commentInsert = commentStore.CreateInsertCommand();
        foreach (var project in solution.Projects)
        {
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

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                ThrowIfCancelled();
                if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                    continue;

                commentStore.DeleteByFile(syntaxTree.FilePath, commentProjectId);

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

        // Phase 5: Extract call graph and dataflow
        StartPhase("extracting_call_graph");
        _log?.Invoke("Extracting call graph...");
        projectIndex = 0;
        var argumentFlowStore = new ArgumentFlowStore(conn);
        var returnFlowStore = new ReturnFlowStore(conn);
        using var argumentFlowInsert = argumentFlowStore.CreateInsertCommand();
        using var returnFlowInsert = returnFlowStore.CreateInsertCommand();
        foreach (var project in solution.Projects)
        {
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

            if (callProjectId is { } callClearId)
            {
                // Clear existing call edges made from THIS logical (per-TFM) project's code only, so a
                // sibling framework's call edges for a shared source file survive this phase.
                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    if (!string.IsNullOrEmpty(syntaxTree.FilePath))
                        callGraphStore.DeleteByFile(syntaxTree.FilePath, callClearId);
                }
            }

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
            var projectsWithConsumers = new HashSet<long>();
            foreach (var project in solution.Projects)
            {
                if (projectRoslynToId.TryGetValue(project.Id, out var pid))
                {
                    var consumers = dependencyStore.GetByDependency(pid);
                    if (consumers.Count > 0)
                        projectsWithConsumers.Add(pid);
                }
            }

            foreach (var projectId in projectsWithConsumers)
            {
                // Delete existing snapshot for this commit (idempotent)
                apiSurfaceStore.DeleteByProjectAndCommit(projectId, gitCommit);

                // Get all public/protected symbols for this project
                var publicSymbols = symbolStore.GetByProjectAndAccessibility(projectId, ["public", "protected"]);
                foreach (var sym in publicSymbols)
                {
                    var sigHash = sym.SignatureHash ?? ComputeSignatureHash(sym.Signature ?? sym.FullyQualifiedName);
                    apiSurfaceStore.Insert(new ApiSurfaceSnapshot
                    {
                        ProjectId = projectId,
                        SymbolId = sym.Id,
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
            _log?.Invoke($"  {catalog.AmbiguousEdgeBindings} cross-project edge(s) bound to a " +
                         "deterministic pick due to a key defined in multiple projects; exact " +
                         "per-project resolution deferred to the document-oriented extractor phase.");
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
