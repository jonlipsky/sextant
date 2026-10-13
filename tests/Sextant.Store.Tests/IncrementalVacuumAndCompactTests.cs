using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>Disk give-back (issue #252): incremental vacuum and the offline catalog compaction.</summary>
[TestClass]
public class IncrementalVacuumAndCompactTests
{
    private readonly List<string> _files = [];

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _files)
            foreach (var suffix in new[] { "", "-wal", "-shm", ".compacting" })
                if (File.Exists(f + suffix)) File.Delete(f + suffix);
    }

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_vac_{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private string SeededDb(string autoVacuum, int rows = 3000, int pageSize = 4096, int blobBytes = 2000)
    {
        var path = TempFile();
        using var c = Open(path);
        // page_size must come first: auto_vacuum fixes the header, after which page_size is silently ignored.
        Exec(c, $"PRAGMA page_size = {pageSize};");
        Exec(c, $"PRAGMA auto_vacuum = {autoVacuum};");
        Exec(c, "PRAGMA journal_mode = WAL;");
        Exec(c, "CREATE TABLE t (id INTEGER PRIMARY KEY, payload BLOB NOT NULL);");
        Exec(c, $"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {rows})
            INSERT INTO t (id, payload) SELECT i, zeroblob({blobBytes}) FROM n;
            """);
        Exec(c, "DELETE FROM t WHERE id % 3 <> 0;");
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        return path;
    }

    [TestMethod]
    public void NewCatalog_IsCreatedIncremental()
    {
        var path = TempFile();
        using var db = new IndexDatabase(path);
        db.RunMigrations();
        Assert.AreEqual(IncrementalVacuum.AutoVacuumIncremental, IncrementalVacuum.AutoVacuumMode(db.GetConnection()));
    }

    [TestMethod]
    public void Step_ShrinksAnIncrementalDatabase_InBoundedSteps()
    {
        var path = SeededDb("INCREMENTAL", rows: 15000, pageSize: 512, blobBytes: 400);
        using var c = Open(path);
        var free = IncrementalVacuum.FreelistPages(c);
        var pagesBefore = Scalar(c, "PRAGMA page_count;");
        Assert.IsTrue(free > IncrementalVacuum.DefaultStepPages);

        var freed = IncrementalVacuum.Step(c);
        Assert.AreEqual(IncrementalVacuum.DefaultStepPages, freed, "one step returns at most the step size");
        Assert.AreEqual(free - IncrementalVacuum.DefaultStepPages, IncrementalVacuum.FreelistPages(c));

        while (IncrementalVacuum.Step(c) > 0) { }
        Assert.IsTrue(IncrementalVacuum.CheckpointTruncate(c));
        Assert.AreEqual(0, IncrementalVacuum.FreelistPages(c));
        Assert.IsTrue(Scalar(c, "PRAGMA page_count;") < pagesBefore);
        Assert.AreEqual(5000, Scalar(c, "SELECT COUNT(*) FROM t;"));
    }

    [TestMethod]
    public void CheckpointTruncate_WhenAReaderPinsTheWal_ReturnsFalseWithoutWaiting_AndSucceedsLater()
    {
        var path = SeededDb("INCREMENTAL");
        using var writer = Open(path);
        Exec(writer, "PRAGMA busy_timeout = 5000;");
        Exec(writer, "INSERT INTO t (id, payload) VALUES (99999, zeroblob(100));");
        using var reader = Open(path);
        Exec(reader, "BEGIN;");
        Assert.AreEqual(1001, Scalar(reader, "SELECT COUNT(*) FROM t;"));
        Exec(writer, "INSERT INTO t (id, payload) VALUES (100000, zeroblob(100));");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var complete = IncrementalVacuum.CheckpointTruncate(writer);
        Assert.IsFalse(complete);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(3), "the checkpoint must not wait busy_timeout");
        Assert.AreEqual(5000L, Scalar(writer, "PRAGMA busy_timeout;"), "the wait is restored");

        Exec(reader, "COMMIT;");
        Assert.IsTrue(IncrementalVacuum.CheckpointTruncate(writer));
    }

    [TestMethod]
    public void Step_IsANoOp_OnAutoVacuumNone()
    {
        var path = SeededDb("NONE");
        using var c = Open(path);
        var free = IncrementalVacuum.FreelistPages(c);
        var pages = Scalar(c, "PRAGMA page_count;");
        Assert.IsTrue(free > 0);
        Assert.AreEqual(0, IncrementalVacuum.Step(c));
        Assert.AreEqual(free, IncrementalVacuum.FreelistPages(c));
        Assert.AreEqual(pages, Scalar(c, "PRAGMA page_count;"));
    }

    [TestMethod]
    public void Compact_WritesAnIncrementalCheckedCopy_WithIdenticalRowCounts_AndNeverTouchesTheInput()
    {
        var input = SeededDb("NONE");
        var inputBytes = new FileInfo(input).Length;
        var inputStamp = File.GetLastWriteTimeUtc(input);
        var output = TempFile();

        var result = CatalogCompactor.Compact(input, output, fullIntegrityCheck: true, countRows: true);

        Assert.AreEqual(IncrementalVacuum.AutoVacuumNone, result.InputAutoVacuum);
        Assert.AreEqual(IncrementalVacuum.AutoVacuumIncremental, result.OutputAutoVacuum);
        Assert.IsTrue(result.CheckPassed, result.CheckResult);
        Assert.IsTrue(result.RowCountsMatch);
        Assert.AreEqual(1000L, result.RowCounts!["t"].Output);
        Assert.IsTrue(result.OutputBytes < result.InputBytes, "the free pages are not copied");
        Assert.IsTrue(File.Exists(output));
        Assert.IsFalse(File.Exists(output + ".compacting"));
        Assert.AreEqual(inputBytes, new FileInfo(input).Length);
        Assert.AreEqual(inputStamp, File.GetLastWriteTimeUtc(input));

        using var copy = Open(output);
        Assert.AreEqual(IncrementalVacuum.AutoVacuumIncremental, IncrementalVacuum.AutoVacuumMode(copy));
        Assert.AreEqual(1000, Scalar(copy, "SELECT COUNT(*) FROM t;"));
    }

    [TestMethod]
    public void Compact_RefusesAStaleStagingFile_AndLeavesItAlone()
    {
        var input = SeededDb("NONE");
        var output = TempFile();
        File.WriteAllText(output + ".compacting", "not ours");
        var ex = Assert.ThrowsExactly<IOException>(() => CatalogCompactor.Compact(input, output));
        StringAssert.Contains(ex.Message, output + ".compacting");
        Assert.AreEqual("not ours", File.ReadAllText(output + ".compacting"));
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public void Compact_RefusesAHeldWriterLease()
    {
        var input = SeededDb("NONE");
        using (var c = Open(input))
        {
            Exec(c, "CREATE TABLE writer_lease (id INTEGER PRIMARY KEY, owner_token TEXT, holder TEXT, acquired_at INTEGER, heartbeat_at INTEGER, expires_at INTEGER);");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Exec(c, $"INSERT INTO writer_lease VALUES (1, 'tok', 'service', {now}, {now}, {now + 600_000});");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var output = TempFile();
        var ex = Assert.ThrowsExactly<IOException>(() => CatalogCompactor.Compact(input, output));
        StringAssert.Contains(ex.Message, "writer lease");
        Assert.IsFalse(File.Exists(output));
        Assert.IsFalse(File.Exists(output + ".compacting"));

        using (var c = Open(input))
            Exec(c, "UPDATE writer_lease SET expires_at = 1;");
        CatalogCompactor.Compact(input, output);
        Assert.IsTrue(File.Exists(output), "an expired lease does not block");
    }

    [TestMethod]
    public void Compact_RefusesAHotWal()
    {
        var input = SeededDb("NONE");
        using var c = Open(input);
        Exec(c, "PRAGMA wal_autocheckpoint = 0;");
        Exec(c, "INSERT INTO t (id, payload) VALUES (500000, zeroblob(100));");
        var output = TempFile();
        var ex = Assert.ThrowsExactly<IOException>(() => CatalogCompactor.Compact(input, output));
        StringAssert.Contains(ex.Message, "write-ahead");
        Assert.IsFalse(File.Exists(output + ".compacting"));
    }

    [TestMethod]
    public void Compact_WhenCancelled_RemovesItsStagingFiles_AndWritesNoOutput()
    {
        var input = SeededDb("NONE");
        var output = TempFile();
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            CatalogCompactor.Compact(input, output, log: m => { if (m.StartsWith("Writing", StringComparison.Ordinal)) cancel.Cancel(); },
                cancellationToken: cancel.Token));
        Assert.IsFalse(File.Exists(output));
        foreach (var suffix in new[] { ".compacting", ".compacting-wal", ".compacting-shm" })
            Assert.IsFalse(File.Exists(output + suffix), suffix);

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            CatalogCompactor.Compact(input, output, cancellationToken: new CancellationToken(true)));
        Assert.IsFalse(File.Exists(output + ".compacting"));
    }

    [TestMethod]
    public void Compact_RefusesAnExistingOutput_AndTheInputItself()

    {
        var input = SeededDb("NONE");
        var existing = TempFile();
        File.WriteAllText(existing, "x");
        Assert.ThrowsExactly<IOException>(() => CatalogCompactor.Compact(input, existing));
        Assert.ThrowsExactly<ArgumentException>(() => CatalogCompactor.Compact(input, input));
        Assert.AreEqual("x", File.ReadAllText(existing));
    }
}
