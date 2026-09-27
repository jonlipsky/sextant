using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Sextant.Store;
using static Sextant.Store.Tests.CrossRepoUsageSeed;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #160 synthetic-scale regression. It builds a monorepo consumer with hundreds of logical projects
/// and hundreds of thousands of pure references, then runs the AUTHORIZED usage query (the MCP resolver
/// always passes an authorization list). The pre-fix plan cost is (consumer logical projects x all pure
/// references): here about 80 M index steps, about 20 s on a developer machine and ~410 s on the prod
/// catalog. The fixed, target-driven plan touches only the target's few hundred occurrences and returns
/// in milliseconds.
/// <para>
/// Each query runs under <see cref="Bound"/>, enforced with <c>sqlite3_interrupt</c>. A regression
/// therefore fails at the bound instead of running on for the full pre-fix time, which keeps CI fast even
/// when the test fails. The bound is thousands of times the fixed query's cost, so load cannot flake it.
/// </para>
/// </summary>
[TestClass]
public class CrossRepositoryUsageScaleTests
{
    private const int LogicalProjects = 400;
    private const int PureReferences = 200_000;
    private const int TargetUsages = 600;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_xrepo_scale_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void AuthorizedUsageQuery_OnAMonorepoScaleCatalog_CompletesWithinBound()
    {
        var seed = CrossRepoUsageSeed.Create(_conn, LogicalProjects, PureReferences, TargetUsages);
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM occurrences WHERE source_symbol_id IS NULL;") >= PureReferences,
            "the seed must be monorepo-scale");

        RunAll(seed, "no statistics");

        // Statistics must not reopen the pre-fix plan either.
        using (var analyze = _conn.CreateCommand())
        {
            analyze.CommandText = "ANALYZE;";
            analyze.ExecuteNonQuery();
        }
        RunAll(seed, "after ANALYZE");
    }

    private void RunAll(CrossRepoUsageSeed seed, string phase)
    {
        var store = new SnapshotDependencyStore(_conn);
        var mono = new[] { seed.MonorepoId };
        var both = new[] { seed.MonorepoId, seed.OtherRepoId };

        AssertBounded($"{phase}: authorized usages, default heads", TargetUsages,
            () => store.FindCrossRepositoryUsages(ProviderUrl, TargetKey, CrossRepoUsageScope.DefaultHeads, mono).Count);
        AssertBounded($"{phase}: authorized usages, branch scope", TargetUsages,
            () => store.FindCrossRepositoryUsages(ProviderUrl, TargetKey, new CrossRepoUsageScope { Branch = "main" }, mono).Count);
        AssertBounded($"{phase}: authorized usages, commit scope", TargetUsages,
            () => store.FindCrossRepositoryUsages(ProviderUrl, TargetKey, new CrossRepoUsageScope { ConsumerCommitSha = MonorepoHeadCommit }, mono).Count);
        AssertBounded($"{phase}: usages authorized for both consumers", TargetUsages + OtherTargetUsages,
            () => store.FindCrossRepositoryUsages(ProviderUrl, TargetKey, CrossRepoUsageScope.DefaultHeads, both).Count);
        AssertBounded($"{phase}: allow-all usages", TargetUsages + OtherTargetUsages,
            () => store.FindCrossRepositoryUsages(ProviderUrl, TargetKey, CrossRepoUsageScope.DefaultHeads, null).Count);
        AssertBounded($"{phase}: candidate consumer repositories", 2,
            () => store.GetCandidateConsumerRepositories(ProviderUrl, TargetKey, CrossRepoUsageScope.DefaultHeads).Count);
    }

    /// <summary>
    /// Runs <paramref name="query"/> and interrupts it at <see cref="Bound"/>. An interrupted or late
    /// query fails the test with the elapsed time.
    /// </summary>
    private void AssertBounded(string label, int expectedCount, Func<int> query)
    {
        var handle = _conn.Handle!;
        var sw = Stopwatch.StartNew();
        var timer = new Timer(_ => raw.sqlite3_interrupt(handle), null, Bound, Timeout.InfiniteTimeSpan);
        int count;
        try
        {
            count = query();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            Assert.Fail($"{label}: interrupted after {sw.ElapsedMilliseconds} ms; it must finish within {Bound.TotalSeconds} s (issue #160).");
            return;
        }
        finally
        {
            // Wait for any in-flight callback so a late interrupt cannot hit the next query. An interrupt
            // with no statement running is a no-op.
            using var disposed = new ManualResetEvent(false);
            timer.Dispose(disposed);
            disposed.WaitOne();
        }
        sw.Stop();

        Assert.IsTrue(sw.Elapsed < Bound, $"{label}: took {sw.ElapsedMilliseconds} ms (issue #160).");
        Assert.AreEqual(expectedCount, count, $"{label}: wrong row count.");
    }

    private long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
