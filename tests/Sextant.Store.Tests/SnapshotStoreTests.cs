using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 9 — immutable repository snapshots. These tests map 1:1 to the six acceptance criteria plus
/// the whole-generation reader-isolation guarantee deferred from Phase 3 and the branch-pointer
/// retention protection, exercising the real Phase-9 store machinery (<see cref="SnapshotStore"/>,
/// <see cref="SnapshotReadScope"/>, snapshot-aware <see cref="ProjectStore"/>, and
/// <see cref="BranchPointerProtection"/>) that the orchestrator drives during a full index.
/// </summary>
[TestClass]
public class SnapshotStoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_snapshots_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    // ==== Criterion 1: two commits of one project coexist without overwriting ====================

    [TestMethod]
    public void C1_TwoCommitsOfOneProject_Coexist_WithoutOverwrite()
    {
        var repoId = EnsureRepo("https://github.com/org/repo");
        var a = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var b = SeedCompleteSnapshot(repoId, "commit_bbbb", LogicalCanonical, Fqn, symbolKey: "S_B");

        Assert.AreNotEqual(a.snapshotId, b.snapshotId, "distinct commits produce distinct snapshots");
        Assert.AreNotEqual(a.projectId, b.projectId, "each snapshot gets its OWN project-version row (no overwrite)");

        // Both project-version rows and both snapshots' symbols coexist in the store.
        Assert.AreEqual(1, CountSymbols(a.projectId), "snapshot A's symbol survives");
        Assert.AreEqual(1, CountSymbols(b.projectId), "snapshot B's symbol survives (did not overwrite A)");

        // The same display FQN resolves within each snapshot's scope to exactly that snapshot's row.
        var inA = ScopedSymbols(a.snapshotId).ResolveByFqn(Fqn);
        var inB = ScopedSymbols(b.snapshotId).ResolveByFqn(Fqn);
        Assert.AreEqual(1, inA.Count);
        Assert.AreEqual(1, inB.Count);
        Assert.AreEqual(a.projectId, inA[0].ProjectId, "scope A resolves A's project-version");
        Assert.AreEqual(b.projectId, inB[0].ProjectId, "scope B resolves B's project-version");

        // Both share ONE commit-invariant logical identity (correlatable across commits).
        Assert.AreEqual(LogicalCanonical, LogicalCanonicalOf(a.projectId));
        Assert.AreEqual(LogicalCanonical, LogicalCanonicalOf(b.projectId));
    }

    // ==== Criterion 2: branch pointer advance/rollback leaves snapshots immutable ================

    [TestMethod]
    public void C2_BranchAdvanceAndRollback_LeavesSnapshotsByteIdentical()
    {
        var repoId = EnsureRepo("https://github.com/org/repo");
        var a = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var b = SeedCompleteSnapshot(repoId, "commit_bbbb", LogicalCanonical, Fqn, symbolKey: "S_B");
        var store = new SnapshotStore(_conn);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        store.SetBranchPointer(branchId, a.snapshotId, now: 2);

        var aBefore = SnapshotFingerprint(a.snapshotId, a.projectId);
        var bBefore = SnapshotFingerprint(b.snapshotId, b.projectId);
        Assert.AreEqual(a.snapshotId, store.GetSelectedSnapshotId(), "selected follows the pointer (A)");

        // Advance the mutable pointer A -> B. Only the branch row moves; no snapshot row is touched.
        store.SetBranchPointer(branchId, b.snapshotId, now: 3);
        Assert.AreEqual(b.snapshotId, store.GetSelectedSnapshotId(), "selected follows the pointer (B)");
        Assert.AreEqual(aBefore, SnapshotFingerprint(a.snapshotId, a.projectId), "advancing did not mutate snapshot A");
        Assert.AreEqual(bBefore, SnapshotFingerprint(b.snapshotId, b.projectId), "advancing did not mutate snapshot B");

        // Roll the pointer back B -> A. Still no snapshot mutation; a rollback is pointer-only.
        store.SetBranchPointer(branchId, a.snapshotId, now: 4);
        Assert.AreEqual(a.snapshotId, store.GetSelectedSnapshotId(), "rollback restores selection to A");
        Assert.AreEqual(aBefore, SnapshotFingerprint(a.snapshotId, a.projectId), "rollback did not mutate snapshot A");
        Assert.AreEqual(bBefore, SnapshotFingerprint(b.snapshotId, b.projectId), "rollback did not mutate snapshot B");
    }

    [TestMethod]
    public void C2_ReselectingSupersededSnapshot_RestoresHead_WithoutMutatingItsData()
    {
        // git checkout to an OLDER, already-indexed commit whose snapshot is Superseded. The publish path
        // must RE-SELECT that snapshot (restore complete + repoint the branch) instead of rebuilding and
        // republishing it: the guarded pending->complete publish rejects a superseded snapshot, which
        // previously threw mid-rebuild and corrupted the immutable snapshot. Proves the guard rejects AND
        // the reselect restores the head without touching the snapshot's immutable data (criteria 1 & 2).
        var repoId = EnsureRepo("https://github.com/org/repo");
        var a = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var b = SeedCompleteSnapshot(repoId, "commit_bbbb", LogicalCanonical, Fqn, symbolKey: "S_B");
        var aFingerprint = SnapshotFingerprint(a.snapshotId, a.projectId); // captured while A is complete
        var store = new SnapshotStore(_conn);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);

        // Publish A, then advance to B (superseding A) — the working head is now B.
        store.SetBranchPointer(branchId, a.snapshotId, now: 1);
        store.SetBranchPointer(branchId, b.snapshotId, now: 2);
        store.MarkStatus(a.snapshotId, SnapshotStatus.Superseded);

        // The guarded publish path would REJECT re-publishing A (it is not pending) — this 0-row result is
        // exactly why the orchestrator must take the reselect path instead of rebuild + MarkComplete.
        Assert.AreEqual(0, store.MarkComplete(a.snapshotId, publishedAt: 3),
            "a superseded snapshot is not pending, so the guarded publish rejects it");

        // Reselect A (checkout aaaa): restore complete + repoint the branch, superseding B. No rebuild.
        store.MarkStatus(a.snapshotId, SnapshotStatus.Complete);
        var previous = store.GetBranchSnapshotId(branchId);
        store.SetBranchPointer(branchId, a.snapshotId, now: 4);
        if (previous is long prev && prev != a.snapshotId) store.MarkStatus(prev, SnapshotStatus.Superseded);

        Assert.AreEqual(a.snapshotId, store.GetSelectedSnapshotId(), "A is the selected head again after reselect");
        Assert.AreEqual(SnapshotStatus.Superseded, store.GetById(b.snapshotId)!.Status, "B is now superseded");
        Assert.AreEqual(aFingerprint, SnapshotFingerprint(a.snapshotId, a.projectId),
            "A's snapshot data is byte-identical — the reselect never rebuilt it");
    }

    // ==== Criterion 3: atomic + idempotent publish ==============================================

    [TestMethod]
    public void C3_DuplicatePublish_AttachesToExistingWork_NeverCreatesSecondSnapshot()
    {
        var repoId = EnsureRepo("https://github.com/org/repo");
        var store = new SnapshotStore(_conn);
        var commitId = store.EnsureCommit(repoId, "commit_aaaa", "tree_aaaa", now: 1);
        var identity = Identity("https://github.com/org/repo", "commit_aaaa", "tree_aaaa");

        var first = store.BeginPending(identity, repoId, commitId, runId: null, now: 1);
        Assert.IsFalse(first.existed, "first BeginPending creates a fresh pending snapshot");

        // A duplicate publish request (identical identity) attaches to the existing work.
        var second = store.BeginPending(identity, repoId, commitId, runId: null, now: 2);
        Assert.IsTrue(second.existed, "a duplicate identity attaches to existing work");
        Assert.AreEqual(first.id, second.id, "no second snapshot id is minted");
        Assert.AreEqual(1, ScalarLong("SELECT COUNT(*) FROM snapshots;"), "exactly one snapshot row exists");

        // Publish is guarded on pending: it succeeds once, and a duplicate publish is a no-op.
        Assert.AreEqual(1, store.MarkComplete(first.id, publishedAt: 3), "first publish flips pending -> complete");
        Assert.AreEqual(0, store.MarkComplete(first.id, publishedAt: 4), "a second publish is a guarded no-op");

        // After completion a duplicate BeginPending still attaches (idempotent) and reports complete.
        var afterComplete = store.BeginPending(identity, repoId, commitId, runId: null, now: 5);
        Assert.IsTrue(afterComplete.existed);
        Assert.AreEqual(SnapshotStatus.Complete, afterComplete.status, "an already-published identity is reported complete");
        Assert.AreEqual(1, ScalarLong("SELECT COUNT(*) FROM snapshots;"), "still exactly one snapshot row");
    }

    [TestMethod]
    public void C3_PendingOrFailedSnapshot_IsNeverSelectedAsBranchHead()
    {
        var repoId = EnsureRepo("https://github.com/org/repo");
        var store = new SnapshotStore(_conn);
        var identity = Identity("https://github.com/org/repo", "commit_aaaa", "tree_aaaa");
        var (pendingId, _, _) = store.BeginPending(identity, repoId, commitId: null, runId: null, now: 1);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);

        // Even if a branch pointer references it, an unpublished (pending) snapshot is never SELECTED:
        // GetSelectedSnapshotId requires status = complete, so a half-built generation is never served.
        store.SetBranchPointer(branchId, pendingId, now: 2);
        Assert.IsNull(store.GetSelectedSnapshotId(), "a pending snapshot is never selected as head");

        // A failed snapshot is likewise diagnosable but never selected.
        store.MarkStatus(pendingId, SnapshotStatus.Failed);
        Assert.IsNull(store.GetSelectedSnapshotId(), "a failed snapshot is never selected as head");
        Assert.AreEqual(SnapshotStatus.Failed, store.GetById(pendingId)!.Status, "the failed snapshot stays diagnosable");
    }

    [TestMethod]
    public void C3_PartialSnapshot_IsNeverSelected_AndLeavesPreviousCompleteHeadCurrent()
    {
        // The completeness gate: a full index that hits a compilation failure marks its generation PARTIAL
        // and does NOT advance the branch pointer. A partial snapshot is diagnosable but must never be
        // selected, and the previously-complete head stays the current selection (criterion 3). Mirrors the
        // orchestrator's partial-publish path (compilationFailures > 0 -> MarkStatus(Partial), no advance).
        var repoId = EnsureRepo("https://github.com/org/repo");
        var good = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var store = new SnapshotStore(_conn);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        store.SetBranchPointer(branchId, good.snapshotId, now: 1);
        Assert.AreEqual(good.snapshotId, store.GetSelectedSnapshotId(), "the good generation is the selected head");

        // A later full index of commit bbbb fails to compile a project: it is staged then marked PARTIAL,
        // and the branch pointer is left where it was (still pointing at the previous complete generation).
        var commitId = store.EnsureCommit(repoId, "commit_bbbb", "tree_bbbb", now: 2);
        var identityB = Identity("https://github.com/org/repo", "commit_bbbb", "tree_bbbb");
        var (partialId, _, _) = store.BeginPending(identityB, repoId, commitId, runId: null, now: 2);
        store.MarkStatus(partialId, SnapshotStatus.Partial);

        Assert.AreEqual(SnapshotStatus.Partial, store.GetById(partialId)!.Status, "the partial snapshot stays diagnosable");
        Assert.AreEqual(good.snapshotId, store.GetSelectedSnapshotId(),
            "a partial snapshot is never selected; the previous complete generation stays current");

        // A re-index of the same commit attaches to the same partial snapshot (idempotent) so the
        // orchestrator can reset it to pending and retry, rather than minting a second snapshot.
        var retry = store.BeginPending(identityB, repoId, commitId, runId: null, now: 3);
        Assert.IsTrue(retry.existed, "a re-index attaches to the existing partial snapshot");
        Assert.AreEqual(partialId, retry.id, "no second snapshot is minted for the retry");
        Assert.AreEqual(SnapshotStatus.Partial, retry.status, "the retry observes the partial status before resetting it to pending");
    }

    // ==== Criterion 4: incompatibility prevents unsafe reuse ====================================

    [TestMethod]
    public void C4_IncompatibleFingerprint_IsNotReused_ForcesFreshSnapshot()
    {
        var repoId = EnsureRepo("https://github.com/org/repo");
        var store = new SnapshotStore(_conn);
        var commitId = store.EnsureCommit(repoId, "commit_aaaa", "tree_aaaa", now: 1);

        // Publish + select a snapshot built with one fingerprint tuple.
        var baseId = new SnapshotIdentity
        {
            RepositoryRemoteUrl = "https://github.com/org/repo",
            CommitSha = "commit_aaaa",
            TreeSha = "tree_aaaa",
            SchemaVersion = 13,
            AnalyzerVersion = "1",
            ConfigHash = "cfgA",
            ToolchainFingerprint = "tcA"
        };
        var (snap1, _, _) = store.BeginPending(baseId, repoId, commitId, runId: null, now: 1);
        store.MarkComplete(snap1, publishedAt: 2);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 2);
        store.SetBranchPointer(branchId, snap1, now: 2);

        // Each incompatible-fingerprint variant hashes differently, so BeginPending must NOT attach to
        // snap1 — it mints a fresh snapshot instead of reusing an incompatible one (criterion 4).
        foreach (var incompatible in new[]
        {
            baseId with { SchemaVersion = 14 },
            baseId with { AnalyzerVersion = "2" },
            baseId with { ConfigHash = "cfgB" },
            baseId with { ToolchainFingerprint = "tcB" }
        })
        {
            Assert.AreNotEqual(baseId.Hash, incompatible.Hash, "an incompatible tuple yields a different identity hash");
            var (candidate, existed, _) = store.BeginPending(incompatible, repoId, commitId, runId: null, now: 3);
            Assert.IsFalse(existed, "an incompatible identity never attaches to the existing snapshot");
            Assert.AreNotEqual(snap1, candidate, "a fresh snapshot is built for the incompatible fingerprint");

            // The incompatible (pending) snapshot is never selected — the compatible published one holds.
            Assert.AreEqual(snap1, store.GetSelectedSnapshotId(), "selection never picks the incompatible snapshot");
        }

        // The original compatible identity still resolves to snap1 (reuse only on an exact tuple match).
        Assert.AreEqual(snap1, store.GetByIdentityHash(baseId.Hash)!.Id);
    }

    // ==== Criterion 6: backward-compatible scope-less default ===================================

    [TestMethod]
    public void C6_SingleRepo_SelectedSnapshot_IsTheScopelessDefault()
    {
        var repoId = EnsureRepo("https://github.com/org/repo");
        var a = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var b = SeedCompleteSnapshot(repoId, "commit_bbbb", LogicalCanonical, Fqn, symbolKey: "S_B");
        var store = new SnapshotStore(_conn);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        store.SetBranchPointer(branchId, b.snapshotId, now: 2);

        // A scope-less resolver defaults to the selected snapshot (B), never the superseded one (A).
        Assert.AreEqual(b.snapshotId, store.GetSelectedSnapshotId());
        var scoped = SnapshotReadScope.ForSelected(_conn);
        Assert.IsTrue(scoped.IsScoped);
        Assert.AreEqual(b.snapshotId, scoped.SnapshotId);

        var symbols = new SymbolStore(_conn) { Scope = scoped }.ResolveByFqn(Fqn);
        Assert.AreEqual(1, symbols.Count, "the scope-less default returns exactly the selected snapshot's rows");
        Assert.AreEqual(b.projectId, symbols[0].ProjectId);
    }

    [TestMethod]
    public void C6_LegacyDatabase_NoSnapshots_BehavesExactlyAsBeforePhase9()
    {
        // A pre-Phase-9 (direct-seed) database has no repositories/snapshots: the selected id is null,
        // the read scope is a no-op, and a bare store returns the legacy (snapshot_id IS NULL) rows.
        var now = 1L;
        var projectId = new ProjectStore(_conn).Insert(new ProjectIdentity
        {
            CanonicalId = LogicalCanonical,
            GitRemoteUrl = "https://github.com/org/repo",
            RepoRelativePath = "src/App/App.csproj"
        }, now);
        InsertSymbol(projectId, "S_legacy", Fqn);

        Assert.IsNull(new SnapshotStore(_conn).GetSelectedSnapshotId(), "no snapshot is selected for a legacy DB");
        var scope = SnapshotReadScope.ForSelected(_conn);
        Assert.IsFalse(scope.IsScoped, "the resolved scope is a no-op for a legacy DB");
        Assert.AreEqual(1, new SymbolStore(_conn) { Scope = scope }.ResolveByFqn(Fqn).Count,
            "a scope-less query returns the legacy rows exactly as before Phase 9");
    }

    [TestMethod]
    public void C6_MultiRepoDatabase_HasNoScopelessSelection()
    {
        // With more than one repository, there is no single local default (Phase 11 supplies explicit
        // scope). GetSelectedSnapshotId returns null even though a default branch points at a complete
        // snapshot, so a scope-less read applies no filter rather than guessing a repository.
        var repoId = EnsureRepo("https://github.com/org/repo");
        EnsureRepo("https://github.com/org/other");
        var a = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var store = new SnapshotStore(_conn);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        store.SetBranchPointer(branchId, a.snapshotId, now: 2);

        Assert.IsNull(store.GetSelectedSnapshotId(), "a multi-repo DB has no scope-less selected snapshot");
        Assert.IsFalse(SnapshotReadScope.ForSelected(_conn).IsScoped);
    }

    [TestMethod]
    public void C6_TwoBranchesOfOneRepo_ScopelessDefault_FollowsMostRecentlyIndexedBranch()
    {
        // Reproduces the multi-branch publish scenario: a full index marks the branch it just published
        // as default. When a SECOND branch of the same repo is indexed, the publish path demotes the
        // first (PromoteSoleDefaultBranch), so is_default stays single-valued and the scope-less default
        // follows the working head just indexed — never the older branch by id tiebreak (criterion 6).
        var repoId = EnsureRepo("https://github.com/org/repo");
        var onMain = SeedCompleteSnapshot(repoId, "commit_main", LogicalCanonical, Fqn, symbolKey: "S_main");
        var onDev = SeedCompleteSnapshot(repoId, "commit_dev", LogicalCanonical, Fqn, symbolKey: "S_dev");
        var store = new SnapshotStore(_conn);

        // Index main first (lower branch id), then develop — mirroring the orchestrator publish sequence.
        var mainBranch = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        store.PromoteSoleDefaultBranch(repoId, mainBranch);
        store.SetBranchPointer(mainBranch, onMain.snapshotId, now: 1);
        Assert.AreEqual(onMain.snapshotId, store.GetSelectedSnapshotId(), "main is selected while it is the only head");

        var devBranch = store.EnsureBranch(repoId, "develop", isDefault: true, now: 2);
        store.PromoteSoleDefaultBranch(repoId, devBranch);
        store.SetBranchPointer(devBranch, onDev.snapshotId, now: 2);

        // Exactly one default remains, and it is the most-recently-indexed branch (develop), so a
        // scope-less query returns develop's generation rather than silently serving main's.
        Assert.AreEqual(1, CountDefaultBranches(repoId), "is_default stays single-valued after the second index");
        Assert.AreEqual(onDev.snapshotId, store.GetSelectedSnapshotId(), "scope-less default follows the latest indexed branch");
        var symbols = new SymbolStore(_conn) { Scope = SnapshotReadScope.ForSelected(_conn) }.ResolveByFqn(Fqn);
        Assert.AreEqual(1, symbols.Count);
        Assert.AreEqual(onDev.projectId, symbols[0].ProjectId, "resolves develop's project-version, not main's");
    }

    // ==== Criterion 7: whole-generation reader isolation across a concurrent rebuild ============

    [TestMethod]
    public void C7_ConcurrentReader_AlwaysSeesExactlyOneCompleteGeneration()
    {
        var repoId = EnsureRepo("https://github.com/org/repo");
        var a = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var writer = new SnapshotStore(_conn);
        var branchId = writer.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        writer.SetBranchPointer(branchId, a.snapshotId, now: 2);

        // A concurrent reader on its OWN connection: it must resolve the selected generation and query
        // its rows without ever observing an uncommitted/torn generation. A private-cache WAL connection
        // gets true snapshot isolation (it never blocks on, nor observes, the writer's open transaction).
        using var readerConn = OpenIndependentReader();
        List<SymbolInfo> ReaderSymbols()
        {
            var scope = SnapshotReadScope.ForSelected(readerConn);
            return new SymbolStore(readerConn) { Scope = scope }.ResolveByFqn(Fqn);
        }

        var before = ReaderSymbols();
        Assert.AreEqual(1, before.Count, "reader sees the old selected generation (A)");
        Assert.AreEqual(a.projectId, before[0].ProjectId);

        // Stage a full rebuild: build generation B's rows inside ONE open write transaction and DO NOT
        // flip the branch pointer yet (this is the half-built pending generation).
        Exec("BEGIN IMMEDIATE;");
        var commitId = writer.EnsureCommit(repoId, "commit_bbbb", "tree_bbbb", now: 3);
        var identityB = Identity("https://github.com/org/repo", "commit_bbbb", "tree_bbbb");
        var (snapB, _, _) = writer.BeginPending(identityB, repoId, commitId, runId: null, now: 3);
        var logicalId = writer.EnsureLogicalProject(repoId, LogicalCanonical, "src/App/App.csproj", "net10.0", now: 3);
        var projectB = new ProjectStore(_conn).UpsertSnapshotProject(
            Identity3("https://github.com/org/repo"), snapB, logicalId, lastIndexedAt: 3);
        writer.MapProject(snapB, projectB);
        InsertSymbol(projectB, "S_B", Fqn);

        // WHILE the rebuild transaction is open, the reader still sees ONLY the old complete generation
        // — never the pending B rows, never a torn mix (the Phase-3 isolation guarantee).
        var during = ReaderSymbols();
        Assert.AreEqual(1, during.Count, "reader never observes the half-built pending generation");
        Assert.AreEqual(a.projectId, during[0].ProjectId, "reader still resolves the previous complete generation");

        // Publish + select B atomically in the same transaction, then commit.
        writer.MarkComplete(snapB, publishedAt: 4);
        writer.SetBranchPointer(branchId, snapB, now: 4);
        writer.MarkStatus(a.snapshotId, SnapshotStatus.Superseded);
        Exec("COMMIT;");

        // After the atomic commit the reader flips wholesale to the new generation B — again exactly one.
        var after = ReaderSymbols();
        Assert.AreEqual(1, after.Count, "reader now sees exactly the new selected generation (B)");
        Assert.AreEqual(projectB, after[0].ProjectId, "the selection flipped atomically to B");
    }

    [TestMethod]
    public void C7_FirstUpgradeIndex_LegacyPin_HidesPendingRowsFromScopelessReader()
    {
        // Upgrade scenario: a Phase-8 database already holds legacy rows (snapshot_id IS NULL). The FIRST
        // Phase-9 full index registers the repository and stages a PENDING snapshot's rows, but has not yet
        // selected a complete snapshot. A scope-less reader must stay pinned to the legacy generation and
        // never observe the half-built pending rows (criterion 7, across the first upgrade rebuild).
        var repoId = EnsureRepo("https://github.com/org/repo");

        // Legacy generation: a pre-Phase-9 mutable project row + symbol (snapshot_id IS NULL).
        var legacyProjectId = new ProjectStore(_conn).Insert(new ProjectIdentity
        {
            CanonicalId = LogicalCanonical,
            GitRemoteUrl = "https://github.com/org/repo",
            RepoRelativePath = "src/App/App.csproj",
            TargetFramework = "net10.0"
        }, 1L);
        InsertSymbol(legacyProjectId, "S_legacy", Fqn);

        // Half-built pending snapshot: its own project-version + a symbol for the SAME FQN, NOT completed.
        var store = new SnapshotStore(_conn);
        var commitId = store.EnsureCommit(repoId, "commit_bbbb", "tree_bbbb", now: 2);
        var identityB = Identity("https://github.com/org/repo", "commit_bbbb", "tree_bbbb");
        var (pendingSnap, _, _) = store.BeginPending(identityB, repoId, commitId, runId: null, now: 2);
        var logicalId = store.EnsureLogicalProject(repoId, LogicalCanonical, "src/App/App.csproj", "net10.0", now: 2);
        var pendingProjectId = new ProjectStore(_conn).UpsertSnapshotProject(
            Identity3("https://github.com/org/repo"), pendingSnap, logicalId, lastIndexedAt: 2);
        store.MapProject(pendingSnap, pendingProjectId);
        InsertSymbol(pendingProjectId, "S_pending", Fqn);

        // No complete snapshot is selected, but snapshot-tagged rows exist → ForSelected pins to legacy.
        Assert.IsNull(store.GetSelectedSnapshotId(), "no complete snapshot is selected during the first index");
        Assert.IsTrue(store.HasUnselectedSnapshotProjectRows(), "snapshot-tagged rows exist without a selection");
        var scope = SnapshotReadScope.ForSelected(_conn);
        Assert.IsTrue(scope.IsScoped, "the scope is legacy-pinned during the first upgrade index");
        Assert.IsNull(scope.SnapshotId, "a legacy pin carries no snapshot id");

        var symbols = new SymbolStore(_conn) { Scope = scope }.ResolveByFqn(Fqn);
        Assert.AreEqual(1, symbols.Count, "the reader sees ONLY the legacy generation, never the pending rows");
        Assert.AreEqual(legacyProjectId, symbols[0].ProjectId, "resolves the legacy project-version, not the pending one");
    }

    // ==== Branch-pointer retention protection ===================================================

    [TestMethod]
    public void BranchPointerProtection_ShieldsBranchHeadGeneration_FromGc()
    {
        // Three complete generations; the newest (G3) is servable. A branch points at the OLDEST (G1)
        // snapshot, which is neither servable nor within a keep=0 window, so only the branch-pointer
        // provider can save it. G2 (unreferenced) must be collected; G1 (branch-pointed) must survive.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var runStore = new IndexRunStore(_conn);
        var g1 = CompleteRun(runStore, now);
        var g2 = CompleteRun(runStore, now + 1);
        var g3 = CompleteRun(runStore, now + 2);

        var repoId = EnsureRepo("https://github.com/org/repo");
        var store = new SnapshotStore(_conn);
        var identity = Identity("https://github.com/org/repo", "commit_g1", "tree_g1");
        var (snapId, _, _) = store.BeginPending(identity, repoId, commitId: null, runId: g1, now: now);
        store.MarkComplete(snapId, now);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: now);
        store.SetBranchPointer(branchId, snapId, now);

        var policy = new RetentionPolicy { KeepCompleteGenerations = 0, ApiSnapshotKeepCommits = 10 };
        new RetentionService(_conn, policy).Execute();

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM index_runs WHERE id = {g1};"),
            "the branch-pointed generation survives even at keep=0");
        Assert.AreEqual(0, ScalarLong($"SELECT COUNT(*) FROM index_runs WHERE id = {g2};"),
            "an unreferenced non-servable generation is still collected");
        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM index_runs WHERE id = {g3};"),
            "the servable generation is always retained");
    }

    // ==== Criterion 5: API/semantic history survives reindexing ================================

    [TestMethod]
    public void C5_ApiHistory_SurvivesWorkingRowRebuild_IndependentOfLiveSymbols()
    {
        var repoId = EnsureRepo("https://github.com/org/repo");
        var s = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var liveSymbolId = ScalarLong($"SELECT id FROM symbols WHERE project_id = {s.projectId} LIMIT 1;");

        // Capture a historical API-surface snapshot for this commit, referencing the live symbol but
        // carrying its OWN self-contained identity (Phase-4 migration 009: symbol_key/fqn/accessibility).
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO api_surface_snapshots
                    (project_id, symbol_id, symbol_key, fully_qualified_name, accessibility, signature_hash, captured_at, git_commit)
                VALUES (@p, @sym, 'KEY_A', @fqn, 'public', 'sighash', 1, 'commit_aaaa');
                """;
            cmd.Parameters.AddWithValue("@p", s.projectId);
            cmd.Parameters.AddWithValue("@sym", liveSymbolId);
            cmd.Parameters.AddWithValue("@fqn", Fqn);
            cmd.ExecuteNonQuery();
        }

        // A working-index rebuild deletes and replaces the live symbol rows (the Phase-4 dual cascade).
        new SymbolStore(_conn).DeleteByProject(s.projectId);

        // The historical API snapshot SURVIVES the rebuild: its FK to the live symbol is reset to NULL
        // (ON DELETE SET NULL) rather than cascade-deleting the history, and its self-contained identity
        // fields are intact — so a comparison still resolves independently of any current mutable row.
        using var read = _conn.CreateCommand();
        read.CommandText = """
            SELECT symbol_id, symbol_key, fully_qualified_name, accessibility, git_commit
            FROM api_surface_snapshots WHERE git_commit = 'commit_aaaa';
            """;
        using var reader = read.ExecuteReader();
        Assert.IsTrue(reader.Read(), "the historical API snapshot survived the working-row rebuild");
        Assert.IsTrue(reader.IsDBNull(0), "the live-symbol FK was reset to NULL, not cascade-deleted");
        Assert.AreEqual("KEY_A", reader.GetString(1), "self-contained symbol_key survives");
        Assert.AreEqual(Fqn, reader.GetString(2), "self-contained FQN survives");
        Assert.AreEqual("public", reader.GetString(3), "self-contained accessibility survives");
        Assert.AreEqual("commit_aaaa", reader.GetString(4), "the commit identity survives");
        Assert.AreEqual(0, CountSymbols(s.projectId), "the live symbols were indeed rebuilt away");
    }

    [TestMethod]
    public void C5_ApiHistory_CrossSnapshot_ResolvesFromCurrentSnapshotProject()
    {
        // Each immutable snapshot of a project gets a NEW physical project_id, but api-surface comparisons
        // must still reach an OLDER commit's rows from the CURRENT snapshot's project row (criterion 5):
        // the api-history lookups resolve across the whole logical-project group, not one physical id.
        var repoId = EnsureRepo("https://github.com/org/repo");
        var a = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var b = SeedCompleteSnapshot(repoId, "commit_bbbb", LogicalCanonical, Fqn, symbolKey: "S_B");
        Assert.AreNotEqual(a.projectId, b.projectId, "distinct snapshots get distinct physical project rows");

        var api = new ApiSurfaceStore(_conn);
        api.Insert(new ApiSurfaceSnapshot
        {
            ProjectId = a.projectId, SymbolKey = "KEY", FullyQualifiedName = Fqn,
            Accessibility = "public", SignatureHash = "h_a", CapturedAt = 10, GitCommit = "commit_aaaa"
        });
        api.Insert(new ApiSurfaceSnapshot
        {
            ProjectId = b.projectId, SymbolKey = "KEY", FullyQualifiedName = Fqn,
            Accessibility = "public", SignatureHash = "h_b", CapturedAt = 20, GitCommit = "commit_bbbb"
        });

        // From the CURRENT snapshot's project (pB) the older commit's api rows remain reachable...
        var old = api.GetByProjectAndCommit(b.projectId, "commit_aaaa");
        Assert.AreEqual(1, old.Count, "an older commit's api surface resolves across the logical-project group");
        Assert.AreEqual("h_a", old[0].SignatureHash, "it is commit aaaa's captured surface");

        // ...the previous commit is discoverable across snapshots...
        Assert.AreEqual("commit_aaaa", api.GetPreviousCommit(b.projectId, "commit_bbbb"),
            "the previous commit is found across snapshots, not just within one physical project");

        // ...and the latest surface resolves to the most recently captured one (commit bbbb).
        var latest = api.GetLatestByProject(b.projectId);
        Assert.AreEqual(1, latest.Count);
        Assert.AreEqual("h_b", latest[0].SignatureHash, "latest resolves to the most recently captured api surface");
    }

    [TestMethod]
    public void ApiSurface_MultipleProjectVersionsSameCommit_ResolveInDeterministicOrder()
    {
        // Rubber-duck follow-up to issue #29: when two physical project-versions of ONE logical project
        // carry api-surface rows stamped with the SAME git_commit (a base snapshot plus a re-index),
        // GetByProjectAndCommit returns BOTH via the logical-project group. BreakingChangeDetector folds
        // them into a symbol_key-keyed dictionary last-writer-wins, so the query MUST impose a stable
        // order or the resulting breaking-change classification is nondeterministic. This asserts the
        // ORDER BY (captured_at, id) so the newest capture of a symbol_key is deterministically last.
        var repoId = EnsureRepo("https://github.com/org/repo");
        var a = SeedCompleteSnapshot(repoId, "commit_aaaa", LogicalCanonical, Fqn, symbolKey: "S_A");
        var b = SeedCompleteSnapshot(repoId, "commit_bbbb", LogicalCanonical, Fqn, symbolKey: "S_B");
        Assert.AreNotEqual(a.projectId, b.projectId, "two distinct physical project-versions of one logical project");

        var api = new ApiSurfaceStore(_conn);
        // Both rows share one symbol_key AND one git_commit but live under different project-versions,
        // with the OLDER capture inserted last so insertion order alone would NOT yield ascending order.
        api.Insert(new ApiSurfaceSnapshot
        {
            ProjectId = b.projectId, SymbolKey = "KEY", FullyQualifiedName = Fqn,
            Accessibility = "public", SignatureHash = "h_new", CapturedAt = 20, GitCommit = "commit_shared"
        });
        api.Insert(new ApiSurfaceSnapshot
        {
            ProjectId = a.projectId, SymbolKey = "KEY", FullyQualifiedName = Fqn,
            Accessibility = "public", SignatureHash = "h_old", CapturedAt = 10, GitCommit = "commit_shared"
        });

        var rows = api.GetByProjectAndCommit(b.projectId, "commit_shared");
        Assert.AreEqual(2, rows.Count, "both project-versions' rows for the shared commit resolve");
        CollectionAssert.AreEqual(
            new[] { "h_old", "h_new" }, rows.Select(r => r.SignatureHash).ToList(),
            "rows come back in ascending (captured_at, id) order regardless of insertion order");
        Assert.AreEqual("h_new", rows[^1].SignatureHash,
            "the newest capture is deterministically last, so last-writer-wins picks it every run");
    }

    // ==== helpers ================================================================================

    private const string LogicalCanonical = "logical_app_0123456789";
    private const string Fqn = "global::App.Widget";

    private long EnsureRepo(string url) => new SnapshotStore(_conn).EnsureRepository(url, now: 1);

    /// <summary>Opens a second, independent WAL connection to the same database. Private cache (the
    /// default) gives it MVCC snapshot reads, so a SELECT taken while the writer holds an open
    /// transaction sees the last committed state and never the uncommitted rows — the exact concurrent
    /// reader the whole-generation isolation guarantee must satisfy.</summary>
    private SqliteConnection OpenIndependentReader()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static SnapshotIdentity Identity(string repo, string commit, string tree) => new()
    {
        RepositoryRemoteUrl = repo,
        CommitSha = commit,
        TreeSha = tree,
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc"
    };

    private static ProjectIdentity Identity3(string repo) => new()
    {
        CanonicalId = LogicalCanonical,
        GitRemoteUrl = repo,
        RepoRelativePath = "src/App/App.csproj",
        TargetFramework = "net10.0"
    };

    /// <summary>Seeds a published, self-contained snapshot: commit + logical project + project-version
    /// row + one symbol + snapshot_projects mapping, flipped to complete.</summary>
    private (long snapshotId, long projectId) SeedCompleteSnapshot(
        long repoId, string commitSha, string logicalCanonical, string fqn, string symbolKey)
    {
        var store = new SnapshotStore(_conn);
        var now = 1L;
        var commitId = store.EnsureCommit(repoId, commitSha, $"tree_{commitSha}", now);
        var identity = Identity(RepoUrlOf(repoId), commitSha, $"tree_{commitSha}");
        var (snapId, _, _) = store.BeginPending(identity, repoId, commitId, runId: null, now: now);
        var logicalId = store.EnsureLogicalProject(repoId, logicalCanonical, "src/App/App.csproj", "net10.0", now);
        var projectId = new ProjectStore(_conn).UpsertSnapshotProject(Identity3(RepoUrlOf(repoId)), snapId, logicalId, now);
        store.MapProject(snapId, projectId);
        InsertSymbol(projectId, symbolKey, fqn);
        store.MarkComplete(snapId, publishedAt: now);
        return (snapId, projectId);
    }

    private string RepoUrlOf(long repoId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT remote_url FROM repositories WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", repoId);
        return (string)cmd.ExecuteScalar()!;
    }

    private SymbolStore ScopedSymbols(long snapshotId) =>
        new(_conn) { Scope = new SnapshotReadScope(snapshotId) };

    private static long CompleteRun(IndexRunStore runStore, long now)
    {
        var id = runStore.BeginRun("full", now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(id, now, 1);
        return id;
    }

    private void InsertSymbol(long projectId, string symbolKey, string fqn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 line_start, line_end, last_indexed_at)
            VALUES (@p, @k, @fqn, @dn, 0, 0, 1, 1, 1);
            """;
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@k", symbolKey);
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@dn", fqn.Split('.').Last());
        cmd.ExecuteNonQuery();
    }

    private int CountSymbols(long projectId) =>
        (int)ScalarLong($"SELECT COUNT(*) FROM symbols WHERE project_id = {projectId};");

    private int CountDefaultBranches(long repositoryId) =>
        (int)ScalarLong($"SELECT COUNT(*) FROM branches WHERE repository_id = {repositoryId} AND is_default = 1;");

    private string LogicalCanonicalOf(long projectId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT lp.canonical_id FROM projects p
            JOIN logical_projects lp ON lp.id = p.logical_project_id WHERE p.id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", projectId);
        return (string)cmd.ExecuteScalar()!;
    }

    /// <summary>A stable content fingerprint of a snapshot: its row status + its project-version's
    /// ordered symbols + its snapshot_projects mapping. Used to prove immutability across pointer moves.</summary>
    private string SnapshotFingerprint(long snapshotId, long projectId)
    {
        var parts = new List<string>();
        using (var snap = _conn.CreateCommand())
        {
            snap.CommandText = "SELECT status, identity_hash, commit_id FROM snapshots WHERE id = @id;";
            snap.Parameters.AddWithValue("@id", snapshotId);
            using var r = snap.ExecuteReader();
            if (r.Read()) parts.Add($"snap[{r.GetString(0)}|{r.GetString(1)}|{(r.IsDBNull(2) ? "-" : r.GetInt64(2))}]");
        }
        using (var syms = _conn.CreateCommand())
        {
            syms.CommandText =
                "SELECT symbol_key, fully_qualified_name FROM symbols WHERE project_id = @p ORDER BY symbol_key;";
            syms.Parameters.AddWithValue("@p", projectId);
            using var r = syms.ExecuteReader();
            while (r.Read()) parts.Add($"sym[{r.GetString(0)}|{r.GetString(1)}]");
        }
        using (var map = _conn.CreateCommand())
        {
            map.CommandText = "SELECT project_id FROM snapshot_projects WHERE snapshot_id = @s ORDER BY project_id;";
            map.Parameters.AddWithValue("@s", snapshotId);
            using var r = map.ExecuteReader();
            while (r.Read()) parts.Add($"map[{r.GetInt64(0)}]");
        }
        return string.Join(";", parts);
    }

    private long ScalarLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
