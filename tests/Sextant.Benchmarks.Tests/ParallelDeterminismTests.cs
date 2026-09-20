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
            var sequentialDump = await IndexAndDumpAsync(
                slnx, Path.Combine(seqDir, "index.db"), ExtractionParallelismOptions.Sequential);

            // Parallel: force the bounded pipeline's parallel per-document path regardless of the host
            // core count so the comparison is meaningful even on a small CI box.
            var parallelOptions = new ExtractionParallelismOptions { MaxParallelism = 8, QueueCapacity = 4 };
            var parallelDump = await IndexAndDumpAsync(
                slnx, Path.Combine(parDir, "index.db"), parallelOptions);

            TestContext.WriteLine($"canonical dump length: sequential={sequentialDump.Length} parallel={parallelDump.Length}");
            Assert.IsTrue(sequentialDump.Length > 0, "the corpus produced an empty canonical dump");

            if (!string.Equals(sequentialDump, parallelDump, StringComparison.Ordinal))
                Assert.Fail("parallel extraction diverged from sequential extraction:\n" +
                            FirstDifference(sequentialDump, parallelDump));
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

    private static async Task<string> IndexAndDumpAsync(
        string slnx, string dbPath, ExtractionParallelismOptions parallelism)
    {
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var solution = await SolutionLoader.LoadSolutionAsync(slnx);
        await new IndexOrchestrator(db, useDocumentExtractor: true, parallelism: parallelism)
            .IndexSolutionAsync(solution);
        return CanonicalIndexDump.Dump(db.GetConnection());
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
