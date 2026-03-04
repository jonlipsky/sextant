using Sextant.Core;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

[TestClass]
public class IncrementalIndexerTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private long _projectId;
    private FileIndexStore _fileIndexStore = null!;
    private SymbolStore _symbolStore = null!;
    private string _tempDir = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_incr_test_{Guid.NewGuid():N}.db");
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
        _symbolStore = new SymbolStore(conn);
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_incr_files_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public void SkipsUnchangedFiles_ByContentHash()
    {
        var filePath = Path.Combine(_tempDir, "Test.cs");
        File.WriteAllText(filePath, "public class Test { }");

        var hash = IncrementalIndexer.ComputeFileHash(filePath);

        // Record the file in the index
        _fileIndexStore.Upsert(new FileIndexEntry
        {
            ProjectId = _projectId,
            FilePath = filePath,
            ContentHash = hash,
            LastIndexedAt = 1000
        });

        // Check: same hash means unchanged
        var entry = _fileIndexStore.GetByProjectAndFile(_projectId, filePath);
        Assert.IsNotNull(entry);
        Assert.AreEqual(hash, entry.ContentHash);

        // File hasn't changed, so hash should still match
        var currentHash = IncrementalIndexer.ComputeFileHash(filePath);
        Assert.AreEqual(hash, currentHash);
    }

    [TestMethod]
    public void DetectsChangedFiles_ByContentHash()
    {
        var filePath = Path.Combine(_tempDir, "Test.cs");
        File.WriteAllText(filePath, "public class Test { }");

        var originalHash = IncrementalIndexer.ComputeFileHash(filePath);

        _fileIndexStore.Upsert(new FileIndexEntry
        {
            ProjectId = _projectId,
            FilePath = filePath,
            ContentHash = originalHash,
            LastIndexedAt = 1000
        });

        // Change the file
        File.WriteAllText(filePath, "public class Test { void NewMethod() { } }");

        var newHash = IncrementalIndexer.ComputeFileHash(filePath);
        Assert.AreNotEqual(originalHash, newHash);

        var entry = _fileIndexStore.GetByProjectAndFile(_projectId, filePath);
        Assert.IsNotNull(entry);
        Assert.AreNotEqual(entry.ContentHash, newHash); // Different from stored hash
    }

    [TestMethod]
    public void DeletesAndReinserts_SymbolsForChangedFiles()
    {
        var filePath = "src/Changed.cs";

        // Insert symbols for the file
        _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.OldClass",
            DisplayName = "OldClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = filePath,
            LineStart = 1, LineEnd = 10,
            LastIndexedAt = 1000
        });

        // Verify symbol exists
        var symbols = _symbolStore.GetByFile(filePath);
        Assert.AreEqual(1, symbols.Count);

        // Delete symbols for the file (simulating what IncrementalIndexer does)
        _symbolStore.DeleteByFile(filePath);

        // Verify symbols are deleted
        symbols = _symbolStore.GetByFile(filePath);
        Assert.AreEqual(0, symbols.Count);

        // Insert new symbol (simulating re-extraction)
        _symbolStore.Insert(new SymbolInfo
        {
            ProjectId = _projectId,
            FullyQualifiedName = "global::Test.NewClass",
            DisplayName = "NewClass",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = filePath,
            LineStart = 1, LineEnd = 15,
            LastIndexedAt = 2000
        });

        // Verify new symbol exists
        symbols = _symbolStore.GetByFile(filePath);
        Assert.AreEqual(1, symbols.Count);
        Assert.AreEqual("NewClass", symbols[0].DisplayName);
    }
}
