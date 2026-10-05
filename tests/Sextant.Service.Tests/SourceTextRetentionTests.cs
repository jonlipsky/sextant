using System.Security.Cryptography;
using System.Text;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #244: the service keeps every indexed file's bytes in a content-addressed store on the artifact volume.
/// An executed retention pass deletes the blobs no <c>file_versions</c> row references any more (and only those);
/// a dry run deletes nothing.
/// </summary>
[TestClass]
public class SourceTextRetentionTests
{
    private string _dbPath = null!;
    private string _dataRoot = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _dataRoot = ServiceTestFixtures.NewDataRoot();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath, _dataRoot), new FakeSnapshotWorker(_db), _db);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _service.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }

    [TestMethod]
    public async Task ExecutedRetention_DeletesOnlyUnreferencedSourceText()
    {
        var texts = _service.SourceTexts;
        Assert.AreEqual(_service.Paths.SourceTextRoot, texts.Root, "the query side reads the root the worker writes");
        var referenced = Bytes("class Referenced { }");
        var orphan = Bytes("class Orphan { }");
        texts.Put(SHA256.HashData(referenced), referenced);
        texts.Put(SHA256.HashData(orphan), orphan);

        // A published, branch-pointed snapshot whose project indexed the referenced file.
        var ensured = await _service.EnsureSnapshotAsync(ServiceTestFixtures.Request(branch: "main"));
        var conn = _db.GetConnection();
        var projectId = new SnapshotStore(conn).GetSnapshotProjectIds(ensured.SnapshotId!.Value).Single();
        var fileVersionId = new FileStore(conn).ResolveFileVersionId(projectId, "src/App/Referenced.cs", SHA256.HashData(referenced));
        // Retention prunes a file version nothing points at, so a symbol declares in this one.
        using (var symbol = conn.CreateCommand())
        {
            symbol.CommandText = """
                INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                                     file_version_id, line_start, line_end, last_indexed_at)
                VALUES (@p, 'T:App.Referenced', 'global::App.Referenced', 'Referenced', 0, 0, @fv, 1, 1, 1);
                """;
            symbol.Parameters.AddWithValue("@p", projectId);
            symbol.Parameters.AddWithValue("@fv", fileVersionId);
            symbol.ExecuteNonQuery();
        }

        var dryRun = _service.RunRetention(execute: false);
        Assert.IsNull(dryRun.SourceTextsDeleted, "a dry run deletes no source text");
        Assert.IsNotNull(texts.TryGet(SHA256.HashData(orphan)));

        var executed = _service.RunRetention(execute: true);
        Assert.AreEqual(1, executed.SourceTextsDeleted);
        CollectionAssert.AreEqual(referenced, texts.TryGet(SHA256.HashData(referenced)));
        Assert.IsNull(texts.TryGet(SHA256.HashData(orphan)));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
