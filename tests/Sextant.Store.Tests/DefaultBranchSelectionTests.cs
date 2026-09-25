using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #104: the multi-tenant read selector <see cref="SnapshotStore.GetSelectedSnapshotIdForRepository"/>
/// resolves ONLY a branch with <c>is_default = 1</c>, but the attach path inserts branches non-default and
/// the legacy promotion primitive (<see cref="SnapshotStore.PromoteSoleDefaultBranch"/>) only DEMOTES
/// siblings — it never SETS the kept branch. These store-level tests pin the two new primitives that close
/// that gap: <see cref="SnapshotStore.SetSoleDefaultBranch"/> (sets keep=1 AND demotes siblings atomically)
/// and <see cref="SnapshotStore.ShouldOwnDefault"/> (the decoupled default-ownership decision, incl. the
/// "first/sole consumer branch becomes default" safety net), and the end-to-end selectability they restore.
/// </summary>
[TestClass]
public class DefaultBranchSelectionTests
{
    private const string Repo = "https://github.com/org/app";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _repo;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_defbranch_{Guid.NewGuid():N}.db");
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
    public void SetSoleDefaultBranch_SetsKeepDefault_WhenCurrentlyNonDefault()
    {
        // The core gap: a branch inserted NON-default (as AttachBranchPointer does) must become promotable.
        var branch = _snapshots.AttachBranchPointer(_repo, "main", Snapshot("commit-A"), _now);
        Assert.IsNull(_snapshots.GetDefaultBranchId(_repo), "attach inserts the branch non-default (precondition)");

        _snapshots.SetSoleDefaultBranch(_repo, branch);

        Assert.AreEqual(branch, _snapshots.GetDefaultBranchId(_repo),
            "SetSoleDefaultBranch SETS is_default on the kept branch (unlike PromoteSoleDefaultBranch, which only demotes)");
    }

    [TestMethod]
    public void PromoteSoleDefaultBranch_OnNonDefaultBranch_IsANoOp_DocumentingTheGap()
    {
        // Documents WHY a new primitive is needed: the legacy demote-only method silently leaves an
        // attach-inserted branch non-default, which is exactly the #104 empty-result cause.
        var branch = _snapshots.AttachBranchPointer(_repo, "main", Snapshot("commit-A"), _now);

        _snapshots.PromoteSoleDefaultBranch(_repo, branch);

        Assert.IsNull(_snapshots.GetDefaultBranchId(_repo),
            "PromoteSoleDefaultBranch only demotes siblings — a keep row at is_default=0 stays non-default");
    }

    [TestMethod]
    public void SetSoleDefaultBranch_DemotesSiblings_PreservingSingleDefaultInvariant()
    {
        var main = _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
        var feature = _snapshots.AttachBranchPointer(_repo, "feature", Snapshot("commit-B"), _now);

        _snapshots.SetSoleDefaultBranch(_repo, feature);

        Assert.AreEqual(feature, _snapshots.GetDefaultBranchId(_repo), "the kept branch becomes the default");
        Assert.AreEqual(1, DefaultCount(_repo), "exactly one default remains (the sibling was demoted)");
        Assert.AreEqual(0, IsDefault(main), "the previous default was demoted");
    }

    [TestMethod]
    public void SetSoleDefaultBranch_IsScopedToRepository()
    {
        var otherRepo = _snapshots.EnsureRepository("https://github.com/org/other", _now);
        var otherMain = _snapshots.EnsureBranch(otherRepo, "main", isDefault: true, _now);
        var mine = _snapshots.AttachBranchPointer(_repo, "main", Snapshot("commit-A"), _now);

        _snapshots.SetSoleDefaultBranch(_repo, mine);

        Assert.AreEqual(mine, _snapshots.GetDefaultBranchId(_repo), "my repo gains its default");
        Assert.AreEqual(otherMain, _snapshots.GetDefaultBranchId(otherRepo),
            "another repository's default is never touched (repository-scoped update)");
    }

    [TestMethod]
    public void ShouldOwnDefault_ExplicitFlag_ShortCircuitsTrue()
        => Assert.IsTrue(_snapshots.ShouldOwnDefault(_repo, "any", isDefault: true),
            "an explicit default assertion always owns the default (byte-identical local path relies on this short-circuit)");

    [TestMethod]
    public void ShouldOwnDefault_NoDefaultExists_ReturnsTrue_SafetyNet()
        => Assert.IsTrue(_snapshots.ShouldOwnDefault(_repo, "main", isDefault: false),
            "first/sole consumer branch becomes default when the repo has none — the self-healing safety net (#104)");

    [TestMethod]
    public void ShouldOwnDefault_BranchIsAlreadyTheDefault_ReturnsTrue()
    {
        _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
        Assert.IsTrue(_snapshots.ShouldOwnDefault(_repo, "main", isDefault: false),
            "re-ensuring the current default keeps it default even without the flag (monotonic, no demotion)");
    }

    [TestMethod]
    public void ShouldOwnDefault_SecondaryBranch_WithExistingDefault_ReturnsFalse()
    {
        _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
        Assert.IsFalse(_snapshots.ShouldOwnDefault(_repo, "feature", isDefault: false),
            "a secondary branch never steals the default from an existing default (#62 preserved)");
    }

    [TestMethod]
    public void AttachThenPromote_RestoresRepositorySelectability_TheRootCauseAndFix()
    {
        // Reproduces the #104 chain at the store layer: a fully-published snapshot attached to a
        // NON-default branch is NOT selectable, and promoting the branch makes it selectable.
        var snap = Snapshot("commit-A");
        var branch = _snapshots.AttachBranchPointer(_repo, "main", snap, _now);

        Assert.IsNull(_snapshots.GetSelectedSnapshotIdForRepository(Repo),
            "root cause: a complete snapshot on a non-default branch is unselectable → /mcp queries are empty");

        _snapshots.SetSoleDefaultBranch(_repo, branch);

        Assert.AreEqual(snap, _snapshots.GetSelectedSnapshotIdForRepository(Repo),
            "fix: promoting the sole consumer branch to default makes the snapshot selectable (criterion 4)");
    }

