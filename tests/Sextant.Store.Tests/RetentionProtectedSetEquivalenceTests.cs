using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #303: the protected-set computation is set-based SQL. These tests pin it to the former managed
/// fixpoint algorithm (kept below as a reference) over randomized catalogs, check that every delete batch
/// re-verifies its candidates under the writer, and that a large catalog computes quickly.
/// </summary>
[TestClass]
public class RetentionProtectedSetEquivalenceTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_retequiv_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Exec("PRAGMA foreign_keys = OFF;");
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(2, 0)]
    [DataRow(3, 3)]
    [DataRow(4, 2)]
    [DataRow(5, 0)]
    [DataRow(6, 5)]
    [DataRow(7, 1)]
    [DataRow(8, 4)]
    [DataRow(9, 0)]
    [DataRow(10, 2)]
    public void ProtectedSet_MatchesTheManagedReference_OverARandomCatalog(int seed, int quota)
    {
        var rng = new Random(seed);
        var deletable = Seed(rng, snapshots: 120 + seed * 7, runs: 14, repositories: 4);
        var policy = new RetentionPolicy { MaxSnapshotsPerRepository = quota };

        var expected = ReferenceOrphans(_conn, policy, deletable);
        var actual = new RetentionService(_conn, policy).ComputeOrphanedSnapshotIds(deletable);

        CollectionAssert.AreEqual(expected, actual, $"seed {seed}, quota {quota}");
        Assert.IsTrue(expected.Count > 0 && expected.Count < 120 + seed * 7, "the random catalog must exercise both outcomes");
    }

    [TestMethod]
    public void ProtectedSet_MatchesTheReference_WhenEveryRunIsDeletable()
    {
        var rng = new Random(77);
        Seed(rng, snapshots: 200, runs: 10, repositories: 3);
        var all = ScalarList("SELECT id FROM index_runs;");
        var policy = new RetentionPolicy();

        CollectionAssert.AreEqual(ReferenceOrphans(_conn, policy, all),
            new RetentionService(_conn, policy).ComputeOrphanedSnapshotIds(all));
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_ThatBecameBranchPointedSinceThePlan()
    {
        var (_, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();
        Assert.IsTrue(plan.HasWork);

        Exec($"INSERT INTO branches (repository_id, name, snapshot_id, is_default, updated_at) VALUES (1, 'late', {candidate}, 0, {_now});");
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"), "a branch-pointed snapshot survives");
        Assert.AreEqual(0, report.SnapshotsDeleted);
        Assert.IsTrue(report.SnapshotsSkipped >= 1);
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_ThatBecameThePendingBaseOfANewSnapshot()
    {
        var (_, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();

        InsertSnapshot(id: 9000, repo: 1, runId: null, status: SnapshotStatus.Pending, baseId: candidate);
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"), "a base of a newer snapshot survives");
        Assert.AreEqual(0, report.SnapshotsDeleted);
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_ThatBecameAProviderOfANewSnapshot()
    {
        var (_, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();

        var newRun = (long)ScalarLong("SELECT MAX(id) FROM index_runs;");
        InsertSnapshot(id: 9001, repo: 1, runId: newRun, status: SnapshotStatus.Complete, baseId: null);
        Exec($"""
            INSERT INTO snapshot_dependencies (consumer_snapshot_id, consumer_project_id, provider_snapshot_id, provider_project_id,
                provider_repository_id, provider_commit_sha, reference_kind, created_at)
            VALUES (9001, 1, {candidate}, 2, 1, 'x', 'submodule', {_now});
            """);
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.AreEqual(0, report.SnapshotsDeleted);
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_ThatBecamePending()
    {
        var (_, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();

        Exec($"UPDATE snapshots SET status = 'pending' WHERE id = {candidate};");
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.AreEqual(0, report.SnapshotsDeleted);
    }

    [TestMethod]
    public void EveryBatch_DeletesAnUntouchedCandidate_AndIgnoresASnapshotCreatedAfterThePlan()
    {
        var (_, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();
        InsertSnapshot(id: 9100, repo: 1, runId: null, status: SnapshotStatus.Failed, baseId: null);

        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(0, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.AreEqual(1, ScalarLong("SELECT COUNT(*) FROM snapshots WHERE id = 9100;"), "never a candidate of an older plan");
        Assert.AreEqual(1, report.Batches);
        Assert.IsTrue(report.MaxBatchWriterHoldMs >= 0);
    }

    [TestMethod]
    public void ABudgetStop_KeepsTheCountsComputedAndDeletedSoFar()
    {
        var (old, first) = OldGenerationWithCandidate();
        InsertSnapshot(id: 101, repo: 1, runId: old, status: SnapshotStatus.Complete, baseId: null);
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();
        Assert.AreEqual(2, plan.SnapshotCandidates);

        var done = service.ExecuteBatch(plan);
        Assert.AreEqual(1, done.SnapshotsDeleted);
        Assert.IsTrue(done.MoreRemaining);

        var stopped = service.ExecuteBatch(plan, timeLimit: TimeSpan.FromTicks(1));
        Assert.IsNotNull(stopped.StopReason);
        Assert.IsTrue(stopped.MoreRemaining);

        var total = RetentionService.Combine(RetentionService.Combine(new RetentionReport { DryRun = false }, done), stopped);
        Assert.AreEqual(1, total.SnapshotsDeleted, "what was deleted before the stop stays counted");
        Assert.IsTrue(total.MoreRemaining);
        Assert.AreEqual(1, ScalarLong("SELECT COUNT(*) FROM snapshots WHERE id IN (100, 101);"), "the unfinished candidate is untouched");
        Assert.AreNotEqual(0, first);
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_WhoseRunBecameServableSinceThePlan()
    {
        var (_, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();

        Exec("UPDATE index_runs SET status = 'abandoned' WHERE id = (SELECT MAX(id) FROM index_runs);");
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.AreEqual(0, report.SnapshotsDeleted);
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_ThatBecameAnOpenPullRequestRoot()
    {
        var (_, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();

        Exec($"INSERT INTO pull_request_snapshots (repository_id, pr_number, snapshot_id, state, updated_at) VALUES (1, 7, {candidate}, 'open', {_now});");
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.AreEqual(0, report.SnapshotsDeleted);
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_WhoseRunIsStagingAgain()
    {
        var (old, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();

        Exec($"UPDATE index_runs SET status = 'staging' WHERE id = {old};");
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.AreEqual(0, report.SnapshotsDeleted);
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_WhoseRunAProviderProtectedSinceThePlan()
    {
        var (old, candidate) = OldGenerationWithCandidate();
        var provider = new LateProtection(old);
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 }, [provider]);
        var plan = service.CreatePlan();
        Assert.IsTrue(plan.HasWork);

        provider.Active = true;
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.AreEqual(0, report.SnapshotsDeleted);
    }

    [TestMethod]
    public void EveryBatch_SkipsACandidate_ThatMovedToAnotherRunSinceThePlan()
    {
        var (_, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0 });
        var plan = service.CreatePlan();

        Exec($"UPDATE snapshots SET run_id = (SELECT MAX(id) FROM index_runs) WHERE id = {candidate};");
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.AreEqual(0, report.SnapshotsDeleted);
    }

    [TestMethod]
    public void EveryBatch_KeepsAnApiCommit_ThatWasRecapturedIntoTheKeepWindowSinceThePlan()
    {
        Exec($"INSERT INTO repositories (id, remote_url, created_at) VALUES (1, 'https://github.com/org/app', {_now});");
        InsertApi("c_old", 100);
        InsertApi("c_mid", 200);
        InsertApi("c_new", 300);
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0, ApiSnapshotKeepCommits = 2 });
        var plan = service.CreatePlan();
        Assert.IsTrue(plan.HasWork);

        InsertApi("c_old", 400);
        var report = service.ExecuteBatch(plan);

        Assert.AreEqual(0, report.ApiSnapshotsDeleted);
        Assert.AreEqual(2, ScalarLong("SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = 'c_old';"));
    }

    private void InsertApi(string commit, long capturedAt) =>
        Exec($"""
            INSERT INTO api_surface_snapshots (project_id, symbol_id, symbol_key, fully_qualified_name, accessibility, signature_hash, captured_at, git_commit)
            VALUES (1, NULL, 'k_{commit}_{capturedAt}', 'global::A.{commit}', 'public', 'sig', {capturedAt}, '{commit}');
            """);

    private sealed class LateProtection(long runId) : IRetentionProtectionProvider
    {
        public bool Active { get; set; }
        public string Name => "late";

        public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder)
        {
            if (Active) builder.ProtectGeneration(runId, "late protection");
        }
    }

    [TestMethod]
    public void AUnit_SlowerThanTheBatchLimit_ButUnderTheCap_Completes()
    {
        var (old, candidate) = OldGenerationWithCandidate();
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0, UnitMaxSeconds = 10 })
        {
            BeforeUnitDelete = _ => Thread.Sleep(600)
        };
        var plan = service.CreatePlan();

        var report = service.ExecuteBatch(plan, timeLimit: TimeSpan.FromMilliseconds(100));

        Assert.IsNull(report.StopReason);
        Assert.AreEqual(1, report.SnapshotsDeleted);
        Assert.AreEqual(0, report.Oversized);
        Assert.AreEqual(0, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {candidate};"));
        Assert.IsTrue(report.MaxBatchWriterHoldMs >= 600);
        Assert.AreNotEqual(0, old);
    }

    [TestMethod]
    public void AUnit_OverTheCap_IsRolledBackAndReported_AndLaterUnitsProgress()
    {
        var (old, slow) = OldGenerationWithCandidate();
        InsertSnapshot(id: 101, repo: 1, runId: old, status: SnapshotStatus.Complete, baseId: null);
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 0, UnitMaxSeconds = 1 })
        {
            BeforeUnitDelete = id => { if (id == slow) Thread.Sleep(1300); }
        };
        var plan = service.CreatePlan();

        var total = new RetentionReport { DryRun = false };
        for (var i = 0; i < 5; i++)
        {
            var batch = service.ExecuteBatch(plan);
            Assert.IsNull(batch.StopReason);
            total = RetentionService.Combine(total, batch);
            if (!batch.MoreRemaining) break;
        }

        CollectionAssert.AreEqual(new long[] { slow }, total.OversizedSnapshotIds.ToArray());
        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {slow};"), "the oversized unit rolled back whole");
        Assert.AreEqual(0, ScalarLong("SELECT COUNT(*) FROM snapshots WHERE id = 101;"), "the next unit still progressed");
        Assert.AreEqual(1, total.SnapshotsDeleted);

        // Fast again on the next pass: the oversized snapshot is retried and deleted.
        service.BeforeUnitDelete = null;
        var retry = service.Execute();
        Assert.AreEqual(1, retry.SnapshotsDeleted);
        Assert.AreEqual(0, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE id = {slow};"));
    }

    [TestMethod]
    [TestCategory("Performance")]
    public void ProtectedSet_OfAFiveThousandSnapshotCatalogWithChains_ComputesWellWithinTheBudget()
    {
        var rng = new Random(303);
        var deletable = Seed(rng, snapshots: 5000, runs: 40, repositories: 20, chain: true);
        var service = new RetentionService(_conn, new RetentionPolicy { MaxSnapshotsPerRepository = 50 });

        var watch = Stopwatch.StartNew();
        var actual = service.ComputeOrphanedSnapshotIds(deletable);
        watch.Stop();

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        CollectionAssert.AreEqual(ReferenceOrphans(_conn, new RetentionPolicy { MaxSnapshotsPerRepository = 50 }, deletable), actual);
    }

    // === seeding ===================================================================================

    private (long run, long candidate) OldGenerationWithCandidate()
    {
        Exec($"INSERT INTO repositories (id, remote_url, created_at) VALUES (1, 'https://github.com/org/app', {_now});");
        var runs = new IndexRunStore(_conn);
        var old = runs.BeginRun("full", _now, IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runs.MarkComplete(old, _now, 1);
        InsertSnapshot(id: 100, repo: 1, runId: old, status: SnapshotStatus.Complete, baseId: null);
        var fresh = runs.BeginRun("full", _now + 1, IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runs.MarkComplete(fresh, _now + 1, 1);
        return (old, 100);
    }

    private List<long> Seed(Random rng, int snapshots, int runs, int repositories, bool chain = false)
    {
        for (var r = 1; r <= repositories; r++)
            Exec($"INSERT INTO repositories (id, remote_url, created_at) VALUES ({r}, 'https://github.com/org/r{r}', {_now});");

        var runIds = new List<long>();
        var store = new IndexRunStore(_conn);
        for (var i = 0; i < runs; i++)
        {
            var id = store.BeginRun("full", _now + i, IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
            switch (rng.Next(4))
            {
                case 0: store.AbandonRun(id, _now + i); break;
                case 1: break; // staging
                default: store.MarkComplete(id, _now + i, 1); break;
            }
            runIds.Add(id);
        }

        var statuses = new[] { SnapshotStatus.Complete, SnapshotStatus.Complete, SnapshotStatus.Complete,
            SnapshotStatus.Superseded, SnapshotStatus.Pending, SnapshotStatus.Failed };
        for (var id = 1; id <= snapshots; id++)
        {
            var runId = rng.Next(10) == 0 ? (long?)null : rng.Next(10) == 0 ? 99999 : runIds[rng.Next(runIds.Count)];
            long? baseId = chain
                ? (id > 1 && rng.Next(3) == 0 ? id - 1 : null)
                : (id > 1 && rng.Next(5) == 0 ? rng.Next(1, id) : null);
            InsertSnapshot(id, rng.Next(1, repositories + 1), runId, statuses[rng.Next(statuses.Length)], baseId,
                createdAt: _now + rng.Next(0, 1000), isProvider: rng.Next(6) == 0);
        }

        var edges = snapshots / 3;
        for (var e = 0; e < edges; e++)
        {
            var consumer = chain ? rng.Next(2, snapshots + 1) : rng.Next(1, snapshots + 1);
            var provider = chain ? consumer - 1 : rng.Next(1, snapshots + 1);
            Exec($"""
                INSERT OR IGNORE INTO snapshot_dependencies (consumer_snapshot_id, consumer_project_id, provider_snapshot_id,
                    provider_project_id, provider_repository_id, provider_commit_sha, reference_kind, created_at)
                VALUES ({consumer}, {e * 2 + 1}, {provider}, {e * 2 + 2}, 1, 'x', 'submodule', {_now});
                """);
        }

        for (var b = 0; b < snapshots / 25 + 1; b++)
            Exec($"INSERT OR IGNORE INTO branches (repository_id, name, snapshot_id, is_default, updated_at) VALUES ({rng.Next(1, repositories + 1)}, 'b{b}', {rng.Next(1, snapshots + 1)}, 0, {_now});");
        for (var p = 0; p < snapshots / 40 + 1; p++)
            Exec($"INSERT OR IGNORE INTO pull_request_snapshots (repository_id, pr_number, snapshot_id, state, updated_at) VALUES ({rng.Next(1, repositories + 1)}, {p}, {rng.Next(1, snapshots + 1)}, '{(rng.Next(3) == 0 ? "closed" : "open")}', {_now});");

        return runIds.Where(_ => rng.Next(2) == 0).ToList();
    }

    private void InsertSnapshot(long id, long repo, long? runId, string status, long? baseId, long? createdAt = null, bool isProvider = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots (id, repository_id, run_id, identity_hash, schema_version, analyzer_version, status,
                                   created_at, base_snapshot_id, is_provider)
            VALUES (@id, @repo, @run, @hash, 1, 't', @status, @created, @base, @prov);
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@repo", repo);
        cmd.Parameters.AddWithValue("@run", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", $"h{id}");
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@created", createdAt ?? _now);
        cmd.Parameters.AddWithValue("@base", (object?)baseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prov", isProvider ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private List<long> ScalarList(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<long>();
        while (reader.Read()) list.Add(reader.GetInt64(0));
        return list;
    }

    // === the former managed algorithm (pre-#303), verbatim in behavior =============================

    private static List<long> ReferenceOrphans(SqliteConnection connection, RetentionPolicy policy, List<long> deletableRunIds)
    {
        var snapshotStore = new SnapshotStore(connection);
        var snapshots = snapshotStore.GetSnapshotsForRetention();
        if (snapshots.Count == 0) return [];

        var deletableRuns = deletableRunIds.ToHashSet();
        var rootPinned = snapshotStore.GetBranchPointedSnapshotIds()
            .Concat(snapshotStore.GetOpenPullRequestSnapshotIds())
            .ToHashSet();
        var edges = snapshotStore.GetSnapshotDependencyEdges();

        var baseOf = new Dictionary<long, long>();
        foreach (var s in snapshots)
            if (s.baseSnapshotId is long b)
                baseOf[s.id] = b;

        var providersOf = new Dictionary<long, List<long>>();
        foreach (var (consumer, provider) in edges)
            (providersOf.TryGetValue(consumer, out var list) ? list : providersOf[consumer] = []).Add(provider);

        HashSet<long> ExpandClosure(IEnumerable<long> seed)
        {
            var set = new HashSet<long>(seed);
            var frontier = new Queue<long>(set);
            while (frontier.Count > 0)
            {
                var id = frontier.Dequeue();
                if (baseOf.TryGetValue(id, out var baseId) && set.Add(baseId))
                    frontier.Enqueue(baseId);
                if (providersOf.TryGetValue(id, out var provs))
                    foreach (var p in provs)
                        if (set.Add(p))
                            frontier.Enqueue(p);
            }
            return set;
        }

        var pending = snapshots.Where(s => s.status == SnapshotStatus.Pending).Select(s => s.id);
        var runs = new IndexRunStore(connection).GetAllRuns().ToDictionary(r => r.Id);
        var uncertainOwnership = snapshots.Where(s =>
            s.status is SnapshotStatus.Complete or SnapshotStatus.Superseded &&
            (s.runId == null || !runs.TryGetValue(s.runId.Value, out var run) || run.Status == IndexRunState.Abandoned))
            .Select(s => s.id);
        var hardProtected = ExpandClosure(pending.Concat(rootPinned).Concat(uncertainOwnership));

        var windowSeed = new HashSet<long>(hardProtected);
        foreach (var s in snapshots)
            if (s.runId is long rid && !deletableRuns.Contains(rid))
                windowSeed.Add(s.id);
        var retained = ExpandClosure(windowSeed);

        if (policy.MaxSnapshotsPerRepository > 0)
        {
            var evictions = ReferenceQuotaEvictions(policy, snapshots, retained, hardProtected, baseOf, providersOf);
            retained.ExceptWith(evictions);
        }

        return snapshots.Where(s => s.status != SnapshotStatus.Pending && !retained.Contains(s.id))
            .Select(s => s.id).Order().ToList();
    }

    private static List<long> ReferenceQuotaEvictions(
        RetentionPolicy policy,
        IReadOnlyList<(long id, long repositoryId, long? runId, string status, long? baseSnapshotId, bool isProvider, long createdAt)> snapshots,
        HashSet<long> retained, HashSet<long> hardProtected,
        Dictionary<long, long> baseOf, Dictionary<long, List<long>> providersOf)
    {
        var cap = policy.MaxSnapshotsPerRepository;
        var evict = new HashSet<long>();
        foreach (var repoGroup in snapshots
                     .Where(s => s.status == SnapshotStatus.Complete && retained.Contains(s.id) && !hardProtected.Contains(s.id))
                     .GroupBy(s => s.repositoryId))
        {
            var ranked = repoGroup.OrderByDescending(s => s.createdAt).ThenByDescending(s => s.id).ToList();
            for (var i = cap; i < ranked.Count; i++)
                evict.Add(ranked[i].id);
        }
        if (evict.Count == 0) return [];

        bool changed;
        do
        {
            changed = false;
            foreach (var kept in retained)
            {
                if (evict.Contains(kept)) continue;
                if (baseOf.TryGetValue(kept, out var baseId) && evict.Remove(baseId))
                    changed = true;
                if (providersOf.TryGetValue(kept, out var provs))
                    foreach (var p in provs)
                        if (evict.Remove(p))
                            changed = true;
            }
        }
        while (changed);
        return evict.ToList();
    }
}
