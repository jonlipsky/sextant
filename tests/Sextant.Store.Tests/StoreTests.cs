using Sextant.Core;

namespace Sextant.Store.Tests;

[TestClass]
public class StoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private ProjectStore _projectStore = null!;
    private SymbolStore _symbolStore = null!;
    private ReferenceStore _referenceStore = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_test_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        _projectStore = new ProjectStore(conn);
        _symbolStore = new SymbolStore(conn);
        _referenceStore = new ReferenceStore(conn);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [TestMethod]
    public void ProjectStore_InsertAndQueryByCanonicalId()
    {
        var project = new ProjectIdentity
        {
            CanonicalId = "abc123def456gh78",
            GitRemoteUrl = "https://github.com/org/repo",
            RepoRelativePath = "src/MyProject/MyProject.csproj",
            DiskPath = "/home/user/repo/src/MyProject/MyProject.csproj"
        };

        var id = _projectStore.Insert(project, 1000);
        Assert.IsTrue(id > 0);

        var result = _projectStore.GetByCanonicalId("abc123def456gh78");
        Assert.IsNotNull(result);
        Assert.AreEqual("https://github.com/org/repo", result.Value.project.GitRemoteUrl);
        Assert.AreEqual("src/MyProject/MyProject.csproj", result.Value.project.RepoRelativePath);
    }

    [TestMethod]
    public void SymbolStore_InsertAndExactFqnLookup()
    {
        var projectId = InsertTestProject();

        var symbol = new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::MyNamespace.MyClass",
            DisplayName = "MyClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/MyClass.cs",
            LineStart = 10,
            LineEnd = 50,
            LastIndexedAt = 1000
        };

        var symbolId = _symbolStore.Insert(symbol);
        Assert.IsTrue(symbolId > 0);

        var result = _symbolStore.GetByFqn("global::MyNamespace.MyClass");
        Assert.IsNotNull(result);
        Assert.AreEqual("MyClass", result.DisplayName);
        Assert.AreEqual(SymbolKind.Class, result.Kind);
    }

    [TestMethod]
    public void SymbolStore_Fts5Search()
    {
        var projectId = InsertTestProject();

        _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::MyNamespace.CustomerService",
            DisplayName = "CustomerService",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/CustomerService.cs",
            LineStart = 1,
            LineEnd = 100,
            DocComment = "Handles customer operations",
            LastIndexedAt = 1000
        });

        _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::MyNamespace.OrderService",
            DisplayName = "OrderService",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/OrderService.cs",
            LineStart = 1,
            LineEnd = 50,
            LastIndexedAt = 1000
        });

        var results = _symbolStore.SearchFts("Customer", 10);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("CustomerService", results[0].DisplayName);
    }

    [TestMethod]
    public void ReferenceStore_InsertAndQueryBySymbolId()
    {
        var projectId = InsertTestProject();

        var symbolId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::MyNamespace.MyClass.MyMethod",
            DisplayName = "MyMethod",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/MyClass.cs",
            LineStart = 20,
            LineEnd = 30,
            LastIndexedAt = 1000
        });

        var refId = _referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = symbolId,
            InProjectId = projectId,
            FilePath = "src/Caller.cs",
            Line = 15,
            ContextSnippet = "myObj.MyMethod();",
            ReferenceKind = ReferenceKind.Invocation
        });

        Assert.IsTrue(refId > 0);

        var refs = _referenceStore.GetBySymbolId(symbolId);
        Assert.AreEqual(1, refs.Count);
        Assert.AreEqual(ReferenceKind.Invocation, refs[0].ReferenceKind);
        Assert.AreEqual("myObj.MyMethod();", refs[0].ContextSnippet);
    }

    [TestMethod]
    public void RelationshipStore_InsertAndGetBySymbol()
    {
        var relationshipStore = new RelationshipStore(_db.GetConnection());
        var projectId = InsertTestProject();

        var fromSymbolId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::MyNamespace.IService",
            DisplayName = "IService",
            Kind = SymbolKind.Interface,
            Accessibility = Accessibility.Public,
            FilePath = "src/IService.cs",
            LineStart = 1,
            LineEnd = 10,
            LastIndexedAt = 1000
        });

        var toSymbolId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::MyNamespace.ServiceImpl",
            DisplayName = "ServiceImpl",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/ServiceImpl.cs",
            LineStart = 1,
            LineEnd = 50,
            LastIndexedAt = 1000
        });

        var relId = relationshipStore.Insert(new RelationshipInfo
        {
            FromSymbolId = fromSymbolId,
            ToSymbolId = toSymbolId,
            Kind = RelationshipKind.Implements,
            LastIndexedAt = 1000
        });

        Assert.IsTrue(relId > 0);

        var fromResults = relationshipStore.GetByFromSymbol(fromSymbolId);
        Assert.AreEqual(1, fromResults.Count);
        Assert.AreEqual(toSymbolId, fromResults[0].ToSymbolId);
        Assert.AreEqual(RelationshipKind.Implements, fromResults[0].Kind);

        var toResults = relationshipStore.GetByToSymbol(toSymbolId);
        Assert.AreEqual(1, toResults.Count);
        Assert.AreEqual(fromSymbolId, toResults[0].FromSymbolId);
        Assert.AreEqual(RelationshipKind.Implements, toResults[0].Kind);

        var filteredResults = relationshipStore.GetByFromSymbol(fromSymbolId, RelationshipKind.Inherits);
        Assert.AreEqual(0, filteredResults.Count);
    }

    [TestMethod]
    public void RelationshipStore_DeleteByFile()
    {
        var relationshipStore = new RelationshipStore(_db.GetConnection());
        var projectId = InsertTestProject();

        var symbolA = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::NS.ClassA",
            DisplayName = "ClassA",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/ToDelete.cs",
            LineStart = 1,
            LineEnd = 20,
            LastIndexedAt = 1000
        });

        var symbolB = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::NS.ClassB",
            DisplayName = "ClassB",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/Keep.cs",
            LineStart = 1,
            LineEnd = 20,
            LastIndexedAt = 1000
        });

        relationshipStore.Insert(new RelationshipInfo
        {
            FromSymbolId = symbolA,
            ToSymbolId = symbolB,
            Kind = RelationshipKind.Inherits,
            LastIndexedAt = 1000
        });

        var before = relationshipStore.GetByFromSymbol(symbolA);
        Assert.AreEqual(1, before.Count);

        relationshipStore.DeleteByFile("src/ToDelete.cs");

        var after = relationshipStore.GetByFromSymbol(symbolA);
        Assert.AreEqual(0, after.Count);
    }

    [TestMethod]
    public void CallGraphStore_InsertAndGetByCaller()
    {
        var callGraphStore = new CallGraphStore(_db.GetConnection());
        var projectId = InsertTestProject();

        var callerId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::NS.Caller.DoWork",
            DisplayName = "DoWork",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/Caller.cs",
            LineStart = 10,
            LineEnd = 20,
            LastIndexedAt = 1000
        });

        var calleeId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::NS.Service.Execute",
            DisplayName = "Execute",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/Service.cs",
            LineStart = 5,
            LineEnd = 15,
            LastIndexedAt = 1000
        });

        var edgeId = callGraphStore.Insert(new CallGraphEdge
        {
            CallerSymbolId = callerId,
            CalleeSymbolId = calleeId,
            CallSiteFile = "src/Caller.cs",
            CallSiteLine = 15,
            LastIndexedAt = 1000
        });

        Assert.IsTrue(edgeId > 0);

        var edges = callGraphStore.GetByCaller(callerId);
        Assert.AreEqual(1, edges.Count);
        Assert.AreEqual(calleeId, edges[0].CalleeSymbolId);
        Assert.AreEqual("src/Caller.cs", edges[0].CallSiteFile);
        Assert.AreEqual(15, edges[0].CallSiteLine);
    }

    [TestMethod]
    public void CallGraphStore_DeleteByFile()
    {
        var callGraphStore = new CallGraphStore(_db.GetConnection());
        var projectId = InsertTestProject();

        var callerId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::NS.CallerDel.Run",
            DisplayName = "Run",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/CallerDel.cs",
            LineStart = 1,
            LineEnd = 10,
            LastIndexedAt = 1000
        });

        var calleeId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::NS.TargetDel.Process",
            DisplayName = "Process",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/TargetDel.cs",
            LineStart = 1,
            LineEnd = 10,
            LastIndexedAt = 1000
        });

        callGraphStore.Insert(new CallGraphEdge
        {
            CallerSymbolId = callerId,
            CalleeSymbolId = calleeId,
            CallSiteFile = "src/CallerDel.cs",
            CallSiteLine = 5,
            LastIndexedAt = 1000
        });

        var before = callGraphStore.GetByCaller(callerId);
        Assert.AreEqual(1, before.Count);

        callGraphStore.DeleteByFile("src/CallerDel.cs");

        var after = callGraphStore.GetByCaller(callerId);
        Assert.AreEqual(0, after.Count);
    }

    [TestMethod]
    public void ReferenceStore_DeleteByFile()
    {
        var projectId = InsertTestProject();

        var symbolId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::NS.RefTarget.Method",
            DisplayName = "Method",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/RefTarget.cs",
            LineStart = 1,
            LineEnd = 10,
            LastIndexedAt = 1000
        });

        _referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = symbolId,
            InProjectId = projectId,
            FilePath = "src/RefCaller.cs",
            Line = 10,
            ContextSnippet = "target.Method();",
            ReferenceKind = ReferenceKind.Invocation,
            LastIndexedAt = 1000
        });

        _referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = symbolId,
            InProjectId = projectId,
            FilePath = "src/OtherCaller.cs",
            Line = 20,
            ContextSnippet = "target.Method();",
            ReferenceKind = ReferenceKind.Invocation,
            LastIndexedAt = 1000
        });

        var before = _referenceStore.GetBySymbolId(symbolId);
        Assert.AreEqual(2, before.Count);

        _referenceStore.DeleteByFile("src/RefCaller.cs");

        var after = _referenceStore.GetBySymbolId(symbolId);
        Assert.AreEqual(1, after.Count);
        Assert.AreEqual("src/OtherCaller.cs", after[0].FilePath);
    }

    [TestMethod]
    public void ApiSurfaceStore_GetLatestByProject()
    {
        var apiSurfaceStore = new ApiSurfaceStore(_db.GetConnection());
        var projectId = InsertTestProject();

        var symbolId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::NS.ApiClass",
            DisplayName = "ApiClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/ApiClass.cs",
            LineStart = 1,
            LineEnd = 50,
            LastIndexedAt = 1000
        });

        apiSurfaceStore.Insert(new ApiSurfaceSnapshot
        {
            ProjectId = projectId,
            SymbolId = symbolId,
            SignatureHash = "hash_old",
            CapturedAt = 1000,
            GitCommit = "aaa111"
        });

        apiSurfaceStore.Insert(new ApiSurfaceSnapshot
        {
            ProjectId = projectId,
            SymbolId = symbolId,
            SignatureHash = "hash_new",
            CapturedAt = 2000,
            GitCommit = "bbb222"
        });

        var latest = apiSurfaceStore.GetLatestByProject(projectId);
        Assert.AreEqual(1, latest.Count);
        Assert.AreEqual("hash_new", latest[0].SignatureHash);
        Assert.AreEqual("bbb222", latest[0].GitCommit);
        Assert.AreEqual(2000, latest[0].CapturedAt);
    }

    [TestMethod]
    public void ProjectDependencyStore_DeleteByConsumer()
    {
        var depStore = new ProjectDependencyStore(_db.GetConnection());
        var consumerId = InsertTestProject();
        var depId = InsertTestProject();

        depStore.Insert(new ProjectDependency
        {
            ConsumerProjectId = consumerId,
            DependencyProjectId = depId,
            ReferenceKind = "ProjectReference",
            SubmodulePinnedCommit = null
        });

        var before = depStore.GetByConsumer(consumerId);
        Assert.AreEqual(1, before.Count);
        Assert.AreEqual(depId, before[0].DependencyProjectId);
        Assert.AreEqual("ProjectReference", before[0].ReferenceKind);

        depStore.DeleteByConsumer(consumerId);

        var after = depStore.GetByConsumer(consumerId);
        Assert.AreEqual(0, after.Count);
    }

    [TestMethod]
    public void Fts5_SyncOnDeleteAndUpdate()
    {
        var projectId = InsertTestProject();

        var symbolId = _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = "global::UniqueNS.ZebraManager",
            DisplayName = "ZebraManager",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/ZebraManager.cs",
            LineStart = 1,
            LineEnd = 30,
            LastIndexedAt = 1000
        });

        var searchBefore = _symbolStore.SearchFts("ZebraManager", 10);
        Assert.AreEqual(1, searchBefore.Count);
        Assert.AreEqual(symbolId, searchBefore[0].Id);

        _symbolStore.DeleteByFile("src/ZebraManager.cs");

        var searchAfter = _symbolStore.SearchFts("ZebraManager", 10);
        Assert.AreEqual(0, searchAfter.Count);
    }

    private long InsertTestProject()
    {
        return _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = $"test{Guid.NewGuid():N}"[..16],
            GitRemoteUrl = "https://github.com/test/repo",
            RepoRelativePath = "src/Test/Test.csproj"
        }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
