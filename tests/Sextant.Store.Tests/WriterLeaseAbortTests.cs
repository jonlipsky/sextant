using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 17 slice 2, acceptance criterion 3 (no corrupt publication under writer loss): a batched
/// <see cref="IndexWriteSession"/> that discovers it LOST the single-writer lease (issue #38) at a batch
/// boundary rolls the open, uncommitted batch back and throws <see cref="WriterLeaseLostException"/> BEFORE
/// committing — so the generation/snapshot pointer flip the orchestrator runs in the final batch never
/// commits and nothing is published. Already-committed staging batches are left for recovery. With no probe
/// wired (the byte-identical single-node default) the session behaves exactly as before.
/// </summary>
[TestClass]
public class WriterLeaseAbortTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_leaseabort_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        Exec("CREATE TABLE scratch (id INTEGER PRIMARY KEY, v TEXT);");
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void Complete_WhenLeaseLost_RollsBackOpenBatch_AndThrows_WithoutCommitting()
    {
        var lost = false;
        using (var session = new IndexWriteSession(_conn, IndexWriteOptions.Default, isWriterLost: () => lost))
        {
            session.Begin();
            Insert("committed-batch");
            session.CommitBatch();        // first batch commits while the lease is still held

            Insert("doomed-batch");        // staged into the open final batch
            lost = true;                   // the lease is stolen mid-run

            Assert.ThrowsExactly<WriterLeaseLostException>(() => session.Complete());
        }

        // The committed batch survives; the doomed (uncommitted) batch was rolled back — no partial publish.
        Assert.AreEqual(1, Count("committed-batch"), "a batch committed before the loss is durable");
        Assert.AreEqual(0, Count("doomed-batch"), "the open batch at the moment of loss is rolled back, never committed");
    }

    [TestMethod]
    public void CommitBatch_WhenLeaseLost_AbortsBeforeCommittingTheBoundaryBatch()
    {
        var lost = true;
        using var session = new IndexWriteSession(_conn, IndexWriteOptions.Default, isWriterLost: () => lost);
        session.Begin();
        Insert("boundary-batch");

        Assert.ThrowsExactly<WriterLeaseLostException>(() => session.CommitBatch());
        Assert.AreEqual(0, Count("boundary-batch"), "a boundary commit under a lost lease is rolled back, not committed");
    }

    [TestMethod]
    public void NoProbe_BehavesIdentically_AndCommits()
    {
        using (var session = _db.BeginWriteSession())
        {
            session.Begin();
            Insert("normal");
            session.Complete();
        }
        Assert.AreEqual(1, Count("normal"), "with no writer-lost probe the session commits exactly as before");
    }

    [TestMethod]
    public void IndexDatabaseProbe_WhenLeaseLost_AbortsThroughBeginWriteSession()
    {
        var lost = false;
        _db.SetWriterLostProbe(() => lost);

        using var session = _db.BeginWriteSession();
        session.Begin();
        Insert("via-db");
        lost = true;

        Assert.ThrowsExactly<WriterLeaseLostException>(() => session.Complete());
        Assert.AreEqual(0, Count("via-db"), "the probe registered on IndexDatabase aborts the write before commit");
    }

    private void Insert(string v)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO scratch (v) VALUES (@v);";
        cmd.Parameters.AddWithValue("@v", v);
        cmd.ExecuteNonQuery();
    }

    private int Count(string v)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scratch WHERE v = @v;";
        cmd.Parameters.AddWithValue("@v", v);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
