using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #162 (a): the branch-pointer decision for a NULL-sequence service ensure that REUSES an
/// already-published snapshot (<see cref="SnapshotStore.AttachOrUpgradeBranchPointer"/>). It must converge on
/// the worker path's outcome whenever that cannot regress history: re-point the branch when the current
/// target is the SAME commit under a different identity (an AnalyzerVersion bump, a Phase-12 provider
/// snapshot reused by a direct ensure) or is no longer usable, and supersede the old head like the worker's
/// advance. Otherwise it stays attach-if-unset (#62): an older-commit reuse never rolls a branch back.
/// </summary>
[TestClass]
public class NullSequenceReusePointerTests
{
    private const string Repo = "https://github.com/org/app";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _repo;
    private long _run;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_nullseq_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repo = _snapshots.EnsureRepository(Repo, _now);
        var runStore = new IndexRunStore(_conn);
        _run = runStore.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(_run, _now, 1);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void SameCommit_NewIdentity_RepointsAndSupersedesTheOldHead()
    {
        var old = Snapshot("commit-A", analyzer: "1");
        var upgraded = Snapshot("commit-A", analyzer: "4");
        var main = PointMain(old);

        var branch = _snapshots.AttachOrUpgradeBranchPointer(_repo, "main", upgraded, _now + 1);

        Assert.AreEqual(main, branch);
        Assert.AreEqual(upgraded, _snapshots.GetBranchSnapshotId(main),
            "a same-commit identity change re-points the branch, as the worker path would (#162)");
        Assert.AreEqual(SnapshotStatus.Superseded, _snapshots.GetById(old)!.Status,
            "the old head is superseded exactly like the worker path's advance");
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(upgraded)!.Status);
    }

    [TestMethod]
    public void OlderCommit_NullSequence_StillDeclines()
    {
        var older = Snapshot("commit-A", analyzer: "4");
        var newer = Snapshot("commit-B", analyzer: "4");
        var main = PointMain(newer);

        _snapshots.AttachOrUpgradeBranchPointer(_repo, "main", older, _now + 1);

        Assert.AreEqual(newer, _snapshots.GetBranchSnapshotId(main),
            "without a sequence the service cannot order two commits, so it never rolls a branch back (#62)");
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(newer)!.Status, "the current head is untouched");
    }

    [TestMethod]
    public void DifferentCommit_EvenWithNewerIdentity_Declines()
    {
        var headOnB = Snapshot("commit-B", analyzer: "1");
        var otherCommit = Snapshot("commit-C", analyzer: "4");
        var main = PointMain(headOnB);

        _snapshots.AttachOrUpgradeBranchPointer(_repo, "main", otherCommit, _now + 1);

        Assert.AreEqual(headOnB, _snapshots.GetBranchSnapshotId(main),
            "only the SAME commit may re-point a null-sequence branch; a newer analyzer does not order commits");
    }

    [TestMethod]
    public void UnusableCurrentTarget_IsReplaced_WithoutRewritingItsStatus()
    {
        var stale = Snapshot("commit-A", analyzer: "4");
        var fresh = Snapshot("commit-B", analyzer: "4");
        var main = PointMain(stale);
        _snapshots.MarkStatus(stale, SnapshotStatus.Superseded);

        _snapshots.AttachOrUpgradeBranchPointer(_repo, "main", fresh, _now + 1);

        Assert.AreEqual(fresh, _snapshots.GetBranchSnapshotId(main),
            "a branch stranded on a superseded snapshot resolves nothing, so any complete target replaces it");
        Assert.AreEqual(SnapshotStatus.Superseded, _snapshots.GetById(stale)!.Status);
    }

    [TestMethod]
    public void ReclaimedCurrentTarget_IsReplaced()
    {
        var reclaimed = Snapshot("commit-A", analyzer: "4");
        var fresh = Snapshot("commit-B", analyzer: "4");
        var main = PointMain(reclaimed);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM snapshots WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", reclaimed);
            cmd.ExecuteNonQuery();
        }
        Assert.IsNull(_snapshots.GetBranchSnapshotId(main), "retention's delete nulls the pointer (ON DELETE SET NULL)");

        _snapshots.AttachOrUpgradeBranchPointer(_repo, "main", fresh, _now + 1);

        Assert.AreEqual(fresh, _snapshots.GetBranchSnapshotId(main));
    }

    [TestMethod]
    public void SharedOldHead_IsRepointedButNotSuperseded()
    {
        // Issue #128 guard: 'feature' shares the old head (#62). Superseding it would strand 'feature' on a
        // snapshot that ResolveBranch no longer returns.
        var old = Snapshot("commit-A", analyzer: "1");
        var upgraded = Snapshot("commit-A", analyzer: "4");
        var main = PointMain(old);
        var feature = _snapshots.AttachBranchPointer(_repo, "feature", old, _now);

        _snapshots.AttachOrUpgradeBranchPointer(_repo, "main", upgraded, _now + 1);

        Assert.AreEqual(upgraded, _snapshots.GetBranchSnapshotId(main));
        Assert.AreEqual(old, _snapshots.GetBranchSnapshotId(feature), "the other branch keeps its pointer");
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(old)!.Status,
            "a snapshot another branch still points at is never superseded (#128)");
    }

    [TestMethod]
    public void NonCompleteTarget_Declines()
    {
        var old = Snapshot("commit-A", analyzer: "1");
        var pending = Snapshot("commit-A", analyzer: "4", complete: false);
        var main = PointMain(old);

        _snapshots.AttachOrUpgradeBranchPointer(_repo, "main", pending, _now + 1);

        Assert.AreEqual(old, _snapshots.GetBranchSnapshotId(main), "a branch is never re-pointed at an unpublished snapshot");
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(old)!.Status);
    }

    [TestMethod]
    public void SameCommit_DirtyOrOverlayIdentity_Declines()
    {
        var clean = Snapshot("commit-A", analyzer: "4");
        var dirty = Snapshot("commit-A", analyzer: "4", delta: "submodule-dirty");
        var main = PointMain(clean);

        _snapshots.AttachOrUpgradeBranchPointer(_repo, "main", dirty, _now + 1);

        Assert.AreEqual(clean, _snapshots.GetBranchSnapshotId(main),
            "a dirty working-tree identity is not the clean commit, so it is not a pure identity change");
    }

    [TestMethod]
    public void UnsetBranch_IsAttached()
    {
        var snap = Snapshot("commit-A", analyzer: "4");

        var branch = _snapshots.AttachOrUpgradeBranchPointer(_repo, "release", snap, _now);

        Assert.AreEqual(snap, _snapshots.GetBranchSnapshotId(branch), "attach-if-unset still applies");
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(snap)!.Status);
    }

    private long PointMain(long snapshotId)
    {
        var main = _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
        _snapshots.SetBranchPointer(main, snapshotId, _now);
        return main;
    }

    private long Snapshot(string commit, string analyzer, bool complete = true, string? delta = null)
    {
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = Repo,
            CommitSha = commit,
            SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
            AnalyzerVersion = analyzer,
            ToolchainFingerprint = "test-toolchain",
            WorkingTreeDelta = delta
        };
        var commitId = _snapshots.EnsureCommit(_repo, commit, null, _now);
        var (id, _, _) = _snapshots.BeginPending(identity, _repo, commitId, _run, _now);
        if (complete)
            _snapshots.MarkComplete(id, _now);
        return id;
    }
}
