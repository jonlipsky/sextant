using Microsoft.Data.Sqlite;
using SQLitePCL;
using Sextant.Store;
using static Sextant.Store.Tests.CrossRepoUsageSeed;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #160: migration 023 rebuilds <c>ix_occ_source</c> as a PARTIAL index over call edges only
/// (<c>WHERE source_symbol_id IS NOT NULL</c>), so a pure-reference predicate (<c>source_symbol_id IS
/// NULL</c>, most of the table) can never select it again. These tests pin the consequences:
/// <list type="bullet">
/// <item>the index DDL itself, and an upgrade from a schema-22 database that keeps data and index_runs;</item>
/// <item>every store query that filters on <c>source_symbol_id</c> keeps (or gains) its real driver index.
/// The SQL is captured from the real store methods with <c>sqlite3_profile</c>, then explained, so the
/// test never drifts from a mirrored copy;</item>
/// <item>the ON DELETE CASCADE child lookup for a deleted caller symbol still uses the partial index (zero
/// full-scan steps), so per-project/per-file deletes never become full scans.</item>
/// </list>
/// </summary>
[TestClass]
public class OccurrenceIndexPlanTests
{
    private const string PartialIndexPredicate = "WHERE source_symbol_id IS NOT NULL";

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private long _projectId;
    private long _snapshotId;
    private long _targetSymbolId;
    private long _callerSymbolId;
    private string _filePath = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_occ_plan_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        CrossRepoUsageSeed.Create(_conn, logicalProjects: 8, pureReferences: 4_000, targetUsages: 40);

        _projectId = Scalar("SELECT in_project_id FROM occurrences WHERE source_symbol_id IS NULL ORDER BY id LIMIT 1;");
        _snapshotId = Scalar($"SELECT snapshot_id FROM projects WHERE id = {_projectId};");
        _targetSymbolId = Scalar($"SELECT id FROM symbols WHERE symbol_key = '{TargetKey}';");
        _callerSymbolId = Scalar("SELECT source_symbol_id FROM occurrences WHERE source_symbol_id IS NOT NULL ORDER BY id LIMIT 1;");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT repo_relative_path FROM files WHERE project_id = {_projectId};";
        _filePath = (string)cmd.ExecuteScalar()!;
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    // ==== the index itself ======================================================================

    [TestMethod]
    public void OccurrenceSourceIndex_IsPartialOverCallEdges()
    {
        StringAssert.Contains(IndexSql(_conn, "ix_occ_source"), PartialIndexPredicate,
            "ix_occ_source must index call edges only, so `source_symbol_id IS NULL` can never select it (issue #160)");
        foreach (var index in new[] { "ix_occ_target", "ix_occ_project", "ix_occ_file_version" })
            Assert.IsFalse(IndexSql(_conn, index).Contains(" WHERE "), $"{index} must stay a full index");
    }

