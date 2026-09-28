using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #85: <see cref="SnapshotStore.IsReselectable"/> is the "still intact / servable" predicate the
/// service ensure reuse path consults before un-superseding an already-built snapshot for a branch reset /
/// force-push (A→B→A). Only a published, non-overlay, data-bearing Superseded snapshot whose providers are
/// all still published may be resurrected as Complete; anything else must be rebuilt.
/// </summary>
[TestClass]
public class SnapshotReselectableTests
{
    private const string RepoUrl = "https://github.com/org/app";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private IndexRunStore _runs = null!;
    private long _repo;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_reselect_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _runs = new IndexRunStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repo = _snapshots.EnsureRepository(RepoUrl, _now);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void IntactSupersededSnapshot_IsReselectable()
    {
        var a = PublishedSnapshot("commit-A");
        _snapshots.MarkStatus(a, SnapshotStatus.Superseded);

        Assert.IsTrue(_snapshots.IsReselectable(a),
            "a published, data-bearing superseded snapshot on a complete generation is reselectable");
    }

    [TestMethod]
    public void NonSupersededStatuses_AreNotReselectable()
    {
        var a = PublishedSnapshot("commit-A");
        foreach (var status in new[]
                 {
                     SnapshotStatus.Complete, SnapshotStatus.Pending, SnapshotStatus.Partial,
                     SnapshotStatus.Failed, SnapshotStatus.Unsupported
                 })
        {
            _snapshots.MarkStatus(a, status);
            Assert.IsFalse(_snapshots.IsReselectable(a), $"a {status} snapshot is never reselected (only superseded)");
        }
        Assert.IsFalse(_snapshots.IsReselectable(999_999), "an unknown snapshot id is not reselectable");
    }

    [TestMethod]
    public void NeverPublishedSnapshot_IsNotReselectable()
    {
        var run = CompleteRun();
        var (id, _, _) = _snapshots.BeginPending(Identity("commit-A"), _repo, null, run, _now);
        AddOwnedProject(id);
        _snapshots.MarkStatus(id, SnapshotStatus.Superseded);

        Assert.IsFalse(_snapshots.IsReselectable(id),
            "a snapshot that never went through the guarded publish (published_at NULL) is not reselectable");
    }

    [TestMethod]
    public void DataLessSupersededSnapshot_IsNotReselectable()
    {
        var a = PublishedSnapshot("commit-A");
        _snapshots.MarkStatus(a, SnapshotStatus.Superseded);
        Exec("DELETE FROM projects WHERE snapshot_id = @id;", a);

        Assert.IsFalse(_snapshots.IsReselectable(a),
            "a superseded snapshot whose data rows are gone must be rebuilt, never resurrected as Complete");
    }

    [TestMethod]
    public void SnapshotMappingOnlyAnotherSnapshotsProject_IsNotReselectable()
    {
        var owner = PublishedSnapshot("commit-owner");
        var borrower = PublishedSnapshotOnRun("commit-borrower", CompleteRun(), withProject: false);
        var ownerProject = OwnedProjectOf(owner);
        _snapshots.MapProject(borrower, ownerProject);
        _snapshots.MarkStatus(borrower, SnapshotStatus.Superseded);

        Assert.IsFalse(_snapshots.IsReselectable(borrower),
            "only project-version rows the snapshot OWNS count as its intact data");
    }

    [TestMethod]
    public void OverlaySnapshot_IsNotReselectable()
    {
        var a = PublishedSnapshot("commit-A");
        _snapshots.MarkStatus(a, SnapshotStatus.Superseded);
        Exec("UPDATE snapshots SET is_overlay = 1 WHERE id = @id;", a);

        Assert.IsFalse(_snapshots.IsReselectable(a), "an overlay generation is never reselected by an ensure");
    }

    [TestMethod]
    public void SnapshotPublishedByARetry_StillBoundToItsAbandonedFirstRun_IsReselectable()
    {
        // run_id is bound only when the row is first staged. A first attempt that committed a batch and was
        // then cancelled/crashed leaves the row Pending on run R1 (later abandoned); the retry rebuilds into
        // the SAME snapshot id under R2 and publishes it (published_at set) while run_id still says R1. The
        // snapshot is intact and published, so it must stay reselectable — the ledger is not consulted.
        var firstAttempt = _runs.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        _runs.AbandonRun(firstAttempt, _now + 1);
        var a = PublishedSnapshotOnRun("commit-A", firstAttempt);
        _snapshots.MarkStatus(a, SnapshotStatus.Superseded);

        Assert.IsTrue(_snapshots.IsReselectable(a),
            "a snapshot republished by a retry is intact even though run_id still names the abandoned first run");
    }

