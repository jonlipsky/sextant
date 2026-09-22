namespace Sextant.Indexer.Tests;

/// <summary>
/// Phase 10 — the authoritative, rename-aware Git working-tree discovery (<see cref="GitChangeProvider"/>).
/// These tests run the real git binary over a throwaway repo and assert the machine-readable
/// <c>status --porcelain=v2 -z</c> parse plus the working-tree-delta digest. They cover:
/// <list type="bullet">
///   <item>criterion 1 — a clean checkout yields an empty change set (→ empty overlay);</item>
///   <item>criterion 2 — modified/added/deleted/renamed/untracked/staged files map to the right kinds;</item>
///   <item>criterion 3 / issue #43 — the delta digest is deterministic for an identical working-tree
///   state (so a restart reconstructs the same overlay) and shifts when content changes.</item>
/// </list>
/// If git is unavailable the tests are inconclusive rather than failing (no git in the environment).
/// </summary>
[TestClass]
public class GitChangeProviderTests
{
    private static TestGitRepo CreateRepo(params (string path, string content)[] files)
    {
        var repo = TestGitRepo.TryCreate(files.ToDictionary(f => f.path, f => f.content));
        if (repo == null)
            Assert.Inconclusive("git is unavailable in this environment; skipping git-discovery test.");
        return repo!;
    }

    [TestMethod]
    public void CleanCheckout_YieldsEmptyChangeSet()
    {
        using var repo = CreateRepo(("src/A.cs", "class A {}"));

        var set = GitChangeProvider.TryGetChangeSet(repo.Root)!;

        Assert.IsNotNull(set, "a clean tree returns an (empty) change set, not null");
        Assert.IsTrue(set.IsClean, "a clean checkout is clean (criterion 1 → empty overlay)");
        Assert.AreEqual(0, set.Changes.Count);
        Assert.IsNull(set.ComputeDeltaDigest(), "a clean tree has no working-tree delta");
    }

    [TestMethod]
    public void StatusUnavailableFallback_HasDistinctIdentityFromCleanBase()
    {
        // Finding 2 / issue #43: when git status is UNAVAILABLE we cannot prove the tree is clean, so the
        // full-index fallback must NOT be published under the clean base commit's identity (delta = null)
        // — otherwise a genuinely dirty tree would collide with (and be re-selected as) the clean base via
        // BeginPending's identity-hash dedup. The stable sentinel delta keeps the fallback identity
        // DISTINCT from the clean base yet deterministic across repeated status-unavailable passes.
        static Sextant.Core.SnapshotIdentity Identity(string? delta) => new()
        {
            RepositoryRemoteUrl = "git@example.com:o/r.git",
            CommitSha = "commit_head",
            TreeSha = "tree_head",
            SchemaVersion = 1,
            AnalyzerVersion = "analyzer",
            ConfigHash = "cfg",
            ToolchainFingerprint = "tc",
            WorkingTreeDelta = delta
        };

        var cleanBase = Identity(null);
        var statusUnavailable = Identity(LocalOverlayReconciler.StatusUnavailableDeltaSentinel);

        Assert.AreNotEqual(cleanBase.Hash, statusUnavailable.Hash,
            "a status-unavailable fallback must never collide with the clean base commit's snapshot identity (#43)");
        Assert.AreEqual(statusUnavailable.Hash, Identity(LocalOverlayReconciler.StatusUnavailableDeltaSentinel).Hash,
            "the sentinel yields a deterministic identity so repeated status-unavailable passes re-select idempotently");
    }

    [TestMethod]
    public void ModifiedFile_IsClassifiedModified()
    {
        using var repo = CreateRepo(("src/A.cs", "class A {}"));
        repo.Write("src/A.cs", "class A { int X; }");

        var change = SingleChange(repo);
        Assert.AreEqual("src/A.cs", change.RepoRelativePath);
        Assert.AreEqual(WorkingTreeChangeKind.Modified, change.Kind);
    }

    [TestMethod]
    public void UntrackedFile_IsClassifiedUntracked()
    {
        using var repo = CreateRepo(("src/A.cs", "class A {}"));
        repo.Write("src/New.cs", "class New {}");

        var change = SingleChange(repo);
        Assert.AreEqual("src/New.cs", change.RepoRelativePath);
        Assert.AreEqual(WorkingTreeChangeKind.Untracked, change.Kind);
    }

    [TestMethod]
    public void StagedNewFile_IsClassifiedAdded()
    {
        using var repo = CreateRepo(("src/A.cs", "class A {}"));
        repo.Write("src/Added.cs", "class Added {}");
        repo.Run("add src/Added.cs");

        var change = SingleChange(repo);
        Assert.AreEqual("src/Added.cs", change.RepoRelativePath);
        Assert.AreEqual(WorkingTreeChangeKind.Added, change.Kind);
    }