    [TestMethod]
    public void Migration023_UpgradesASchema22Database_InPlace_KeepingDataAndRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_occ_v22_{Guid.NewGuid():N}.db");
        IndexDatabase? db = null;
        try
        {
            // A genuine schema-22 database: embedded migrations 001-022 only, then a seeded catalog and
            // a complete generation on the run ledger, so the upgrade must keep it servable.
            db = new IndexDatabase(path);
            var conn = db.GetConnection();
            ApplyMigrationsThrough(conn, 22);
            Assert.IsFalse(IndexSql(conn, "ix_occ_source").Contains(" WHERE "), "precondition: the schema-22 full index");
            CrossRepoUsageSeed.Create(conn, logicalProjects: 8, pureReferences: 4_000, targetUsages: 40);
            var runs = new IndexRunStore(conn);
            var runId = runs.BeginRun("full", startedAt: 10);
            Assert.AreEqual(1, runs.MarkComplete(runId, completedAt: 20, projects: 1));
            var occurrences = Scalar(conn, "SELECT COUNT(*) FROM occurrences;");
            var callEdges = Scalar(conn, "SELECT COUNT(*) FROM occurrences WHERE source_symbol_id IS NOT NULL;");
            var ledger = Scalar(conn, "SELECT COUNT(*) FROM index_runs;");
            Assert.AreEqual(22, db.CurrentSchemaVersion);

            SqliteTestDatabase.Delete(null, db);
            db = new IndexDatabase(path);
            db.RunMigrations();
            conn = db.GetConnection();

            Assert.AreEqual(IndexDatabase.LatestSchemaVersion, db.CurrentSchemaVersion);
            StringAssert.Contains(IndexSql(conn, "ix_occ_source"), PartialIndexPredicate, "the upgrade rebuilds ix_occ_source as partial");
            Assert.AreEqual(occurrences, Scalar(conn, "SELECT COUNT(*) FROM occurrences;"), "the upgrade must not touch occurrence rows");
            Assert.AreEqual(callEdges,
                Scalar(conn, "SELECT COUNT(*) FROM occurrences INDEXED BY ix_occ_source WHERE source_symbol_id IS NOT NULL;"),
                "the partial index covers every call edge");
            Assert.AreEqual(ledger, Scalar(conn, "SELECT COUNT(*) FROM index_runs;"), "migration 023 is not rebuild-required: index_runs is kept");
            Assert.AreEqual(runId, new IndexRunStore(conn).GetLastCompleteRun()?.Id, "the complete generation stays published");
            Assert.IsTrue(db.CheckReadiness().Ready, "an upgraded database stays servable without a rebuild");
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            Assert.AreEqual("ok", check.ExecuteScalar());
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    // ==== the swept queries: real store SQL, explained ============================================

    [TestMethod]
    public void ReferenceStoreReads_KeepTheirDriverIndex()
    {
        var refs = new ReferenceStore(_conn);
        AssertPlans("references by symbol", () => refs.GetBySymbolId(_targetSymbolId), "SEARCH o USING INDEX ix_occ_target (target_symbol_id=?)");
        AssertPlans("references by project", () => refs.GetByProject(_projectId), "SEARCH o USING INDEX ix_occ_project (in_project_id=?)");

        foreach (var (name, scope) in new[] { ("snapshot", new SnapshotReadScope(_snapshotId)), ("legacy", SnapshotReadScope.LegacyPinned) })
        {
            var scoped = new ReferenceStore(_conn) { Scope = scope };
            AssertPlans($"{name}-scoped references by symbol", () => scoped.GetBySymbolId(_targetSymbolId), "ix_occ_target");
            AssertPlans($"{name}-scoped references by project", () => scoped.GetByProject(_projectId), "ix_occ_project");
        }
    }

    [TestMethod]
    public void CrossProjectPairs_IsOneSequentialPass()
    {
        // Every pure reference is visited; an index walk would add a random table lookup per row.
        var pairs = AssertPlans("cross-project pairs", () => new ReferenceStore(_conn).GetCrossProjectPairs(),
            required: "SCAN o", allowScan: true);

        // Oracle: the same pairs via a different access path (symbol-driven through ix_occ_target).
        var expected = new List<(long, long)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT o.in_project_id, s.project_id
                FROM symbols s CROSS JOIN occurrences o INDEXED BY ix_occ_target ON o.target_symbol_id = s.id
                WHERE o.source_symbol_id IS NULL AND o.in_project_id != s.project_id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                expected.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }
        Assert.IsTrue(expected.Count > 0, "precondition: the seed has cross-project (consumer -> provider) references");
        CollectionAssert.AreEquivalent(expected, pairs);
    }

    [TestMethod]
    public void CallGraphReads_UseThePartialSourceIndexForCallers()
    {
        var calls = new CallGraphStore(_conn);
        var byCaller = AssertPlans("calls by caller", () => calls.GetByCaller(_callerSymbolId), "ix_occ_source (source_symbol_id=?)");
        Assert.IsTrue(byCaller.Count > 0, "the caller has call edges");
        AssertPlans("calls by callee", () => calls.GetByCallee(_targetSymbolId), "SEARCH o USING INDEX ix_occ_target (target_symbol_id=?)");
        var scoped = new CallGraphStore(_conn) { Scope = new SnapshotReadScope(_snapshotId) };
        AssertPlans("snapshot-scoped calls by caller", () => scoped.GetByCaller(_callerSymbolId), "ix_occ_source (source_symbol_id=?)");
    }

    [TestMethod]
    public void UnreferencedSymbols_ProbeOccurrencesByTarget()
    {
        var symbols = new SymbolStore(_conn);
        AssertPlans("unreferenced symbols", () => symbols.GetUnreferenced(_projectId, null, excludeTestProjects: false, accessibility: null),
            "ix_occ_target (target_symbol_id=?)");
    }

