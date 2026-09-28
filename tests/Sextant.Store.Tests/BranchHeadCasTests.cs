using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// SVC-6: the <c>expected_head_commit</c> compare-and-swap on a branch pointer. <see cref="SnapshotStore.BranchHeadMatches"/>
/// is the predicate every service advance path (and retire) evaluates inside its write transaction;
/// <see cref="SnapshotStore.AdvanceBranchPointerIfHeadMatches"/> is the guarded advance. A match, a branch
/// create (no row, <c>""</c>/all-zero expected) and an unusable target pass; anything else leaves the branch
/// untouched. The CAS never writes <c>head_sequence</c>.
/// </summary>
[TestClass]
public class BranchHeadCasTests
{
    private const string Repo = "https://github.com/acme/widgets";
    private const string ZeroSha = "0000000000000000000000000000000000000000";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _repo;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_headcas_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repo = _snapshots.EnsureRepository(Repo, _now);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    [DataRow("", true)]
    [DataRow(ZeroSha, true)]
    [DataRow("0", true)]
    [DataRow("commit-A", false)]
    [DataRow("00a0", false)]
    public void AbsentBranch_PassesOnlyForAnAbsentHeadValue(string expected, bool passes)
    {
        Assert.AreEqual(passes, _snapshots.BranchHeadMatches(null, expected),
            "a branch create passes only when the push says the branch had no head");
    }

    [TestMethod]
    public void Match_Advances_AndSupersedesThePreviousHead()
    {
        var a = Snapshot("commit-A");
        var b = Snapshot("commit-B");
        var main = PointedBranch("main", a);

        Assert.IsTrue(_snapshots.AdvanceBranchPointerIfHeadMatches(main, b, "commit-A", _now));

        Assert.AreEqual(b, _snapshots.GetBranchSnapshotId(main));
        Assert.AreEqual(SnapshotStatus.Superseded, _snapshots.GetById(a)!.Status);
        Assert.IsNull(_snapshots.GetBranchHeadSequence(main), "the CAS never writes head_sequence");
    }

    [TestMethod]
    public void Match_IsCaseInsensitive()
    {
        var a = Snapshot("ABCDEF");
        var b = Snapshot("commit-B");
        var main = PointedBranch("main", a);

        Assert.IsTrue(_snapshots.AdvanceBranchPointerIfHeadMatches(main, b, "abcdef", _now));
        Assert.AreEqual(b, _snapshots.GetBranchSnapshotId(main));
    }

