using System.Security.Cryptography;
using Sextant.Core;
using Sextant.Store;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Sextant.Indexer;

public sealed class IncrementalIndexer
{
    private readonly IndexDatabase _db;
    private readonly Action<string>? _log;

    public IncrementalIndexer(IndexDatabase db, Action<string>? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Returns the set of file paths whose signatures changed (callers may need re-resolution).
    /// </summary>
    public Task<List<string>> IndexChangedFilesAsync(
        Solution solution,
        IReadOnlyList<string> changedFilePaths)
        => IndexChangedFilesAsync(solution, changedFilePaths, default);

    /// <summary>
    /// Returns the set of file paths whose signatures changed (callers may need re-resolution).
    /// </summary>
    public async Task<List<string>> IndexChangedFilesAsync(
        Solution solution,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var conn = _db.GetConnection();
        var fileIndexStore = new FileIndexStore(conn);
        var symbolStore = new SymbolStore(conn);
        var referenceStore = new ReferenceStore(conn);
        var callGraphStore = new CallGraphStore(conn);
        var relationshipStore = new RelationshipStore(conn);
        var projectStore = new ProjectStore(conn);
        var runStore = new IndexRunStore(conn);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signatureChangedFiles = new List<string>();

        // Build Roslyn-project → stored-id mapping. Keyed by the Roslyn ProjectId so the multiple
        // evaluated-TFM instances of one multi-targeted csproj resolve to distinct logical projects.
        var projectRoslynToId = new Dictionary<ProjectId, long>();
        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null) continue;
            var targetFramework = ProjectIdentityFactory.ResolveEvaluatedTargetFramework(project);
            var identity = GitRemoteResolver.Resolve(project.FilePath, targetFramework);
            var existing = projectStore.GetByCanonicalId(identity.CanonicalId);
            if (existing != null)
                projectRoslynToId[project.Id] = existing.Value.id;
        }

