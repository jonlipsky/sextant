using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Query-plan guards for the hot read paths. These previously asserted absolute wall-clock latencies
/// (issue #33), which flaked ~1/15 on Windows under parallel test load because a single Stopwatch
/// sample is perturbed by GC pauses and scheduler hiccups. The performance-critical property is not a
/// millisecond count but that each hot query is *index-backed* rather than a full table scan — a
/// deterministic fact we read from <c>EXPLAIN QUERY PLAN</c>. Correctness of the same query is asserted
/// through the real store method. The SELECT/WHERE shapes here mirror the SQL composed by
/// <see cref="SymbolStore"/>; if those change, update the mirrored SQL below.
/// </summary>
[TestClass]
public class PerformanceTests
{
    private const int SeedCount = 1000;

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private ProjectStore _projectStore = null!;
    private SymbolStore _symbolStore = null!;
    private long _projectId;

    // Mirror of SymbolStore.SelectPrefix (id column only — enough to fix the query plan).
    private const string SelectPrefix = """
        SELECT s.id FROM symbols s
        LEFT JOIN file_versions fv ON fv.id = s.file_version_id
        LEFT JOIN files f ON f.id = fv.file_id
        LEFT JOIN projects p ON p.id = s.project_id
        """;

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

        for (var i = 0; i < SeedCount; i++)
        {
            _symbolStore.Insert(new SymbolInfo
            {
                ProjectId = _projectId,
                SymbolKey = $"global::PerfTest.Namespace{i / 100}.Class{i}", FullyQualifiedName = $"global::PerfTest.Namespace{i / 100}.Class{i}",
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
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void ExactFqnLookup_IsIndexBacked()
    {
        var result = _symbolStore.GetByFqn("global::PerfTest.Namespace25.Class2500");
        // (This exact FQN exists only when SeedCount > 2500; the plan assertion is the guard, and the
        // lookup below uses an in-range FQN for the correctness check.)
        result = _symbolStore.GetByFqn("global::PerfTest.Namespace5.Class500");
        Assert.IsNotNull(result, "exact FQN lookup must resolve a seeded symbol");

        var plan = QueryPlan(SelectPrefix + " WHERE s.fully_qualified_name = @fqn;", ("@fqn", "global::PerfTest.Namespace5.Class500"));
        StringAssert.Contains(plan, "USING INDEX ix_symbols_fqn_lookup",
            $"exact FQN lookup must use the FQN index, not a table scan. Plan:\n{plan}");
        Assert.IsFalse(plan.Contains("SCAN s\n") || plan.EndsWith("SCAN s"),
            $"exact FQN lookup must not full-scan symbols. Plan:\n{plan}");
    }

    [TestMethod]
    public void Fts5Search_UsesFtsVirtualTable()
    {
        var results = _symbolStore.SearchFts("Class500", 20);
        Assert.IsNotNull(results);
        Assert.IsTrue(results.Count >= 1, "FTS search must find the seeded symbol");

        var plan = QueryPlan(
            SelectPrefix + " JOIN symbols_fts fts ON fts.rowid = s.id WHERE symbols_fts MATCH @q ORDER BY rank LIMIT 20;",
            ("@q", "Class500"));
        StringAssert.Contains(plan, "VIRTUAL TABLE INDEX",
            $"FTS search must be driven by the symbols_fts virtual-table index. Plan:\n{plan}");
        StringAssert.Contains(plan, "SEARCH s USING INTEGER PRIMARY KEY",
            $"FTS search must resolve symbols by rowid, not a table scan. Plan:\n{plan}");
    }

    [TestMethod]
    public void GetByProjectAndAccessibility_IsIndexBacked()
    {
        var symbols = _symbolStore.GetByProjectAndAccessibility(_projectId, ["public"]);
        Assert.AreEqual(SeedCount, symbols.Count, "all seeded symbols are public");

        var plan = QueryPlan(
            SelectPrefix + " WHERE s.project_id = @project_id AND s.accessibility IN (0);",
            ("@project_id", _projectId));
        StringAssert.Contains(plan, "USING INDEX ix_symbols_project_access",
            $"project+accessibility filter must use the composite index, not a table scan. Plan:\n{plan}");
    }

    private string QueryPlan(string sql, params (string name, object value)[] parameters)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        using var reader = cmd.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
            lines.Add(reader.GetString(3));
        return string.Join("\n", lines);
    }
}
