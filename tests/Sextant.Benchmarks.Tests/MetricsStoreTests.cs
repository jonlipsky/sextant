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
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "references" (symbol_id, in_project_id, file_path, line, reference_kind)
            VALUES (@symbol_id, 1, @file, @line, @kind);
            """;
        cmd.Parameters.AddWithValue("@symbol_id", symbolId);
        cmd.Parameters.AddWithValue("@file", file);
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
