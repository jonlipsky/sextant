using System.Diagnostics;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Benchmarks.Tests;

/// <summary>
/// End-to-end tests that load real generated projects through MSBuildWorkspace. These require
/// a NuGet restore of the generated corpus and are heavier than the unit tests.
/// </summary>
[TestClass]
public sealed class BenchmarkRunIntegrationTests
{
    private static string _sharedRoot = "";
    private static string _sharedSlnx = "";

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _sharedRoot = NewTempDir("shared");
        _sharedSlnx = CorpusGenerator.GenerateCorrectnessCorpus(_sharedRoot);
        Restore(_sharedSlnx);
    }

    [ClassCleanup]
    public static void ClassCleanup() => TryDelete(_sharedRoot);

    [TestMethod]
    public async Task FullAndIncrementalRunProduceMetrics()
    {
        var work = NewTempDir("full");
        try
        {
            var options = new BenchmarkOptions
            {
                Corpus = "correctness",
                WorkDir = work,
                SampleIntervalMs = 20
            };

            var report = await BenchmarkRunner.RunAsync(options);

            Assert.IsNotNull(report.FullIndex);
            var full = report.FullIndex!;
            Assert.AreEqual(IndexRunStatus.Completed, full.Status, full.FailureReason);
            Assert.IsTrue(full.Rows.Symbols > 0, "should extract symbols");
            Assert.IsTrue(full.Rows.References > 0, "should extract references");
            Assert.IsTrue(full.Storage.FinalDbBytes > 0, "final db should be > 0 bytes");
            Assert.IsTrue(
                full.Storage.PeakDbPlusWalBytes >= full.Storage.FinalDbBytes,
                "peak db+wal must be at least the final db size");
            Assert.IsTrue(
                full.Rows.DistinctReferenceOccurrences <= full.Rows.References,
                "distinct occurrences cannot exceed total references");
            Assert.AreEqual(
                full.Rows.References - full.Rows.DistinctReferenceOccurrences,
                full.Rows.DuplicateReferenceRows,
                "duplicate rows must equal total minus distinct");
            Assert.IsTrue(full.Phases.Any(p => p.Name == "extracting_references"), "reference phase recorded");
            Assert.IsTrue(full.Phases.All(p => p.Status == IndexRunStatus.Completed), "all phases completed");

            Assert.IsNotNull(report.IncrementalIndex);
            var inc = report.IncrementalIndex!;
            Assert.AreEqual(IndexRunStatus.Completed, inc.Status, inc.FailureReason);
            Assert.AreEqual(1, inc.ChangedFileCount);
            Assert.IsTrue(inc.Storage.FinalDbBytes > 0);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [TestMethod]
    public async Task CancellationStopsRunAndPreservesPhases()
    {
        var solution = await SolutionLoader.LoadSolutionAsync(_sharedSlnx);
        var dbDir = NewTempDir("cancel");
        try
        {
            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();

            var metrics = new IndexingMetrics { Mode = "full" };
            using var cts = new CancellationTokenSource();
            var progress = new CancelOnFirstReport(cts);
            var orchestrator = new IndexOrchestrator(db);

            OperationCanceledException? caught = null;
            try
            {
                await orchestrator.IndexSolutionAsync(solution, progress, metrics, cts.Token);
            }
            catch (OperationCanceledException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "a cancelled run should throw OperationCanceledException");
            Assert.AreNotEqual(
                IndexRunStatus.Completed, metrics.Status,
                "the orchestrator must not mark a cancelled run Completed");
            Assert.IsTrue(metrics.Phases.Count >= 1, "phases recorded before cancellation are preserved");
            Assert.AreEqual("registering_projects", metrics.Phases[0].Name);
        }
        finally
        {
            TryDelete(dbDir);
        }
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource cts) : IProgress<IndexingProgress>
    {
        public void Report(IndexingProgress value) => cts.Cancel();
    }

    private static void Restore(string solutionPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        stdoutTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the generated corpus failed (exit {process.ExitCode}): {stderr}");
    }

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant-bench-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
