using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 7 — criterion 6 (rebuild gate). Opening an index built at an older, incompatible schema, or a
/// compact schema that has never been re-populated by a full run, must surface an actionable rebuild
/// message and never be mistaken for a complete new-generation index. The signals are the
/// <c>schema_version</c> and the <c>index_runs</c> last-complete pointer.
/// </summary>
[TestClass]
public class RebuildGateTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_rebuild_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void FreshlyMigratedIndex_WithNoData_IsNotReady()
    {
        // Migration 011 recreates the compact schema empty and clears index_runs (the rebuild gate).
        var readiness = _db.CheckReadiness();

        Assert.IsFalse(readiness.Ready, "a compact schema with no complete generation must not be servable");
        Assert.IsNotNull(readiness.Message);
        StringAssert.Contains(readiness.Message!, "full index", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void PopulatedIndex_IsReady()
    {
        var conn = _db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var projectId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "readyproj12345678",
            GitRemoteUrl = "https://github.com/test/repo",
            RepoRelativePath = "src/Test/Test.csproj"
        }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        new SymbolStore(conn).Insert(new SymbolInfo
        {
            ProjectId = projectId,
            SymbolKey = "global::Test.Ready",
            FullyQualifiedName = "global::Test.Ready",
            DisplayName = "Ready",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/Ready.cs",
            LineStart = 1,
            LineEnd = 10,
            LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var readiness = _db.CheckReadiness();

        Assert.IsTrue(readiness.Ready, "an index with symbols present is servable");
        Assert.IsNull(readiness.Message);
    }

    [TestMethod]
    public void OlderSchemaIndex_SurfacesRebuildMessage()
    {
        // Simulate opening an index built before the latest migration: roll the recorded schema
        // version back below what this build embeds.
        var conn = _db.GetConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM schema_version WHERE version = (SELECT MAX(version) FROM schema_version);";
            cmd.ExecuteNonQuery();
        }

        Assert.IsTrue(_db.CurrentSchemaVersion < IndexDatabase.LatestSchemaVersion,
            "precondition: the recorded schema is now older than this build");

        var readiness = _db.CheckReadiness();

        Assert.IsFalse(readiness.Ready);
        StringAssert.Contains(readiness.Message!, "older Sextant schema", StringComparison.OrdinalIgnoreCase);
    }
}
