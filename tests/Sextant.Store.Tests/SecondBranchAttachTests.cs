using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 17 slice 2, fold issue #62: two branches at the SAME commit share ONE immutable snapshot (branch
/// name is not part of snapshot identity). The first branch indexes and points at it; a second branch that
/// merely ATTACHES must get its OWN pointer so it (a) resolves to the snapshot and (b) protects it from
/// retention — WITHOUT demoting the real default branch or superseding the first branch's target (criterion
/// 4 protected-set correctness).
/// </summary>
[TestClass]
public class SecondBranchAttachTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _repo;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_branch2_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repo = _snapshots.EnsureRepository("https://github.com/org/app", _now);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void SecondBranchAtSameCommit_GetsOwnPointer_WithoutDisturbingTheDefault()
    {
        var run = CompleteRun();
        var snap = Snapshot(run, "shared-commit");

        // 'main' is the indexed default branch pointing at the shared snapshot.
        var main = _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
        _snapshots.SetBranchPointer(main, snap, _now);

        // 'feature' is a SECOND branch at the same commit that only attaches (no re-index).
        var feature = _snapshots.AttachBranchPointer(_repo, "feature", snap, _now);

        Assert.AreNotEqual(main, feature, "the second branch is a distinct branch row");
        Assert.AreEqual(main, _snapshots.GetDefaultBranchId(_repo), "the real default branch is never demoted (#62)");
        Assert.AreEqual(snap, BranchSnapshot(feature), "the attached branch has its own pointer to the shared snapshot");
        Assert.AreEqual(snap, BranchSnapshot(main), "the first branch's pointer is not superseded");
        CollectionAssert.Contains(_snapshots.GetBranchPointedSnapshotIds().ToList(), snap,
            "the shared snapshot is branch-pointed (so it is retention-protected)");
    }

    [TestMethod]
    public void AttachingAnAlreadyDefaultBranch_PreservesItsDefaultFlag()
    {
        var run = CompleteRun();
        var snap = Snapshot(run, "shared-commit");
        var main = _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
        _snapshots.SetBranchPointer(main, snap, _now);

        // Re-attaching 'main' (idempotent) must not clear is_default via ON CONFLICT DO NOTHING.
        _snapshots.AttachBranchPointer(_repo, "main", snap, _now);

        Assert.AreEqual(main, _snapshots.GetDefaultBranchId(_repo), "attach never demotes an existing default branch (#62)");
    }

    private long CompleteRun()
    {
        var runStore = new IndexRunStore(_conn);
        var id = runStore.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(id, _now, 1);
        return id;
    }

    private long Snapshot(long runId, string commit)
    {
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = "https://github.com/org/app",
            CommitSha = commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = _snapshots.BeginPending(identity, _repo, null, runId, _now);
        _snapshots.MarkComplete(id, _now);
        return id;
    }

    private long? BranchSnapshot(long branchId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_id FROM branches WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", branchId);
        var v = cmd.ExecuteScalar();
        return v is long l ? l : null;
    }
}
