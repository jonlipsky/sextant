using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #53: a provider snapshot GAINING a project after publish must NEVER mutate a COMPLETE snapshot in
/// place. When a later consumer references a submodule project no prior consumer pinned, the orchestrator
/// re-opens the provider as a PENDING staging generation and republishes it atomically inside the final
/// write transaction — so a concurrent reader observes the previous complete provider or the grown one,
/// never a half-added project on a "complete" snapshot, and a crash mid-growth leaves the previously
/// published provider intact rather than half-grown. This test encodes that contract at the store level
/// (the guarded pending→complete publish + transactional atomicity the orchestrator relies on).
/// </summary>
[TestClass]
public class ProviderGrowthImmutabilityTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _now;
    private long _providerSnapshot;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_provgrow_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var repoId = _snapshots.EnsureRepository("https://github.com/org/lib", _now);
        var runId = BeginCompleteRun();
        var identity = ProviderIdentity(repoId);
        (_providerSnapshot, _, _) = _snapshots.BeginPending(identity, repoId, null, runId, _now, isProvider: true);

        // Publish the provider with project A only.
        AddProject(_providerSnapshot, "A");
        Assert.AreEqual(1, _snapshots.MarkComplete(_providerSnapshot, _now), "provider publishes as complete");
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void MarkComplete_OnAlreadyCompleteProvider_IsGuardedNoOp()
    {
        // The guarded pending→complete flip refuses to re-publish a COMPLETE snapshot, so a caller can
        // never grow-then-"complete" a published provider without first re-opening it to pending.
        Assert.AreEqual(0, _snapshots.MarkComplete(_providerSnapshot, _now + 1),
            "a complete provider cannot be re-published in place");
    }

    [TestMethod]
    public void GrowthCommitted_RepublishesAtomically_PreservingProjectA()
    {
        Exec("BEGIN IMMEDIATE;");
        _snapshots.MarkStatus(_providerSnapshot, SnapshotStatus.Pending);
        AddProject(_providerSnapshot, "B");
        Assert.AreEqual(1, _snapshots.MarkComplete(_providerSnapshot, _now + 2),
            "the re-opened provider republishes as complete");
        Exec("COMMIT;");

        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(_providerSnapshot)!.Status);
        Assert.IsTrue(HasProject(_providerSnapshot, "A"), "project A's original row is preserved");
        Assert.IsTrue(HasProject(_providerSnapshot, "B"), "the late-referenced project B is added");
    }

    [TestMethod]
    public void GrowthRolledBack_LeavesPreviouslyPublishedProviderIntact()
    {
        // Simulate a crash mid-growth: re-open + add project B, then roll back instead of committing.
        Exec("BEGIN IMMEDIATE;");
        _snapshots.MarkStatus(_providerSnapshot, SnapshotStatus.Pending);
        AddProject(_providerSnapshot, "B");
        Exec("ROLLBACK;");

        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(_providerSnapshot)!.Status,
            "a crashed growth leaves the previously published provider COMPLETE, not half-grown");
        Assert.IsTrue(HasProject(_providerSnapshot, "A"), "project A survives the rolled-back growth");
        Assert.IsFalse(HasProject(_providerSnapshot, "B"), "the half-added project B is not observable");
    }

    private long BeginCompleteRun()
    {
        var runStore = new IndexRunStore(_conn);
        var id = runStore.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(id, _now, 1);
        return id;
    }

    private static SnapshotIdentity ProviderIdentity(long repoId) => new()
    {
        RepositoryRemoteUrl = $"repo-{repoId}",
        CommitSha = "pinned-provider-commit",
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = "test-analyzer",
        ToolchainFingerprint = "test-toolchain"
    };

    private void AddProject(long snapshotId, string tag)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at, snapshot_id)
            VALUES (@c, @g, @p, @now, @snap);
            """;
        cmd.Parameters.AddWithValue("@c", $"proj_{tag}_{snapshotId}");
        cmd.Parameters.AddWithValue("@g", "https://github.com/org/lib");
        cmd.Parameters.AddWithValue("@p", $"src/{tag}/{tag}.csproj");
        cmd.Parameters.AddWithValue("@now", _now);
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        cmd.ExecuteNonQuery();
    }

    private bool HasProject(long snapshotId, string tag)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projects WHERE snapshot_id = @snap AND canonical_id = @c;";
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        cmd.Parameters.AddWithValue("@c", $"proj_{tag}_{snapshotId}");
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
