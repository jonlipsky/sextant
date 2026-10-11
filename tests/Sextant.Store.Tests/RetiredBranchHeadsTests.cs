using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #299: <c>retired_branch_heads</c> (migration 030), the service's identity-neutral record of the head an idle
/// non-default branch was retired at. A missing branch's CAS passes for exactly that head when the caller names the
/// branch, creating the branch consumes the record, and old records are pruned. Every other CAS outcome is unchanged.
/// </summary>
[TestClass]
public class RetiredBranchHeadsTests
{
    private const string Repo = "https://github.com/acme/retired";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotStore _store = null!;
    private long _repoId;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_retired_heads_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _store = new SnapshotStore(_db.GetConnection());
        _repoId = _store.EnsureRepository(Repo, 1);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void Migration030_IsIdentityNeutral_AndCreatesAServiceJobTable()
    {
        Assert.Contains(30, IndexDatabase.IdentityNeutralMigrations);
        Assert.Contains("retired_branch_heads", IndexDatabase.ServiceJobTables);
        Assert.AreEqual(26, IndexDatabase.SnapshotSchemaVersion, "recording retired heads re-indexes nothing");
        Assert.AreEqual(IndexDatabase.LatestSchemaVersion, _db.CurrentSchemaVersion);
    }

    [TestMethod]
    public void MissingBranch_MatchesItsRetiredHead_OnlyWhenTheCallerNamesTheBranch()
    {
        _store.RecordRetiredHead(_repoId, "feature", "ABC123", now: 10, SnapshotStore.RetiredHeadCauseIdle);

        Assert.AreEqual("abc123", _store.GetRetiredHead(_repoId, "feature"));
        Assert.IsTrue(_store.BranchHeadMatches(null, "abc123", _repoId, "feature"));
        Assert.IsTrue(_store.BranchHeadMatches(null, "ABC123", _repoId, "feature"), "case-insensitive, like hex SHAs");
        Assert.IsFalse(_store.BranchHeadMatches(null, "def456", _repoId, "feature"), "another commit is still refused");
        Assert.IsFalse(_store.BranchHeadMatches(null, "abc123", _repoId, "other"), "per branch");
        Assert.IsFalse(_store.BranchHeadMatches(null, "abc123"), "without the branch named, the CAS is unchanged");
        Assert.IsTrue(_store.BranchHeadMatches(null, ""), "a create still passes");
        Assert.IsTrue(_store.BranchHeadMatches(null, "", _repoId, "feature"));
    }

    [TestMethod]
    public void CreatingTheBranch_ConsumesItsRetiredHead()
    {
        _store.RecordRetiredHead(_repoId, "a", "aaaa", 10, SnapshotStore.RetiredHeadCauseIdle);
        _store.RecordRetiredHead(_repoId, "b", "bbbb", 10, SnapshotStore.RetiredHeadCauseIdle);

        _store.EnsureBranch(_repoId, "a", isDefault: false, now: 20);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = Repo,
            CommitSha = "bbbb",
            SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (snapshot, _, _) = _store.BeginPending(identity, _repoId, null, null, 20);
        _store.AttachBranchPointer(_repoId, "b", snapshot, 20);

        Assert.IsNull(_store.GetRetiredHead(_repoId, "a"), "EnsureBranch consumes it");
        Assert.IsNull(_store.GetRetiredHead(_repoId, "b"), "AttachBranchPointer consumes it");
    }

    [TestMethod]
    public void Prune_DeletesOnlyRecordsOlderThanTheCutoff_AndRecordingReplaces()
    {
        _store.RecordRetiredHead(_repoId, "old", "aaaa", 10, SnapshotStore.RetiredHeadCauseIdle);
        _store.RecordRetiredHead(_repoId, "new", "bbbb", 100, SnapshotStore.RetiredHeadCauseIdle);
        _store.RecordRetiredHead(_repoId, "new", "cccc", 200, SnapshotStore.RetiredHeadCauseIdle);

        Assert.AreEqual(1, _store.PruneRetiredHeads(cutoffMs: 150));
        Assert.IsNull(_store.GetRetiredHead(_repoId, "old"));
        Assert.AreEqual("cccc", _store.GetRetiredHead(_repoId, "new"), "a later retirement replaced the earlier record");
        Assert.IsTrue(_store.ConsumeRetiredHead(_repoId, "new"));
        Assert.IsFalse(_store.ConsumeRetiredHead(_repoId, "new"));
    }
}
