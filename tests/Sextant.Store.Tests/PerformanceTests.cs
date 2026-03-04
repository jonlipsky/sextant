using System.Diagnostics;
using Sextant.Core;

namespace Sextant.Store.Tests;

[TestClass]
public class PerformanceTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private ProjectStore _projectStore = null!;
    private SymbolStore _symbolStore = null!;
    private long _projectId;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_perf_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        _projectStore = new ProjectStore(conn);
        _symbolStore = new SymbolStore(conn);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _projectId = _projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "perf_test_proj001",
            GitRemoteUrl = "https://github.com/org/perf-test",
            RepoRelativePath = "src/PerfTest/PerfTest.csproj"
        }, now);

        // Seed with 5000 symbols to simulate a real project
        for (var i = 0; i < 5000; i++)
        {
            _symbolStore.Insert(new SymbolInfo
            {
                ProjectId = _projectId,
                FullyQualifiedName = $"global::PerfTest.Namespace{i / 100}.Class{i}",
                DisplayName = $"Class{i}",
                Kind = i % 5 == 0 ? SymbolKind.Interface : SymbolKind.Class,
                Accessibility = Accessibility.Public,
                Signature = $"public class Class{i}",
                FilePath = $"src/PerfTest/Class{i}.cs",
                LineStart = 1, LineEnd = 50,
                DocComment = $"Documentation for Class{i} in namespace {i / 100}",
                LastIndexedAt = now
            });
        }
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [TestMethod]
    public void ExactFqnLookup_Under5ms()
    {
        // Warm up
        _symbolStore.GetByFqn("global::PerfTest.Namespace25.Class2500");

        // Measure average over 100 lookups
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            var idx = i * 50; // Spread across different symbols
            var result = _symbolStore.GetByFqn($"global::PerfTest.Namespace{idx / 100}.Class{idx}");
            Assert.IsNotNull(result);
        }
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / 100;
        Assert.IsTrue(avgMs < 5, $"Average exact FQN lookup: {avgMs:F2}ms (target: <5ms)");
    }

    [TestMethod]
    public void Fts5Search_Under20ms()
    {
        // Warm up
        _symbolStore.SearchFts("Class100", 20);

        // Measure average over 50 searches
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            var results = _symbolStore.SearchFts($"Class{i * 100}", 20);
            Assert.IsNotNull(results);
        }
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / 50;
        Assert.IsTrue(avgMs < 20, $"Average FTS5 search: {avgMs:F2}ms (target: <20ms)");
    }

    [TestMethod]
    public void GetByProjectAndAccessibility_Under20ms()
    {
        // Warm up
        _symbolStore.GetByProjectAndAccessibility(_projectId, ["public"]);

        var sw = Stopwatch.StartNew();
        var symbols = _symbolStore.GetByProjectAndAccessibility(_projectId, ["public"]);
        sw.Stop();

        Assert.AreEqual(5000, symbols.Count);
        Assert.IsTrue(sw.ElapsedMilliseconds < 20, $"GetByProjectAndAccessibility took {sw.ElapsedMilliseconds}ms (target: <20ms)");
    }
}