    [TestMethod]
    public void IndexStatusReferenceCount_UsesTheProjectIndex()
    {
        // Mirror of the reference_count subquery in Sextant.Mcp's GetIndexStatusTool (not referenced by
        // this test project). Keep in sync if that query changes.
        var plan = string.Join("\n", QueryPlan.Explain(_conn, """
            SELECT p.id,
                   (SELECT COUNT(*) FROM occurrences WHERE in_project_id = p.id AND source_symbol_id IS NULL) AS reference_count
            FROM projects p WHERE p.snapshot_id = @snap;
            """));
        StringAssert.Contains(plan, "SEARCH occurrences USING INDEX ix_occ_project (in_project_id=?)", $"Plan:\n{plan}");
        Assert.IsFalse(plan.Contains("ix_occ_source"), $"Plan:\n{plan}");
    }

    [TestMethod]
    public void IndexMetrics_AreCorrect_AndNeverUseTheSourceIndexForPureReferences()
    {
        var expectedTotal = Scalar("SELECT COUNT(*) FROM occurrences NOT INDEXED WHERE source_symbol_id IS NULL;");
        var expectedDistinct = Scalar("""
            SELECT COUNT(*) FROM (SELECT DISTINCT target_symbol_id, file_version_id, line, kind
                                  FROM occurrences NOT INDEXED WHERE source_symbol_id IS NULL);
            """);
        var expectedCalls = Scalar("SELECT COUNT(*) FROM occurrences NOT INDEXED WHERE source_symbol_id IS NOT NULL;");

        var metrics = AssertPlans("index metrics", () => new IndexMetricsStore(_conn).Collect(), required: "SCAN occurrences", allowScan: true);

        Assert.AreEqual(expectedTotal, metrics.References);
        Assert.AreEqual(expectedDistinct, metrics.DistinctReferenceOccurrences);
        Assert.AreEqual(expectedTotal - expectedDistinct, metrics.DuplicateReferenceRows);
        Assert.AreEqual(expectedCalls, metrics.CallGraphEdges);
    }

    [TestMethod]
    public void PerFileDeletes_AreIndexDriven()
    {
        var refs = new ReferenceStore(_conn);
        var calls = new CallGraphStore(_conn);
        AssertPlans("reference delete by file", () => refs.DeleteByFile(_filePath), "SEARCH occurrences USING INDEX ix_occ_file_version");
        AssertPlans("reference delete by file in project", () => refs.DeleteByFile(_filePath, _projectId), "SEARCH occurrences USING INDEX");
        AssertPlans("call delete by file", () => calls.DeleteByFile(_filePath), "SEARCH occurrences USING INDEX ix_occ_file_version");
        AssertPlans("call delete by file in project", () => calls.DeleteByFile(_filePath, _projectId), "SEARCH occurrences USING INDEX");
    }

    // ==== the delete-path FK cascade ============================================================

    [TestMethod]
    public void DeletingACallerSymbol_CascadesThroughThePartialIndex_WithoutAFullScan()
    {
        Assert.IsTrue(Scalar($"SELECT COUNT(*) FROM occurrences WHERE source_symbol_id = {_callerSymbolId};") > 0, "precondition");

        Assert.AreEqual(0, FullScanStepsOf($"DELETE FROM symbols WHERE id = {_callerSymbolId};"),
            "the ON DELETE CASCADE lookup on occurrences.source_symbol_id must use the partial ix_occ_source");
        Assert.AreEqual(0, Scalar($"SELECT COUNT(*) FROM occurrences WHERE source_symbol_id = {_callerSymbolId};"), "the cascade removed the call edges");

        // Sensitivity check: with no source index at all, the same cascade scans the whole table.
        var nextCaller = Scalar("SELECT source_symbol_id FROM occurrences WHERE source_symbol_id IS NOT NULL ORDER BY id LIMIT 1;");
        Exec("DROP INDEX ix_occ_source;");
        Assert.IsTrue(FullScanStepsOf($"DELETE FROM symbols WHERE id = {nextCaller};") > 0,
            "without an index the cascade is a full scan, so the zero above is meaningful");
    }

    // ==== helpers ===============================================================================