        // Build a project-aware symbol catalog for the whole solution (needed for relationships/calls).
        var catalog = new SymbolCatalog();
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!projectRoslynToId.TryGetValue(project.Id, out var pid))
                continue;
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var sm = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken);
                foreach (var node in root.DescendantNodes())
                {
                    var declared = sm.GetDeclaredSymbol(node);
                    if (declared == null || declared.IsImplicitlyDeclared) continue;
                    if (SemanticSymbolKeyFactory.IsExcludedArtifact(declared)) continue;
                    if (SymbolExtractor.MapSymbolKind(declared) == null) continue;
                    var declKey = SemanticSymbolKeyFactory.DeclarationKey(declared);
                    // Try to get existing ID from DB
                    var existing = symbolStore.GetBySymbolKey(declKey, pid);
                    if (existing != null)
                        catalog.Add(pid, declKey, existing.Id);
                }
            }
        }

        var changedSet = new HashSet<string>(changedFilePaths, StringComparer.OrdinalIgnoreCase);

        // Wrap the whole incremental delta in one bounded write session and staging generation.
        // Each changed file is an atomic batch (delete stale rows + re-insert), committed at its
        // boundary; the generation is published (and the WAL folded back) once every file succeeds.
        using var runScope = runStore.BeginScope("incremental", now);
        using var session = _db.BeginWriteSession();
        using var symbolInsert = symbolStore.CreateInsertCommand();
        using var relationshipInsert = relationshipStore.CreateInsertCommand();
        using var callGraphInsert = callGraphStore.CreateInsertCommand();
        session.Begin();

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!projectRoslynToId.TryGetValue(project.Id, out var projectId))
                continue;

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var filePath = syntaxTree.FilePath;
                if (string.IsNullOrEmpty(filePath) || !changedSet.Contains(filePath))
                    continue;

                if (SymbolExtractor.IsGeneratedFile(filePath))
                    continue;

                // Compute content hash
                var contentHash = ComputeFileHash(filePath);
                var existingEntry = fileIndexStore.GetByProjectAndFile(projectId, filePath);

                if (existingEntry != null && existingEntry.ContentHash == contentHash)
                {
                    _log?.Invoke($"  Skipping unchanged file: {filePath}");
                    continue;
                }

                _log?.Invoke($"  Re-indexing: {filePath}");
                cancellationToken.ThrowIfCancellationRequested();

                // Collect old signature hashes before deleting. Scope to this logical (per-TFM)
                // project so a shared source file's sibling-framework symbols are not read as this
                // project's old signatures.
                var oldSymbols = symbolStore.GetByFile(filePath, projectId);
                var oldSignatures = oldSymbols
                    .Where(s => s.SignatureHash != null)
                    .GroupBy(s => s.SymbolKey)
                    .ToDictionary(g => g.Key, g => g.First().SignatureHash!);

                // Delete stale data for this file, scoped to this logical (per-TFM) project so
                // re-indexing one framework does not delete the sibling framework's rows for the
                // same shared source file (the last-TFM-wins data-loss class this phase fixes).
                relationshipStore.DeleteByFile(filePath, projectId);
                symbolStore.DeleteByFile(filePath, projectId);
                referenceStore.DeleteByFile(filePath, projectId);
                callGraphStore.DeleteByFile(filePath, projectId);

                // Re-extract symbols for this file
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);
                var newSymbols = new List<Sextant.Core.SymbolInfo>();

                foreach (var node in root.DescendantNodes())
                {
                    var declared = semanticModel.GetDeclaredSymbol(node);
                    if (declared == null || declared.IsImplicitlyDeclared) continue;

                    var kind = SymbolExtractor.MapSymbolKind(declared);
                    if (kind == null) continue;

                    var symbolInfo = SymbolExtractor.ExtractSymbolInfo(declared, projectId);
                    if (symbolInfo == null) continue;

                    var id = symbolStore.Insert(symbolInsert, symbolInfo);
                    catalog.Add(projectId, symbolInfo.SymbolKey, id);
                    symbolInfo.Id = id;
                    newSymbols.Add(symbolInfo);
                    session.RowsWritten();
                }

                // Check for signature changes
                foreach (var newSym in newSymbols)
                {
                    if (newSym.SignatureHash != null &&
                        oldSignatures.TryGetValue(newSym.SymbolKey, out var oldHash) &&
                        oldHash != newSym.SignatureHash)
                    {
                        signatureChangedFiles.Add(filePath);
                        break;
                    }
                }

                // Re-extract relationships for types in this file
                foreach (var node in root.DescendantNodes())
                {
                    if (semanticModel.GetDeclaredSymbol(node) is INamedTypeSymbol typeSymbol &&
                        !typeSymbol.IsImplicitlyDeclared &&
                        !SemanticSymbolKeyFactory.IsExcludedArtifact(typeSymbol))
                    {
                        var rels = RelationshipExtractor.ExtractRelationships(typeSymbol);
                        var instantiates = RelationshipExtractor.ExtractInstantiates(typeSymbol, compilation);
                        foreach (var (fromKey, toKey, relKind) in rels.Concat(instantiates))
                        {
                            if (catalog.TryResolveEdge(fromKey, projectId, out var fromId) &&
                                catalog.TryResolveEdge(toKey, projectId, out var toId))
                            {
                                relationshipStore.Insert(relationshipInsert, new RelationshipInfo
                                {
                                    FromSymbolId = fromId,
                                    ToSymbolId = toId,
                                    Kind = relKind,
                                    LastIndexedAt = now
                                });
                                session.RowsWritten();
                            }
                        }
                    }
                }

                // Re-extract call graph for methods in this file
                foreach (var node in root.DescendantNodes())
                {
                    var declared = semanticModel.GetDeclaredSymbol(node);
                    if (declared is not IMethodSymbol methodSymbol || declared.IsImplicitlyDeclared)
                        continue;

                    var callerKey = SemanticSymbolKeyFactory.DeclarationKey(methodSymbol);
                    if (!catalog.TryResolveEdge(callerKey, projectId, out var callerSymbolId))
                        continue;

                    var edges = await CallGraphBuilder.BuildCallGraphAsync(methodSymbol, project);
                    foreach (var edge in edges)
                    {
                        if (catalog.TryResolveEdge(edge.CalleeKey, projectId, out var calleeSymbolId))
                        {
                            callGraphStore.Insert(callGraphInsert, new CallGraphEdge
                            {
                                CallerSymbolId = callerSymbolId,
                                CalleeSymbolId = calleeSymbolId,
                                CallSiteFile = edge.CallSiteFile,
                                CallSiteLine = edge.CallSiteLine,
                                LastIndexedAt = now
                            });
                            session.RowsWritten();
                        }
                    }
                }

                // Update file_index
                fileIndexStore.Upsert(new FileIndexEntry
                {
                    ProjectId = projectId,
                    FilePath = filePath,
                    ContentHash = contentHash,
                    LastIndexedAt = now
                });
                session.CommitBatch();
            }
        }

        // Publish the incremental generation atomically with the final file's data (see the orchestrator
        // for the rationale). Refuse a no-op publish, then run checkpoint/footprint as best-effort
        // post-publish maintenance so a maintenance failure never discards the successful result.
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (runStore.MarkComplete(runScope.RunId, completedAt, projectRoslynToId.Count) != 1)
            throw new InvalidOperationException(
                $"Index run {runScope.RunId} was not in staging state at publish; aborting to avoid a false completion.");
        session.Complete();
        runScope.Detach();

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

        return signatureChangedFiles;
    }

    public static string ComputeFileHash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
