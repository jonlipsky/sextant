using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #317: the tail of a retention pass (API history and the orphan-blob sweep) re-verifies with index seeks, never
/// regroups the API table inside a batch, takes turns between its two steps, and resumes the sweep from a cursor.
/// </summary>
[TestClass]
public class RetentionTailBatchTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private long _projectId;
    private long _fileId;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_tail_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _projectId = new ProjectStore(_conn).Insert(new ProjectIdentity
        {
            CanonicalId = "proj_tail_0123456789",
            GitRemoteUrl = "https://github.com/org/repo",
            RepoRelativePath = "src/App/App.csproj"
        }, 1);
        Scalar("INSERT INTO files (project_id, repo_relative_path) VALUES (" + _projectId + ", 'src/A.cs') RETURNING id;");
        _fileId = Scalar("SELECT id FROM files LIMIT 1;");
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    private static RetentionPolicy Keep(int commits) => new() { KeepCompleteGenerations = 10, ApiSnapshotKeepCommits = commits };

    // ---- API fast path ----

    [TestMethod]
    public void ApiFastPath_Hit_DeletesCandidatesOutsideTheWindow()
    {
        SeedApi("c1", 100, 3);
        SeedApi("c2", 200, 3);
        SeedApi("c3", 300, 3);
        SeedApi("c4", 400, 3);
        var retention = new RetentionService(_conn, Keep(2));
        var plan = retention.CreatePlan();

        Drain(retention, plan);

        Assert.IsFalse(plan.ApiStale);
        Assert.AreEqual(0L, ApiRows("c1") + ApiRows("c2"));
        Assert.AreEqual(6L, ApiRows("c3") + ApiRows("c4"));
    }

    [TestMethod]
    public void ApiFallback_CandidateLostAllRows_MarksTheApiStepStale_AndDeletesNothing() =>
        AssertStale(plan => Exec("DELETE FROM api_surface_snapshots WHERE git_commit = 'c1';"));

    [TestMethod]
    public void ApiFallback_WindowCommitLostItsRows_MarksTheApiStepStale() =>
        AssertStale(plan => Exec("DELETE FROM api_surface_snapshots WHERE git_commit = 'c4';"));

    [TestMethod]
    public void ApiFallback_WindowCommitFellBelowTheCandidate_MarksTheApiStepStale() =>
        AssertStale(plan => Exec("UPDATE api_surface_snapshots SET captured_at = 50 WHERE git_commit = 'c3';"));

    [TestMethod]
    public void ApiFallback_CandidateOvertookTheWindow_MarksTheApiStepStale() =>
        AssertStale(plan => Exec("UPDATE api_surface_snapshots SET captured_at = 900 WHERE git_commit = 'c1';"));

    private void AssertStale(Action<RetentionPlan> mutate)
    {
        SeedApi("c1", 100, 3);
        SeedApi("c2", 200, 3);
        SeedApi("c3", 300, 3);
        SeedApi("c4", 400, 3);
        var retention = new RetentionService(_conn, Keep(2));
        var plan = retention.CreatePlan();
        mutate(plan);
        var before = Scalar("SELECT COUNT(*) FROM api_surface_snapshots;");

        var total = Drain(retention, plan);

        Assert.IsTrue(plan.ApiStale, "the full regroup is never run inside a batch");
        Assert.IsTrue(plan.ApiDeferred);
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM api_surface_snapshots;") >= before - 3, "at most the one genuinely deletable commit goes");
        Assert.IsFalse(plan.HasWork, "a stale API step does not keep the pass looping");

        // The next plan recomputes and settles it.
        var next = retention.CreatePlan();
        Drain(retention, next);
        Assert.IsFalse(next.ApiStale);
    }

    [TestMethod]
    public void StaleApiStep_KeepsMoreRemaining_AsApiDeferred_AndALaterPlanDeletesTheCommit()
    {
        SeedApi("c1", 100, 3);
        SeedApi("c2", 200, 3);
        SeedApi("c3", 300, 3);
        SeedApi("c4", 400, 3);
        var retention = new RetentionService(_conn, Keep(2));
        var plan = retention.CreatePlan();
        Exec("UPDATE api_surface_snapshots SET captured_at = 900 WHERE git_commit = 'c1';");

        Drain(retention, plan);

        Assert.IsTrue(plan.ApiStale);
        Assert.IsTrue(plan.ApiDeferred, "the pass (Execute/the service) reports MoreRemaining with api_deferred from this");

        var next = retention.CreatePlan();
        Drain(retention, next);

        Assert.AreEqual(0L, ApiRows("c2"), "the next plan deletes the commit that fell outside the window");
        Assert.AreEqual(3L, ApiRows("c1"));
    }

    [TestMethod]
    public void PartialApiDelete_ThenAnotherBatch_StaysOnTheFastPath()
    {
        SeedApi("old", 100, 300);
        SeedApi("new", 500, 2);
        var retention = new RetentionService(_conn, Keep(1));
        var plan = retention.CreatePlan();

        var first = retention.ExecuteBatch(plan);
        Assert.AreEqual(RetentionService.BatchRowLimit, first.ApiSnapshotsDeleted);
        Assert.IsTrue(first.MoreRemaining);
        // Deleting from a commit can only lower its newest capture; it may even change mid-way.
        Exec("UPDATE api_surface_snapshots SET captured_at = 90 WHERE git_commit = 'old' AND captured_at = 100 AND id IN (SELECT id FROM api_surface_snapshots WHERE git_commit = 'old' LIMIT 3);");

        var second = Drain(retention, plan);

        Assert.IsFalse(plan.ApiStale);
        Assert.AreEqual(300 - RetentionService.BatchRowLimit, second.ApiSnapshotsDeleted);
        Assert.AreEqual(0L, ApiRows("old"));
        Assert.AreEqual(2L, ApiRows("new"));
    }

    // ---- turn taking ----

    [TestMethod]
    public void ApiBacklog_DoesNotStarveTheBlobSweep()
    {
        SeedApi("old", 100, RetentionService.BatchRowLimit * 3);
        SeedApi("new", 500, 1);
        SeedOrphans(RetentionService.BatchRowLimit * 3);
        var retention = new RetentionService(_conn, Keep(1));
        var plan = retention.CreatePlan();

        var a = retention.ExecuteBatch(plan);
        var b = retention.ExecuteBatch(plan);

        Assert.AreEqual(RetentionService.BatchRowLimit, a.ApiSnapshotsDeleted + b.ApiSnapshotsDeleted);
        Assert.AreEqual(RetentionService.BatchRowLimit, a.FileVersionsDeleted + b.FileVersionsDeleted,
            "two tail batches: one API step and one sweep step");
        Drain(retention, plan);
        Assert.AreEqual(0L, ApiRows("old"));
        Assert.AreEqual(0L, Scalar("SELECT COUNT(*) FROM file_versions;"));
    }

    [TestMethod]
    public void ASideWithNoWorkYieldsItsTurn()
    {
        SeedOrphans(RetentionService.BatchRowLimit * 2);
        var retention = new RetentionService(_conn, Keep(5));
        var plan = retention.CreatePlan();

        Assert.AreEqual(RetentionService.BatchRowLimit, retention.ExecuteBatch(plan).FileVersionsDeleted);
        Assert.AreEqual(RetentionService.BatchRowLimit, retention.ExecuteBatch(plan).FileVersionsDeleted);
    }

    // ---- sweep cursor ----

    [TestMethod]
    public void SweepCursor_ResumesAcrossTheSpan_TheCapBoundary_AndAcrossPlans()
    {
        const int protectedCount = RetentionService.FileSweepSpan + 104;
        SeedOrphans(protectedCount + 300);
        var cursor = new FileSweepCursor();
        var provider = new ProtectUpTo(Scalar($"SELECT id FROM file_versions ORDER BY id LIMIT 1 OFFSET {protectedCount - 1};"));
        var policy = Keep(5);

        var first = new RetentionService(_conn, policy, [provider], cursor).CreatePlan();
        var retention = new RetentionService(_conn, policy, [provider], cursor);
        var b1 = retention.ExecuteBatch(first);
        Assert.AreEqual(0, b1.FileVersionsDeleted, "the first span is all protected");
        Assert.IsTrue(b1.MoreRemaining);
        Assert.AreEqual(Scalar("SELECT id FROM file_versions ORDER BY id LIMIT 1 OFFSET " + (RetentionService.FileSweepSpan - 1) + ";"),
            first.FileVersionCursor, "the cursor sits on the last id of the span");

        var firstCursor = first.FileVersionCursor;
        // A later pass (a new plan) continues from the shared cursor instead of restarting at 0.
        var second = new RetentionService(_conn, policy, [provider], cursor).CreatePlan();
        Assert.AreEqual(firstCursor, second.FileVersionCursor);
        var b2 = retention.ExecuteBatch(second);
        Assert.AreEqual(RetentionService.BatchRowLimit, b2.FileVersionsDeleted, "stops at the cap boundary");
        var capCursor = second.FileVersionCursor;
        Assert.IsTrue(capCursor > firstCursor);

        var b3 = retention.ExecuteBatch(second);
        Assert.AreEqual(300 - RetentionService.BatchRowLimit, b3.FileVersionsDeleted, "resumes right after the capped id");
        Assert.IsTrue(second.FileVersionsDone);
        Assert.AreEqual(0L, cursor.Position, "a finished sweep rewinds the shared cursor");
        Assert.AreEqual((long)protectedCount, Scalar("SELECT COUNT(*) FROM file_versions;"));
    }

    [TestMethod]
    public void RolledBackBatch_RestoresTheCursor_AndTheApiState()
    {
        SeedApi("old", 100, 2);
        SeedApi("new", 500, 2);
        SeedOrphans(10);
        var cursor = new FileSweepCursor();
        var retention = new RetentionService(_conn, Keep(1), fileSweep: cursor);
        var plan = retention.CreatePlan();
        plan.ApiTurn = false;
        var cursorBefore = plan.FileVersionCursor;
        var filesBefore = Scalar("SELECT COUNT(*) FROM file_versions;");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            retention.ExecuteBatch(plan, beforeCommit: () => throw new InvalidOperationException("boom")));

        Assert.AreEqual(cursorBefore, plan.FileVersionCursor);
        Assert.AreEqual(cursorBefore, cursor.Position);
        Assert.IsFalse(plan.ApiTurn, "the turn rewinds too");
        Assert.IsFalse(plan.FileVersionsDone);
        Assert.AreEqual(filesBefore, Scalar("SELECT COUNT(*) FROM file_versions;"));
        Assert.IsNotNull(plan.PeekCommit(), "the API position rewinds");
    }

    // ---- shared sweep cursor ----

    [TestMethod]
    public void OverlappingPlans_ShareTheCursor_WithoutOnePassResettingTheOther()
    {
        SeedOrphans(RetentionService.FileSweepSpan + 100);
        var cursor = new FileSweepCursor();
        var provider = new ProtectUpTo(Scalar("SELECT MAX(id) FROM file_versions;"));
        var retention = new RetentionService(_conn, Keep(5), [provider], cursor);
        var a = retention.CreatePlan();
        var b = retention.CreatePlan();

        retention.ExecuteBatch(a);
        var spanEnd = cursor.Position;
        Assert.IsTrue(spanEnd > 0);
        Assert.AreEqual(0L, b.FileVersionCursor, "a plan's position is private until its own batch commits");

        // Another pass moves the shared cursor after `a` last saw it; `a` finishing must not rewind it.
        var other = spanEnd + 50;
        cursor.Advance(other);
        retention.ExecuteBatch(a);
        Assert.IsTrue(a.FileVersionsDone);
        Assert.AreEqual(other, cursor.Position, "a finished sweep rewinds only when no other pass advanced it since");

        retention.ExecuteBatch(b);
        Assert.AreEqual(other, cursor.Position, "a pass never moves the shared cursor backwards");
    }

    [TestMethod]
    public void RolledBackBatch_DoesNotMoveTheSharedCursor()
    {
        SeedOrphans(RetentionService.FileSweepSpan + 100);
        var cursor = new FileSweepCursor();
        var provider = new ProtectUpTo(Scalar("SELECT MAX(id) FROM file_versions;"));
        var retention = new RetentionService(_conn, Keep(5), [provider], cursor);
        var plan = retention.CreatePlan();
        retention.ExecuteBatch(plan);
        var committed = cursor.Position;
        Assert.IsTrue(committed > 0);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            retention.ExecuteBatch(plan, beforeCommit: () => throw new InvalidOperationException("boom")));

        Assert.AreEqual(committed, cursor.Position, "only a committed batch publishes");
        Assert.AreEqual(committed, plan.FileVersionCursor);
        Assert.IsFalse(plan.FileVersionsDone);
    }

    [TestMethod]
    public void SweepIsCappedPerPass_AndTheNextPassContinues_WithoutAStopReason()
    {
        const int extra = 10;
        SeedOrphans(RetentionService.FileSweepSpan * 2 + extra);
        var cursor = new FileSweepCursor();
        var provider = new ProtectUpTo(Scalar("SELECT MAX(id) FROM file_versions;"));
        var retention = new RetentionService(_conn, Keep(5), [provider], cursor) { SweepSpansPerPass = 2 };
        var first = retention.CreatePlan();

        var total = Drain(retention, first);

        Assert.IsNull(total.StopReason);
        Assert.IsFalse(first.FileVersionsDone, "the allowance ran out before the table did");
        Assert.IsFalse(first.HasWork, "an exhausted allowance is not outstanding work");
        Assert.AreEqual(Scalar($"SELECT id FROM file_versions ORDER BY id LIMIT 1 OFFSET {RetentionService.FileSweepSpan * 2 - 1};"),
            cursor.Position);

        var second = retention.CreatePlan();
        Drain(retention, second);

        Assert.IsTrue(second.FileVersionsDone);
        Assert.AreEqual(0L, cursor.Position, "the finished sweep wraps");
    }

    // ---- helpers ----

    private RetentionReport Drain(RetentionService retention, RetentionPlan plan)
    {
        var total = new RetentionReport { DryRun = false };
        for (var i = 0; i < 200; i++)
        {
            var batch = retention.ExecuteBatch(plan);
            Assert.IsNull(batch.StopReason);
            total = RetentionService.Combine(total, batch);
            if (!batch.MoreRemaining) return total;
        }
        Assert.Fail("did not converge");
        return total;
    }

    private void SeedApi(string commit, long capturedAt, int rows)
    {
        Exec("BEGIN;");
        for (var r = 0; r < rows; r++)
            Exec($"""
                INSERT INTO api_surface_snapshots (project_id, symbol_id, symbol_key, fully_qualified_name, accessibility, signature_hash, captured_at, git_commit)
                VALUES ({_projectId}, NULL, 'k{commit}{r}', 'f', 'public', 'h', {capturedAt}, '{commit}');
                """);
        Exec("COMMIT;");
    }

    private void SeedOrphans(int count)
    {
        Exec("BEGIN;");
        for (var i = 0; i < count; i++)
            Exec($"INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES ({_fileId}, randomblob(32), 1);");
        Exec("COMMIT;");
    }

    private long ApiRows(string commit) => Scalar($"SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = '{commit}';");

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private sealed class ProtectUpTo(long maxId) : IRetentionProtectionProvider
    {
        public string Name => "test-protect-file-versions";

        public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT id FROM file_versions WHERE id <= {maxId};";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) builder.ProtectFileVersion(reader.GetInt64(0), "test");
        }
    }
}
