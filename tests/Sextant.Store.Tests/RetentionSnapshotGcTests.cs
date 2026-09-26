using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Service-owned retention/GC of immutable snapshot DATA (issues #46/#37/#54). Phase 9 only added
/// BranchPointerProtection and DEFERRED snapshot-data GC to the service. Now that the service owns the
/// durable catalog + retention endpoint, retention reclaims the semantic rows owned by snapshots that no
/// RETAINED generation, branch pointer, or retained consumer still references — while sparing:
/// <list type="bullet">
///   <item>a provider referenced by ANY retained consumer generation, not just branch-pointed heads (#54);</item>
///   <item>a branch-pointed snapshot (Phase-9 BranchPointerProtection);</item>
///   <item>source blobs still referenced by a surviving snapshot's symbols, with a BOUNDED prune (#37).</item>
/// </list>
/// </summary>
[TestClass]
public class RetentionSnapshotGcTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _now;

    private long _consumerNew, _consumerOld, _providerShared, _providerOrphan, _branchPointed;
    private long _cnBlob, _coBlob, _psBlob, _poBlob, _bpBlob;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_snapgc_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var appRepo = _snapshots.EnsureRepository("https://github.com/org/app", _now);
        var libRepo = _snapshots.EnsureRepository("https://github.com/org/lib", _now);

        // Three generations, each isolated so branch-pointer protection of one does not spare another:
        //   runData   — superseded consumer + its providers (no branch pointer, not servable ⇒ deletable)
        //   runBranch — the branch-pointed snapshot (protected by BranchPointerProtection)
        //   runNew    — newest complete ⇒ servable, holds the retained consumer
        var runData = CompleteRun(1);
        var runBranch = CompleteRun(2);
        var runNew = CompleteRun(3);

        // Retained consumer on the servable generation.
        _consumerNew = Snapshot(appRepo, runNew, "commit-new");
        var cnProj = ProjectRow(_consumerNew, "cn");
        _cnBlob = SymbolWithBlob(cnProj, "cn");

        // Superseded consumer on the deletable generation (data-GC eligible).
        _consumerOld = Snapshot(appRepo, runData, "commit-old");
        var coProj = ProjectRow(_consumerOld, "co");
        _coBlob = SymbolWithBlob(coProj, "co");

        // Provider shared by BOTH consumers — must survive because a RETAINED consumer references it (#54).
        _providerShared = Snapshot(libRepo, runData, "prov-shared", isProvider: true);
        var psProj = ProjectRow(_providerShared, "ps");
        _psBlob = SymbolWithBlob(psProj, "ps");
        Dep(_consumerNew, cnProj, _providerShared, psProj, libRepo);
        Dep(_consumerOld, coProj, _providerShared, psProj, libRepo);

        // Provider referenced ONLY by the superseded consumer — orphaned, must be GC'd.
        _providerOrphan = Snapshot(libRepo, runData, "prov-orphan", isProvider: true);
        var poProj = ProjectRow(_providerOrphan, "po");
        _poBlob = SymbolWithBlob(poProj, "po");
        Dep(_consumerOld, coProj, _providerOrphan, poProj, libRepo);

        // Branch-pointed snapshot on its own generation — always spared (BranchPointerProtection).
        _branchPointed = Snapshot(appRepo, runBranch, "branch-head");
        var bpProj = ProjectRow(_branchPointed, "bp");
        _bpBlob = SymbolWithBlob(bpProj, "bp");
        var branchId = _snapshots.EnsureBranch(appRepo, "release/1.0", false, _now);
        _snapshots.SetBranchPointer(branchId, _branchPointed, _now);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    // keep=0 forces every non-servable generation deletable regardless of GetAllRuns ordering, so the
    // classification is deterministic: runOld's superseded snapshots are the only data-GC candidates.
    private RetentionPolicy Policy() =>
        new() { KeepCompleteGenerations = 0, ApiSnapshotKeepCommits = 0, PruneSupersededSourceBlobs = true };

    [TestMethod]
    public void Plan_DoesNotModify_ButReportsOrphanedSnapshotData()
    {
        var before = ScalarLong("SELECT COUNT(*) FROM snapshots;");
        var report = new RetentionService(_conn, Policy()).Plan();

        Assert.IsTrue(report.DryRun);
        Assert.AreEqual(2, report.SnapshotsDeleted, "consumerOld + providerOrphan are reported orphaned");
        Assert.AreEqual(2, report.SnapshotProjectVersionsDeleted, "their project-version rows are reported reclaimable");
        Assert.AreEqual(before, ScalarLong("SELECT COUNT(*) FROM snapshots;"), "Plan must not delete snapshots");
    }

    [TestMethod]
    public void Execute_GcsOrphanedSnapshotData_SparesRetainedProvidersAndBranchHeads()
    {
        var report = new RetentionService(_conn, Policy()).Execute();

        Assert.AreEqual(2, report.SnapshotsDeleted);
        Assert.AreEqual(2, report.SnapshotProjectVersionsDeleted);

        // Orphaned snapshots and their data are gone.
        Assert.IsNull(_snapshots.GetById(_consumerOld), "superseded consumer snapshot GC'd (#46)");
        Assert.IsNull(_snapshots.GetById(_providerOrphan), "provider referenced only by a superseded consumer GC'd (#46)");
        Assert.AreEqual(0, ProjectCount(_consumerOld), "superseded consumer's project rows cascade-deleted");
        Assert.AreEqual(0, ProjectCount(_providerOrphan), "orphan provider's project rows cascade-deleted");

        // #54: the shared provider survives because a RETAINED consumer still references it.
        Assert.IsNotNull(_snapshots.GetById(_providerShared), "provider referenced by a retained consumer is spared (#54)");
        Assert.IsTrue(ProjectCount(_providerShared) > 0, "shared provider's project-version rows are preserved");

        // Retained + branch-pointed snapshots untouched.
        Assert.IsNotNull(_snapshots.GetById(_consumerNew), "servable consumer snapshot preserved");
        Assert.IsNotNull(_snapshots.GetById(_branchPointed), "branch-pointed snapshot preserved (BranchPointerProtection)");

        // #37: source blobs newly orphaned by the snapshot GC are pruned; still-referenced blobs survive.
        Assert.AreEqual(0, BlobCount(_coBlob), "superseded consumer's blob pruned (#37)");
        Assert.AreEqual(0, BlobCount(_poBlob), "orphan provider's blob pruned (#37)");
        Assert.AreEqual(1, BlobCount(_cnBlob), "servable consumer's blob retained");
        Assert.AreEqual(1, BlobCount(_psBlob), "shared provider's blob retained (#54)");
        Assert.AreEqual(1, BlobCount(_bpBlob), "branch-pointed snapshot's blob retained");
    }

    [TestMethod]
    public void Execute_GcdSnapshot_CascadesItsCoverageRow_RetainedSnapshotKeepsIt()
    {
        var coverage = new SnapshotCoverageStore(_conn);
        var partial = new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Partial, Reasons = ["gap"] };
        coverage.Record(_consumerOld, partial, _now);
        coverage.Record(_consumerNew, partial, _now);

        new RetentionService(_conn, Policy()).Execute();

        Assert.IsNull(_snapshots.GetById(_consumerOld));
        Assert.AreEqual(0, ScalarLong($"SELECT COUNT(*) FROM snapshot_coverage WHERE snapshot_id = {_consumerOld};"),
            "a GC'd snapshot's coverage row cascades with it (#119)");
        Assert.IsNotNull(coverage.Get(_consumerNew), "a retained snapshot keeps its coverage");
    }

    // === seeding helpers =========================================================================

    private long CompleteRun(long ord)
    {
        var runStore = new IndexRunStore(_conn);
        var id = runStore.BeginRun("full", _now + ord,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(id, _now + ord, 1);
        return id;
    }

    private long Snapshot(long repoId, long runId, string commit, bool isProvider = false)
    {
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = $"repo-{repoId}",
            CommitSha = commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = _snapshots.BeginPending(identity, repoId, null, runId, _now, isProvider: isProvider);
        _snapshots.MarkComplete(id, _now);
        return id;
    }

    private long ProjectRow(long snapshotId, string tag)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at, snapshot_id)
            VALUES (@c, @g, @p, @now, @snap) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@c", $"proj_{tag}_{snapshotId}");
        cmd.Parameters.AddWithValue("@g", "https://github.com/org/x");
        cmd.Parameters.AddWithValue("@p", $"src/{tag}/{tag}.csproj");
        cmd.Parameters.AddWithValue("@now", _now);
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        return (long)cmd.ExecuteScalar()!;
    }

    private long SymbolWithBlob(long projectId, string tag)
    {
        long fileId;
        using (var f = _conn.CreateCommand())
        {
            f.CommandText = "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
            f.Parameters.AddWithValue("@p", projectId);
            f.Parameters.AddWithValue("@path", $"src/{tag}/{tag}.cs");
            fileId = (long)f.ExecuteScalar()!;
        }

        long fvId;
        using (var fv = _conn.CreateCommand())
        {
            fv.CommandText =
                "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@f, @h, @now) RETURNING id;";
            fv.Parameters.AddWithValue("@f", fileId);
            fv.Parameters.AddWithValue("@h", NewHash(projectId));
            fv.Parameters.AddWithValue("@now", _now);
            fvId = (long)fv.ExecuteScalar()!;
        }

        using var s = _conn.CreateCommand();
        s.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 file_version_id, line_start, line_end, last_indexed_at)
            VALUES (@p, @key, @fqn, @name, 0, 0, @fv, 1, 10, @now);
            """;
        s.Parameters.AddWithValue("@p", projectId);
        s.Parameters.AddWithValue("@key", $"global::{tag}.T:{projectId}");
        s.Parameters.AddWithValue("@fqn", $"global::{tag}.T");
        s.Parameters.AddWithValue("@name", $"{tag}T");
        s.Parameters.AddWithValue("@fv", fvId);
        s.Parameters.AddWithValue("@now", _now);
        s.ExecuteNonQuery();
        return fvId;
    }

    private void Dep(long consumerSnap, long consumerProj, long providerSnap, long providerProj, long providerRepo)
    {
        new SnapshotDependencyStore(_conn).Insert(new SnapshotDependencyEdge
        {
            ConsumerSnapshotId = consumerSnap,
            ConsumerProjectId = consumerProj,
            ProviderSnapshotId = providerSnap,
            ProviderProjectId = providerProj,
            ProviderRepositoryId = providerRepo,
            ProviderCommitSha = "deadbeef",
            ReferenceKind = "call",
            CreatedAt = _now
        });
    }

    private static byte[] NewHash(long seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((seed + i) & 0xFF);
        return bytes;
    }

    private long ProjectCount(long snapshotId) =>
        ScalarLong($"SELECT COUNT(*) FROM projects WHERE snapshot_id = {snapshotId};");

    private long BlobCount(long fileVersionId) =>
        ScalarLong($"SELECT COUNT(*) FROM file_versions WHERE id = {fileVersionId};");

    private long ScalarLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
