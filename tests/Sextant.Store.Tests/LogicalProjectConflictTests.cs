using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 17 slice 2, fold issue #69: <see cref="SnapshotStore.EnsureLogicalProject"/> is insert-or-VERIFY,
/// not insert-or-UPDATE. A contribution (or a second producer) must never silently mutate another
/// producer's shared logical-project metadata: an honest, matching tuple attaches to the existing row, but
/// a DIVERGING (path/tfm) tuple for the same canonical id is rejected with
/// <see cref="LogicalProjectConflictException"/> instead of overwriting shared state.
/// </summary>
[TestClass]
public class LogicalProjectConflictTests
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
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_lp_{Guid.NewGuid():N}.db");
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
    public void MatchingTuple_AttachesToTheSameRow_WithoutMutation()
    {
        var first = _snapshots.EnsureLogicalProject(_repo, "canon-1", "src/App/App.csproj", "net8.0", _now);
        var second = _snapshots.EnsureLogicalProject(_repo, "canon-1", "src/App/App.csproj", "net8.0", _now + 1);

        Assert.AreEqual(first, second, "an identical tuple attaches to the one existing logical-project row");
    }

    [TestMethod]
    public void DivergingPath_IsRejected_AndSharedMetadataIsUnchanged()
    {
        var id = _snapshots.EnsureLogicalProject(_repo, "canon-1", "src/App/App.csproj", "net8.0", _now);

        Assert.ThrowsExactly<LogicalProjectConflictException>(() =>
            _snapshots.EnsureLogicalProject(_repo, "canon-1", "src/Other/Other.csproj", "net8.0", _now + 1));

        Assert.AreEqual("src/App/App.csproj", StoredPath(id), "the shared logical-project path is never overwritten (#69)");
    }

    [TestMethod]
    public void DivergingTargetFramework_IsRejected()
    {
        _snapshots.EnsureLogicalProject(_repo, "canon-1", "src/App/App.csproj", "net8.0", _now);

        Assert.ThrowsExactly<LogicalProjectConflictException>(() =>
            _snapshots.EnsureLogicalProject(_repo, "canon-1", "src/App/App.csproj", "net9.0", _now + 1));
    }

    private string StoredPath(long logicalId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT repo_relative_path FROM logical_projects WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", logicalId);
        return (string)cmd.ExecuteScalar()!;
    }
}
