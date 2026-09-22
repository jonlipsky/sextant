using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #84: the forward-only branch-head advance the SERVICE ensure path uses. The control plane ensures
/// EVERY delivered commit (including out-of-order/older ones), so an unconditional advance could transiently
/// regress the data-plane branch pointer. <see cref="SnapshotStore.AdvanceBranchPointerForwardOnly"/> is the
/// gate the orchestrator's <c>AdvanceBranchToSnapshot</c> delegates to: a higher supplied sequence advances
/// the pointer + stores the sequence; a lower/equal one leaves the pointer untouched; a NULL sequence (the
/// local CLI/daemon path) advances unconditionally, byte-identical to the pre-#84 behavior.
/// </summary>
[TestClass]
public class BranchHeadSequenceTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _repo;
    private long _branch;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_headseq_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repo = _snapshots.EnsureRepository("https://github.com/org/app", _now);
        _branch = _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void Migration021_LatestSchemaVersion_DerivesTo21()
    {
        Assert.AreEqual(21, IndexDatabase.LatestSchemaVersion,
            "migration 021 is the last on disk, so the auto-derived latest schema version is 21 (criterion 4)");
    }

    [TestMethod]
    public void HeadSequence_IsNullOnAFreshBranch()
    {
        Assert.IsNull(_snapshots.GetBranchHeadSequence(_branch),
            "a branch never advanced by a sequence-bearing ensure has no head sequence (nullable column)");
    }

    [TestMethod]
    public void HigherSequence_AdvancesPointer_AndStoresSequence()
    {
        var a = Snapshot("commit-A");
        var b = Snapshot("commit-B");

        Assert.IsTrue(_snapshots.AdvanceBranchPointerForwardOnly(_branch, a, headSequence: 10, _now));
        Assert.AreEqual(a, _snapshots.GetBranchSnapshotId(_branch));
        Assert.AreEqual(10, _snapshots.GetBranchHeadSequence(_branch));

        // A strictly higher sequence advances to the newer snapshot and supersedes the previous target.
        Assert.IsTrue(_snapshots.AdvanceBranchPointerForwardOnly(_branch, b, headSequence: 11, _now + 1),
            "a strictly higher sequence advances the pointer (criterion 1)");
        Assert.AreEqual(b, _snapshots.GetBranchSnapshotId(_branch));
        Assert.AreEqual(11, _snapshots.GetBranchHeadSequence(_branch));
        Assert.AreEqual(SnapshotStatus.Superseded, _snapshots.GetById(a)!.Status,
            "the previous target is superseded on a genuine advance");
    }

    [TestMethod]
    public void LowerSequence_DoesNotRegressPointer_OrSupersedeNewer()
    {
        var older = Snapshot("commit-A");
        var newer = Snapshot("commit-B");

        // The branch has already advanced to the newer commit B at sequence 20.
        _snapshots.AdvanceBranchPointerForwardOnly(_branch, newer, headSequence: 20, _now);

        // A late/out-of-order ensure for the OLDER commit A carries a LOWER sequence (5). It must NOT move
        // the pointer or supersede B (criterion 1 no-regress / criterion 3).
        var advanced = _snapshots.AdvanceBranchPointerForwardOnly(_branch, older, headSequence: 5, _now + 1);

        Assert.IsFalse(advanced, "a lower sequence does not advance the pointer");
        Assert.AreEqual(newer, _snapshots.GetBranchSnapshotId(_branch), "the pointer stays at the newer commit (no regression)");
        Assert.AreEqual(20, _snapshots.GetBranchHeadSequence(_branch), "the stored sequence is not regressed");
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(newer)!.Status, "the newer target is not superseded");
    }

    [TestMethod]
    public void EqualSequence_DoesNotAdvance()
    {
        var newer = Snapshot("commit-B");
        var duplicate = Snapshot("commit-B2");
        _snapshots.AdvanceBranchPointerForwardOnly(_branch, newer, headSequence: 7, _now);

        // A delayed DUPLICATE advance carrying the SAME sequence (defense-in-depth) must not move the pointer.
        var advanced = _snapshots.AdvanceBranchPointerForwardOnly(_branch, duplicate, headSequence: 7, _now + 1);

        Assert.IsFalse(advanced, "an equal sequence does not advance the pointer (forward-only, strict >)");
        Assert.AreEqual(newer, _snapshots.GetBranchSnapshotId(_branch));
    }

    [TestMethod]
    public void NullSequence_AdvancesUnconditionally_ByteIdenticalToLegacy()
    {
        var older = Snapshot("commit-A");
        var newer = Snapshot("commit-B");

        // The legacy local CLI/daemon path passes no sequence and advances unconditionally.
        _snapshots.AdvanceBranchPointerForwardOnly(_branch, newer, headSequence: null, _now);
        Assert.AreEqual(newer, _snapshots.GetBranchSnapshotId(_branch));

        // Even "backwards" to an older snapshot (an older-commit re-checkout on the local path) — this is
        // exactly the pre-#84 behavior AdvanceBranchToSnapshot must keep for the local path (criterion 2).
        var advanced = _snapshots.AdvanceBranchPointerForwardOnly(_branch, older, headSequence: null, _now + 1);
        Assert.IsTrue(advanced, "a null sequence always advances (unconditional)");
        Assert.AreEqual(older, _snapshots.GetBranchSnapshotId(_branch), "the null path moves the pointer unconditionally");
        Assert.AreEqual(SnapshotStatus.Superseded, _snapshots.GetById(newer)!.Status, "the previous target is superseded");
        Assert.IsNull(_snapshots.GetBranchHeadSequence(_branch), "the null path never writes a head sequence");
    }

    [TestMethod]
    public void FirstSequenceBearingAdvance_OnABranchWithNoStoredSequence_Advances()
    {
        var a = Snapshot("commit-A");
        // No stored sequence yet → the gate must not decline; the first sequence-bearing ensure always wins.
        var advanced = _snapshots.AdvanceBranchPointerForwardOnly(_branch, a, headSequence: 1, _now);
        Assert.IsTrue(advanced);
        Assert.AreEqual(a, _snapshots.GetBranchSnapshotId(_branch));
        Assert.AreEqual(1, _snapshots.GetBranchHeadSequence(_branch));
    }

    [TestMethod]
    public void DecliningReselectOfOlderSnapshot_LeavesItAttachedAndComplete_ButPointerAtNewer()
    {
        // Mirrors IndexOrchestrator.SelectExistingSnapshot on the SERVICE out-of-order re-select path
        // (issue #84): an older commit A that was already indexed is now Superseded (the branch advanced to
        // the newer commit B). A late/out-of-order ensure for A re-selects it — the orchestrator restores A
        // to Complete (so the ensure succeeds and A stays resolvable by commit) THEN runs the forward-only
        // gate with A's lower sequence. The gate must decline: the pointer stays at B, B is not superseded,
        // yet A remains Complete and attached (criterion 3 — "attaches its immutable snapshot but leaves the
        // pointer at the newer commit"). The two Completes never collide because branch selection resolves
        // strictly via the branch pointer, and only a commit-scoped query (for A) sees A.
        var older = Snapshot("commit-A");
        var newer = Snapshot("commit-B");
        _snapshots.AdvanceBranchPointerForwardOnly(_branch, newer, headSequence: 20, _now);
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(older)!.Status,
            "precondition: older A is Complete before it is (in a real run) superseded by advancing to B");

        // Emulate the SelectExistingSnapshot composition: restore-to-Complete, then the gated advance.
        _snapshots.MarkStatus(older, SnapshotStatus.Complete);
        var advanced = _snapshots.AdvanceBranchPointerForwardOnly(_branch, older, headSequence: 5, _now + 1);

        Assert.IsFalse(advanced, "the out-of-order lower-sequence re-select declines the pointer move");
        Assert.AreEqual(newer, _snapshots.GetBranchSnapshotId(_branch), "the branch pointer stays at the newer commit B");
        Assert.AreEqual(20, _snapshots.GetBranchHeadSequence(_branch), "the stored head sequence is not regressed");
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(newer)!.Status, "the newer target B is not superseded");
        Assert.AreEqual(SnapshotStatus.Complete, _snapshots.GetById(older)!.Status,
            "the older snapshot A stays Complete → attached and resolvable by commit, just not the branch head");
        Assert.AreEqual(older, _snapshots.ResolveCompleteSnapshotByCommit(_repo, "commit-A"),
            "a commit-scoped query for A still resolves A's immutable snapshot");
    }

    private long Snapshot(string commit)
    {
        var runStore = new IndexRunStore(_conn);
        var runId = runStore.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, _now, 1);
        var commitId = _snapshots.EnsureCommit(_repo, commit, treeSha: null, _now);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = "https://github.com/org/app",
            CommitSha = commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = _snapshots.BeginPending(identity, _repo, commitId, runId, _now);
        _snapshots.MarkComplete(id, _now);
        return id;
    }
}