    /// <summary>
    /// Runs <paramref name="action"/> while capturing every statement it executes, explains each one
    /// that reads or writes <c>occurrences</c>, and asserts: at least one plan contains
    /// <paramref name="required"/>; a pure-reference statement (<c>source_symbol_id IS NULL</c>) never
    /// walks <c>ix_occ_source</c> except through its call-edge-only <c>IS NOT NULL</c> range; and, unless
    /// <paramref name="allowScan"/>, no statement full-scans occurrences.
    /// </summary>
    private T AssertPlans<T>(string label, Func<T> action, string required, bool allowScan = false)
    {
        T result = default!;
        var statements = Capture(() => result = action())
            .Where(sql => sql.Contains("occurrences", StringComparison.Ordinal))
            .ToList();
        Assert.IsTrue(statements.Count > 0, $"{label}: no occurrences statement was captured");

        var plans = statements.Select(sql => (sql, plan: QueryPlan.Explain(_conn, sql))).ToList();
        var all = string.Join("\n---\n", plans.Select(p => string.Join("\n", p.plan)));
        Assert.IsTrue(all.Contains(required, StringComparison.Ordinal), $"{label}: expected '{required}'. Plans:\n{all}");
        foreach (var (sql, plan) in plans)
        {
            var text = string.Join("\n", plan);
            // A pure-reference statement may still count call edges through the partial index's
            // IS NOT NULL range (`source_symbol_id>?`, which visits only call edges); any other use of
            // ix_occ_source there is the #160 trap.
            if (sql.Contains("source_symbol_id IS NULL", StringComparison.Ordinal))
                Assert.IsFalse(plan.Any(l => l.Contains("ix_occ_source", StringComparison.Ordinal) && !l.Contains("(source_symbol_id>?)", StringComparison.Ordinal)),
                    $"{label}: a pure-reference statement walked ix_occ_source (issue #160). Plan:\n{text}");
            if (!allowScan)
                Assert.IsFalse(plan.Any(l => l is "SCAN o" or "SCAN occurrences"), $"{label}: full scan of occurrences. Plan:\n{text}");
        }
        return result;
    }

    private void AssertPlans(string label, Action action, string required) =>
        AssertPlans<object?>(label, () => { action(); return null; }, required);

    /// <summary>The original text of every statement <paramref name="action"/> runs, via sqlite3_profile.</summary>
    private List<string> Capture(Action action)
    {
        var statements = new List<string>();
        strdelegate_profile hook = (_, sql, _) => statements.Add(sql);
        raw.sqlite3_profile(_conn.Handle, hook, null);
        try
        {
            action();
        }
        finally
        {
            raw.sqlite3_profile(_conn.Handle, (strdelegate_profile)null!, null);
            GC.KeepAlive(hook);
        }
        return statements;
    }

    /// <summary>Runs one statement through the raw API and returns its SQLITE_STMTSTATUS_FULLSCAN_STEP
    /// count, which includes the FK-cascade and trigger sub-programs it runs.</summary>
    private int FullScanStepsOf(string sql)
    {
        Assert.AreEqual(raw.SQLITE_OK, raw.sqlite3_prepare_v2(_conn.Handle, sql, out sqlite3_stmt stmt), raw.sqlite3_errmsg(_conn.Handle).utf8_to_string());
        try
        {
            Assert.AreEqual(raw.SQLITE_DONE, raw.sqlite3_step(stmt), raw.sqlite3_errmsg(_conn.Handle).utf8_to_string());
            return raw.sqlite3_stmt_status(stmt, raw.SQLITE_STMTSTATUS_FULLSCAN_STEP, 0);
        }
        finally
        {
            raw.sqlite3_finalize(stmt);
        }
    }

    private static string IndexSql(SqliteConnection conn, string index)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @name;";
        cmd.Parameters.AddWithValue("@name", index);
        return cmd.ExecuteScalar() as string ?? throw new AssertFailedException($"index {index} does not exist");
    }

    private long Scalar(string sql) => Scalar(_conn, sql);

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Applies the embedded migrations up to <paramref name="lastVersion"/> exactly as
    /// <see cref="IndexDatabase.RunMigrations"/> would, recording each version.</summary>
    private static void ApplyMigrationsThrough(SqliteConnection conn, int lastVersion)
    {
        const string prefix = "Sextant.Store.Migrations.";
        var assembly = typeof(IndexDatabase).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (version: int.Parse(n[prefix.Length..].Split('_')[0]), name: n))
            .Where(m => m.version <= lastVersion)
            .OrderBy(m => m.version);

        using (var table = conn.CreateCommand())
        {
            table.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL, applied_at INTEGER NOT NULL);";
            table.ExecuteNonQuery();
        }
        foreach (var (version, name) in migrations)
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = reader.ReadToEnd() + $"\nINSERT INTO schema_version (version, applied_at) VALUES ({version}, 1);";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
