using Sextant.Core;

namespace Sextant.Store.Tests;

[TestClass]
public class FileIndexStoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private FileIndexStore _fileIndexStore = null!;
    private long _projectId;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_fileindex_test_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();

        var projectStore = new ProjectStore(conn);
        _projectId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "test0123456789ab",
            GitRemoteUrl = "https://github.com/test/repo",
            RepoRelativePath = "src/Test/Test.csproj"
        }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _fileIndexStore = new FileIndexStore(conn);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [TestMethod]
    public void Upsert_InsertsNewEntry()
    {
        var id = _fileIndexStore.Upsert(new FileIndexEntry
        {
            ProjectId = _projectId,
            FilePath = "src/MyFile.cs",
            ContentHash = "abc123",
            LastIndexedAt = 1000
        });

        Assert.IsTrue(id > 0);
        var entry = _fileIndexStore.GetByProjectAndFile(_projectId, "src/MyFile.cs");
        Assert.IsNotNull(entry);
        Assert.AreEqual("abc123", entry.ContentHash);
    }

    [TestMethod]
    public void Upsert_UpdatesExistingEntry()
    {
        _fileIndexStore.Upsert(new FileIndexEntry
        {
            ProjectId = _projectId,
            FilePath = "src/MyFile.cs",
            ContentHash = "abc123",
            LastIndexedAt = 1000
        });

        _fileIndexStore.Upsert(new FileIndexEntry
        {
            ProjectId = _projectId,
            FilePath = "src/MyFile.cs",
            ContentHash = "def456",
            LastIndexedAt = 2000
        });

        var entry = _fileIndexStore.GetByProjectAndFile(_projectId, "src/MyFile.cs");
        Assert.IsNotNull(entry);
        Assert.AreEqual("def456", entry.ContentHash);
        Assert.AreEqual(2000, entry.LastIndexedAt);
    }

    [TestMethod]
    public void GetByProject_ReturnsAllEntriesForProject()
    {
        _fileIndexStore.Upsert(new FileIndexEntry { ProjectId = _projectId, FilePath = "src/A.cs", ContentHash = "a", LastIndexedAt = 1000 });
        _fileIndexStore.Upsert(new FileIndexEntry { ProjectId = _projectId, FilePath = "src/B.cs", ContentHash = "b", LastIndexedAt = 1000 });

        var entries = _fileIndexStore.GetByProject(_projectId);
        Assert.AreEqual(2, entries.Count);
    }
}
