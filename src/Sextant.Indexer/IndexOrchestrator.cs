using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sextant.Core;
using Sextant.Store;
using Microsoft.CodeAnalysis;

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

    public Task IndexSolutionAsync(Solution solution)
        => IndexSolutionAsync(solution, null);

    public async Task IndexSolutionAsync(Solution solution, IProgress<IndexingProgress>? progress)
    {
        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var symbolStore = new SymbolStore(conn);
        var referenceStore = new ReferenceStore(conn);
        var callGraphStore = new CallGraphStore(conn);
        var relationshipStore = new RelationshipStore(conn);
        var dependencyStore = new ProjectDependencyStore(conn);
        var apiSurfaceStore = new ApiSurfaceStore(conn);

        var solutionStore = new SolutionStore(conn);

        var projectPathToId = new Dictionary<string, long>();
        var symbolFqnToId = new Dictionary<string, long>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
            if (project.FilePath == null) continue;

            ProjectIdentity identity;
            if (repoRoot != null)
            {
                var submodule = SubmoduleDiscovery.FindContainingSubmodule(
                    project.FilePath, submodules, repoRoot);
                if (submodule != null)
                {
                    var submoduleFullPath = Path.GetFullPath(Path.Combine(repoRoot, submodule.Path));
                    identity = GitRemoteResolver.ResolveForSubmodule(
                        project.FilePath, submodule.RemoteUrl, submoduleFullPath);
                }
                else
                {
                    identity = GitRemoteResolver.Resolve(project.FilePath);
                }
            }
            else
            {
                identity = GitRemoteResolver.Resolve(project.FilePath);
            }

            identity = identity with
            {
                AssemblyName = project.AssemblyName,
                TargetFramework = ReadTargetFramework(project.FilePath),
                IsTestProject = TestProjectDetector.IsTestProject(project)
            };

            var projectId = projectStore.Insert(identity, now);
            projectPathToId[project.FilePath] = projectId;
            _log?.Invoke($"  Project: {project.Name} (id={projectId}, test={identity.IsTestProject})");
        }

        // Record solution → project mappings
        if (solution.FilePath != null)
        {
            var solutionId = solutionStore.Upsert(solution.FilePath, Path.GetFileNameWithoutExtension(solution.FilePath), now);
            foreach (var pid in projectPathToId.Values)
                solutionStore.AddProjectMapping(solutionId, pid);
        }

        // Phase 2: Extract symbols from all projects
        _log?.Invoke("Extracting symbols...");
        projectIndex = 0;
        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null || !projectPathToId.TryGetValue(project.FilePath, out var projectId))
                continue;

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
                var id = symbolStore.Insert(symbol);
                symbolFqnToId[symbol.FullyQualifiedName] = id;
            }

            _log?.Invoke($"    {symbols.Count} symbols extracted");
        }

        // Phase 3: Extract relationships
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
            projectIndex++;
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_relationships",
                Description = $"Extracting relationships from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                foreach (var node in root.DescendantNodes())
                {
                    if (semanticModel.GetDeclaredSymbol(node) is INamedTypeSymbol typeSymbol && !typeSymbol.IsImplicitlyDeclared)
                    {
                        var rels = RelationshipExtractor.ExtractRelationships(typeSymbol);
                        var instantiates = RelationshipExtractor.ExtractInstantiates(typeSymbol, compilation);
                        foreach (var (fromFqn, toFqn, kind) in rels.Concat(instantiates))
                        {
                            if (symbolFqnToId.TryGetValue(fromFqn, out var fromId) &&
                                symbolFqnToId.TryGetValue(toFqn, out var toId))
                            {
                                relationshipStore.Insert(new RelationshipInfo
                                {
                                    FromSymbolId = fromId,
                                    ToSymbolId = toId,
                                    Kind = kind,
                                    LastIndexedAt = now
                                });
                            }
                        }
                    }
                }
            }
        }

        // Phase 4: Extract references
        _log?.Invoke("Extracting references...");
        projectIndex = 0;
        foreach (var project in solution.Projects)
        {
            projectIndex++;
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_references",
                Description = $"Extracting references from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            if (project.FilePath != null && projectPathToId.ContainsKey(project.FilePath))
            {
                // Clear existing references for files in this project
                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    if (!string.IsNullOrEmpty(syntaxTree.FilePath))
                        referenceStore.DeleteByFile(syntaxTree.FilePath);
                }
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                    continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                foreach (var node in root.DescendantNodes())
                {
                    var declaredSymbol = semanticModel.GetDeclaredSymbol(node);
                    if (declaredSymbol == null || declaredSymbol.IsImplicitlyDeclared)
                        continue;

                    var fqn = declaredSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (!symbolFqnToId.TryGetValue(fqn, out var symbolId))
                        continue;

                    // Only extract references for types and type members
                    if (SymbolExtractor.MapSymbolKind(declaredSymbol) == null)
                        continue;

                    var refs = await ReferenceExtractor.ExtractReferencesAsync(
                        declaredSymbol, symbolId, solution, projectPathToId);

                    foreach (var refInfo in refs)
                    {
                        referenceStore.Insert(refInfo);
                    }
                }
            }

            _log?.Invoke($"  {project.Name}: references extracted");
        }

        // Phase 4.5: Extract tagged comments
        _log?.Invoke("Extracting tagged comments...");
        projectIndex = 0;
        var commentStore = new CommentStore(conn);
        foreach (var project in solution.Projects)
        {
            projectIndex++;
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_comments",
                Description = $"Extracting comments from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            if (project.FilePath == null || !projectPathToId.TryGetValue(project.FilePath, out var commentProjectId))
                continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                    continue;

                commentStore.DeleteByFile(syntaxTree.FilePath);

                var comments = CommentExtractor.ExtractComments(syntaxTree);
                foreach (var comment in comments)
                {
                    var enclosingSymbolId = ResolveEnclosingSymbol(comment.Line, comment.FilePath, symbolStore);
                    commentStore.Insert(commentProjectId, comment.FilePath, comment.Line, comment.Tag,
                                       comment.Text, enclosingSymbolId, now);
                }
            }

            _log?.Invoke($"  {project.Name}: comments extracted");
        }

        // Phase 5: Extract call graph and dataflow
        _log?.Invoke("Extracting call graph...");
        projectIndex = 0;
        var argumentFlowStore = new ArgumentFlowStore(conn);
        var returnFlowStore = new ReturnFlowStore(conn);
        foreach (var project in solution.Projects)
        {
            projectIndex++;
            progress?.Report(new IndexingProgress
            {
                Phase = "extracting_call_graph",
                Description = $"Extracting call graph from {project.Name}",
                CurrentProject = project.Name,
                ProjectIndex = projectIndex,
                ProjectCount = totalProjects
            });
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            if (project.FilePath != null && projectPathToId.ContainsKey(project.FilePath))
            {
                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    if (!string.IsNullOrEmpty(syntaxTree.FilePath))
                        callGraphStore.DeleteByFile(syntaxTree.FilePath);
                }
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                    continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                foreach (var node in root.DescendantNodes())
                {
                    var declaredSymbol = semanticModel.GetDeclaredSymbol(node);
                    if (declaredSymbol is not IMethodSymbol methodSymbol || declaredSymbol.IsImplicitlyDeclared)
                        continue;

                    var callerFqn = methodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (!symbolFqnToId.TryGetValue(callerFqn, out var callerSymbolId))
                        continue;

                    var edges = await CallGraphBuilder.BuildCallGraphAsync(methodSymbol, project);

                    foreach (var edge in edges)
                    {
                        if (symbolFqnToId.TryGetValue(edge.CalleeFqn, out var calleeSymbolId))
                        {
                            var edgeId = callGraphStore.Insert(new CallGraphEdge
                            {
                                CallerSymbolId = callerSymbolId,
                                CalleeSymbolId = calleeSymbolId,
                                CallSiteFile = edge.CallSiteFile,
                                CallSiteLine = edge.CallSiteLine,
                                LastIndexedAt = now
                            });

                            // Extract and store dataflow for this call site
                            if (edge.InvocationSyntax != null && edge.SemanticModel != null)
                            {
                                var dfResult = DataflowExtractor.ExtractFromInvocation(
                                    edge.InvocationSyntax, edge.SemanticModel);

                                foreach (var arg in dfResult.Arguments)
                                {
                                    argumentFlowStore.Insert(edgeId, arg.ParameterOrdinal, arg.ParameterName,
                                        arg.ArgumentExpression, arg.ArgumentKind, arg.SourceSymbolFqn, now);
                                }

                                if (dfResult.ReturnDestination != null)
                                {
                                    returnFlowStore.Insert(edgeId, dfResult.ReturnDestination.DestinationKind,
                                        dfResult.ReturnDestination.DestinationVariable,
                                        dfResult.ReturnDestination.DestinationSymbolFqn, now);
                                }
                            }
                        }
                    }
                }
            }

            _log?.Invoke($"  {project.Name}: call graph extracted");
        }

        // Phase 6: Record project dependencies
        if (repoRoot != null)
        {
            _log?.Invoke("Recording project dependencies...");
            progress?.Report(new IndexingProgress
            {
                Phase = "recording_dependencies",
                Description = "Recording project dependencies",
                ProjectIndex = totalProjects,
                ProjectCount = totalProjects
            });
            var deps = DependencyExtractor.ExtractDependencies(solution, projectPathToId, submodules, repoRoot);
            foreach (var dep in deps)
            {
                if (dep.DependencyProjectId == 0)
                    continue; // Skip NuGet refs with no indexed project
                dependencyStore.Insert(dep);
            }
            _log?.Invoke($"  {deps.Count} dependencies recorded");
        }

        // Phase 7: Capture API surface snapshots for projects with inbound dependencies
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
                if (project.FilePath != null && projectPathToId.TryGetValue(project.FilePath, out var pid))
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
                }
            }
            _log?.Invoke($"  API surface captured for {projectsWithConsumers.Count} project(s)");
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

    private static string? ReadTargetFramework(string? projectFilePath)
    {
        if (projectFilePath == null || !File.Exists(projectFilePath)) return null;
        try
        {
            var xml = System.Xml.Linq.XDocument.Load(projectFilePath);
            return xml.Descendants("TargetFramework").FirstOrDefault()?.Value
                ?? xml.Descendants("TargetFrameworks").FirstOrDefault()?.Value?.Split(';').FirstOrDefault();
        }
        catch { return null; }
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

    private static long? ResolveEnclosingSymbol(int line, string filePath, SymbolStore symbolStore)
    {
        var fileSymbols = symbolStore.GetByFile(filePath);
        var enclosing = fileSymbols
            .Where(s => s.LineStart <= line && s.LineEnd >= line)
            .OrderByDescending(s => s.LineStart)
            .FirstOrDefault();
        return enclosing?.Id;
    }
}
