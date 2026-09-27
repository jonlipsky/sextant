using Microsoft.Data.Sqlite;
using Sextant.Store;
using static Sextant.Store.Tests.CrossRepoUsageSeed;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #160 plan gate for the cross-repository usage and candidate-consumer queries. The pre-fix plan
/// led with the authorization filter (<c>crepo.id IN (...)</c>), walked every consumer logical project
/// through <c>sqlite_autoindex_logical_projects_1</c>, and for each one walked EVERY pure reference
/// through <c>ix_occ_source (source_symbol_id=?)</c> (the <c>IS NULL</c> predicate). These tests explain
/// the exact commands the store runs (every scope, with and without an authorization filter) and require
/// the target-driven loop: the provider symbol by key, its occurrences by <c>ix_occ_target</c>, the one
/// dependency edge by its unique key, and everything else by primary key. They hold with and without
/// <c>sqlite_stat1</c>.
/// </summary>
[TestClass]
public class CrossRepositoryUsageQueryPlanTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private CrossRepoUsageSeed _seed = null!;
    private long _providerRepoId;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_xrepo_plan_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _seed = CrossRepoUsageSeed.Create(_conn, logicalProjects: 12, pureReferences: 2_000, targetUsages: 30);
        _providerRepoId = new SnapshotStore(_conn).GetRepositoryId(ProviderUrl)!.Value;
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    public static IEnumerable<object[]> ScopesAndFilters()
    {
        foreach (var scope in new[] { "default", "branch", "commit" })
            foreach (var filter in new[] { "none", "one", "many" })
                yield return [scope, filter];
    }

    [TestMethod]
    [DynamicData(nameof(ScopesAndFilters))]
    public void UsageQuery_IsTargetDriven_WhateverTheAuthorizationFilter(string scopeName, string filterName)
    {
        IReadOnlyCollection<long>? filter = filterName switch
        {
            "none" => null,
            "one" => [_seed.MonorepoId],
            _ => [_seed.MonorepoId, _seed.OtherRepoId, 424242]
        };
        using var cmd = new SnapshotDependencyStore(_conn).CreateCrossRepositoryUsagesCommand(
            _providerRepoId, TargetKey, Scope(scopeName), filter);

        AssertTargetDriven(QueryPlan.Explain(cmd), $"usages scope={scopeName} filter={filterName}");
    }

    [TestMethod]
    [DataRow("default")]
    [DataRow("branch")]
    [DataRow("commit")]
    public void CandidateQuery_IsTargetDriven(string scopeName)
    {
        using var cmd = new SnapshotDependencyStore(_conn).CreateCandidateConsumerRepositoriesCommand(
            _providerRepoId, TargetKey, Scope(scopeName));

        AssertTargetDriven(QueryPlan.Explain(cmd), $"candidates scope={scopeName}");
    }

    [TestMethod]
    public void UsageAndCandidatePlans_StayTargetDriven_WithStatistics()
    {
        // ANALYZE over the skewed seed (many pure references, few target usages) must not re-open the
        // pre-fix plan: the forced join order does not depend on statistics.
        using (var analyze = _conn.CreateCommand())
        {
            analyze.CommandText = "ANALYZE;";
            analyze.ExecuteNonQuery();
        }
        var store = new SnapshotDependencyStore(_conn);
        using var usages = store.CreateCrossRepositoryUsagesCommand(_providerRepoId, TargetKey, CrossRepoUsageScope.DefaultHeads, [_seed.MonorepoId]);
        using var candidates = store.CreateCandidateConsumerRepositoriesCommand(_providerRepoId, TargetKey, CrossRepoUsageScope.DefaultHeads);

        AssertTargetDriven(QueryPlan.Explain(usages), "usages after ANALYZE", withStatistics: true);
        AssertTargetDriven(QueryPlan.Explain(candidates), "candidates after ANALYZE", withStatistics: true);
    }

    [TestMethod]
    public void UsageAndCandidateResults_MatchTheSeed()
    {
        var store = new SnapshotDependencyStore(_conn);

        Assert.AreEqual(_seed.MonorepoTargetUsages,
            store.FindCrossRepositoryUsages(ProviderUrl, TargetKey, CrossRepoUsageScope.DefaultHeads, [_seed.MonorepoId]).Count);
        Assert.AreEqual(_seed.MonorepoTargetUsages + OtherTargetUsages,
            store.FindCrossRepositoryUsages(ProviderUrl, TargetKey, CrossRepoUsageScope.DefaultHeads, null).Count);
        CollectionAssert.AreEquivalent(
            new[] { _seed.MonorepoId, _seed.OtherRepoId },
            store.GetCandidateConsumerRepositories(ProviderUrl, TargetKey, CrossRepoUsageScope.DefaultHeads).Select(r => r.repositoryId).ToArray());
    }

    private static CrossRepoUsageScope Scope(string name) => name switch
    {
        "default" => CrossRepoUsageScope.DefaultHeads,
        "branch" => new CrossRepoUsageScope { Branch = "main" },
        _ => new CrossRepoUsageScope { ConsumerCommitSha = MonorepoHeadCommit }
    };

    /// <param name="withStatistics">With sqlite_stat1 the planner may scan the tiny <c>repositories</c>
    /// table per row instead of probing its primary key; the loop order is still forced.</param>
    internal static void AssertTargetDriven(List<string> plan, string label, bool withStatistics = false)
    {
        var text = string.Join("\n", plan);
        var loops = QueryPlan.LoopOrder(plan, ignoreAliases: "pd");

        Assert.AreEqual("psym", loops.FirstOrDefault(),
            $"{label}: the provider symbol (by key) must be the outermost loop. Plan:\n{text}");
        StringAssert.Contains(text, "SEARCH psym USING COVERING INDEX ix_symbols_key (project_id=? AND symbol_key=?)",
            $"{label}: the provider symbol must be found by (pinned provider project, symbol_key). Plan:\n{text}");
        StringAssert.Contains(text, "SEARCH o USING INDEX ix_occ_target (target_symbol_id=?)",
            $"{label}: occurrences must be reached through the target symbol only. Plan:\n{text}");
        StringAssert.Contains(text,
            "SEARCH d USING INDEX sqlite_autoindex_snapshot_dependencies_1 (consumer_project_id=? AND provider_project_id=?)",
            $"{label}: the dependency edge must be the one-row unique-key lookup per occurrence. Plan:\n{text}");
        if (!withStatistics)
            StringAssert.Contains(text, "SEARCH crepo USING INTEGER PRIMARY KEY (rowid=?)",
                $"{label}: the consumer repository (the authorization filter) must be a per-row primary-key check. Plan:\n{text}");

        Assert.IsFalse(text.Contains("ix_occ_source"),
            $"{label}: the pure-reference query must never walk ix_occ_source (issue #160). Plan:\n{text}");
        Assert.IsFalse(text.Contains("ix_occ_project"), $"{label}: occurrences must not be driven per consumer project. Plan:\n{text}");
        Assert.IsFalse(text.Contains("sqlite_autoindex_logical_projects_1"),
            $"{label}: the consumer repository's logical projects must not be enumerated. Plan:\n{text}");
        var neverScanned = withStatistics
            ? new[] { "psym", "o", "d", "cs", "cp", "clp", "cfv", "cf" }
            : new[] { "psym", "o", "d", "cs", "cp", "clp", "crepo", "cfv", "cf" };
        foreach (var alias in neverScanned)
            Assert.IsFalse(plan.Any(l => l.StartsWith($"SCAN {alias} ") || l == $"SCAN {alias}"),
                $"{label}: {alias} must not be scanned. Plan:\n{text}");

        Assert.IsTrue(loops.IndexOf("o") < loops.IndexOf("d") && loops.IndexOf("d") < loops.IndexOf("crepo"),
            $"{label}: the loop order must be psym -> o -> d -> ... -> crepo. Loops: {string.Join(", ", loops)}. Plan:\n{text}");
    }
}
