using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 17 slice 2, acceptance criterion 4: retention never deletes a protected branch head, an
/// OPEN-pull-request snapshot, a submodule provider pin, or an active-overlay base, and the per-repository
/// quota GC is protected-set-aware (it evicts only oldest-over-cap complete snapshots that are neither
/// hard-protected nor depended on by a still-retained snapshot). These tests VALIDATE the cumulative
/// protected-set assembled across Phases 9/10/12/13 plus the new open-PR root (migration 019).
/// </summary>
[TestClass]
public class RetentionQuotaAndPrProtectionTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _repo;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_quota_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repo = _snapshots.EnsureRepository("https://github.com/org/app", _now);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void OpenPullRequestSnapshot_IsProtected_OnADeletableGeneration()
    {
        // The PR snapshot lives on the OLDER generation; a newer complete generation is servable, so the
        // older one is deletable and ONLY the open-PR protection can spare the snapshot's data.
        var oldRun = CompleteRun(1);
        var prSnap = Snapshot(oldRun, "pr-head", createdAt: _now);
        CompleteRun(2); // newer servable generation supersedes oldRun
        new PullRequestSnapshotStore(_conn).Register(_repo, 42, prSnap, "pr-head", _now);

        new RetentionService(_conn, ZeroKeep()).Execute();

        Assert.IsNotNull(_snapshots.GetById(prSnap), "an OPEN pull request's snapshot must survive retention (criterion 4)");
    }

    [TestMethod]
    public void ClosedPullRequestSnapshot_IsReleased_AndBecomesGcEligible()
    {
        var oldRun = CompleteRun(1);
        var prSnap = Snapshot(oldRun, "pr-head", createdAt: _now);
        CompleteRun(2); // newer servable generation supersedes oldRun
        var prStore = new PullRequestSnapshotStore(_conn);
        prStore.Register(_repo, 7, prSnap, "pr-head", _now);
        prStore.Close(_repo, 7, _now);

        new RetentionService(_conn, ZeroKeep()).Execute();

        Assert.IsNull(_snapshots.GetById(prSnap), "a CLOSED pull request no longer protects its snapshot on a deletable generation");
    }

    [TestMethod]
    public void Quota_EvictsOldestOverCap_ButNeverAProtectedSnapshot()
    {
        // Four complete snapshots for one repo on the SAME servable generation, ages oldest→newest.
        var run = CompleteRun(1);
        var s1 = Snapshot(run, "c1", createdAt: _now + 1);
        var s2 = Snapshot(run, "c2", createdAt: _now + 2);
        var s3 = Snapshot(run, "c3", createdAt: _now + 3);
        var s4 = Snapshot(run, "c4", createdAt: _now + 4);

        // Pin the OLDEST via an open PR so the quota is forced to evict a NEWER one instead of the oldest.
        new PullRequestSnapshotStore(_conn).Register(_repo, 1, s1, "c1", _now);

        // Cap = 2 complete snapshots per repo. Protected s1 is kept; of the rest the oldest evictable
        // (s2) is dropped, leaving the cap's worth of the newest (s3, s4) plus the protected s1.
        var policy = new RetentionPolicy { KeepCompleteGenerations = 10, MaxSnapshotsPerRepository = 2 };
        new RetentionService(_conn, policy).Execute();

        Assert.IsNotNull(_snapshots.GetById(s1), "protected (open-PR) snapshot is never evicted by quota");
        Assert.IsNull(_snapshots.GetById(s2), "oldest evictable over-cap snapshot is quota-evicted");
        Assert.IsNotNull(_snapshots.GetById(s3), "newest snapshots within the cap are kept");
        Assert.IsNotNull(_snapshots.GetById(s4), "newest snapshots within the cap are kept");
    }

    [TestMethod]
    public void Quota_Unbounded_WhenMaxIsZero_IsIdenticalToNoQuota()
    {
        var run = CompleteRun(1);
        var ids = Enumerable.Range(1, 5).Select(i => Snapshot(run, $"c{i}", createdAt: _now + i)).ToList();

        // MaxSnapshotsPerRepository = 0 (default) ⇒ no quota eviction; keep window keeps the servable gen.
        new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 10 }).Execute();

        foreach (var id in ids)
            Assert.IsNotNull(_snapshots.GetById(id), "with an unbounded quota every snapshot on a retained generation survives");
    }

    // === helpers =================================================================================

    private RetentionPolicy ZeroKeep() =>
        new() { KeepCompleteGenerations = 0, ApiSnapshotKeepCommits = 0 };

    private long CompleteRun(long ord)
    {
        var runStore = new IndexRunStore(_conn);
        var id = runStore.BeginRun("full", _now + ord,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(id, _now + ord, 1);
        return id;
    }

    private long Snapshot(long runId, string commit, long createdAt)
    {
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = "https://github.com/org/app",
            CommitSha = commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = _snapshots.BeginPending(identity, _repo, null, runId, createdAt);
        _snapshots.MarkComplete(id, createdAt);
        // BeginPending records created_at = the supplied now; force the intended ordering explicitly.
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE snapshots SET created_at = @c WHERE id = @id;";
        cmd.Parameters.AddWithValue("@c", createdAt);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        return id;
    }
}
