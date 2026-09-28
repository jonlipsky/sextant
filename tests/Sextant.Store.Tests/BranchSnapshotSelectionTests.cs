using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// SVC-2: <see cref="SnapshotStore.GetSelectedSnapshotRowForRepositoryBranch"/> resolves a NAMED branch of a
/// NAMED consumer repository to the complete snapshot its pointer targets, with the same null contract as the
/// default-branch selector: an unknown repository or branch, a branch without a pointer, a pointer at a
/// non-complete snapshot, and a provider repository all resolve to null.
/// </summary>
[TestClass]
public class BranchSnapshotSelectionTests
{
    private const string Repo = "https://github.com/acme/widgets";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _repo;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_branchsel_{Guid.NewGuid():N}.db");
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
    public void NamedBranch_ResolvesItsPointer_NotTheDefault()
    {
        var mainSnap = Point("main", isDefault: true, Snapshot("commit-A"));
        var featureSnap = Point("feature/x", isDefault: false, Snapshot("commit-B"));

        Assert.AreEqual(featureSnap, _snapshots.GetSelectedSnapshotRowForRepositoryBranch(Repo, "feature/x")?.Id);
        Assert.AreEqual(mainSnap, _snapshots.GetSelectedSnapshotRowForRepositoryBranch(Repo, "main")?.Id,
            "the default branch can also be named");
        Assert.AreEqual(mainSnap, _snapshots.GetSelectedSnapshotIdForRepository(Repo), "the default selector is unchanged");
    }

    [TestMethod]
    public void RepositorySpelling_IsNormalized_BranchNameIsExact()
    {
        var snap = Point("feature/x", isDefault: false, Snapshot("commit-A"));

        Assert.AreEqual(snap, _snapshots.GetSelectedSnapshotIdForRepositoryBranch("https://github.com/acme/widgets.git", "feature/x"),
            "an equivalent repository spelling selects the same row");
        Assert.IsNull(_snapshots.GetSelectedSnapshotIdForRepositoryBranch(Repo, "Feature/X"),
            "branch names are matched exactly");
    }

    [TestMethod]
    public void UnknownRepositoryOrBranch_IsNull()
    {
        Point("main", isDefault: true, Snapshot("commit-A"));

        Assert.IsNull(_snapshots.GetSelectedSnapshotRowForRepositoryBranch(Repo, "no-such-branch"));
        Assert.IsNull(_snapshots.GetSelectedSnapshotRowForRepositoryBranch("https://github.com/acme/gadgets", "main"));
    }

    [TestMethod]
    public void BranchWithoutACompleteSnapshot_IsNull()
    {
        _snapshots.EnsureBranch(_repo, "no-pointer", isDefault: false, _now);
        Point("pending", isDefault: false, Snapshot("commit-P", complete: false));

        Assert.IsNull(_snapshots.GetSelectedSnapshotRowForRepositoryBranch(Repo, "no-pointer"));
        Assert.IsNull(_snapshots.GetSelectedSnapshotRowForRepositoryBranch(Repo, "pending"),
            "a branch pointing at a non-complete snapshot selects nothing");
    }

    [TestMethod]
    public void AnotherRepositorysBranch_IsNeverSelected()
    {
        Point("feature/x", isDefault: false, Snapshot("commit-A"));
        _snapshots.EnsureRepository("https://github.com/acme/gadgets", _now);

        Assert.IsNull(_snapshots.GetSelectedSnapshotRowForRepositoryBranch("https://github.com/acme/gadgets", "feature/x"));
    }

    [TestMethod]
    public void ProviderRepository_IsNotSelectable()
    {
        Point("main", isDefault: true, Snapshot("commit-A"));
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE repositories SET is_provider = 1 WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", _repo);
            cmd.ExecuteNonQuery();
        }

        Assert.IsNull(_snapshots.GetSelectedSnapshotRowForRepositoryBranch(Repo, "main"));
    }

    private long Point(string branch, bool isDefault, long snapshotId)
    {
        _snapshots.SetBranchPointer(_snapshots.EnsureBranch(_repo, branch, isDefault, _now), snapshotId, _now);
        return snapshotId;
    }

    private long Snapshot(string commit, bool complete = true)
    {
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = Repo,
            CommitSha = commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = _snapshots.BeginPending(identity, _repo, null, null, _now);
        if (complete)
            _snapshots.MarkComplete(id, _now);
        return id;
    }
}
