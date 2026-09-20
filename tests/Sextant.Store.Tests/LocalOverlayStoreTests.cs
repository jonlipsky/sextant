using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 10 — Git-aware local overlay indexing, store-level guarantees. These deterministic tests
/// exercise the overlay identity/persistence primitives (<see cref="SnapshotIdentity"/>,
/// <see cref="SnapshotStore"/> overlay columns, <see cref="OverlayBaseProtection"/>) directly, without
/// git or Roslyn, and map to the folded-in issues:
/// <list type="bullet">
///   <item>#43 — a dirty working tree's snapshot identity is <em>commit + delta</em>, never the bare
///   committed base, and an identical dirty tree recomputes the identical identity (idempotent restart);</item>
///   <item>#44 — an overlay is a distinct snapshot layered on the base; the base row is untouched and a
///   branch-pointed overlay pins its base generation so retention never deletes shared rows;</item>
///   <item>criterion 5 — a fallback full local index records an explicit reason.</item>
/// </list>
/// The end-to-end orchestration (base stays byte-identical across an overlay run, etc.) is proven by
/// <c>LocalOverlayIntegrationTests</c>; these are the fast, robust store-level backstops.
/// </summary>
[TestClass]
public class LocalOverlayStoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_overlay_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    // ==== Issue #43: a dirty tree's identity is commit+delta, never the bare committed base ========

    [TestMethod]
    public void DirtyDeltaIdentity_DiffersFromCleanBase_AndIsDeterministic()
    {
        var baseIdentity = BaseIdentity(delta: null);
        var dirtyIdentity = BaseIdentity(delta: "delta_abc123");

        Assert.AreNotEqual(baseIdentity.Hash, dirtyIdentity.Hash,
            "a dirty working tree must never share the clean base commit's snapshot identity (#43)");

        // An identical dirty state recomputes the identical identity — the property that lets a
        // restart/periodic pass idempotently re-select the same overlay instead of rebuilding it.
        var dirtyAgain = BaseIdentity(delta: "delta_abc123");
        Assert.AreEqual(dirtyIdentity.Hash, dirtyAgain.Hash,
            "the same dirty working-tree delta must recompute the same identity (idempotent restart)");

        // A different delta is a different identity (a further edit is a new overlay generation).
        var otherDirty = BaseIdentity(delta: "delta_zzz999");
        Assert.AreNotEqual(dirtyIdentity.Hash, otherDirty.Hash,
            "a different working-tree delta must produce a different overlay identity");
    }

    // ==== Overlay persistence round-trip ==========================================================

    [TestMethod]
    public void BeginPending_Overlay_RecordsOverlayProvenance()
    {
        var repoId = new SnapshotStore(_conn).EnsureRepository(RepoUrl, now: 1);
        var (baseId, _) = SeedBaseSnapshot(repoId, "commit_base");
        var store = new SnapshotStore(_conn);
        var commitId = store.EnsureCommit(repoId, "commit_base", "tree_commit_base", now: 1);

        var overlayIdentity = BaseIdentity(delta: "delta_edit1");
        var (overlayId, existed, status) = store.BeginPending(
            overlayIdentity, repoId, commitId, runId: null, now: 2, baseSnapshotId: baseId);

        Assert.IsFalse(existed, "a fresh overlay identity creates a new snapshot");
        Assert.AreEqual(SnapshotStatus.Pending, status);

        var row = store.GetById(overlayId)!;
        Assert.IsTrue(row.IsOverlay, "an overlay snapshot is flagged is_overlay");
        Assert.AreEqual(baseId, row.BaseSnapshotId, "the overlay records its committed base");
        Assert.AreEqual("delta_edit1", row.WorkingTreeDelta, "the overlay records its working-tree delta (#43)");
        Assert.IsNull(row.FallbackReason, "an overlay over a base is not a fallback");

        // The base row is untouched by staging the overlay (issue #44).
        var baseRow = store.GetById(baseId)!;
        Assert.IsFalse(baseRow.IsOverlay, "the base is not an overlay");
        Assert.IsNull(baseRow.BaseSnapshotId, "the base has no base of its own");
        Assert.AreEqual(SnapshotStatus.Complete, baseRow.Status, "staging an overlay never mutates the base status (#44)");
    }

    [TestMethod]
    public void BeginPending_Overlay_IsIdempotentForSameDelta()
    {
        var repoId = new SnapshotStore(_conn).EnsureRepository(RepoUrl, now: 1);
        var (baseId, _) = SeedBaseSnapshot(repoId, "commit_base");
        var store = new SnapshotStore(_conn);
        var commitId = store.EnsureCommit(repoId, "commit_base", "tree_commit_base", now: 1);
        var identity = BaseIdentity(delta: "delta_same");

        var (first, firstExisted, _) = store.BeginPending(identity, repoId, commitId, null, 2, baseSnapshotId: baseId);
        var (second, secondExisted, _) = store.BeginPending(identity, repoId, commitId, null, 3, baseSnapshotId: baseId);

        Assert.IsFalse(firstExisted, "the first stage creates the overlay");
        Assert.IsTrue(secondExisted, "re-staging the same dirty delta attaches to the existing overlay (idempotent restart)");
        Assert.AreEqual(first, second, "the same dirty working-tree delta resolves to the same overlay snapshot");
    }

    // ==== Criterion 5: an explicit fallback reason is recorded =====================================

    [TestMethod]
    public void BeginPending_Fallback_RecordsExplicitReason()
    {
        var repoId = new SnapshotStore(_conn).EnsureRepository(RepoUrl, now: 1);
        var store = new SnapshotStore(_conn);
        var commitId = store.EnsureCommit(repoId, "commit_x", "tree_x", now: 1);
        const string reason = "no compatible committed base snapshot for HEAD commit_x; indexed the dirty working tree fully";

        var identity = BaseIdentity(delta: "delta_fallback");
        var (id, _, _) = store.BeginPending(identity, repoId, commitId, runId: null, now: 2, fallbackReason: reason);

        var row = store.GetById(id)!;
        Assert.IsFalse(row.IsOverlay, "a full local fallback is not an overlay");
        Assert.IsNull(row.BaseSnapshotId, "a fallback has no base to layer on");
        Assert.AreEqual(reason, row.FallbackReason, "the fallback records an explicit reason (criterion 5)");
        Assert.AreEqual("delta_fallback", row.WorkingTreeDelta, "the fallback still carries the delta so it is not confused with the clean base (#43)");
    }

    // ==== Issue #44: retention pins a branch-pointed overlay's base generation =====================

    [TestMethod]
    public void OverlayBaseProtection_PinsBaseGeneration_WhenBranchPointsAtOverlay()
    {
        var repoId = new SnapshotStore(_conn).EnsureRepository(RepoUrl, now: 1);
        var (baseId, baseRunId) = SeedBaseSnapshot(repoId, "commit_base");
        var store = new SnapshotStore(_conn);
        var commitId = store.EnsureCommit(repoId, "commit_base", "tree_commit_base", now: 1);

        // Stage + publish an overlay and point the default branch at it.
        var overlayIdentity = BaseIdentity(delta: "delta_edit");
        var (overlayId, _, _) = store.BeginPending(overlayIdentity, repoId, commitId, runId: null, now: 2, baseSnapshotId: baseId);
        store.MarkComplete(overlayId, publishedAt: 3);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 3);
        store.SetBranchPointer(branchId, overlayId, now: 3);

        // The overlay-base protection must spare the base's generation and commit.
        var builder = new RetentionProtectionBuilder();
        new OverlayBaseProtection().Contribute(_conn, builder);
        var protection = builder.Build();

        Assert.IsTrue(protection.IsGenerationProtected(baseRunId),
            "a branch-pointed overlay pins its base generation so shared rows are never GC'd (#44)");
        Assert.IsTrue(protection.IsCommitProtected("commit_base"),
            "the base commit's API history is pinned while an overlay layers on it (#44)");
    }

    [TestMethod]
    public void OverlayBaseProtection_ContributesNothing_WhenNoOverlayIsPointed()
    {
        var repoId = new SnapshotStore(_conn).EnsureRepository(RepoUrl, now: 1);
        var (baseId, baseRunId) = SeedBaseSnapshot(repoId, "commit_base");
        var store = new SnapshotStore(_conn);

        // A branch pointing directly at the base (no overlay) needs no overlay-base protection — the
        // base is protected by BranchPointerProtection instead.
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 2);
        store.SetBranchPointer(branchId, baseId, now: 2);

        var builder = new RetentionProtectionBuilder();
        new OverlayBaseProtection().Contribute(_conn, builder);
        var protection = builder.Build();

        Assert.IsFalse(protection.IsGenerationProtected(baseRunId),
            "with no overlay pointed, the overlay-base provider contributes nothing (the base is a direct branch head)");
    }

    // ==== helpers =================================================================================

    private const string RepoUrl = "https://github.com/org/overlay-repo";
    private const string LogicalCanonical = "logical_overlay_0123456789";

    private static SnapshotIdentity BaseIdentity(string? delta) => new()
    {
        RepositoryRemoteUrl = RepoUrl,
        CommitSha = "commit_base",
        TreeSha = "tree_commit_base",
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc",
        WorkingTreeDelta = delta
    };

    /// <summary>Seeds a published base snapshot with its own complete generation, returning its id + run id.</summary>
    private (long baseId, long runId) SeedBaseSnapshot(long repoId, string commitSha)
    {
        var store = new SnapshotStore(_conn);
        var runStore = new IndexRunStore(_conn);
        var runId = runStore.BeginRun("full", 1,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, 1, 1);

        var commitId = store.EnsureCommit(repoId, commitSha, $"tree_{commitSha}", now: 1);
        var identity = BaseIdentity(delta: null);
        var (baseId, _, _) = store.BeginPending(identity, repoId, commitId, runId, now: 1);
        store.MarkComplete(baseId, publishedAt: 1);
        return (baseId, runId);
    }
}
