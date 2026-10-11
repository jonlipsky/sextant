using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>Issue #303, migration 031: retention's API-history discovery and per-commit delete use ix_api_surface_git_commit.</summary>
[TestClass]
public class ApiSurfaceGitCommitIndexTests
{
    private string _dbPath = null!;

    [TestInitialize]
    public void TestInitialize() => _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_apiidx_{Guid.NewGuid():N}.db");

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath);

    [TestMethod]
    public void Migration031_IsIdentityNeutral()
    {
        Assert.Contains(31, IndexDatabase.IdentityNeutralMigrations);
        Assert.AreEqual(26, IndexDatabase.SnapshotSchemaVersion);
        Assert.IsGreaterThanOrEqualTo(31, IndexDatabase.LatestSchemaVersion);
    }

    [TestMethod]
    public void CommitDiscoveryAndPerCommitDelete_UseTheIndex()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();

        var discovery = QueryPlan.Explain(conn, """
            SELECT git_commit, MAX(captured_at) AS m
            FROM api_surface_snapshots
            GROUP BY git_commit
            ORDER BY m DESC, git_commit DESC;
            """);
        Assert.IsTrue(discovery.Any(l => l.Contains("ix_api_surface_git_commit")), string.Join("\n", discovery));
        Assert.IsFalse(discovery.Any(l => l.StartsWith("SCAN api_surface_snapshots") && !l.Contains("USING")), string.Join("\n", discovery));

        var delete = QueryPlan.Explain(conn,
            "DELETE FROM api_surface_snapshots WHERE id IN (SELECT id FROM api_surface_snapshots WHERE git_commit = 'c' LIMIT 256);");
        Assert.IsTrue(delete.Any(l => l.Contains("SEARCH") && l.Contains("ix_api_surface_git_commit")), string.Join("\n", delete));
    }
}