    [TestMethod]
    public void DeletedFile_IsClassifiedDeleted()
    {
        using var repo = CreateRepo(("src/A.cs", "class A {}"), ("src/B.cs", "class B {}"));
        repo.Delete("src/B.cs");

        var change = SingleChange(repo);
        Assert.AreEqual("src/B.cs", change.RepoRelativePath);
        Assert.AreEqual(WorkingTreeChangeKind.Deleted, change.Kind);
    }

    [TestMethod]
    public void RenamedFile_IsClassifiedRenamed_WithOldPath()
    {
        using var repo = CreateRepo(("src/Old.cs", "class Widget { void M() {} }"));
        // Stage a rename so git's rename detection fires against the committed base.
        repo.Run("mv src/Old.cs src/Renamed.cs");
        repo.Run("add -A");

        var set = GitChangeProvider.TryGetChangeSet(repo.Root)!;
        var rename = set.Changes.SingleOrDefault(c => c.Kind == WorkingTreeChangeKind.Renamed);
        Assert.IsNotNull(rename, "a staged rename is reported as a rename (rename-aware discovery)");
        Assert.AreEqual("src/Renamed.cs", rename!.RepoRelativePath);
        Assert.AreEqual("src/Old.cs", rename.OldRepoRelativePath, "a rename carries its pre-rename path");

        // Both the source and destination absolute paths must be handed to the closure computation.
        var touched = set.TouchedAbsolutePaths();
        StringAssert.Contains(string.Join("|", touched), "Renamed.cs");
        StringAssert.Contains(string.Join("|", touched), "Old.cs");
    }

    [TestMethod]
    public void MixedState_ReportsEveryChange()
    {
        using var repo = CreateRepo(
            ("src/Mod.cs", "class Mod {}"),
            ("src/Del.cs", "class Del {}"));
        repo.Write("src/Mod.cs", "class Mod { int Y; }"); // modified
        repo.Delete("src/Del.cs");                          // deleted
        repo.Write("src/Untracked.cs", "class U {}");       // untracked

        var set = GitChangeProvider.TryGetChangeSet(repo.Root)!;
        Assert.AreEqual(3, set.Changes.Count, "modified + deleted + untracked are all discovered in one pass");
        CollectionAssert.AreEquivalent(
            new[] { WorkingTreeChangeKind.Modified, WorkingTreeChangeKind.Deleted, WorkingTreeChangeKind.Untracked },
            set.Changes.Select(c => c.Kind).ToList());
    }

    // ==== Criterion 3 / issue #43: delta-digest determinism ========================================

    [TestMethod]
    public void DeltaDigest_IsDeterministic_ForIdenticalWorkingTreeState()
    {
        using var repo = CreateRepo(("src/A.cs", "class A {}"));
        repo.Write("src/A.cs", "class A { int X; }");
        repo.Write("src/New.cs", "class New {}");

        var first = GitChangeProvider.TryGetChangeSet(repo.Root)!.ComputeDeltaDigest();
        var second = GitChangeProvider.TryGetChangeSet(repo.Root)!.ComputeDeltaDigest();

        Assert.IsNotNull(first, "a dirty tree has a delta digest");
        Assert.AreEqual(first, second,
            "an identical working-tree state recomputes the identical delta digest — a restart reconstructs the same overlay (criterion 3, #43)");
    }

    [TestMethod]
    public void DeltaDigest_Changes_WhenDirtyContentChanges()
    {
        using var repo = CreateRepo(("src/A.cs", "class A {}"));
        repo.Write("src/A.cs", "class A { int X; }");
        var before = GitChangeProvider.TryGetChangeSet(repo.Root)!.ComputeDeltaDigest();

        // A further edit to the same dirty file is a different working-tree state → a new overlay identity.
        repo.Write("src/A.cs", "class A { int X; int Y; }");
        var after = GitChangeProvider.TryGetChangeSet(repo.Root)!.ComputeDeltaDigest();

        Assert.AreNotEqual(before, after,
            "a change to the dirty content changes the delta digest (a new overlay generation, #43)");
    }

    private static WorkingTreeChange SingleChange(TestGitRepo repo)
    {
        var set = GitChangeProvider.TryGetChangeSet(repo.Root)!;
        Assert.AreEqual(1, set.Changes.Count, $"expected exactly one change, saw: {string.Join(", ", set.Changes.Select(c => $"{c.Kind}:{c.RepoRelativePath}"))}");
        return set.Changes[0];
    }
}
