using Sextant.Core;

namespace Sextant.Store.Tests;

[TestClass]
public class SymbolKeyStoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private ProjectStore _projectStore = null!;
    private SymbolStore _symbolStore = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_key_test_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        _projectStore = new ProjectStore(conn);
        _symbolStore = new SymbolStore(conn);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    private long InsertProject(string suffix)
        => _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = $"proj{suffix}".PadRight(16, '0')[..16],
            GitRemoteUrl = "https://github.com/test/repo",
            RepoRelativePath = $"src/{suffix}/{suffix}.csproj"
        }, 1000);

    private static SymbolInfo Symbol(long projectId, string key, string fqn, string display) => new()
    {
        ProjectId = projectId,
        SymbolKey = key,
        FullyQualifiedName = fqn,
        DisplayName = display,
        Kind = SymbolKind.Method,
        Accessibility = Accessibility.Public,
        FilePath = "src/File.cs",
        LineStart = 1,
        LineEnd = 2,
        LastIndexedAt = 1000
    };

    [TestMethod]
    public void Insert_OverloadsShareFqn_ButBothPersistUnderDistinctKeys()
    {
        var projectId = InsertProject("a");

        var id1 = _symbolStore.Insert(Symbol(projectId,
            "M:Ns.Calc.Add(System.Int32,System.Int32)", "global::Ns.Calc.Add", "Add"));
        var id2 = _symbolStore.Insert(Symbol(projectId,
            "M:Ns.Calc.Add(System.Double,System.Double)", "global::Ns.Calc.Add", "Add"));

        Assert.AreNotEqual(id1, id2, "Overloads must be stored as separate rows, not overwrite each other.");

        var matches = _symbolStore.ResolveByFqn("global::Ns.Calc.Add");
        Assert.AreEqual(2, matches.Count, "Both overloads must survive under the shared display FQN.");
    }

    [TestMethod]
    public void Insert_SameProjectAndKey_UpsertsToOneRow()
    {
        var projectId = InsertProject("b");

        var id1 = _symbolStore.Insert(Symbol(projectId, "T:Ns.Type", "global::Ns.Type", "Type"));
        var id2 = _symbolStore.Insert(Symbol(projectId, "T:Ns.Type", "global::Ns.Type", "Type"));

        Assert.AreEqual(id1, id2, "Re-inserting the same (project, key) must update the existing row.");
        Assert.AreEqual(1, _symbolStore.ResolveByFqn("global::Ns.Type").Count);
    }

    [TestMethod]
    public void ResolveByFqn_SameFqnInTwoProjects_ReturnsBothOrdered()
    {
        var projectA = InsertProject("a");
        var projectB = InsertProject("b");

        // Identical declaration key in two projects — legitimate, distinguished by project.
        _symbolStore.Insert(Symbol(projectB, "T:Ns.Service", "global::Ns.Service", "Service"));
        _symbolStore.Insert(Symbol(projectA, "T:Ns.Service", "global::Ns.Service", "Service"));

        var all = _symbolStore.ResolveByFqn("global::Ns.Service");
        Assert.AreEqual(2, all.Count, "Both projects' definitions must be reported (ambiguous).");
        CollectionAssert.AreEqual(
            new[] { Math.Min(projectA, projectB), Math.Max(projectA, projectB) },
            all.Select(s => s.ProjectId).ToArray(),
            "Candidates must be ordered deterministically by project id.");

        var scoped = _symbolStore.ResolveByFqn("global::Ns.Service", projectA);
        Assert.AreEqual(1, scoped.Count);
        Assert.AreEqual(projectA, scoped[0].ProjectId);
    }

    [TestMethod]
    public void GetBySymbolKey_IsScopedToProject()
    {
        var projectA = InsertProject("a");
        var projectB = InsertProject("b");

        var idA = _symbolStore.Insert(Symbol(projectA, "T:Ns.Service", "global::Ns.Service", "Service"));
        var idB = _symbolStore.Insert(Symbol(projectB, "T:Ns.Service", "global::Ns.Service", "Service"));

        Assert.AreEqual(idA, _symbolStore.GetBySymbolKey("T:Ns.Service", projectA)!.Id);
        Assert.AreEqual(idB, _symbolStore.GetBySymbolKey("T:Ns.Service", projectB)!.Id);
    }

    [TestMethod]
    public void GetByFqn_UnambiguousLookup_ReturnsSingleRow()
    {
        var projectId = InsertProject("a");
        _symbolStore.Insert(Symbol(projectId, "T:Ns.Only", "global::Ns.Only", "Only"));

        var result = _symbolStore.GetByFqn("global::Ns.Only");
        Assert.IsNotNull(result);
        Assert.AreEqual("Only", result.DisplayName);
    }
}
