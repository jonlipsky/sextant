using System.Security.Cryptography;
using Sextant.Core;
using Sextant.Store;
using Microsoft.CodeAnalysis;

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
    public async Task<List<string>> IndexChangedFilesAsync(
        Solution solution,
        IReadOnlyList<string> changedFilePaths)
    {
        var conn = _db.GetConnection();
        var fileIndexStore = new FileIndexStore(conn);
        var symbolStore = new SymbolStore(conn);
        var referenceStore = new ReferenceStore(conn);
        var callGraphStore = new CallGraphStore(conn);
        var relationshipStore = new RelationshipStore(conn);
        var projectStore = new ProjectStore(conn);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signatureChangedFiles = new List<string>();

        // Build project path → id mapping
        var projectPathToId = new Dictionary<string, long>();
        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null) continue;
            var identity = GitRemoteResolver.Resolve(project.FilePath);
            var existing = projectStore.GetByCanonicalId(identity.CanonicalId);
            if (existing != null)
                projectPathToId[project.FilePath] = existing.Value.id;
        }

        // Build symbol FQN → ID map for the whole solution (needed for relationships/references)
        var symbolFqnToId = new Dictionary<string, long>();
        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null || !projectPathToId.TryGetValue(project.FilePath, out var pid))
                continue;
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var sm = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync();
                foreach (var node in root.DescendantNodes())
                {
                    var declared = sm.GetDeclaredSymbol(node);
                    if (declared == null || declared.IsImplicitlyDeclared) continue;
                    if (SymbolExtractor.MapSymbolKind(declared) == null) continue;
                    var fqn = declared.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    // Try to get existing ID from DB
                    var existing = symbolStore.GetByFqn(fqn, pid);
                    if (existing != null)
                        symbolFqnToId[fqn] = existing.Id;
                }
            }
        }

        var changedSet = new HashSet<string>(changedFilePaths, StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null || !projectPathToId.TryGetValue(project.FilePath, out var projectId))
                continue;

            var compilation = await project.GetCompilationAsync();
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

                // Collect old signature hashes before deleting
                var oldSymbols = symbolStore.GetByFile(filePath);
                var oldSignatures = oldSymbols
                    .Where(s => s.SignatureHash != null)
                    .ToDictionary(s => s.FullyQualifiedName, s => s.SignatureHash!);

                // Delete stale data for this file
                relationshipStore.DeleteByFile(filePath);
                symbolStore.DeleteByFile(filePath);
                referenceStore.DeleteByFile(filePath);
                callGraphStore.DeleteByFile(filePath);

                // Re-extract symbols for this file
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();
                var newSymbols = new List<Sextant.Core.SymbolInfo>();

                foreach (var node in root.DescendantNodes())
                {
                    var declared = semanticModel.GetDeclaredSymbol(node);
                    if (declared == null || declared.IsImplicitlyDeclared) continue;

                    var kind = SymbolExtractor.MapSymbolKind(declared);
                    if (kind == null) continue;

                    var symbolInfo = SymbolExtractor.ExtractSymbolInfo(declared, projectId);
                    if (symbolInfo == null) continue;

                    var id = symbolStore.Insert(symbolInfo);
                    symbolFqnToId[symbolInfo.FullyQualifiedName] = id;
                    symbolInfo.Id = id;
                    newSymbols.Add(symbolInfo);
                }

                // Check for signature changes
                foreach (var newSym in newSymbols)
                {
                    if (newSym.SignatureHash != null &&
                        oldSignatures.TryGetValue(newSym.FullyQualifiedName, out var oldHash) &&
                        oldHash != newSym.SignatureHash)
                    {
                        signatureChangedFiles.Add(filePath);
                        break;
                    }
                }

                // Re-extract relationships for types in this file
                foreach (var node in root.DescendantNodes())
                {
                    if (semanticModel.GetDeclaredSymbol(node) is INamedTypeSymbol typeSymbol && !typeSymbol.IsImplicitlyDeclared)
                    {
                        var rels = RelationshipExtractor.ExtractRelationships(typeSymbol);
                        var instantiates = RelationshipExtractor.ExtractInstantiates(typeSymbol, compilation);
                        foreach (var (fromFqn, toFqn, relKind) in rels.Concat(instantiates))
                        {
                            if (symbolFqnToId.TryGetValue(fromFqn, out var fromId) &&
                                symbolFqnToId.TryGetValue(toFqn, out var toId))
                            {
                                relationshipStore.Insert(new RelationshipInfo
                                {
                                    FromSymbolId = fromId,
                                    ToSymbolId = toId,
                                    Kind = relKind,
                                    LastIndexedAt = now
                                });
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

                    var callerFqn = methodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (!symbolFqnToId.TryGetValue(callerFqn, out var callerSymbolId))
                        continue;

                    var edges = await CallGraphBuilder.BuildCallGraphAsync(methodSymbol, project);
                    foreach (var edge in edges)
                    {
                        if (symbolFqnToId.TryGetValue(edge.CalleeFqn, out var calleeSymbolId))
                        {
                            callGraphStore.Insert(new CallGraphEdge
                            {
                                CallerSymbolId = callerSymbolId,
                                CalleeSymbolId = calleeSymbolId,
                                CallSiteFile = edge.CallSiteFile,
                                CallSiteLine = edge.CallSiteLine,
                                LastIndexedAt = now
                            });
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
            }
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
