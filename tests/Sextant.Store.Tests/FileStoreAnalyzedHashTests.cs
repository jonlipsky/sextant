using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #35 (TOCTOU), folded into Phase 10. The overlay's changed-file contribution must persist the
/// hash of the EXACT bytes Roslyn analyzed, not a re-read of the file at persist time — otherwise a
/// file that changes between analysis and persistence gets a <c>file_versions.content_hash</c> that
/// never matches what was analyzed, silently breaking the query-time snippet gate and incremental
/// staleness checks. <see cref="FileStore.CaptureAnalyzedHash"/> snapshots the analyzed bytes' hash and
/// <see cref="FileStore.ResolveFileVersionId"/> prefers it, closing the window.
/// </summary>
[TestClass]
public class FileStoreAnalyzedHashTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private string _root = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_toctou_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _root = Path.Combine(Path.GetTempPath(), $"sextant_toctou_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src", "App"));
    }

    [TestCleanup]
    public void TestCleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void ResolveFileVersionId_UsesAnalyzedHash_EvenWhenFileChangesAfterCapture()
    {
        var projectId = SeedProject("app0000000000001");
        var file = Path.Combine(_root, "src", "App", "Widget.cs");
        const string analyzed = "class Widget { void M() {} }";
        File.WriteAllText(file, analyzed);
        var analyzedHash = Sha256(analyzed);

        var fileStore = new FileStore(_conn);

        // Roslyn "analyzed" these bytes: capture the hash at analysis time (#35).
        var captured = fileStore.CaptureAnalyzedHash(projectId, file);
        CollectionAssert.AreEqual(analyzedHash, captured, "the captured hash is of the analyzed bytes");

        // The file drifts AFTER analysis but BEFORE the file-version row is persisted (the TOCTOU window).
        const string drifted = "class Widget { void M() { Changed(); } void Changed() {} }";
        File.WriteAllText(file, drifted);

        // Persisting must record the ANALYZED bytes' hash, not the drifted on-disk bytes.
        var fvId = fileStore.ResolveFileVersionId(projectId, file);
        var stored = ReadContentHash(fvId);

        CollectionAssert.AreEqual(analyzedHash, stored,
            "the persisted file_version hash is the analyzed bytes' hash, closing the TOCTOU window (#35)");
        CollectionAssert.AreNotEqual(Sha256(drifted), stored,
            "the persisted hash must NOT be the post-analysis drifted content");
    }

    [TestMethod]
    public void ResolveFileVersionId_WithoutCapture_ReadsDisk()
    {
        // Control: with no analysis-time capture, resolution falls back to a disk read — this is why the
        // capture in the symbol phase matters, and proves the #35 fix is doing something observable.
        var projectId = SeedProject("app0000000000002");
        var file = Path.Combine(_root, "src", "App", "Other.cs");
        File.WriteAllText(file, "class Other {}");

        var fvId = new FileStore(_conn).ResolveFileVersionId(projectId, file);
        CollectionAssert.AreEqual(Sha256("class Other {}"), ReadContentHash(fvId),
            "with no captured analyzed hash, the on-disk bytes are hashed");
    }

    private long SeedProject(string canonicalId) => new ProjectStore(_conn).Insert(new ProjectIdentity
    {
        CanonicalId = canonicalId,
        GitRemoteUrl = "https://github.com/org/app",
        RepoRelativePath = "src/App/App.csproj",
        DiskPath = Path.Combine(_root, "src", "App", "App.csproj"),
        TargetFramework = "net10.0"
    }, 1);

    private byte[] ReadContentHash(long fileVersionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT content_hash FROM file_versions WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", fileVersionId);
        return (byte[])cmd.ExecuteScalar()!;
    }

    private static byte[] Sha256(string content) => SHA256.HashData(Encoding.UTF8.GetBytes(content));
}
