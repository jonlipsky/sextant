using System.Diagnostics;
using Sextant.Core;

namespace Sextant.Store.Tests;

[TestClass]
public class StressTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private ProjectStore _projectStore = null!;
    private SymbolStore _symbolStore = null!;
    private ReferenceStore _referenceStore = null!;
    private RelationshipStore _relationshipStore = null!;
    private CallGraphStore _callGraphStore = null!;
    private ProjectDependencyStore _dependencyStore = null!;
    private ApiSurfaceStore _apiSurfaceStore = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_stress_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        _projectStore = new ProjectStore(conn);
        _symbolStore = new SymbolStore(conn);
        _referenceStore = new ReferenceStore(conn);
        _relationshipStore = new RelationshipStore(conn);
        _callGraphStore = new CallGraphStore(conn);
        _dependencyStore = new ProjectDependencyStore(conn);
        _apiSurfaceStore = new ApiSurfaceStore(conn);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [TestMethod]
    public void StressTest_MultipleSolutions_ThousandsOfSymbols()
    {
        // Simulate 5 solutions with 10 projects each, 100 symbols per project
        var projectIds = new List<long>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (var sol = 0; sol < 5; sol++)
        {
            for (var proj = 0; proj < 10; proj++)
            {
                var projectId = _projectStore.Insert(new ProjectIdentity
                {
                    CanonicalId = $"sol{sol}_proj{proj}_id",
                    GitRemoteUrl = $"https://github.com/org/solution{sol}",
                    RepoRelativePath = $"src/Project{proj}/Project{proj}.csproj"
                }, now);
                projectIds.Add(projectId);

                // Insert 100 symbols per project
                for (var sym = 0; sym < 100; sym++)
                {
                    _symbolStore.Insert(new SymbolInfo
                    {
                        ProjectId = projectId,
                        FullyQualifiedName = $"global::Sol{sol}.Proj{proj}.Class{sym}",
                        DisplayName = $"Class{sym}",
                        Kind = SymbolKind.Class,
                        Accessibility = Accessibility.Public,
                        FilePath = $"src/Project{proj}/Class{sym}.cs",
                        LineStart = 1,
                        LineEnd = 50,
                        LastIndexedAt = now
                    });
                }
            }
        }

        // Total: 50 projects, 5000 symbols
        Assert.AreEqual(50, projectIds.Count);

        // Verify exact FQN lookup is fast
        var sw = Stopwatch.StartNew();
        var result = _symbolStore.GetByFqn("global::Sol2.Proj5.Class42");
        sw.Stop();
        Assert.IsNotNull(result);
        Assert.IsTrue(sw.ElapsedMilliseconds < 50, $"Exact FQN lookup took {sw.ElapsedMilliseconds}ms");

        // Verify FTS5 search works across all symbols
        sw.Restart();
        var searchResults = _symbolStore.SearchFts("Class42", 100);
        sw.Stop();
        Assert.IsTrue(searchResults.Count >= 5); // Class42 in each of 5 solutions
        Assert.IsTrue(sw.ElapsedMilliseconds < 100, $"FTS search took {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public void StressTest_ComplexSubmoduleGraph()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Create a complex dependency graph:
        // App → LibA → SharedCore
        // App → LibB → SharedCore
        // App → SubmoduleX → SharedCore
        // LibA → SubmoduleY

        var sharedCoreId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "shared_core_id001",
            GitRemoteUrl = "https://github.com/org/shared-core",
            RepoRelativePath = "src/SharedCore/SharedCore.csproj"
        }, now);

        var libAId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "lib_a_id00000001",
            GitRemoteUrl = "https://github.com/org/app",
            RepoRelativePath = "src/LibA/LibA.csproj"
        }, now);

        var libBId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "lib_b_id00000001",
            GitRemoteUrl = "https://github.com/org/app",
            RepoRelativePath = "src/LibB/LibB.csproj"
        }, now);

        var subXId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "submodule_x_id01",
            GitRemoteUrl = "https://github.com/org/submodule-x",
            RepoRelativePath = "src/SubX/SubX.csproj"
        }, now);

        var subYId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "submodule_y_id01",
            GitRemoteUrl = "https://github.com/org/submodule-y",
            RepoRelativePath = "src/SubY/SubY.csproj"
        }, now);

        var appId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "app_id0000000001",
            GitRemoteUrl = "https://github.com/org/app",
            RepoRelativePath = "src/App/App.csproj"
        }, now);

        // Record dependencies
        _dependencyStore.Insert(new ProjectDependency { ConsumerProjectId = appId, DependencyProjectId = libAId, ReferenceKind = "project_ref" });
        _dependencyStore.Insert(new ProjectDependency { ConsumerProjectId = appId, DependencyProjectId = libBId, ReferenceKind = "project_ref" });
        _dependencyStore.Insert(new ProjectDependency { ConsumerProjectId = appId, DependencyProjectId = subXId, ReferenceKind = "submodule_ref", SubmodulePinnedCommit = "abc123" });
        _dependencyStore.Insert(new ProjectDependency { ConsumerProjectId = libAId, DependencyProjectId = sharedCoreId, ReferenceKind = "project_ref" });
        _dependencyStore.Insert(new ProjectDependency { ConsumerProjectId = libBId, DependencyProjectId = sharedCoreId, ReferenceKind = "project_ref" });
        _dependencyStore.Insert(new ProjectDependency { ConsumerProjectId = subXId, DependencyProjectId = sharedCoreId, ReferenceKind = "submodule_ref", SubmodulePinnedCommit = "def456" });
        _dependencyStore.Insert(new ProjectDependency { ConsumerProjectId = libAId, DependencyProjectId = subYId, ReferenceKind = "submodule_ref", SubmodulePinnedCommit = "ghi789" });

        // Verify dependency graph
        var appDeps = _dependencyStore.GetByConsumer(appId);
        Assert.AreEqual(3, appDeps.Count);

        // Verify reverse lookup: who depends on SharedCore?
        var sharedConsumers = _dependencyStore.GetByDependency(sharedCoreId);
        Assert.AreEqual(3, sharedConsumers.Count); // LibA, LibB, SubmoduleX

        // Add symbols to SharedCore and create API surface
        var coreSymbolIds = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            var symbolId = _symbolStore.Insert(new SymbolInfo
            {
                ProjectId = sharedCoreId,
                FullyQualifiedName = $"global::SharedCore.Service{i}",
                DisplayName = $"Service{i}",
                Kind = SymbolKind.Class,
                Accessibility = Accessibility.Public,
                Signature = $"public class Service{i}",
                SignatureHash = $"hash_service{i}_v1",
                FilePath = $"src/SharedCore/Service{i}.cs",
                LineStart = 1, LineEnd = 30,
                LastIndexedAt = now
            });
            coreSymbolIds.Add(symbolId);
        }

        // Capture API surface snapshot
        foreach (var symbolId in coreSymbolIds)
        {
            _apiSurfaceStore.Insert(new ApiSurfaceSnapshot
            {
                ProjectId = sharedCoreId,
                SymbolId = symbolId,
                SignatureHash = $"hash_service{symbolId}_v1",
                CapturedAt = now,
                GitCommit = "commit_v1"
            });
        }

        var snapshots = _apiSurfaceStore.GetByProjectAndCommit(sharedCoreId, "commit_v1");
        Assert.AreEqual(50, snapshots.Count);

        // Add cross-project references
        for (var i = 0; i < 20; i++)
        {
            _referenceStore.Insert(new ReferenceInfo
            {
                SymbolId = coreSymbolIds[i],
                InProjectId = libAId,
                FilePath = $"src/LibA/Usage{i}.cs",
                Line = 10,
                ContextSnippet = $"var s = new Service{i}();",
                ReferenceKind = ReferenceKind.ObjectCreation
            });
        }

        for (var i = 0; i < 15; i++)
        {
            _referenceStore.Insert(new ReferenceInfo
            {
                SymbolId = coreSymbolIds[i],
                InProjectId = libBId,
                FilePath = $"src/LibB/Usage{i}.cs",
                Line = 10,
                ContextSnippet = $"var s = new Service{i}();",
                ReferenceKind = ReferenceKind.ObjectCreation
            });
        }

        // Verify cross-project references
        var refs = _referenceStore.GetBySymbolId(coreSymbolIds[0]);
        Assert.AreEqual(2, refs.Count); // Referenced from both LibA and LibB
    }

    [TestMethod]
    public void StressTest_DeepCallGraph()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var projectId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "callgraph_stress1",
            GitRemoteUrl = "https://github.com/org/callgraph",
            RepoRelativePath = "src/CallGraph/CallGraph.csproj"
        }, now);

        // Create a chain of 20 method calls: M0 -> M1 -> M2 -> ... -> M19
        var methodIds = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            var id = _symbolStore.Insert(new SymbolInfo
            {
                ProjectId = projectId,
                FullyQualifiedName = $"global::App.Handler.Method{i}()",
                DisplayName = $"Method{i}",
                Kind = SymbolKind.Method,
                Accessibility = Accessibility.Public,
                FilePath = "src/CallGraph/Handler.cs",
                LineStart = i * 10, LineEnd = i * 10 + 5,
                LastIndexedAt = now
            });
            methodIds.Add(id);
        }

        for (var i = 0; i < methodIds.Count - 1; i++)
        {
            _callGraphStore.Insert(new CallGraphEdge
            {
                CallerSymbolId = methodIds[i],
                CalleeSymbolId = methodIds[i + 1],
                CallSiteFile = "src/CallGraph/Handler.cs",
                CallSiteLine = i * 10 + 3
            });
        }

        // Also add fan-out: M0 calls M5, M10, M15 directly
        _callGraphStore.Insert(new CallGraphEdge { CallerSymbolId = methodIds[0], CalleeSymbolId = methodIds[5], CallSiteFile = "src/CallGraph/Handler.cs", CallSiteLine = 4 });
        _callGraphStore.Insert(new CallGraphEdge { CallerSymbolId = methodIds[0], CalleeSymbolId = methodIds[10], CallSiteFile = "src/CallGraph/Handler.cs", CallSiteLine = 5 });
        _callGraphStore.Insert(new CallGraphEdge { CallerSymbolId = methodIds[0], CalleeSymbolId = methodIds[15], CallSiteFile = "src/CallGraph/Handler.cs", CallSiteLine = 6 });

        // Verify callee lookup
        var callees = _callGraphStore.GetByCaller(methodIds[0]);
        Assert.AreEqual(4, callees.Count); // M1, M5, M10, M15

        // Verify caller lookup
        var callers = _callGraphStore.GetByCallee(methodIds[1]);
        Assert.AreEqual(1, callers.Count);

        // Verify deep chain still returns results
        var deepCallees = _callGraphStore.GetByCaller(methodIds[18]);
        Assert.AreEqual(1, deepCallees.Count); // Only M19
    }

    [TestMethod]
    public void StressTest_BreakingChangeDetection_LargeApiSurface()
    {
        // Simulate breaking change detection on 200 symbol API surface
        var oldSurface = new List<(string fqn, string signatureHash, string accessibility)>();
        var newSurface = new List<(string fqn, string signatureHash, string accessibility)>();

        for (var i = 0; i < 200; i++)
        {
            var fqn = $"global::Api.Class{i}";
            oldSurface.Add((fqn, $"hash_v1_{i}", "public"));

            if (i < 190) // 10 symbols removed
            {
                if (i == 50) // 1 signature changed
                    newSurface.Add((fqn, $"hash_v2_{i}", "public"));
                else if (i == 100) // 1 accessibility reduced
                    newSurface.Add((fqn, $"hash_v1_{i}", "internal"));
                else
                    newSurface.Add((fqn, $"hash_v1_{i}", "public"));
            }
        }

        // Add 5 new symbols
        for (var i = 200; i < 205; i++)
            newSurface.Add(($"global::Api.Class{i}", $"hash_v1_{i}", "public"));

        var sw = Stopwatch.StartNew();
        var changes = BreakingChangeDetector.DetectChanges(oldSurface, newSurface);
        var overall = BreakingChangeDetector.GetOverallClassification(changes);
        sw.Stop();

        Assert.AreEqual(ChangeClassification.Breaking, overall);

        var breaking = changes.Where(c => c.Classification == ChangeClassification.Breaking).ToList();
        Assert.AreEqual(12, breaking.Count); // 10 removed + 1 signature + 1 accessibility

        var additive = changes.Where(c => c.Classification == ChangeClassification.Additive).ToList();
        Assert.AreEqual(5, additive.Count);

        Assert.IsTrue(sw.ElapsedMilliseconds < 100, $"Breaking change detection took {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public void StressTest_ConcurrentReads_DuringWrites()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var projectId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "concurrent_test01",
            GitRemoteUrl = "https://github.com/org/concurrent",
            RepoRelativePath = "src/App/App.csproj"
        }, now);

        // Insert symbols
        for (var i = 0; i < 100; i++)
        {
            _symbolStore.Insert(new SymbolInfo
            {
                ProjectId = projectId,
                FullyQualifiedName = $"global::Concurrent.Class{i}",
                DisplayName = $"Class{i}",
                Kind = SymbolKind.Class,
                Accessibility = Accessibility.Public,
                FilePath = $"src/App/Class{i}.cs",
                LineStart = 1, LineEnd = 10,
                LastIndexedAt = now
            });
        }

        // Concurrent reads while inserting more data
        var readTask = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                var result = _symbolStore.GetByFqn($"global::Concurrent.Class{i}");
                Assert.IsNotNull(result);
            }
        });

        var writeTask = Task.Run(() =>
        {
            for (var i = 100; i < 150; i++)
            {
                _symbolStore.Insert(new SymbolInfo
                {
                    ProjectId = projectId,
                    FullyQualifiedName = $"global::Concurrent.Class{i}",
                    DisplayName = $"Class{i}",
                    Kind = SymbolKind.Class,
                    Accessibility = Accessibility.Public,
                    FilePath = $"src/App/Class{i}.cs",
                    LineStart = 1, LineEnd = 10,
                    LastIndexedAt = now
                });
            }
        });

        Task.WaitAll(readTask, writeTask);
        // No exceptions = WAL mode works correctly
    }
}