    [TestMethod]
    public void SnapshotWhoseRunLedgerWasReclaimed_IsReselectable()
    {
        var run = CompleteRun();
        var a = PublishedSnapshotOnRun("commit-A", run);
        _snapshots.MarkStatus(a, SnapshotStatus.Superseded);
        _runs.DeleteRun(run);

        Assert.IsTrue(_snapshots.IsReselectable(a),
            "retention reclaims the run ledger independently of the snapshot data; the data is what's judged");

        var contribution = PublishedSnapshotOnRun("commit-contrib", runId: null);
        _snapshots.MarkStatus(contribution, SnapshotStatus.Superseded);
        Assert.IsTrue(_snapshots.IsReselectable(contribution),
            "a run-less (contribution-assembled) snapshot is judged on its data alone");
    }

    [TestMethod]
    public void ProviderStatus_GatesReselect()
    {
        var consumer = PublishedSnapshot("commit-consumer");
        var provider = PublishedSnapshot("commit-provider");
        AddDependency(consumer, provider);
        _snapshots.MarkStatus(consumer, SnapshotStatus.Superseded);

        Assert.IsTrue(_snapshots.IsReselectable(consumer), "a complete provider keeps the consumer reselectable");

        _snapshots.MarkStatus(provider, SnapshotStatus.Superseded);
        Assert.IsTrue(_snapshots.IsReselectable(consumer),
            "a superseded provider is still published (Phase-12 published-status gate)");

        foreach (var status in new[] { SnapshotStatus.Pending, SnapshotStatus.Failed, SnapshotStatus.Partial })
        {
            _snapshots.MarkStatus(provider, status);
            Assert.IsFalse(_snapshots.IsReselectable(consumer),
                $"a consumer whose provider is {status} (unpublished) must be rebuilt, not resurrected");
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private long CompleteRun()
    {
        var runId = _runs.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        _runs.MarkComplete(runId, _now, 1);
        return runId;
    }

    private long PublishedSnapshot(string commit) => PublishedSnapshotOnRun(commit, CompleteRun());

    private long PublishedSnapshotOnRun(string commit, long? runId, bool withProject = true)
    {
        var commitId = _snapshots.EnsureCommit(_repo, commit, treeSha: null, _now);
        var (id, _, _) = _snapshots.BeginPending(Identity(commit), _repo, commitId, runId, _now);
        if (withProject)
            AddOwnedProject(id);
        _snapshots.MarkComplete(id, _now);
        return id;
    }

    private static SnapshotIdentity Identity(string commit) => new()
    {
        RepositoryRemoteUrl = RepoUrl,
        CommitSha = commit,
        SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
        AnalyzerVersion = "test-analyzer",
        ToolchainFingerprint = "test-toolchain"
    };

    private void AddOwnedProject(long snapshotId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at, snapshot_id)
            VALUES (@c, @g, @p, @now, @snap) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@c", $"proj_{snapshotId}");
        cmd.Parameters.AddWithValue("@g", RepoUrl);
        cmd.Parameters.AddWithValue("@p", "src/App/App.csproj");
        cmd.Parameters.AddWithValue("@now", _now);
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        var projectId = (long)cmd.ExecuteScalar()!;
        _snapshots.MapProject(snapshotId, projectId);
    }

    private void AddDependency(long consumer, long provider)
    {
        var consumerProject = OwnedProjectOf(consumer);
        var providerProject = OwnedProjectOf(provider);
        new SnapshotDependencyStore(_conn).Insert(new SnapshotDependencyEdge
        {
            ConsumerSnapshotId = consumer,
            ConsumerProjectId = consumerProject,
            ProviderSnapshotId = provider,
            ProviderProjectId = providerProject,
            ProviderRepositoryId = _repo,
            ProviderCommitSha = "commit-provider",
            ReferenceKind = "project",
            CreatedAt = _now
        });
    }

    private void Exec(string sql, long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    private long OwnedProjectOf(long snapshotId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM projects WHERE snapshot_id = @id;";
        cmd.Parameters.AddWithValue("@id", snapshotId);
        return (long)cmd.ExecuteScalar()!;
    }
}
