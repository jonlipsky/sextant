using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Proves the project-scoped <c>DeleteByFile(filePath, projectId)</c> overloads only clear the target
/// logical (per-TFM) project's rows for a shared source file, leaving a sibling project's rows for the
/// same file intact. This is the store-layer guarantee behind the fix for the last-TFM-wins data loss:
/// the two evaluated target frameworks of one multi-targeted csproj share source files, so re-indexing
/// one framework must not delete the other's symbols, references, comments, call edges, or relationships.
/// </summary>
[TestClass]
public class ProjectScopedDeleteTests
{
    private const string SharedFile = "src/MultiTarget/Formatter.cs";

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private ProjectStore _projectStore = null!;
    private SymbolStore _symbolStore = null!;
    private long _projectA;
    private long _projectB;
    private long _symbolA;
    private long _symbolB;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_scoped_del_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        _projectStore = new ProjectStore(conn);
        _symbolStore = new SymbolStore(conn);

        // Two logical projects standing in for the two evaluated TFMs of one multi-targeted csproj.
        _projectA = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "projA0000000000",
            GitRemoteUrl = "https://github.com/test/repo",
            RepoRelativePath = "src/MultiTarget/MultiTarget.csproj",
            TargetFramework = "net10.0"
        }, 1000);
        _projectB = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "projB0000000000",
            GitRemoteUrl = "https://github.com/test/repo",
            RepoRelativePath = "src/MultiTarget/MultiTarget.csproj",
            TargetFramework = "netstandard2.0"
        }, 1000);

        // One symbol per project, both declared in the same shared source file.
        _symbolA = _symbolStore.Insert(Symbol(_projectA, "M:MultiTarget.Formatter.Join(System.String,System.String)"));
        _symbolB = _symbolStore.Insert(Symbol(_projectB, "M:MultiTarget.Formatter.Join(System.String,System.String)"));
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* environment-only file lock; ignore */ }
        }
    }

    private static SymbolInfo Symbol(long projectId, string key) => new()
    {
        ProjectId = projectId,
        SymbolKey = key,
        FullyQualifiedName = "global::MultiTarget.Formatter.Join",
        DisplayName = "Join",
        Kind = SymbolKind.Method,
        Accessibility = Accessibility.Public,
        FilePath = SharedFile,
        LineStart = 5,
        LineEnd = 8,
        LastIndexedAt = 1000
    };

    [TestMethod]
    public void SymbolStore_DeleteByFile_IsProjectScoped()
    {
        _symbolStore.DeleteByFile(SharedFile, _projectA);

        Assert.AreEqual(0, _symbolStore.GetByFile(SharedFile, _projectA).Count,
            "The target project's symbols for the shared file must be cleared.");
        var survivors = _symbolStore.GetByFile(SharedFile, _projectB);
        Assert.AreEqual(1, survivors.Count, "The sibling project's symbols for the shared file must survive.");
        Assert.AreEqual(_symbolB, survivors[0].Id);
    }

    [TestMethod]
    public void ReferenceStore_DeleteByFile_IsProjectScoped()
    {
        var store = new ReferenceStore(_db.GetConnection());
        store.Insert(Reference(_symbolA, _projectA));
        store.Insert(Reference(_symbolB, _projectB));

        store.DeleteByFile(SharedFile, _projectA);

        Assert.AreEqual(0, store.GetByProject(_projectA).Count,
            "The target project's references in the shared file must be cleared.");
        Assert.AreEqual(1, store.GetByProject(_projectB).Count,
            "The sibling project's references in the shared file must survive.");
    }

    [TestMethod]
    public void CommentStore_DeleteByFile_IsProjectScoped()
    {
        var store = new CommentStore(_db.GetConnection());
        store.Insert(_projectA, SharedFile, 6, "TODO", "a", null, 1000);
        store.Insert(_projectB, SharedFile, 6, "TODO", "b", null, 1000);

        store.DeleteByFile(SharedFile, _projectA);

        var remaining = store.GetByFile(SharedFile);
        Assert.AreEqual(1, remaining.Count, "Only the sibling project's comment must remain.");
        Assert.AreEqual(_projectB, remaining[0].ProjectId);
    }

    [TestMethod]
    public void CallGraphStore_DeleteByFile_IsProjectScoped()
    {
        var store = new CallGraphStore(_db.GetConnection());
        store.Insert(Edge(_symbolA, _symbolB)); // caller in project A
        store.Insert(Edge(_symbolB, _symbolA)); // caller in project B

        store.DeleteByFile(SharedFile, _projectA);

        Assert.AreEqual(0, store.GetByCaller(_symbolA).Count,
            "Call edges made from the target project's code must be cleared.");
        Assert.AreEqual(1, store.GetByCaller(_symbolB).Count,
            "Call edges made from the sibling project's code must survive.");
    }

    [TestMethod]
    public void RelationshipStore_DeleteByFile_IsProjectScoped()
    {
        var store = new RelationshipStore(_db.GetConnection());
        store.Insert(new RelationshipInfo { FromSymbolId = _symbolA, ToSymbolId = _symbolA, Kind = RelationshipKind.Inherits, LastIndexedAt = 1000 });
        store.Insert(new RelationshipInfo { FromSymbolId = _symbolB, ToSymbolId = _symbolB, Kind = RelationshipKind.Inherits, LastIndexedAt = 1000 });

        store.DeleteByFile(SharedFile, _projectA);

        Assert.AreEqual(0, store.GetByFromSymbol(_symbolA).Count,
            "Relationships anchored on the target project's symbols must be cleared.");
        Assert.AreEqual(1, store.GetByFromSymbol(_symbolB).Count,
            "Relationships anchored on the sibling project's symbols must survive.");
    }

    [TestMethod]
    public void RelationshipStore_DeleteByFile_ScopesCrossProjectEdgesByEndpointProject()
    {
        var store = new RelationshipStore(_db.GetConnection());
        // A genuine cross-project edge A -> B, plus a sibling-only edge B -> B, all in the shared file.
        // Deleting project A must clear the A->B edge (its FROM endpoint is A's symbol) via the
        // project-scoped from_symbol_id branch, while the B->B edge (neither endpoint in A) survives.
        store.Insert(new RelationshipInfo { FromSymbolId = _symbolA, ToSymbolId = _symbolB, Kind = RelationshipKind.Inherits, LastIndexedAt = 1000 });
        store.Insert(new RelationshipInfo { FromSymbolId = _symbolB, ToSymbolId = _symbolB, Kind = RelationshipKind.Inherits, LastIndexedAt = 1000 });

        store.DeleteByFile(SharedFile, _projectA);

        Assert.AreEqual(0, store.GetByFromSymbol(_symbolA).Count,
            "The cross-project edge anchored on the target project's FROM symbol must be cleared.");
        var survivors = store.GetByFromSymbol(_symbolB);
        Assert.AreEqual(1, survivors.Count, "The sibling-only edge must survive.");
        Assert.AreEqual(_symbolB, survivors[0].ToSymbolId);
    }

    [TestMethod]
    public void RelationshipStore_DeleteByFile_ScopesToSymbolEndpointByProject()
    {
        var store = new RelationshipStore(_db.GetConnection());
        // Cross-project edge B -> A: its TO endpoint is the target project's symbol, so deleting
        // project A must clear it via the project-scoped to_symbol_id branch, not leave it dangling.
        store.Insert(new RelationshipInfo { FromSymbolId = _symbolB, ToSymbolId = _symbolA, Kind = RelationshipKind.Inherits, LastIndexedAt = 1000 });
        store.Insert(new RelationshipInfo { FromSymbolId = _symbolB, ToSymbolId = _symbolB, Kind = RelationshipKind.Inherits, LastIndexedAt = 1000 });

        store.DeleteByFile(SharedFile, _projectA);

        Assert.AreEqual(0, store.GetByToSymbol(_symbolA).Count,
            "The cross-project edge anchored on the target project's TO symbol must be cleared.");
        Assert.AreEqual(1, store.GetByToSymbol(_symbolB).Count,
            "The sibling-only edge must survive.");
    }

    private static ReferenceInfo Reference(long symbolId, long inProjectId) => new()
    {
        SymbolId = symbolId,
        InProjectId = inProjectId,
        FilePath = SharedFile,
        Line = 6,
        ReferenceKind = ReferenceKind.Invocation,
        LastIndexedAt = 1000
    };

    private static CallGraphEdge Edge(long caller, long callee) => new()
    {
        CallerSymbolId = caller,
        CalleeSymbolId = callee,
        CallSiteFile = SharedFile,
        CallSiteLine = 6,
        LastIndexedAt = 1000
    };
}
