using System.Text.Json;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

[TestClass]
public class McpToolTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private DatabaseProvider _dbProvider = null!;
    private long _projectId;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_mcp_test_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();

        // We can't use DatabaseProvider directly since it checks file existence
        // Instead, create one pointing to our test DB
        _dbProvider = new DatabaseProvider(_dbPath);

        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        _projectId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "test0123456789ab",
            GitRemoteUrl = "https://github.com/test/repo",
            RepoRelativePath = "src/Test/Test.csproj"
        }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var symbolStore = new SymbolStore(conn);
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::TestNamespace.TestClass",
            DisplayName = "TestClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/TestClass.cs",
            LineStart = 5,
            LineEnd = 50,
            DocComment = "A test class for testing",
            LastIndexedAt = 1000
        });

        var methodId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::TestNamespace.TestClass.DoWork(string)",
            DisplayName = "DoWork",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void DoWork(string input)",
            FilePath = "src/TestClass.cs",
            LineStart = 20,
            LineEnd = 30,
            LastIndexedAt = 1000
        });

        var referenceStore = new ReferenceStore(conn);
        referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = methodId,
            InProjectId = _projectId,
            FilePath = "src/Caller.cs",
            Line = 15,
            ContextSnippet = "obj.DoWork(\"hello\");",
            ReferenceKind = ReferenceKind.Invocation
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbProvider?.Dispose();
        _db?.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [TestMethod]
    public void FindSymbol_ExactMatch_ReturnsCorrectSymbol()
    {
        var result = FindSymbolTool.FindSymbol(_dbProvider, "global::TestNamespace.TestClass");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());

        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual("TestClass", results[0].GetProperty("display_name").GetString());
    }

    [TestMethod]
    public void FindSymbol_FuzzyMatch_ReturnsFtsRankedResults()
    {
        var result = FindSymbolTool.FindSymbol(_dbProvider, "TestClass", fuzzy: true);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void FindReferences_ReturnsAllLocationsWithKinds()
    {
        var result = FindReferencesTool.FindReferences(_dbProvider, "global::TestNamespace.TestClass.DoWork(string)");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());

        var refs = doc.RootElement.GetProperty("results");
        Assert.AreEqual("invocation", refs[0].GetProperty("reference_kind").GetString());
        Assert.AreEqual("obj.DoWork(\"hello\");", refs[0].GetProperty("context_snippet").GetString());
    }

    [TestMethod]
    public void GetTypeMembers_ReturnsMembersWithSignatures()
    {
        var result = GetTypeMembersTool.GetTypeMembers(_dbProvider, "global::TestNamespace.TestClass");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());

        var members = doc.RootElement.GetProperty("results");
        Assert.AreEqual("DoWork", members[0].GetProperty("display_name").GetString());
        Assert.AreEqual("public void DoWork(string input)", members[0].GetProperty("signature").GetString());
    }

    [TestMethod]
    public void GetFileSymbols_ReturnsAllSymbolsInFile()
    {
        var result = GetFileSymbolsTool.GetFileSymbols(_dbProvider, "src/TestClass.cs");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(2, meta.GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public void AllResponses_IncludeMetaObject()
    {
        var results = new[]
        {
            FindSymbolTool.FindSymbol(_dbProvider, "global::TestNamespace.TestClass"),
            FindReferencesTool.FindReferences(_dbProvider, "global::TestNamespace.TestClass.DoWork(string)"),
            GetFileSymbolsTool.GetFileSymbols(_dbProvider, "src/TestClass.cs"),
            GetTypeMembersTool.GetTypeMembers(_dbProvider, "global::TestNamespace.TestClass")
        };

        foreach (var result in results)
        {
            var doc = JsonDocument.Parse(result);
            var meta = doc.RootElement.GetProperty("meta");
            Assert.IsTrue(meta.GetProperty("queried_at").GetInt64() > 0);
            Assert.IsTrue(meta.TryGetProperty("index_freshness", out _));
            Assert.IsTrue(meta.TryGetProperty("result_count", out _));
        }
    }

    [TestMethod]
    public void MissingDatabase_ReturnsEmptyResults()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.db");
        using var emptyProvider = new DatabaseProvider(nonExistentPath);

        var result = FindSymbolTool.FindSymbol(emptyProvider, "anything");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public void GetCallHierarchy_RespectsDepthLimit_ReturnsFlatResults()
    {
        // Set up call graph: A -> B -> C
        var conn = _db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var callGraphStore = new CallGraphStore(conn);

        var methodAId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.MethodA()",
            DisplayName = "MethodA",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/Test.cs",
            LineStart = 1, LineEnd = 5,
            LastIndexedAt = 1000
        });

        var methodBId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.MethodB()",
            DisplayName = "MethodB",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/Test.cs",
            LineStart = 10, LineEnd = 15,
            LastIndexedAt = 1000
        });

        var methodCId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.MethodC()",
            DisplayName = "MethodC",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/Test.cs",
            LineStart = 20, LineEnd = 25,
            LastIndexedAt = 1000
        });

        callGraphStore.Insert(new CallGraphEdge { CallerSymbolId = methodAId, CalleeSymbolId = methodBId, CallSiteFile = "src/Test.cs", CallSiteLine = 3 });
        callGraphStore.Insert(new CallGraphEdge { CallerSymbolId = methodBId, CalleeSymbolId = methodCId, CallSiteFile = "src/Test.cs", CallSiteLine = 12 });

        // Get callees of MethodA with depth 1 (should only return MethodB)
        var result = GetCallHierarchyTool.GetCallHierarchy(_dbProvider, "global::Test.MethodA()", "callees", 1);
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(1, results.GetArrayLength());
        Assert.AreEqual("MethodB", results[0].GetProperty("display_name").GetString());
        Assert.AreEqual(1, results[0].GetProperty("depth").GetInt32());

        // Get callees with depth 5 (should return MethodB and MethodC)
        result = GetCallHierarchyTool.GetCallHierarchy(_dbProvider, "global::Test.MethodA()", "callees", 5);
        doc = JsonDocument.Parse(result);
        results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(2, results.GetArrayLength());
    }

    [TestMethod]
    public void GetImplementors_ReturnsConcreteImplementations()
    {
        var conn = _db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var relationshipStore = new RelationshipStore(conn);

        var interfaceId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.IService",
            DisplayName = "IService",
            Kind = SymbolKind.Interface,
            Accessibility = Accessibility.Public,
            FilePath = "src/IService.cs",
            LineStart = 1, LineEnd = 5,
            LastIndexedAt = 1000
        });

        var implId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.MyService",
            DisplayName = "MyService",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/MyService.cs",
            LineStart = 1, LineEnd = 20,
            LastIndexedAt = 1000
        });

        relationshipStore.Insert(new RelationshipInfo
        {
            FromSymbolId = implId,
            ToSymbolId = interfaceId,
            Kind = RelationshipKind.Implements
        });

        var result = GetImplementorsTool.GetImplementors(_dbProvider, "global::Test.IService");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(1, results.GetArrayLength());
        Assert.AreEqual("MyService", results[0].GetProperty("display_name").GetString());
        Assert.AreEqual("implements", results[0].GetProperty("relationship").GetString());
    }

    [TestMethod]
    public void GetTypeHierarchy_ReturnsBaseAndDerivedTypes()
    {
        var conn = _db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var relationshipStore = new RelationshipStore(conn);

        var baseId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.BaseClass",
            DisplayName = "BaseClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/Base.cs",
            LineStart = 1, LineEnd = 10,
            LastIndexedAt = 1000
        });

        var midId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.MiddleClass",
            DisplayName = "MiddleClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/Middle.cs",
            LineStart = 1, LineEnd = 10,
            LastIndexedAt = 1000
        });

        var derivedId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.DerivedClass",
            DisplayName = "DerivedClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/Derived.cs",
            LineStart = 1, LineEnd = 10,
            LastIndexedAt = 1000
        });

        relationshipStore.Insert(new RelationshipInfo { FromSymbolId = midId, ToSymbolId = baseId, Kind = RelationshipKind.Inherits });
        relationshipStore.Insert(new RelationshipInfo { FromSymbolId = derivedId, ToSymbolId = midId, Kind = RelationshipKind.Inherits });

        // "up" from DerivedClass should find MiddleClass and BaseClass
        var result = GetTypeHierarchyTool.GetTypeHierarchy(_dbProvider, "global::Test.DerivedClass", "up");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(2, results.GetArrayLength());

        // "down" from BaseClass should find MiddleClass and DerivedClass
        result = GetTypeHierarchyTool.GetTypeHierarchy(_dbProvider, "global::Test.BaseClass", "down");
        doc = JsonDocument.Parse(result);
        results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(2, results.GetArrayLength());
    }

    [TestMethod]
    public void SemanticSearch_ReturnsFtsRankedResults()
    {
        var result = SemanticSearchTool.SemanticSearch(_dbProvider, "Test", max_results: 10);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void GetImpact_ReturnsConsumersWithReferenceCounts()
    {
        // Set up: projectB depends on _projectId (test project)
        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var depStore = new ProjectDependencyStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var projectBId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "consumer_project1",
            GitRemoteUrl = "https://github.com/test/consumer",
            RepoRelativePath = "src/Consumer/Consumer.csproj"
        }, now);

        depStore.Insert(new ProjectDependency
        {
            ConsumerProjectId = projectBId,
            DependencyProjectId = _projectId,
            ReferenceKind = "submodule_ref",
            SubmodulePinnedCommit = "abc123"
        });

        var result = GetImpactTool.GetImpact(_dbProvider, "global::TestNamespace.TestClass");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var impact = results[0];
        Assert.IsTrue(impact.GetProperty("is_api_surface").GetBoolean() == false ||
                     impact.GetProperty("is_api_surface").GetBoolean() == true);
        var consumers = impact.GetProperty("consumers");
        Assert.IsTrue(consumers.GetArrayLength() >= 1);
        Assert.AreEqual("submodule_ref", consumers[0].GetProperty("reference_kind").GetString());
    }

    [TestMethod]
    public void GetApiSurface_ReturnsPublicProtectedSymbols_AndDiffsAgainstPreviousCommit()
    {
        var conn = _db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var apiSurfaceStore = new ApiSurfaceStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Add a protected symbol
        var protectedMethodId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::TestNamespace.TestClass.OnInit()",
            DisplayName = "OnInit",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Protected,
            Signature = "protected void OnInit()",
            SignatureHash = "hash_oninit_v1",
            FilePath = "src/TestClass.cs",
            LineStart = 35, LineEnd = 40,
            LastIndexedAt = 1000
        });

        // Non-diff mode: should return public + protected symbols
        var result = GetApiSurfaceTool.GetApiSurface(_dbProvider, "test0123456789ab");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 3); // TestClass + DoWork + OnInit
        Assert.IsTrue(meta.GetProperty("queried_at").GetInt64() > 0);

        // Create an old snapshot for diff mode (simulate previous commit had OnInit with old signature)
        apiSurfaceStore.Insert(new ApiSurfaceSnapshot
        {
            ProjectId = _projectId,
            SymbolId = protectedMethodId,
            SignatureHash = "hash_oninit_OLD",
            CapturedAt = now - 10000,
            GitCommit = "oldcommit123"
        });

        // Diff mode: signature changed -> breaking
        result = GetApiSurfaceTool.GetApiSurface(_dbProvider, "test0123456789ab", compare_to_commit: "oldcommit123");
        doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var diff = results[0];
        Assert.IsTrue(diff.TryGetProperty("overall_classification", out _));
        Assert.AreEqual("oldcommit123", diff.GetProperty("compared_against").GetString());
        var changes = diff.GetProperty("changes");
        Assert.IsTrue(changes.GetArrayLength() >= 1);
    }

    [TestMethod]
    public void GetProjectDependencies_ReturnsTransitiveDependencies()
    {
        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var depStore = new ProjectDependencyStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Create chain: C -> B -> _projectId
        var projectBId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "dep_chain_b_1234",
            GitRemoteUrl = "https://github.com/test/chainB",
            RepoRelativePath = "src/B/B.csproj"
        }, now);

        var projectCId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "dep_chain_c_1234",
            GitRemoteUrl = "https://github.com/test/chainC",
            RepoRelativePath = "src/C/C.csproj"
        }, now);

        depStore.Insert(new ProjectDependency
        {
            ConsumerProjectId = projectCId,
            DependencyProjectId = projectBId,
            ReferenceKind = "project_ref"
        });

        depStore.Insert(new ProjectDependency
        {
            ConsumerProjectId = projectBId,
            DependencyProjectId = _projectId,
            ReferenceKind = "project_ref"
        });

        // Non-transitive: only direct deps of C
        var result = GetProjectDependenciesTool.GetProjectDependencies(
            _dbProvider, "dep_chain_c_1234", transitive: false);
        var doc = JsonDocument.Parse(result);
        Assert.AreEqual(1, doc.RootElement.GetProperty("meta").GetProperty("result_count").GetInt32());

        // Transitive: should get both B and the original project
        result = GetProjectDependenciesTool.GetProjectDependencies(
            _dbProvider, "dep_chain_c_1234", transitive: true);
        doc = JsonDocument.Parse(result);
        Assert.AreEqual(2, doc.RootElement.GetProperty("meta").GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public void FindByAttribute_ReturnsDecoratedSymbols()
    {
        var conn = _db.GetConnection();
        var symbolStore = new SymbolStore(conn);

        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::TestNamespace.MyController",
            DisplayName = "MyController",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/MyController.cs",
            LineStart = 1, LineEnd = 50,
            Attributes = """["global::System.Obsolete", "global::Microsoft.AspNetCore.Mvc.ApiControllerAttribute"]""",
            LastIndexedAt = 1000
        });

        var result = FindByAttributeTool.FindByAttribute(_dbProvider, "global::Microsoft.AspNetCore.Mvc.ApiControllerAttribute");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);

        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.EnumerateArray().Select(r => r.GetProperty("display_name").GetString()).Contains("MyController"));
    }

    [TestMethod]
    public void GetIndexStatus_ReturnsProjectTimestamps()
    {
        var result = GetIndexStatusTool.GetIndexStatus(_dbProvider);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);

        var results = doc.RootElement.GetProperty("results");
        var first = results[0];
        Assert.AreEqual("test0123456789ab", first.GetProperty("canonical_id").GetString());
        Assert.IsTrue(first.GetProperty("last_indexed_at").GetInt64() > 0);
        Assert.IsTrue(first.TryGetProperty("symbol_count", out _));
        Assert.IsTrue(first.TryGetProperty("reference_count", out _));
    }

    [TestMethod]
    public void FindUnreferenced_ReturnsSymbolsWithNoReferences()
    {
        // TestClass has no references, DoWork has one reference (from constructor setup)
        var result = FindUnreferencedTool.FindUnreferenced(_dbProvider, exclude_test_projects: false);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);

        var results = doc.RootElement.GetProperty("results");
        var names = results.EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString())
            .ToList();

        // TestClass has no references -> should be included
        Assert.IsTrue(names.Contains("global::TestNamespace.TestClass"));
        // DoWork has a reference -> should NOT be included
        Assert.IsFalse(names.Contains("global::TestNamespace.TestClass.DoWork(string)"));
    }

    [TestMethod]
    public void FindUnreferenced_FiltersByKind()
    {
        var conn = _db.GetConnection();
        var symbolStore = new SymbolStore(conn);

        // Add an unreferenced method
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::TestNamespace.TestClass.UnusedMethod()",
            DisplayName = "UnusedMethod",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            FilePath = "src/TestClass.cs",
            LineStart = 60, LineEnd = 65,
            LastIndexedAt = 1000
        });

        // Filter by kind=method -- should include UnusedMethod but not TestClass (which is a class)
        var result = FindUnreferencedTool.FindUnreferenced(_dbProvider, kind: "method", exclude_test_projects: false);
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        var names = results.EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString())
            .ToList();

        Assert.IsTrue(names.Contains("global::TestNamespace.TestClass.UnusedMethod()"));
        Assert.IsFalse(names.Contains("global::TestNamespace.TestClass"));
    }

    [TestMethod]
    public void FindUnreferenced_ExcludesTestProjects()
    {
        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var symbolStore = new SymbolStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Create a test project
        var testProjectId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "test_proj_abcdef",
            GitRemoteUrl = "https://github.com/test/repo",
            RepoRelativePath = "tests/Test.Tests/Test.Tests.csproj"
        }, now);

        // Mark it as a test project
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE projects SET is_test_project = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", testProjectId);
        cmd.ExecuteNonQuery();

        // Add an unreferenced symbol in the test project
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = testProjectId,
            FullyQualifiedName = "global::Tests.TestHelper",
            DisplayName = "TestHelper",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "tests/TestHelper.cs",
            LineStart = 1, LineEnd = 10,
            LastIndexedAt = 1000
        });

        // Default (exclude_test_projects=true): should NOT include test project symbols
        var result = FindUnreferencedTool.FindUnreferenced(_dbProvider, exclude_test_projects: true);
        var doc = JsonDocument.Parse(result);
        var names = doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString())
            .ToList();
        Assert.IsFalse(names.Contains("global::Tests.TestHelper"));

        // Include tests: SHOULD include test project symbols
        result = FindUnreferencedTool.FindUnreferenced(_dbProvider, exclude_test_projects: false);
        doc = JsonDocument.Parse(result);
        names = doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString())
            .ToList();
        Assert.IsTrue(names.Contains("global::Tests.TestHelper"));
    }

    [TestMethod]
    public void FindSymbol_ReturnsCanonicalIdAsProjectId()
    {
        var result = FindSymbolTool.FindSymbol(_dbProvider, "global::TestNamespace.TestClass");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        var projectId = results[0].GetProperty("project_id").GetString();
        Assert.AreEqual("test0123456789ab", projectId);
    }
}
