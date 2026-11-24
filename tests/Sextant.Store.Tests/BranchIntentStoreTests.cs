using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Store.Tests;

[TestClass]
public sealed class BranchIntentStoreTests
{
    // Issue #273 (FIFO): nothing supersedes. A refresh admitted directly behind a pending, unclaimed refresh is absorbed
    // into it; behind any other kind, or behind a claimed refresh, it queues on its own; every intent runs in order.
    [TestMethod]
    public void Admit_IsFifo_AndOnlyAbsorbsAdjacentPendingRefreshes()
    {
        var db = CreateDatabase(out var path);
        try
        {
            var connection = db.GetConnection();
            var repositoryId = AddRepository(connection);
            var store = new BranchIntentStore(connection);

            var refresh = store.Admit(repositoryId, "feature/a", "refresh", null, "refresh-1", null, 10);
            var absorbed = store.Admit(repositoryId, "feature/a", "refresh", null, "refresh-2", null, 11);
            Assert.AreEqual(refresh.Id, absorbed.Id, "an adjacent pending refresh absorbs the new one");
            Assert.AreEqual("refresh-1", absorbed.AdmissionId);
            Assert.IsNull(store.GetByAdmissionId("refresh-2"));

            var retirement = store.Admit(repositoryId, "feature/a", "retire", null, null, "{}", 12);
            Assert.AreEqual("pending", store.Get(refresh.Id)!.State, "a later retirement never supersedes a refresh");
            var later = store.Admit(repositoryId, "feature/a", "refresh", null, "refresh-3", null, 13);
            Assert.AreNotEqual(refresh.Id, later.Id, "behind a retirement a refresh queues on its own");
            Assert.AreEqual(refresh.Id, store.Head(repositoryId, "feature/a")?.Id);

            Assert.IsTrue(store.Claim(refresh.Id));
            Assert.IsTrue(store.Settle(refresh.Id, "complete", 14));
            Assert.AreEqual(retirement.Id, store.Head(repositoryId, "feature/a")?.Id);
            Assert.IsTrue(store.Settle(retirement.Id, "retired", 15));
            Assert.AreEqual(later.Id, store.Head(repositoryId, "feature/a")?.Id);

            Assert.IsTrue(store.Claim(later.Id));
            var behindClaimed = store.Admit(repositoryId, "feature/a", "refresh", null, "refresh-4", null, 16);
            Assert.AreNotEqual(later.Id, behindClaimed.Id, "a claimed refresh is never absorbed into");
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    [TestMethod]
    public void Recover_ClaimOwnedByGoneTurnIsReclaimable_AndReleaseRecordsRetrySchedule()
    {
        var db = CreateDatabase(out var path);
        try
        {
            var connection = db.GetConnection();
            var repositoryId = AddRepository(connection);
            var store = new BranchIntentStore(connection);
            var refresh = store.Admit(repositoryId, "feature/a", "refresh", null, "refresh-1", null, 10);
            var retire = store.Admit(repositoryId, "feature/b", "retire", null, null, "{}", 11);
            var live = new HashSet<string> { "turn-1" };

            Assert.IsTrue(store.Claim(refresh.Id, "turn-1", live.Contains));
            Assert.AreEqual("turn-1", store.Get(refresh.Id)!.ClaimedByTurn);
            Assert.IsFalse(store.Claim(refresh.Id, "turn-2", live.Contains), "a live owner keeps its claim");

            live.Remove("turn-1");
            Assert.IsTrue(store.Claim(refresh.Id, "turn-2", live.Contains), "an owner-gone claim is reclaimable");
            Assert.AreEqual("turn-2", store.Get(refresh.Id)!.ClaimedByTurn);

            Assert.IsFalse(store.Release(refresh.Id, "turn-1", 50, "stale"), "only the owning turn returns a claim");
            Assert.IsTrue(store.Release(refresh.Id, "turn-2", 50, "turn_SqliteException"));
            var released = store.Get(refresh.Id)!;
            Assert.AreEqual("pending", released.State);
            Assert.IsNull(released.ClaimedByTurn);
            Assert.AreEqual(1, released.Attempts);
            Assert.AreEqual(50L, released.NextAttemptAt);
            Assert.AreEqual("turn_SqliteException", released.LastError);

            Assert.IsTrue(store.Defer(retire.Id, 70, "turn_SqliteException"));
            Assert.AreEqual(70L, store.Get(retire.Id)!.NextAttemptAt);
            CollectionAssert.AreEqual(new[] { refresh.Id, retire.Id }, store.ListHeads().Select(h => h.Id).ToArray());

            Assert.IsTrue(store.Claim(refresh.Id, "turn-3", _ => true));
            Assert.AreEqual(1, store.ReleaseClaims());
            Assert.IsNull(store.Get(refresh.Id)!.ClaimedByTurn);
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    // Review round 29: head discovery visits each branch once, reading the partial queue-order index, and stays fast with
    // a large backlog on one branch (the earlier correlated subquery was quadratic in it).
    [TestMethod]
    public void ListHeads_ReadsTheQueueIndexOnce_AndIsFastForALargeSameBranchBacklog()
    {
        var db = CreateDatabase(out var path);
        try
        {
            var connection = db.GetConnection();
            var repositoryId = AddRepository(connection);
            using (var tx = connection.BeginTransaction())
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO branch_intents (repository_id, branch, kind, admission_id, state, admitted_at)
                    VALUES (@repository, @branch, 'ensure', @admission, 'pending', @at);
                    """;
                var repository = insert.Parameters.Add("@repository", SqliteType.Integer);
                var branch = insert.Parameters.Add("@branch", SqliteType.Text);
                var admission = insert.Parameters.Add("@admission", SqliteType.Text);
                var at = insert.Parameters.Add("@at", SqliteType.Integer);
                for (var i = 0; i < 20_000; i++)
                {
                    repository.Value = repositoryId;
                    branch.Value = i < 19_990 ? "stalled" : "other-" + i;
                    admission.Value = "backlog-" + i;
                    at.Value = 10 + i;
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
            }
            var store = new BranchIntentStore(connection);
            var stalledHead = store.Head(repositoryId, "stalled")!;

            using (var plan = connection.CreateCommand())
            {
                plan.CommandText = "EXPLAIN QUERY PLAN " + BranchIntentStore.ListHeadsSql;
                using var reader = plan.ExecuteReader();
                var details = new List<string>();
                while (reader.Read())
                    details.Add(reader.GetString(3));
                Assert.IsTrue(details.Any(d => d.Contains("ix_branch_intents_queue", StringComparison.Ordinal)),
                    string.Join(" | ", details));
                Assert.IsFalse(details.Any(d => d.Contains("TEMP B-TREE", StringComparison.Ordinal)),
                    "queue order comes from the index, not a sort: " + string.Join(" | ", details));
                Assert.IsFalse(details.Any(d => d.Contains("CORRELATED", StringComparison.Ordinal)),
                    "no per-row subquery: " + string.Join(" | ", details));
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var heads = store.ListHeads();
            watch.Stop();
            Assert.AreEqual(11, heads.Count, "one head per branch");
            Assert.AreEqual(stalledHead.Id, heads.Single(h => h.Branch == "stalled").Id);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), $"ListHeads took {watch.Elapsed}");
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    // Issue #273, review round 21: retention's settled-intent prune is a bounded, indexed oldest-first scan, and stays
    // fast on a large catalog where one branch has a permanently unsettled head and many settled rows.
    [TestMethod]
    public void Retention_PruneOfSettledIntentsUsesTheSettledIndexAndIsFastOnALargeCatalog()
    {
        var db = CreateDatabase(out var path);
        try
        {
            var connection = db.GetConnection();
            var repositoryId = AddRepository(connection);
            using (var head = connection.CreateCommand())
            {
                head.CommandText = """
                    INSERT INTO branch_intents (repository_id, branch, kind, admission_id, state, admitted_at)
                    VALUES (@repository, 'feature', 'retire', 'stuck-head', 'pending', 1);
                    """;
                head.Parameters.AddWithValue("@repository", repositoryId);
                head.ExecuteNonQuery();
            }
            using (var tx = connection.BeginTransaction())
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO branch_intents
                        (repository_id, branch, kind, admission_id, state, outcome, admitted_at, settled_at)
                    VALUES (@repository, 'feature', 'refresh', @admission, 'settled', 'superseded', @at, @at);
                    """;
                var repository = insert.Parameters.Add("@repository", SqliteType.Integer);
                var admission = insert.Parameters.Add("@admission", SqliteType.Text);
                var at = insert.Parameters.Add("@at", SqliteType.Integer);
                for (var i = 0; i < 20_000; i++)
                {
                    repository.Value = repositoryId;
                    admission.Value = "settled-" + i;
                    at.Value = 10 + i;
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
            }

            using (var plan = connection.CreateCommand())
            {
                plan.CommandText = "EXPLAIN QUERY PLAN " + PendingRefreshStore.PruneSettledIntentsSql;
                plan.Parameters.AddWithValue("@olderThan", long.MaxValue);
                plan.Parameters.AddWithValue("@limit", 1000);
                using var reader = plan.ExecuteReader();
                var details = new List<string>();
                while (reader.Read())
                    details.Add(reader.GetString(3));
                Assert.IsTrue(details.Any(d => d.Contains("ix_branch_intents_settled_at", StringComparison.Ordinal)),
                    string.Join(" | ", details));
                Assert.IsFalse(details.Any(d => d.Contains("TEMP B-TREE", StringComparison.Ordinal)),
                    "the candidate scan is ordered by the index, so LIMIT bounds it: " + string.Join(" | ", details));
            }

            var store = new PendingRefreshStore(connection);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var removed = 0;
            int pass;
            while ((pass = store.PruneCompleted(long.MaxValue)) > 0)
                removed += pass;
            watch.Stop();
            Assert.AreEqual(20_000, removed);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"pruning 20,000 rows took {watch.Elapsed}");
            Assert.IsNotNull(new BranchIntentStore(connection).GetByAdmissionId("stuck-head"), "an unsettled head is never pruned");
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    [TestMethod]
    public void AdmissionOrder_IsDurableAcrossConnections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_branch_intents_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new IndexDatabase(path))
            {
                db.RunMigrations();
                var connection = db.GetConnection();
                var repositoryId = AddRepository(connection);
                var store = new BranchIntentStore(connection);
                var first = store.Admit(repositoryId, "main", "ensure", 7, null, "{}", 20);
                var second = store.Admit(repositoryId, "main", "refresh", null, "r7", null, 21);
                Assert.IsTrue(first.Id < second.Id);
            }

            using var reopened = new IndexDatabase(path);
            reopened.RunMigrations();
            var repository = new SnapshotStore(reopened.GetConnection()).GetRepositoryId("https://example.test/owner/repo");
            Assert.IsNotNull(repository);
            var head = new BranchIntentStore(reopened.GetConnection()).Head(repository.Value, "main");
            Assert.IsNotNull(head);
            Assert.AreEqual("ensure", head.Kind);
            Assert.AreEqual(7L, head.QueuedEnsureId);
        }
        finally
        {
            SqliteTestDatabase.Delete(path);
        }
    }

    // Rule 6 boundary (#273 "Not handled"): requests queued before the upgrade are cleared by migration 035, which
    // creates no branch intents from them.
    [TestMethod]
    public void Migration_ClearsRequestsQueuedBeforeTheUpgrade_AndCreatesNoIntents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_branch_intents_upgrade_{Guid.NewGuid():N}.db");
        try
        {
            using var connection = Open(path);
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = """
                    CREATE TABLE repositories (id INTEGER PRIMARY KEY, remote_url TEXT NOT NULL);
                    INSERT INTO repositories VALUES (1, 'https://example.test/owner/repo');
                    CREATE TABLE queued_ensures (seq INTEGER PRIMARY KEY, identity_hash TEXT NOT NULL, request_json TEXT NOT NULL, admitted_at INTEGER NOT NULL);
                    INSERT INTO queued_ensures VALUES
                        (7, 'h7', '{"repository_remote_url":"https://example.test/owner/repo","branch_name":"feature"}', 10);
                    """;
                setup.ExecuteNonQuery();
            }

            using var migrationStream = typeof(IndexDatabase).Assembly.GetManifestResourceStream(
                "Sextant.Store.Migrations.035_branch_intents.sql");
            Assert.IsNotNull(migrationStream);
            using var reader = new StreamReader(migrationStream);
            using var migration = connection.CreateCommand();
            migration.CommandText = reader.ReadToEnd();
            migration.ExecuteNonQuery();

            using var count = connection.CreateCommand();
            count.CommandText = "SELECT (SELECT COUNT(*) FROM queued_ensures), (SELECT COUNT(*) FROM branch_intents);";
            using var counts = count.ExecuteReader();
            Assert.IsTrue(counts.Read());
            Assert.AreEqual(0L, counts.GetInt64(0), "requests queued before the upgrade are cleared");
            Assert.AreEqual(0L, counts.GetInt64(1), "no intent is created from them");
        }
        finally
        {
            SqliteTestDatabase.Delete(path);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        connection.CreateFunction<string, string>("sextant_repository_key",
            Sextant.Core.RemoteUrlIdentity.Normalize, isDeterministic: true);
        return connection;
    }

    private static IndexDatabase CreateDatabase(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"sextant_branch_intents_{Guid.NewGuid():N}.db");
        var db = new IndexDatabase(path);
        db.RunMigrations();
        return db;
    }

    private static long AddRepository(SqliteConnection connection) =>
        new SnapshotStore(connection).EnsureRepository("https://example.test/owner/repo", 1);
}
