using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Migration 026 adds <c>symbols.declaration</c>, the C# declaration the tools print as a member's <c>signature</c>.
/// It must stay a metadata-only change (a 17 GB catalog upgrades instantly), keep every existing row, and advance the
/// snapshot schema so each repository re-indexes once.
/// </summary>
[TestClass]
public class SymbolDeclarationMigrationTests
{
    private string _dbPath = null!;

    [TestInitialize]
    public void TestInitialize() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_test_{Guid.NewGuid():N}.db");

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath);

    [TestMethod]
    public void Migration026_IsOneAddColumnWithNoDefaultAndNoBackfill()
    {
        var sql = MigrationSql(26);
        var statements = Regex.Replace(sql, "--[^\n]*", "")
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // SQLite's ADD COLUMN only rewrites the table's schema text when the column has no DEFAULT (or a constant one)
        // and no constraint that needs a scan; an UPDATE would rewrite every row.
        Assert.HasCount(1, statements);
        Assert.IsTrue(Regex.IsMatch(statements[0], @"^ALTER\s+TABLE\s+symbols\s+ADD\s+COLUMN\s+declaration\s+TEXT$", RegexOptions.IgnoreCase),
            statements[0]);
    }

    [TestMethod]
    public void Migration026_AdvancesTheSnapshotSchema()
    {
        Assert.IsFalse(IndexDatabase.IdentityNeutralMigrations.Contains(26), "026 changes what the indexer stores");
        Assert.AreEqual(26, IndexDatabase.SnapshotSchemaVersion);
        Assert.AreEqual(28, IndexDatabase.LatestSchemaVersion);
    }

    [TestMethod]
    public void Migration026_UpgradesASchema25Database_KeepingRowsWithANullDeclaration()
    {
        long symbolId;
        using (var db = new IndexDatabase(_dbPath))
        {
            db.RunMigrations();
            var conn = db.GetConnection();
            symbolId = new SymbolStore(conn).Insert(Symbol(NewProject(conn), declaration: null));
            var runs = new IndexRunStore(conn);
            Assert.AreEqual(1, runs.MarkComplete(runs.BeginRun("full", 1), 2, 1));
            // Back to the schema a pre-026 build left: no column, version 25.
            Exec(conn, "DROP TABLE snapshot_job_timings; DROP INDEX ix_snapshot_jobs_completed_at; " +
                "ALTER TABLE symbols DROP COLUMN declaration; " +
                "DELETE FROM schema_version WHERE version >= 26;");
            Assert.AreEqual(25, db.CurrentSchemaVersion);
        }

        using (var db = new IndexDatabase(_dbPath))
        {
            db.RunMigrations();
            Assert.AreEqual(28, db.CurrentSchemaVersion);
            Assert.IsNotNull(new IndexRunStore(db.GetConnection()).GetLastCompleteRun(), "026 keeps index_runs");

            var symbol = new SymbolStore(db.GetConnection()).GetById(symbolId);
            Assert.IsNotNull(symbol);
            Assert.IsNull(symbol.Declaration, "a pre-026 row has no declaration until its snapshot is re-indexed");
            Assert.AreEqual("App.Store.Get(string)", symbol.Signature);
        }
    }

    [TestMethod]
    public void Declaration_RoundTripsThroughInsertAndUpsert()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var projectId = NewProject(conn);
        var store = new SymbolStore(conn);
        var id = store.Insert(Symbol(projectId, "Task<int> Get(string id, CancellationToken cancellationToken = default)"));
        Assert.AreEqual("Task<int> Get(string id, CancellationToken cancellationToken = default)", store.GetById(id)!.Declaration);

        Assert.AreEqual(id, store.Insert(Symbol(projectId, "Task<long> Get(string id)")));
        Assert.AreEqual("Task<long> Get(string id)", store.GetById(id)!.Declaration, "an upsert replaces the declaration");
    }

    private static long NewProject(SqliteConnection conn) => new ProjectStore(conn).Insert(new ProjectIdentity
    {
        CanonicalId = "decl0000000000aa",
        GitRemoteUrl = "https://github.com/test/repo",
        RepoRelativePath = "src/App/App.csproj",
        DiskPath = "/repo/src/App/App.csproj"
    }, 1);
    private static SymbolInfo Symbol(long projectId, string? declaration) => new()
    {
        ProjectId = projectId,
        SymbolKey = "M:App.Store.Get(System.String)",
        FullyQualifiedName = "Get",
        DisplayName = "Get",
        Kind = SymbolKind.Method,
        Accessibility = Accessibility.Public,
        Signature = "App.Store.Get(string)",
        SignatureHash = "h",
        Declaration = declaration,
        FilePath = "/repo/src/App/Store.cs",
        LineStart = 3,
        LineEnd = 3,
        LastIndexedAt = 1
    };

    private static string MigrationSql(int version)
    {
        var assembly = typeof(IndexDatabase).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => Regex.IsMatch(n, $@"^Sextant\.Store\.Migrations\.{version:D3}_.*\.sql$"));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
