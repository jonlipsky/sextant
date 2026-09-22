using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Benchmarks.Tests;

[TestClass]
public sealed class MetricsStoreTests
{
    [TestMethod]
    public void CollectCountsDuplicateReferenceOccurrences()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sextant-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");
        try
        {
            using var db = new IndexDatabase(dbPath);
            db.RunMigrations();
            var conn = db.GetConnection();

            // Insert reference rows without parent rows; disable FK enforcement on this connection.
            Execute(conn, "PRAGMA foreign_keys = OFF;");
            // Two identical occurrences (symbol 1, a.cs, line 10, call) plus one distinct occurrence.
            InsertReference(conn, symbolId: 1, file: "a.cs", line: 10, kind: "call");
            InsertReference(conn, symbolId: 1, file: "a.cs", line: 10, kind: "call");
            InsertReference(conn, symbolId: 1, file: "a.cs", line: 11, kind: "call");

            var metrics = new IndexMetricsStore(conn).Collect();

            Assert.AreEqual(3, metrics.References, "total reference rows");
            Assert.AreEqual(2, metrics.DistinctReferenceOccurrences, "distinct occurrences");
            Assert.AreEqual(1, metrics.DuplicateReferenceRows, "duplicate rows");
            Assert.AreEqual(1.0 / 3.0, metrics.DuplicateReferenceRatio, 1e-9);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void InsertReference(SqliteConnection conn, long symbolId, string file, int line, string kind)
    {
        // Phase 7: a "reference" is an occurrence with a NULL source (enclosing) symbol. Distinctness in
        // IndexMetricsStore is (target_symbol_id, file_version_id, line, kind), so map each file name to
        // a stable synthetic file_version id and each kind label to its integer ordinal.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO occurrences
                (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags, last_indexed_at)
            VALUES (1, @target, NULL, @fv, @line, 0, @kind, 0, 0);
            """;
        cmd.Parameters.AddWithValue("@target", symbolId);
        cmd.Parameters.AddWithValue("@fv", (long)(uint)file.GetHashCode());
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@kind", kind == "call" ? 0 : 1);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
