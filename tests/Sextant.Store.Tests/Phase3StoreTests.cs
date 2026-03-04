using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

[TestClass]
public class Phase3StoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private long _projectA;
    private long _projectB;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_p3_test_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();

        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _projectA = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "projA_0123456789",
            GitRemoteUrl = "https://github.com/org/repoA",
            RepoRelativePath = "src/A/A.csproj"
        }, now);

        _projectB = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "projB_0123456789",
            GitRemoteUrl = "https://github.com/org/repoB",
            RepoRelativePath = "src/B/B.csproj"
        }, now);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [TestMethod]
    public void ProjectDependencyStore_RecordsAllReferenceKinds()
    {
        var conn = _db.GetConnection();
        var store = new ProjectDependencyStore(conn);

        // project_ref
        store.Insert(new ProjectDependency
        {
            ConsumerProjectId = _projectA,
            DependencyProjectId = _projectB,
            ReferenceKind = "project_ref"
        });

        var deps = store.GetByConsumer(_projectA);
        Assert.AreEqual(1, deps.Count);
        Assert.AreEqual("project_ref", deps[0].ReferenceKind);
        Assert.IsNull(deps[0].SubmodulePinnedCommit);

        // Update to submodule_ref with pinned commit
        store.Insert(new ProjectDependency
        {
            ConsumerProjectId = _projectA,
            DependencyProjectId = _projectB,
            ReferenceKind = "submodule_ref",
            SubmodulePinnedCommit = "abc123def456"
        });

        deps = store.GetByConsumer(_projectA);
        Assert.AreEqual(1, deps.Count); // upsert, not duplicate
        Assert.AreEqual("submodule_ref", deps[0].ReferenceKind);
        Assert.AreEqual("abc123def456", deps[0].SubmodulePinnedCommit);

        // Verify reverse lookup
        var consumers = store.GetByDependency(_projectB);
        Assert.AreEqual(1, consumers.Count);
        Assert.AreEqual(_projectA, consumers[0].ConsumerProjectId);
    }

    [TestMethod]
    public void ApiSurfaceStore_CapturesPublicProtectedSymbols()
    {
        var conn = _db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var apiStore = new ApiSurfaceStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var symId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectA,
            FullyQualifiedName = "global::A.MyClass",
            DisplayName = "MyClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            Signature = "public class MyClass",
            SignatureHash = "hash123",
            FilePath = "src/MyClass.cs",
            LineStart = 1,
            LineEnd = 10,
            LastIndexedAt = now
        });

        apiStore.Insert(new ApiSurfaceSnapshot
        {
            ProjectId = _projectA,
            SymbolId = symId,
            SignatureHash = "hash123",
            CapturedAt = now,
            GitCommit = "commit1"
        });

        var snapshots = apiStore.GetByProjectAndCommit(_projectA, "commit1");
        Assert.AreEqual(1, snapshots.Count);
        Assert.AreEqual(symId, snapshots[0].SymbolId);
        Assert.AreEqual("hash123", snapshots[0].SignatureHash);
        Assert.AreEqual("commit1", snapshots[0].GitCommit);
    }

    [TestMethod]
    public void BreakingChangeDetection_SymbolRemoved()
    {
        var old = new List<(string fqn, string signatureHash, string accessibility)>
        {
            ("A.Foo", "hash1", "public")
        };
        var @new = new List<(string fqn, string signatureHash, string accessibility)>();

        var changes = BreakingChangeDetector.DetectChanges(old, @new);
        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(ChangeClassification.Breaking, changes[0].Classification);
        Assert.AreEqual("Symbol removed", changes[0].Reason);
    }

    [TestMethod]
    public void BreakingChangeDetection_SignatureChanged()
    {
        var old = new List<(string fqn, string signatureHash, string accessibility)>
        {
            ("A.Foo", "hash1", "public")
        };
        var @new = new List<(string fqn, string signatureHash, string accessibility)>
        {
            ("A.Foo", "hash2", "public")
        };

        var changes = BreakingChangeDetector.DetectChanges(old, @new);
        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(ChangeClassification.Breaking, changes[0].Classification);
        Assert.AreEqual("Signature changed", changes[0].Reason);
    }

    [TestMethod]
    public void BreakingChangeDetection_AccessibilityReduced()
    {
        var old = new List<(string fqn, string signatureHash, string accessibility)>
        {
            ("A.Foo", "hash1", "public")
        };
        var @new = new List<(string fqn, string signatureHash, string accessibility)>
        {
            ("A.Foo", "hash1", "internal")
        };

        var changes = BreakingChangeDetector.DetectChanges(old, @new);
        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(ChangeClassification.Breaking, changes[0].Classification);
        Assert.AreEqual("Accessibility reduced", changes[0].Reason);
    }

    [TestMethod]
    public void BreakingChangeDetection_SymbolAdded_IsAdditive()
    {
        var old = new List<(string fqn, string signatureHash, string accessibility)>
        {
            ("A.Foo", "hash1", "public")
        };
        var @new = new List<(string fqn, string signatureHash, string accessibility)>
        {
            ("A.Foo", "hash1", "public"),
            ("A.Bar", "hash2", "public")
        };

        var changes = BreakingChangeDetector.DetectChanges(old, @new);
        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(ChangeClassification.Additive, changes[0].Classification);
        Assert.AreEqual("Symbol added", changes[0].Reason);
    }

    [TestMethod]
    public void SharedProject_Deduplicated_ByCanonicalId()
    {
        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Insert the same project identity from two different "parents"
        var id1 = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "shared_proj_1234",
            GitRemoteUrl = "https://github.com/org/shared",
            RepoRelativePath = "src/Shared/Shared.csproj",
            DiskPath = "/parent1/submodules/shared/src/Shared/Shared.csproj"
        }, now);

        var id2 = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "shared_proj_1234",
            GitRemoteUrl = "https://github.com/org/shared",
            RepoRelativePath = "src/Shared/Shared.csproj",
            DiskPath = "/parent2/submodules/shared/src/Shared/Shared.csproj"
        }, now);

        // Same canonical_id → same row (upsert)
        Assert.AreEqual(id1, id2);

        // Only one entry in the database
        var all = projectStore.GetAll();
        var shared = all.Count(p => p.project.CanonicalId == "shared_proj_1234");
        Assert.AreEqual(1, shared);
    }
}
