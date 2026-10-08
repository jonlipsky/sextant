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
        CorpusRestore.Restore(_sharedSlnx);
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
    public async Task MultiTargetProject_ProducesDistinctPerTfmRowsWithNoDataLoss()
    {
        var solution = await SolutionLoader.LoadSolutionAsync(_sharedSlnx);
        var dbDir = NewTempDir("multitfm");
        try
        {
            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();

            var orchestrator = new IndexOrchestrator(db);
            await orchestrator.IndexSolutionAsync(solution);

            var conn = db.GetConnection();
            var projectStore = new ProjectStore(conn);
            var symbolStore = new SymbolStore(conn);

            // (a) The two evaluated target frameworks of MultiTarget produce DISTINCT logical project
            // rows: distinct ids, distinct canonical ids, and the expected framework monikers.
            var multiTargetRows = projectStore.GetAll()
                .Where(p => p.project.RepoRelativePath.Contains("MultiTarget", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.AreEqual(2, multiTargetRows.Count,
                "A net10.0;netstandard2.0 project must yield two logical project rows (one per evaluated TFM).");
            CollectionAssert.AllItemsAreUnique(multiTargetRows.Select(r => r.id).ToList(),
                "Per-TFM project rows must have distinct ids.");
            CollectionAssert.AllItemsAreUnique(multiTargetRows.Select(r => r.project.CanonicalId).ToList(),
                "Per-TFM project rows must have distinct canonical ids.");
            CollectionAssert.AreEquivalent(
                new[] { "net10.0", "netstandard2.0" },
                multiTargetRows.Select(r => r.project.TargetFramework).ToList(),
                "Each project row must carry its evaluated target framework.");

            var net10 = multiTargetRows.Single(r => r.project.TargetFramework == "net10.0");
            var netStd = multiTargetRows.Single(r => r.project.TargetFramework == "netstandard2.0");

            List<SymbolInfo> PublicMembers(long projectId) =>
                symbolStore.GetByProjectAndAccessibility(projectId, "public");

            var net10Members = PublicMembers(net10.id);
            var netStdMembers = PublicMembers(netStd.id);

            // (c) The shared member exists under BOTH frameworks.
            var net10Join = net10Members.SingleOrDefault(s => s.DisplayName == "Join");
            var netStdJoin = netStdMembers.SingleOrDefault(s => s.DisplayName == "Join");
            Assert.IsNotNull(net10Join, "Shared member Join must exist under net10.0.");
            Assert.IsNotNull(netStdJoin, "Shared member Join must exist under netstandard2.0.");

            // (b) The #if NET10_0 conditional member exists ONLY under net10.0 — processing the second
            // framework must not have deleted the first framework's conditional symbols (no last-wins).
            Assert.IsTrue(net10Members.Any(s => s.DisplayName == "JoinModern"),
                "Conditional member JoinModern must exist under net10.0.");
            Assert.IsFalse(netStdMembers.Any(s => s.DisplayName == "JoinModern"),
                "Conditional member JoinModern must be absent under netstandard2.0.");

            // (d) The shared member has the same declaration key under both frameworks, and the catalog
            // resolves within a compilation to that framework's variant via preferProjectId.
            Assert.AreEqual(net10Join!.SymbolKey, netStdJoin!.SymbolKey,
                "The shared member has one declaration key; the TFM dimension lives in the project id.");

            var catalog = new SymbolCatalog();
            catalog.Add(net10.id, net10Join.SymbolKey, net10Join.Id);
            catalog.Add(netStd.id, netStdJoin.SymbolKey, netStdJoin.Id);

            var preferNet10 = catalog.Resolve(net10Join.SymbolKey, preferProjectId: net10.id);
            Assert.IsTrue(preferNet10.Found);
            Assert.IsFalse(preferNet10.Ambiguous, "A same-project (same-TFM) match is never ambiguous.");
            Assert.AreEqual(net10Join.Id, preferNet10.SymbolId, "Resolution must prefer the requesting TFM's variant.");

            // (e) Cross-TFM ambiguity (same declaration key in both variants, requesting neither)
            // still returns the deterministic pick (lowest project id) flagged ambiguous.
            var unrelatedProjectId = multiTargetRows.Max(r => r.id) + 1000;
            var ambiguous = catalog.Resolve(net10Join.SymbolKey, preferProjectId: unrelatedProjectId);
            Assert.IsTrue(ambiguous.Found);
            Assert.IsTrue(ambiguous.Ambiguous, "Same key in two TFM variants must be flagged ambiguous.");
            var lowerProjectId = Math.Min(net10.id, netStd.id);
            var expectedSymbolId = lowerProjectId == net10.id ? net10Join.Id : netStdJoin.Id;
            Assert.AreEqual(expectedSymbolId, ambiguous.SymbolId,
                "Ambiguous resolution is deterministic (the lowest project id's variant).");
        }
        finally
        {
            TryDelete(dbDir);
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

    [TestMethod]
    public async Task ServicePath_TwoSolutions_LoadAsTheServiceDoes_AndRecordTheLoad()
    {
        // Issue #267: --service-path selects, restores and loads like the service worker; two solutions in the
        // checkout make the default selection a union, so projects are opened individually.
        var root = NewTempDir("service-path");
        var work = NewTempDir("service-path-work");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            File.Copy(slnx, Path.Combine(Path.GetDirectoryName(slnx)!, "Second.slnx"));

            var report = await BenchmarkRunner.RunAsync(new BenchmarkOptions
            {
                Corpus = "correctness", SolutionPath = Path.GetDirectoryName(slnx), WorkDir = work,
                ServicePath = true, UseDocumentExtractor = true, SampleIntervalMs = 20
            });

            var full = report.FullIndex!;
            Assert.AreEqual(IndexRunStatus.Completed, full.Status, full.FailureReason);
            var load = report.Load!;
            Assert.AreEqual(SolutionLoadModes.Union, load.Mode);
            Assert.AreEqual(2, load.SolutionsSelected);
            Assert.AreEqual(0, load.ProjectsOpened, "issue #268: the union loads in one pass");
            Assert.IsGreaterThan(0, load.ProjectsLoaded);
            Assert.AreEqual(load.WallMs, full.SolutionLoadMs);
            if (OperatingSystem.IsLinux())
                Assert.IsGreaterThan(0, load.BuildHosts!.Launches);
            Assert.IsGreaterThan(0L, full.Phases.Single(p => p.Name == "extracting_symbols").CpuMs,
                "the symbol phase does real work, so it has measured CPU time");
            Assert.IsTrue(full.Projects.Any(p => p.Phase == "extracting_symbols"));
            Assert.IsTrue(full.Projects.Any(p => p.Phase == "extracting_occurrences"));
        }
        finally
        {
            TryDelete(root);
            TryDelete(work);
        }
    }

    [TestMethod]
    public async Task Catalog_IndexesIntoACopy_AndLeavesTheSourceUntouched()
    {
        var work = NewTempDir("catalog");
        var source = Path.Combine(NewTempDir("catalog-source"), "catalog.db");
        try
        {
            using (var seed = new IndexDatabase(source))
            {
                seed.RunMigrations();
                seed.Checkpoint();
            }
            var before = File.ReadAllBytes(source);

            var report = await BenchmarkRunner.RunAsync(new BenchmarkOptions
            {
                Corpus = "self", SolutionPath = _sharedSlnx, WorkDir = work, CatalogPath = source,
                Restore = false, SampleIntervalMs = 20
            });

            Assert.AreEqual(IndexRunStatus.Completed, report.FullIndex!.Status, report.FullIndex.FailureReason);
            Assert.IsGreaterThan(0L, report.FullIndex.Rows.Symbols, "the run wrote into the copy");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(source), "the source catalog is only read");
        }
        finally
        {
            TryDelete(work);
            TryDelete(Path.GetDirectoryName(source)!);
        }
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