    [TestMethod]
    public void WorkerPathComposition_NamedNonDefaultBranch_LeavesRepositorySelectable_TheLiteral104Repro()
    {
        // The LITERAL #104 repro at the worker-path primitive level: a coordinator NAMES the branch it
        // advances (non-null "main") and does NOT assert default (isDefault=false). Replays the exact
        // three-call composition IndexOrchestrator.AdvanceBranchToSnapshot performs — ShouldOwnDefault →
        // EnsureBranch(ownsDefault) → PromoteSoleDefaultBranch — so this stays a faithful pin of that
        // path without a full MSBuild index. Pre-#104 this named branch was inserted is_default=0 and the
        // repository had no selectable snapshot; the safety net now promotes the sole consumer branch.
        var snap = Snapshot("commit-A");
        const string named = "main";
        const bool coordinatorAssertsDefault = false; // coordinator named the branch but sent no default flag

        var ownsDefault = _snapshots.ShouldOwnDefault(_repo, named, coordinatorAssertsDefault);
        var branchId = _snapshots.EnsureBranch(_repo, named, ownsDefault, _now);
        if (ownsDefault)
            _snapshots.PromoteSoleDefaultBranch(_repo, branchId);
        _snapshots.AdvanceBranchPointerForwardOnly(branchId, snap, headSequence: 1L, _now);

        Assert.IsTrue(ownsDefault, "the sole consumer branch owns the default via the safety net");
        Assert.AreEqual(1, DefaultCount(_repo), "the single-default invariant holds");
        Assert.AreEqual(snap, _snapshots.GetSelectedSnapshotIdForRepository(Repo),
            "a service ensure that NAMES a non-default branch still leaves a selectable snapshot so /mcp find_symbol resolves (criterion 4)");
    }

    [TestMethod]
    public void WorkerPathComposition_SecondNamedBranch_DoesNotStealDefault_NorLoseSelectability()
    {
        // A second NAMED branch advancing later (isDefault=false) must NOT steal the default the first
        // consumer established, and the repository must stay selectable at the original default's snapshot.
        var mainSnap = Snapshot("commit-A");
        var mainBranch = _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
        _snapshots.AdvanceBranchPointerForwardOnly(mainBranch, mainSnap, headSequence: 1L, _now);

        var featureSnap = Snapshot("commit-B");
        var ownsDefault = _snapshots.ShouldOwnDefault(_repo, "feature", isDefault: false);
        var featureBranch = _snapshots.EnsureBranch(_repo, "feature", ownsDefault, _now);
        if (ownsDefault)
            _snapshots.PromoteSoleDefaultBranch(_repo, featureBranch);
        _snapshots.AdvanceBranchPointerForwardOnly(featureBranch, featureSnap, headSequence: 1L, _now);

        Assert.IsFalse(ownsDefault, "the second named branch never owns the default while a default exists");
        Assert.AreEqual(mainBranch, _snapshots.GetDefaultBranchId(_repo), "the original default is preserved (#62)");
        Assert.AreEqual(mainSnap, _snapshots.GetSelectedSnapshotIdForRepository(Repo),
            "the repository stays selectable at the default branch's snapshot, not the secondary branch's");
    }

    [TestMethod]
    public void SetSoleDefaultBranch_ForeignOrUnknownKeep_IsNoOp_PreservesExistingDefault()
    {
        // Sol hardening: a keep id that is unknown or belongs to ANOTHER repository must be a genuine
        // no-op. Without the EXISTS ownership guard the is_default=1 clause would still demote THIS repo's
        // real default while promoting nothing, leaving the repo with no selectable default (fail-closed).
        var main = _snapshots.EnsureBranch(_repo, "main", isDefault: true, _now);
        var snap = Snapshot("commit-A");
        _snapshots.AdvanceBranchPointerForwardOnly(main, snap, headSequence: null, _now);

        var otherRepo = _snapshots.EnsureRepository("https://github.com/org/other", _now);
        var foreignBranch = _snapshots.EnsureBranch(otherRepo, "main", isDefault: true, _now);

        _snapshots.SetSoleDefaultBranch(_repo, foreignBranch);          // foreign keep id
        _snapshots.SetSoleDefaultBranch(_repo, keepBranchId: 999_999);  // unknown keep id

        Assert.AreEqual(main, _snapshots.GetDefaultBranchId(_repo),
            "an unknown/foreign keep id never demotes this repo's real default (EXISTS ownership guard)");
        Assert.AreEqual(snap, _snapshots.GetSelectedSnapshotIdForRepository(Repo),
            "the repository stays selectable — the guard prevents a fail-closed no-default state");
        Assert.AreEqual(foreignBranch, _snapshots.GetDefaultBranchId(otherRepo),
            "the other repository's default is likewise untouched");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private long Snapshot(string commit)
    {
        var runStore = new IndexRunStore(_conn);
        var runId = runStore.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, _now, 1);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = Repo,
            CommitSha = commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = _snapshots.BeginPending(identity, _repo, null, runId, _now);
        _snapshots.MarkComplete(id, _now);
        return id;
    }

    private int DefaultCount(long repositoryId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM branches WHERE repository_id = @repo AND is_default = 1;";
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private int IsDefault(long branchId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT is_default FROM branches WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", branchId);
        return (int)(long)cmd.ExecuteScalar()!;
    }
}
