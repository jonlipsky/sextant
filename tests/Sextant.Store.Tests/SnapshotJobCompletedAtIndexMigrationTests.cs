using Microsoft.Data.Sqlite;

namespace Sextant.Store.Tests;

[TestClass]
public class SnapshotJobCompletedAtIndexMigrationTests
{
    private string _dbPath = null!;

    [TestInitialize]
    public void TestInitialize() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_test_{Guid.NewGuid():N}.db");

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath);

    [TestMethod]
    public void Migration027_AddsCompletionTimeIndexWithoutChangingSnapshotIdentity()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();

        Assert.AreEqual("completed_at", IndexColumns(db.GetConnection(), "ix_snapshot_jobs_completed_at"));
        Assert.IsTrue(IndexDatabase.LatestSchemaVersion >= 27, "027 is applied by the migration chain");
        Assert.AreEqual(26, IndexDatabase.SnapshotSchemaVersion);
        Assert.Contains(27, IndexDatabase.IdentityNeutralMigrations);
    }

    [TestMethod]
    public void RecentTerminalQuery_UsesCompletionTimeIndex()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();

        using var cmd = db.GetConnection().CreateCommand();
        cmd.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT status, COUNT(*)
            FROM snapshot_jobs INDEXED BY ix_snapshot_jobs_completed_at
            WHERE completed_at >= @start AND completed_at < @end
              AND status IN ('complete', 'partial', 'failed', 'unsupported', 'cancelled')
            GROUP BY status;
            """;
        cmd.Parameters.AddWithValue("@start", 100);
        cmd.Parameters.AddWithValue("@end", 200);
        using var reader = cmd.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
            plan.Add(reader.GetString(3));

        Assert.IsTrue(plan.Any(line => line.Contains("ix_snapshot_jobs_completed_at", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, plan));
    }

    private static string? IndexColumns(SqliteConnection conn, string index)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_index_xinfo(@index) WHERE key = 1 ORDER BY seqno;";
        cmd.Parameters.AddWithValue("@index", index);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }
}
