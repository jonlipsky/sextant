using Microsoft.Data.Sqlite;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #271: the indexer's hot inserts run through one positionally bound, natively stepped statement. These pin that
/// it binds every value kind faithfully, carries nothing from one row to the next, surfaces SQLite errors as
/// <see cref="SqliteException"/> and stays usable afterwards, and runs inside the write session's transaction.
/// </summary>
[TestClass]
public class PreparedInsertTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_pi_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _conn = _db.GetConnection();
        Exec("CREATE TABLE t (id INTEGER PRIMARY KEY, n INTEGER, s TEXT, b BLOB, k TEXT UNIQUE);");
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    private const string Insert = "INSERT INTO t (n, s, b, k) VALUES (?1, ?2, ?3, ?4) RETURNING id;";

    [TestMethod]
    public void BindsEveryValueKind_AndReturnsTheNewId()
    {
        using var insert = new PreparedInsert(_conn, Insert);
        Assert.AreEqual(4, insert.Parameters);

        var id = insert.Bind(1, long.MaxValue).Bind(2, "héllo ☃").Bind(3, new byte[] { 0, 1, 255 }).Bind(4, "a")
            .ExecuteReturningId();

        using var read = _conn.CreateCommand();
        read.CommandText = "SELECT id, n, s, b FROM t;";
        using var r = read.ExecuteReader();
        Assert.IsTrue(r.Read());
        Assert.AreEqual(id, r.GetInt64(0));
        Assert.AreEqual(long.MaxValue, r.GetInt64(1));
        Assert.AreEqual("héllo ☃", r.GetString(2));
        CollectionAssert.AreEqual(new byte[] { 0, 1, 255 }, (byte[])r[3]);
    }

    [TestMethod]
    public void NullsAndBools_BindAsSqliteDoes()
    {
        using var insert = new PreparedInsert(_conn, Insert);
        insert.Bind(1, (long?)null).Bind(2, (string?)null).Bind(3, (byte[]?)null).Bind(4, "a").ExecuteReturningId();
        insert.Bind(1, true).Bind(4, "b").ExecuteReturningId();

        Assert.AreEqual(1L, Scalar("SELECT COUNT(*) FROM t WHERE n IS NULL AND s IS NULL AND b IS NULL;"));
        Assert.AreEqual(1L, Scalar("SELECT n FROM t WHERE k = 'b';"));
    }

    [TestMethod]
    public void ReuseCarriesNoValueFromThePreviousRow()
    {
        using var insert = new PreparedInsert(_conn, Insert);
        insert.Bind(1, 7).Bind(2, "first").Bind(4, "a").ExecuteReturningId();
        // Positions 1-3 left unbound on the second row must be NULL, not the first row's values.
        insert.Bind(4, "b").ExecuteReturningId();

        Assert.AreEqual(1L, Scalar("SELECT COUNT(*) FROM t WHERE k = 'b' AND n IS NULL AND s IS NULL;"));
    }

    [TestMethod]
    public void ConstraintViolation_ThrowsSqliteException_AndTheStatementStaysUsable()
    {
        using var insert = new PreparedInsert(_conn, Insert);
        insert.Bind(4, "dup").ExecuteReturningId();

        var ex = Assert.ThrowsExactly<SqliteException>(() => insert.Bind(4, "dup").ExecuteReturningId());
        Assert.AreEqual(19, ex.SqliteErrorCode, "SQLITE_CONSTRAINT");

        insert.Bind(4, "next").ExecuteReturningId();
        Assert.AreEqual(2L, Scalar("SELECT COUNT(*) FROM t;"));
    }

    [TestMethod]
    public void BadSql_ThrowsSqliteException()
        => Assert.ThrowsExactly<SqliteException>(() => new PreparedInsert(_conn, "INSERT INTO missing VALUES (?1);"));

    [TestMethod]
    public void RunsInsideTheWriteSessionTransaction()
    {
        using var insert = new PreparedInsert(_conn, Insert);
        using (var session = _db.BeginWriteSession())
        {
            session.Begin();
            insert.Bind(4, "kept").ExecuteReturningId();
            session.CommitBatch();
            insert.Bind(4, "rolled back").ExecuteReturningId();
            session.Rollback();
        }

        Assert.AreEqual(1L, Scalar("SELECT COUNT(*) FROM t;"));
        Assert.AreEqual(1L, Scalar("SELECT COUNT(*) FROM t WHERE k = 'kept';"));
    }

    [TestMethod]
    public void Execute_RunsAStatementWithoutResultRows()
    {
        using var insert = new PreparedInsert(_conn, "INSERT INTO t (k) VALUES (?1);");
        insert.Bind(1, "x").Execute();
        Assert.AreEqual(1L, Scalar("SELECT COUNT(*) FROM t;"));
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
