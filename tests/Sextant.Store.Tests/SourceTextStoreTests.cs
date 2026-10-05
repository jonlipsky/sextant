using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #244: the content-addressed store a published snapshot reads its source text from. A read returns bytes
/// only when they hash to the requested key, so a missing, corrupt or foreign blob reads as absent (never as the
/// wrong text), and <see cref="FileStore"/> stores exactly the bytes behind the hash it persists.
/// </summary>
[TestClass]
public class SourceTextStoreTests
{
    private string _root = null!;

    [TestInitialize]
    public void TestInitialize() =>
        _root = Path.Combine(Path.GetTempPath(), $"sextant_srctext_{Guid.NewGuid():N}");

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void Put_ThenTryGet_ReturnsTheExactBytes()
    {
        var store = new SourceTextStore(_root);
        var content = Bytes("namespace A;\r\npublic class B { }\n// ünïcödé\n");

        store.Put(SHA256.HashData(content), content);

        CollectionAssert.AreEqual(content, store.TryGet(SHA256.HashData(content)));
        Assert.IsTrue(File.Exists(BlobPath(content)), "the blob lives at <root>/<2 hex>/<64 hex>.br");
    }

    [TestMethod]
    public void TryGet_UnknownOrMalformedHash_ReturnsNull()
    {
        var store = new SourceTextStore(_root);

        Assert.IsNull(store.TryGet(SHA256.HashData(Bytes("never stored"))));
        Assert.IsNull(store.TryGet([1, 2, 3]), "a hash that is not 32 bytes is never looked up");
    }

    [TestMethod]
    public void TryGet_CorruptBlob_ReturnsNull()
    {
        var store = new SourceTextStore(_root);
        var content = Bytes("class Corrupt { }");
        store.Put(SHA256.HashData(content), content);

        File.WriteAllBytes(BlobPath(content), [0xFF, 0x00, 0x13, 0x37, 0x42]);

        Assert.IsNull(store.TryGet(SHA256.HashData(content)));
    }

    [TestMethod]
    public void TryGet_BlobHoldingOtherContent_ReturnsNull()
    {
        var store = new SourceTextStore(_root);
        var expected = Bytes("class Expected { }");
        var foreign = Bytes("class Foreign { }");
        WriteBlob(BlobPath(expected), foreign);

        Assert.IsNull(store.TryGet(SHA256.HashData(expected)), "a well-formed blob that does not hash to its key is not served");
    }

    [TestMethod]
    public void Put_RewritesABlobThatDoesNotVerify()
    {
        var store = new SourceTextStore(_root);
        var content = Bytes("class Healed { }");
        WriteBlob(BlobPath(content), Bytes("class Wrong { }"));

        store.Put(SHA256.HashData(content), content);

        CollectionAssert.AreEqual(content, store.TryGet(SHA256.HashData(content)));
    }

