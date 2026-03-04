using Microsoft.Data.Sqlite;

namespace Sextant.Store.Tests;

[TestClass]
public class IndexDatabaseTests
{
    private string _dbPath = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_test_{Guid.NewGuid():N}.db");
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [TestMethod]
    public void DatabaseCreated_WithWalMode()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = cmd.ExecuteScalar()?.ToString();
        Assert.AreEqual("wal", mode);
    }

    [TestMethod]
    public void MigrationRunner_AppliesMigrationsInOrder()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.IsTrue(version >= 1);
    }

    [TestMethod]
    public void MigrationRunner_TracksSchemaVersion()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();

        // Running migrations again should be a no-op
        db.RunMigrations();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.AreEqual(6, count); // One row per migration
    }

    [TestMethod]
    public void AllTablesCreated()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();

        var tables = new[] { "projects", "symbols", "references", "relationships", "call_graph", "file_index", "schema_version", "project_dependencies", "api_surface_snapshots", "solutions", "solution_projects", "comments", "argument_flow", "return_flow" };
        foreach (var table in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name = @name;";
            cmd.Parameters.AddWithValue("@name", table);
            var result = cmd.ExecuteScalar();
            Assert.IsNotNull(result);
        }
    }

    [TestMethod]
    public void ForeignKeyConstraints_AreEnforced()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var fk = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.AreEqual(1, fk);

        // Try inserting a symbol with invalid project_id
        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO symbols (project_id, fully_qualified_name, display_name, kind, accessibility, file_path, line_start, line_end, last_indexed_at)
            VALUES (99999, 'test', 'test', 'class', 'public', 'test.cs', 1, 1, 0);
            """;
        Assert.ThrowsExactly<SqliteException>(() => insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void ConcurrentReads_DontBlockDuringWrites()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();

        // Insert a project so we have data
        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = "INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at) VALUES ('abc', 'https://github.com/test', 'test.csproj', 0);";
        insertCmd.ExecuteNonQuery();

        // Open a read-only connection and verify it works while write connection is open
        using var readConn = db.CreateReadOnlyConnection();
        using var readCmd = readConn.CreateCommand();
        readCmd.CommandText = "SELECT COUNT(*) FROM projects;";
        var count = Convert.ToInt32(readCmd.ExecuteScalar());
        Assert.AreEqual(1, count);
    }
}
