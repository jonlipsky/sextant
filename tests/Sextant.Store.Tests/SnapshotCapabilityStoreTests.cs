using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 15: the worker-capability fingerprint round-trips through the snapshot catalog (migration 017),
/// and a null capability (a local/single-node run) is stored + read back as null so the identity and
/// provenance stay byte-identical to the pre-Phase-15 path (CRITICAL 2).
/// </summary>
[TestClass]
public class SnapshotCapabilityStoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_capability_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void Migration017_BumpsLatestSchemaVersion_ToAtLeast17()
    {
        Assert.IsTrue(IndexDatabase.LatestSchemaVersion >= 17,
            "migration 017 must advance the derived latest schema version");
    }

    [TestMethod]
    public void CapabilityFingerprint_RoundTrips_ThroughBeginPending()
    {
        var store = new SnapshotStore(_conn);
        var repoId = store.EnsureRepository("https://github.com/org/repo", now: 1);
        var commitId = store.EnsureCommit(repoId, "commit_aaaa", "tree_aaaa", now: 1);

        var identity = Identity("commit_aaaa") with { CapabilityFingerprint = "cap-windows-fingerprint" };
        var (snapId, _, _) = store.BeginPending(identity, repoId, commitId, runId: null, now: 1);

        var row = store.GetById(snapId)!;
        Assert.AreEqual("cap-windows-fingerprint", row.CapabilityFingerprint,
            "the capability fingerprint must persist and read back");
        Assert.AreEqual(identity.Hash, store.GetByIdentityHash(identity.Hash)!.IdentityHash,
            "the identity (which folds the capability) resolves the same row");
    }

    [TestMethod]
    public void NullCapability_IsStoredAndReadAsNull()
    {
        var store = new SnapshotStore(_conn);
        var repoId = store.EnsureRepository("https://github.com/org/repo", now: 1);
        var commitId = store.EnsureCommit(repoId, "commit_bbbb", "tree_bbbb", now: 1);

        var identity = Identity("commit_bbbb"); // no capability → local/single-node path
        var (snapId, _, _) = store.BeginPending(identity, repoId, commitId, runId: null, now: 1);

        Assert.IsNull(store.GetById(snapId)!.CapabilityFingerprint,
            "a local run's snapshot records a null capability (identity unchanged)");
    }

    private static SnapshotIdentity Identity(string commit) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/repo",
        CommitSha = commit,
        TreeSha = $"tree_{commit}",
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc"
    };
}