    [TestMethod]
    public void Put_OverTheCapOrWithAMalformedHash_StoresNothing()
    {
        var store = new SourceTextStore(_root, maxBytes: 8);
        var big = Bytes("more than eight bytes");

        store.Put(SHA256.HashData(big), big);
        store.Put([1, 2, 3], Bytes("tiny"));

        Assert.IsFalse(Directory.Exists(_root) && Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public void TryGet_BlobBiggerThanTheCap_ReturnsNull()
    {
        var content = Bytes("a file stored under a generous cap");
        new SourceTextStore(_root).Put(SHA256.HashData(content), content);

        Assert.IsNull(new SourceTextStore(_root, maxBytes: 8).TryGet(SHA256.HashData(content)));
    }

    [TestMethod]
    public void DeleteUnreferenced_KeepsReferencedBlobs_DeletesOrphansUpToTheMax_AndTempFiles()
    {
        var store = new SourceTextStore(_root);
        var kept = Bytes("class Kept { }");
        var orphans = Enumerable.Range(0, 3).Select(i => Bytes($"class Orphan{i} {{ }}")).ToList();
        foreach (var content in orphans.Append(kept))
            store.Put(SHA256.HashData(content), content);
        var temp = Path.Combine(Path.GetDirectoryName(BlobPath(kept))!, "crashed.write.tmp");
        File.WriteAllText(temp, "partial");
        var referenced = new HashSet<UInt128> { SourceTextStore.SweepKey(SHA256.HashData(kept)) };

        Assert.AreEqual(2, store.DeleteUnreferenced(referenced, maxDeletes: 2), "at most maxDeletes blobs go per pass");
        Assert.AreEqual(1, store.DeleteUnreferenced(referenced, maxDeletes: 10));
        Assert.IsFalse(File.Exists(temp), "a pass that walks the whole store sweeps a leftover temp file");
        Assert.AreEqual(0, store.DeleteUnreferenced(referenced, maxDeletes: 10));

        CollectionAssert.AreEqual(kept, store.TryGet(SHA256.HashData(kept)));
        Assert.IsTrue(orphans.All(o => store.TryGet(SHA256.HashData(o)) == null));
    }

    [TestMethod]
    public void DeleteUnreferenced_NoStoreYet_DeletesNothing() =>
        Assert.AreEqual(0, new SourceTextStore(_root).DeleteUnreferenced(new HashSet<UInt128>(), maxDeletes: 10));

    [TestMethod]
    public void FileStore_StoresTheHashedBytes_AndReportsEveryReferencedHash()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sextant_srctext_{Guid.NewGuid():N}.db");
        var db = new IndexDatabase(dbPath);
        try
        {
            db.RunMigrations();
            var conn = db.GetConnection();
            var source = Path.Combine(_root, "repo", "src", "App");
            Directory.CreateDirectory(source);
            var projectId = SeedProject(conn, Path.Combine(source, "App.csproj"));
            var captured = Path.Combine(source, "Captured.cs");
            var resolved = Path.Combine(source, "Resolved.cs");
            File.WriteAllText(captured, "class Captured { }");
            File.WriteAllText(resolved, "class Resolved { }");
            var texts = new SourceTextStore(Path.Combine(_root, "texts"));
            var files = new FileStore(conn) { SourceTexts = texts };

            // The analysis-time capture stores the bytes it hashed (#35): later drift on disk does not change them.
            var capturedHash = files.CaptureAnalyzedHash(projectId, captured);
            File.WriteAllText(captured, "class Captured { int Drifted; }");
            files.ResolveFileVersionId(projectId, captured);
            files.ResolveFileVersionId(projectId, resolved);
            files.ResolveFileVersionId(projectId, Path.Combine(source, "Missing.cs"));

            CollectionAssert.AreEqual(Bytes("class Captured { }"), texts.TryGet(capturedHash));
            CollectionAssert.AreEqual(Bytes("class Resolved { }"), texts.TryGet(SHA256.HashData(Bytes("class Resolved { }"))));
            Assert.AreEqual(2, Directory.EnumerateFiles(texts.Root, "*.br", SearchOption.AllDirectories).Count(),
                "a missing file stores nothing");

            var referenced = files.ReferencedSourceTextKeys();
            Assert.AreEqual(3, referenced.Count, "the missing file's placeholder hash is referenced too");
            Assert.IsTrue(referenced.Contains(SourceTextStore.SweepKey(capturedHash)));
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    public void FileStore_WithoutAStore_KeepsNothing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sextant_srctext_{Guid.NewGuid():N}.db");
        var db = new IndexDatabase(dbPath);
        try
        {
            db.RunMigrations();
            var conn = db.GetConnection();
            var source = Path.Combine(_root, "repo", "src", "App");
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "Plain.cs");
            File.WriteAllText(file, "class Plain { }");

            new FileStore(conn).ResolveFileVersionId(SeedProject(conn, Path.Combine(source, "App.csproj")), file);

            Assert.IsFalse(Directory.EnumerateFiles(_root, "*.br", SearchOption.AllDirectories).Any());
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    private static long SeedProject(SqliteConnection conn, string diskPath) => new ProjectStore(conn).Insert(new ProjectIdentity
    {
        CanonicalId = "srctext000000001",
        GitRemoteUrl = "https://github.com/org/app",
        RepoRelativePath = "src/App/App.csproj",
        DiskPath = diskPath,
        TargetFramework = "net10.0"
    }, 1);

    private string BlobPath(byte[] content)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(content));
        return Path.Combine(_root, hex[..2], hex + ".br");
    }

    private static void WriteBlob(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var brotli = new BrotliStream(file, CompressionLevel.Fastest);
        brotli.Write(content);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
