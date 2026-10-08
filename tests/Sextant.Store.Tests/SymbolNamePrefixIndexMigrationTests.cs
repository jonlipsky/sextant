using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #196: migration 025 adds the search indexes and is identity-neutral. It advances the database schema
/// (<see cref="IndexDatabase.LatestSchemaVersion"/>) but not the schema folded into snapshot identities
/// (<see cref="IndexDatabase.SnapshotSchemaVersion"/>), so upgrading re-indexes nothing.
/// </summary>
[TestClass]
public class SymbolNamePrefixIndexMigrationTests
{
    private string _dbPath = null!;

    [TestInitialize]
    public void TestInitialize() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_test_{Guid.NewGuid():N}.db");

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath);

    [TestMethod]
    public void Migration025_CreatesTheNoCaseSearchIndexes()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();

        Assert.AreEqual("project_id|display_name:NOCASE", IndexColumns(db.GetConnection(), "ix_symbols_project_name_nocase"));
        Assert.AreEqual("remote_url:NOCASE", IndexColumns(db.GetConnection(), "ix_repositories_remote_url_nocase"));
    }

    [TestMethod]
    public void Migration025_UpgradesASchema24Database_KeepingItsRowsAndItsRunLedger()
    {
        using (var db = new IndexDatabase(_dbPath))
        {
            db.RunMigrations();
            var conn = db.GetConnection();
            Exec(conn, "DROP TABLE snapshot_job_timings; " +
                "DROP INDEX ix_symbols_project_name_nocase; DROP INDEX ix_repositories_remote_url_nocase; " +
                "DROP INDEX ix_snapshot_jobs_completed_at; ALTER TABLE symbols DROP COLUMN declaration; " +
                "DELETE FROM schema_version WHERE version >= 25;");
            var runs = new IndexRunStore(conn);
            Assert.AreEqual(1, runs.MarkComplete(runs.BeginRun("full", 1), 2, 1));
            Assert.AreEqual(24, db.CurrentSchemaVersion);
        }

        using (var db = new IndexDatabase(_dbPath))
        {
            db.RunMigrations();
            Assert.AreEqual(IndexDatabase.LatestSchemaVersion, db.CurrentSchemaVersion);
            Assert.IsNotNull(new IndexRunStore(db.GetConnection()).GetLastCompleteRun(), "an index-only migration keeps index_runs");
            Assert.IsNotNull(IndexColumns(db.GetConnection(), "ix_symbols_project_name_nocase"));
        }
    }

    [TestMethod]
    public void SnapshotSchemaVersion_IsTheLatestMigrationThatIsNotIdentityNeutral()
    {
        var versions = Migrations().Select(m => m.Version).ToList();

        Assert.AreEqual(versions.Max(), IndexDatabase.LatestSchemaVersion);
        Assert.AreEqual(versions.Where(v => !IndexDatabase.IdentityNeutralMigrations.Contains(v)).Max(), IndexDatabase.SnapshotSchemaVersion);
        Assert.Contains(25, IndexDatabase.IdentityNeutralMigrations);
        Assert.Contains(27, IndexDatabase.IdentityNeutralMigrations);
        Assert.Contains(28, IndexDatabase.IdentityNeutralMigrations);
        // Adding 025 left every snapshot identity (and so every published snapshot's reuse) as it was at 24.
        Assert.AreEqual(26, versions.Where(v => !IndexDatabase.IdentityNeutralMigrations.Contains(v)).Max());
    }

    [TestMethod]
    public void IdentityNeutralMigrations_OnlyCreateOrDropIndexes_OrCreateServiceJobTables()
    {
        // An identity-neutral migration must not change a table, a row or anything the indexer writes: otherwise a
        // snapshot reused across it would differ from one built after it. Besides index DDL it may create a declared
        // service job table (issues #267 and #273), which no snapshot reads.
        var telemetry = string.Join("|", IndexDatabase.ServiceJobTables.Select(Regex.Escape));
        var migrations = Migrations().ToDictionary(m => m.Version, m => m.Sql);
        foreach (var version in IndexDatabase.IdentityNeutralMigrations)
        {
            Assert.IsTrue(migrations.TryGetValue(version, out var sql), $"migration {version} exists");
            var statements = Regex.Replace(sql, "--[^\n]*", "")
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.IsNotEmpty(statements, $"migration {version} has statements");
            foreach (var statement in statements)
                Assert.IsTrue(Regex.IsMatch(statement, @"^(CREATE\s+(UNIQUE\s+)?INDEX|DROP\s+INDEX)\s", RegexOptions.IgnoreCase)
                              || Regex.IsMatch(statement, $@"^CREATE\s+TABLE\s+({telemetry})\s*\(", RegexOptions.IgnoreCase),
                    $"migration {version} only creates or drops indexes or creates a service job table, but has: {statement}");
        }
    }

    private static IEnumerable<(int Version, string Sql)> Migrations()
    {
        var assembly = typeof(IndexDatabase).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var match = Regex.Match(name, @"^Sextant\.Store\.Migrations\.(\d{3})_.*\.sql$");
            if (!match.Success)
                continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (int.Parse(match.Groups[1].Value), reader.ReadToEnd());
        }
    }

    // "column[:COLLATION]|…" for the index's key columns, or null if there is no such index.
    private static string? IndexColumns(SqliteConnection conn, string index)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, coll FROM pragma_index_xinfo(@index) WHERE key = 1 ORDER BY seqno;";
        cmd.Parameters.AddWithValue("@index", index);
        var columns = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var collation = reader.GetString(1);
            columns.Add(collation == "BINARY" ? reader.GetString(0) : $"{reader.GetString(0)}:{collation}");
        }
        return columns.Count == 0 ? null : string.Join("|", columns);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
