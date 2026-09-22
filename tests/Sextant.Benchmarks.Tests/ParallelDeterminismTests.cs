using Microsoft.Data.Sqlite;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Benchmarks.Tests;

/// <summary>
/// Phase 6 acceptance criterion 1 (determinism) and criterion 4 (clean cancellation teardown) at the
/// full-orchestrator level. Criterion 1 is the hard bar: the canonical semantic output of the
/// document-oriented extractor must be byte-for-byte identical whether per-document analysis runs
/// single-threaded or across the bounded parallel pipeline. These load a real generated corpus through
/// MSBuildWorkspace (a NuGet restore per corpus), so they are heavy integration tests.
/// </summary>
[TestClass]
public sealed class ParallelDeterminismTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DocumentExtractor_ParallelOutput_IsByteEquivalentToSequential()
    {
        var root = NewTempDir("determinism");
        var seqDir = NewTempDir("determinism-seq");
        var parDir = NewTempDir("determinism-par");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            // Sequential: one document at a time (MaxParallelism = 1).
            var sequential = await IndexAndDumpAsync(
                slnx, Path.Combine(seqDir, "index.db"), ExtractionParallelismOptions.Sequential);

            // Parallel: force the bounded pipeline's parallel per-document path regardless of the host
            // core count so the comparison is meaningful even on a small CI box.
            var parallelOptions = new ExtractionParallelismOptions { MaxParallelism = 8, QueueCapacity = 4 };
            var parallel = await IndexAndDumpAsync(
                slnx, Path.Combine(parDir, "index.db"), parallelOptions);

            TestContext.WriteLine($"canonical dump length: sequential={sequential.Canonical.Length} parallel={parallel.Canonical.Length}");
            Assert.IsTrue(sequential.Canonical.Length > 0, "the corpus produced an empty canonical dump");

            // (a) Semantic multiset equivalence: the order-normalized canonical graph is identical.
            if (!string.Equals(sequential.Canonical, parallel.Canonical, StringComparison.Ordinal))
                Assert.Fail("parallel extraction diverged from sequential extraction (canonical):\n" +
                            FirstDifference(sequential.Canonical, parallel.Canonical));

            // (b) The hard bar (criterion 1): actual persisted ROW ORDER is byte-for-byte identical.
            // This dump orders by rowid (insertion order) and includes access_kind, so it catches both
            // a task-completion-order leak into persistence and any access-classification divergence
            // that the order-normalized canonical dump (which also omits access_kind) would hide.
            Assert.IsTrue(sequential.Ordered.Length > 0, "the corpus produced an empty ordered dump");
            if (!string.Equals(sequential.Ordered, parallel.Ordered, StringComparison.Ordinal))
                Assert.Fail("parallel extraction diverged from sequential extraction (row order):\n" +
                            FirstDifference(sequential.Ordered, parallel.Ordered));
        }
        finally
        {
            TryDelete(root);
            TryDelete(seqDir);
            TryDelete(parDir);
        }
    }

    [TestMethod]
    public async Task Cancellation_DuringOccurrencePhase_AbandonsGenerationWithoutDeadlock()
    {
        var root = NewTempDir("cancel");
        var dbDir = NewTempDir("cancel-db");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();
            var solution = await SolutionLoader.LoadSolutionAsync(slnx);

            using var cts = new CancellationTokenSource();
            // Cancel synchronously the first time the parallel pipeline's single consumer reports it is
            // persisting occurrences — i.e. mid-run, while the writer's staging generation is open.
            var progress = new SyncProgress(p =>
            {
                if (p.Phase == "extracting_occurrences")
                    cts.Cancel();
            });

            var orchestrator = new IndexOrchestrator(db, useDocumentExtractor: true,
                parallelism: new ExtractionParallelismOptions { MaxParallelism = 8, QueueCapacity = 4 });

            var run = orchestrator.IndexSolutionAsync(solution, progress, cancellationToken: cts.Token);
            var finished = await Task.WhenAny(run, Task.Delay(120_000));
            if (finished != run)
                Assert.Fail("cancelled index run did not tear down within the timeout — the writer deadlocked");

            await Assert.ThrowsAsync<OperationCanceledException>(() => run);

            // The staging generation must have been abandoned, never published: a reader still sees no
            // complete generation (this was the only run against a fresh DB).
            var lastComplete = new IndexRunStore(db.GetConnection()).GetLastCompleteRun();
            Assert.IsNull(lastComplete,
                "a cancelled run must not publish (mark complete) its staging generation");
        }
        finally
        {
            TryDelete(root);
            TryDelete(dbDir);
        }
    }

    private static async Task<(string Canonical, string Ordered)> IndexAndDumpAsync(
        string slnx, string dbPath, ExtractionParallelismOptions parallelism)
    {
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var solution = await SolutionLoader.LoadSolutionAsync(slnx);
        await new IndexOrchestrator(db, useDocumentExtractor: true, parallelism: parallelism)
            .IndexSolutionAsync(solution);
        var conn = db.GetConnection();
        return (CanonicalIndexDump.Dump(conn), RawOrderedDump(conn));
    }

    /// <summary>
    /// A row-order-sensitive dump of the unified <c>occurrences</c> table (references + call edges)
    /// plus <c>relationships</c>, ordered by <c>rowid</c> — i.e. the actual persistence order — with
    /// each foreign key projected to its stable semantic identity so two databases are comparable.
    /// Unlike the order-normalized <see cref="CanonicalIndexDump"/>, equality here proves the persisted
    /// ROW ORDER (not just the row set) is identical, and it includes <c>source</c> (call vs pure
    /// reference), <c>col</c> and the access <c>flags</c> so a read/write-classification or ordering
    /// divergence under parallelism cannot slip through. This is the byte-equivalence bar of criterion
    /// 1 for the parallel-vs-sequential comparison (both sides are the same extractor, so it carries no
    /// legacy-parity risk).
    /// </summary>
    private static string RawOrderedDump(SqliteConnection conn)
    {
        var sb = new System.Text.StringBuilder();

        AppendOrdered(sb, conn, "occurrences", """
            SELECT ip.canonical_id, tp.canonical_id, ts.symbol_key,
                   COALESCE(sp.canonical_id,''), COALESCE(ss.symbol_key,''),
                   COALESCE(f.repo_relative_path,''), o.line, o.col, o.kind, o.flags
            FROM occurrences o
            JOIN symbols ts ON o.target_symbol_id = ts.id
            JOIN projects tp ON ts.project_id = tp.id
            JOIN projects ip ON o.in_project_id = ip.id
            LEFT JOIN symbols ss ON o.source_symbol_id = ss.id
            LEFT JOIN projects sp ON ss.project_id = sp.id
            LEFT JOIN file_versions fv ON fv.id = o.file_version_id
            LEFT JOIN files f ON f.id = fv.file_id
            ORDER BY o.rowid
            """);

        AppendOrdered(sb, conn, "relationships", """
            SELECT fp.canonical_id, fs.symbol_key, tp.canonical_id, ts.symbol_key, rel.kind
            FROM relationships rel
            JOIN symbols fs ON rel.from_symbol_id = fs.id
            JOIN projects fp ON fs.project_id = fp.id
            JOIN symbols ts ON rel.to_symbol_id = ts.id
            JOIN projects tp ON ts.project_id = tp.id
            ORDER BY rel.rowid
            """);

        return sb.ToString();
    }

    private static void AppendOrdered(System.Text.StringBuilder sb, SqliteConnection conn, string table, string sql)
    {
        sb.Append("=== ").Append(table).Append(" (by rowid) ===\n");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) sb.Append('\u001f');
                sb.Append(reader.IsDBNull(i) ? "\u2205" : reader.GetValue(i)?.ToString() ?? "\u2205");
            }
            sb.Append('\n');
            count++;
        }
        sb.Append("(rows: ").Append(count).Append(")\n\n");
    }

    /// <summary>An <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting
    /// thread, so a test can act (e.g. cancel) deterministically at a known point in the run — unlike
    /// <see cref="Progress{T}"/>, which posts asynchronously to a captured context.</summary>
    private sealed class SyncProgress(Action<IndexingProgress> onReport) : IProgress<IndexingProgress>
    {
        public void Report(IndexingProgress value) => onReport(value);
    }

    private static string FirstDifference(string a, string b)
    {
        var aLines = a.Split('\n');
        var bLines = b.Split('\n');
        var max = Math.Max(aLines.Length, bLines.Length);
        for (var i = 0; i < max; i++)
        {
            var left = i < aLines.Length ? aLines[i] : "<eof>";
            var right = i < bLines.Length ? bLines[i] : "<eof>";
            if (!string.Equals(left, right, StringComparison.Ordinal))
                return $"first difference at line {i + 1}:\n  sequential: {left}\n  parallel:   {right}";
        }
        return "(dumps differ only in length)";
    }

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static void Restore(string solutionPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        stdoutTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the generated corpus failed (exit {process.ExitCode}): {stderr}");
    }
}
