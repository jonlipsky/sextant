namespace Sextant.Store.Tests;

/// <summary>
/// Issue #267: job timings live in <c>snapshot_job_timings</c> (migration 028), an identity-neutral job-telemetry table:
/// one JSON document per job, replaced per attempt, read back newest first, deleted with its job.
/// </summary>
[TestClass]
public class SnapshotJobTimingsTests
{
    private string _dbPath = null!;

    [TestInitialize]
    public void TestInitialize() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_test_{Guid.NewGuid():N}.db");

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath);

    [TestMethod]
    public void Migration028_IsIdentityNeutral_AndCreatesTheTelemetryTable()
    {
        Assert.Contains(28, IndexDatabase.IdentityNeutralMigrations);
        Assert.Contains("snapshot_job_timings", IndexDatabase.ServiceJobTables);
        Assert.AreEqual(26, IndexDatabase.SnapshotSchemaVersion, "adding job telemetry re-indexes nothing");

        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        Assert.AreEqual(IndexDatabase.LatestSchemaVersion, db.CurrentSchemaVersion);
    }

    [TestMethod]
    public void Timings_AreReplacedPerJob_ReadBackNewestFirst_AndDeletedWithTheJob()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var jobs = new SnapshotJobStore(db.GetConnection());
        var (first, _) = jobs.EnsureJob("identity-1", "https://example.test/a.git", "aaaa", "main");
        var (second, _) = jobs.EnsureJob("identity-2", "https://example.test/a.git", "bbbb", "main");

        Assert.IsNull(jobs.GetTimings(first.Id));
        jobs.ReplaceTimings(first.Id, """{"total_ms":1}""");
        jobs.ReplaceTimings(first.Id, """{"total_ms":2}""");
        Thread.Sleep(5);
        jobs.ReplaceTimings(second.Id, """{"total_ms":3}""");

        Assert.AreEqual("""{"total_ms":2}""", jobs.GetTimings(first.Id), "a later attempt replaces the earlier one");
        CollectionAssert.AreEqual(new[] { """{"total_ms":3}""", """{"total_ms":2}""" }, jobs.RecentTimings(10).ToArray());
        Assert.HasCount(1, jobs.RecentTimings(1));

        using (var delete = db.GetConnection().CreateCommand())
        {
            delete.CommandText = "DELETE FROM snapshot_jobs WHERE id = @id;";
            delete.Parameters.AddWithValue("@id", first.Id);
            delete.ExecuteNonQuery();
        }
        Assert.IsNull(jobs.GetTimings(first.Id), "the timings go with their job (ON DELETE CASCADE)");
    }
}