    [TestMethod]
    [DataRow("commit-X")]
    [DataRow("")]
    [DataRow(ZeroSha)]
    public void Mismatch_LeavesTheBranchUntouched(string expected)
    {
        var a = Snapshot("commit-A");
        var b = Snapshot("commit-B");
        var main = PointedBranch("main", a);
        _snapshots.SetBranchHeadSequence(main, 7);

        Assert.IsFalse(_snapshots.AdvanceBranchPointerIfHeadMatches(main, b, expected, _now),
            "a stale before (or a create against an existing head) attaches only");

        Assert.AreEqual(a, _snapshots.GetBranchSnapshotId(main));
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(a)!.Status);
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(b)!.Status);
        Assert.AreEqual(7L, _snapshots.GetBranchHeadSequence(main));
    }

    [TestMethod]
    public void NullPointer_IsAReclaimedTarget_AndPasses()
    {
        var b = Snapshot("commit-B");
        var main = _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);

        Assert.IsTrue(_snapshots.BranchHeadMatches(main, "commit-A"));
        Assert.IsTrue(_snapshots.AdvanceBranchPointerIfHeadMatches(main, b, "commit-A", _now));
        Assert.AreEqual(b, _snapshots.GetBranchSnapshotId(main));
    }

    [TestMethod]
    [DataRow(SnapshotStatus.Failed)]
    [DataRow(SnapshotStatus.Superseded)]
    [DataRow(SnapshotStatus.Pending)]
    public void UnusableTarget_Passes_AndIsNotSuperseded(string status)
    {
        var a = Snapshot("commit-A");
        var b = Snapshot("commit-B");
        var main = PointedBranch("main", a);
        _snapshots.MarkStatus(a, status);

        Assert.IsTrue(_snapshots.AdvanceBranchPointerIfHeadMatches(main, b, "commit-unrelated", _now),
            "an unusable head is replaced whatever the push's before was");

        Assert.AreEqual(b, _snapshots.GetBranchSnapshotId(main));
        Assert.AreEqual(status, _snapshots.GetById(a)!.Status, "only a Complete previous head is superseded");
    }

    [TestMethod]
    public void TargetWithoutACommit_NeverMatchesAHeadValue()
    {
        var a = Snapshot("commit-A", recordCommit: false);
        var main = PointedBranch("main", a);

        Assert.IsFalse(_snapshots.BranchHeadMatches(main, "commit-A"));
        Assert.IsFalse(_snapshots.BranchHeadMatches(main, ""));
    }

    [TestMethod]
    public void AlreadyPointed_IsAPassingNoOp()
    {
        var a = Snapshot("commit-A");
        var main = PointedBranch("main", a);

        Assert.IsTrue(_snapshots.AdvanceBranchPointerIfHeadMatches(main, a, "commit-A", _now));
        Assert.AreEqual(a, _snapshots.GetBranchSnapshotId(main));
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(a)!.Status, "re-pointing a head never supersedes it");
    }

    [TestMethod]
    public void SharedPreviousHead_IsNotSuperseded()
    {
        // Issue #128: another branch still points at the previous head, so it must stay Complete.
        var a = Snapshot("commit-A");
        var b = Snapshot("commit-B");
        var main = PointedBranch("main", a);
        var release = PointedBranch("release", a);

        Assert.IsTrue(_snapshots.AdvanceBranchPointerIfHeadMatches(main, b, "commit-A", _now));

        Assert.AreEqual(b, _snapshots.GetBranchSnapshotId(main));
        Assert.AreEqual(a, _snapshots.GetBranchSnapshotId(release));
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(a)!.Status);
    }

    [TestMethod]
    public void GetBranch_AndDeleteBranch()
    {
        var a = Snapshot("commit-A");
        var feature = PointedBranch("feature", a);
        _snapshots.SetBranchHeadSequence(feature, 3);

        var row = _snapshots.GetBranch(_repo, "feature");
        Assert.IsNotNull(row);
        Assert.AreEqual(feature, row.Id);
        Assert.AreEqual(_repo, row.RepositoryId);
        Assert.AreEqual("feature", row.Name);
        Assert.AreEqual(a, row.SnapshotId);
        Assert.IsFalse(row.IsDefault);
        Assert.AreEqual(3L, row.HeadSequence);
        Assert.AreEqual(row, _snapshots.GetBranchById(feature));
        Assert.IsNull(_snapshots.GetBranch(_repo, "missing"));

        Assert.IsTrue(_snapshots.DeleteBranch(feature));
        Assert.IsFalse(_snapshots.DeleteBranch(feature), "a second delete finds nothing");
        Assert.IsNull(_snapshots.GetBranch(_repo, "feature"));
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(a)!.Status, "deleting a branch never touches its snapshot");
    }

    private long PointedBranch(string name, long snapshotId)
    {
        var branch = _snapshots.EnsureBranch(_repo, name, isDefault: name == "main", _now);
        _snapshots.SetBranchPointer(branch, snapshotId, _now);
        return branch;
    }

    private long Snapshot(string commit, bool recordCommit = true)
    {
        var runStore = new IndexRunStore(_conn);
        var runId = runStore.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, _now, 1);
        long? commitId = recordCommit ? _snapshots.EnsureCommit(_repo, commit, treeSha: null, _now) : null;
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = Repo,
            CommitSha = commit,
            SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = _snapshots.BeginPending(identity, _repo, commitId, runId, _now);
        _snapshots.MarkComplete(id, _now);
        return id;
    }
}
