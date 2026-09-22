using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 12 — deduplicate submodules and enable cross-repository usages. These tests exercise the real
/// <see cref="SnapshotDependencyStore"/> reverse-dependency catalog and the provider-snapshot machinery
/// on <see cref="SnapshotStore"/>, mapping the six acceptance criteria plus the folded-in follow-ups
/// (#32 exact stable-identity resolution, #48 dirty submodule state) to concrete assertions. Everything
/// is direct-seeded (no git, no Roslyn) so the criteria are pinned deterministically; the orchestrator
/// end-to-end dedup lives in the integration suite.
/// </summary>
[TestClass]
public class SnapshotDependencyStoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_snapdeps_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    private const string ProviderUrl = "https://github.com/org/mixandmatch";
    private const string ProviderCanonical = "logical_mix_0123456789";
    private const string ProviderFqn = "global::Mix.Combiner";
    private const string ProviderKey = "M:Mix.Combiner.Combine";
    private const string ProviderCommit = "prov_commit_aaaa";

    // ==== Criterion 1 (dedup, store layer): ONE provider project version for a pin shared by parents ==

    [TestMethod]
    public void C1_SamePinBeginPending_Deduplicates_ToOneProviderSnapshot()
    {
        // The provider-snapshot identity is (submodule remote, pinned commit). Two parents that pin the
        // SAME submodule commit resolve — via the identity hash — to ONE provider snapshot, so the shared
        // code is never staged twice (the orchestrator then skips re-extraction). A second BeginPending
        // with the identical identity returns existed=true and the same id.
        var providerRepo = new SnapshotStore(_conn).EnsureProviderRepository(ProviderUrl, now: 1);
        var commitId = new SnapshotStore(_conn).EnsureCommit(providerRepo, ProviderCommit, null, now: 1);
        var identity = ProviderIdentity(ProviderCommit, dirty: false);

        var (first, existed1, _) = new SnapshotStore(_conn).BeginPending(identity, providerRepo, commitId, null, now: 1, isProvider: true);
        Assert.IsFalse(existed1, "the first parent stages a fresh provider snapshot");

        var (second, existed2, _) = new SnapshotStore(_conn).BeginPending(identity, providerRepo, commitId, null, now: 2, isProvider: true);
        Assert.IsTrue(existed2, "the second parent attaches to the existing provider snapshot (dedup)");
        Assert.AreEqual(first, second, "the same pin resolves to exactly one provider snapshot");
        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM snapshots WHERE is_provider = 1;"),
            "only one provider snapshot exists for the shared pin");
    }

    // ==== Criterion 2: each parent retains its OWN dependency edge and pin =========================

    [TestMethod]
    public void C2_TwoParents_SamePin_EachRetainsOwnEdge()
    {
        var provider = SeedProvider(ProviderCommit, dirty: false);

        var parentA = SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            provider, providerCommit: ProviderCommit, line: 10);
        var parentB = SeedConsumerWithUsage("https://github.com/org/appB", "appB_head", "logical_appB",
            provider, providerCommit: ProviderCommit, line: 20);

        var deps = new SnapshotDependencyStore(_conn);
        var edgesA = deps.GetByConsumerSnapshot(parentA.snapshotId);
        var edgesB = deps.GetByConsumerSnapshot(parentB.snapshotId);
        Assert.AreEqual(1, edgesA.Count, "parent A has its own edge");
        Assert.AreEqual(1, edgesB.Count, "parent B has its own edge");

        // Both edges pin the SAME deduplicated provider project version, yet are distinct per-parent rows.
        Assert.AreEqual(provider.projectId, edgesA[0].ProviderProjectId);
        Assert.AreEqual(provider.projectId, edgesB[0].ProviderProjectId);
        Assert.AreNotEqual(edgesA[0].Id, edgesB[0].Id, "each parent keeps a distinct edge row");
        Assert.AreNotEqual(edgesA[0].ConsumerProjectId, edgesB[0].ConsumerProjectId);
        Assert.AreEqual(ProviderCommit, edgesA[0].ProviderCommitSha, "parent A records its own pin");
        Assert.AreEqual(ProviderCommit, edgesB[0].ProviderCommitSha, "parent B records its own pin");

        // The reverse index sees both parents pinning the one provider project version.
        var reverse = deps.GetByProviderProject(provider.projectId);
        Assert.AreEqual(2, reverse.Count, "the shared provider project version has two consumer edges");
    }

    // ==== Criterion 3: a producer-symbol usage query returns authorized consumers from default heads ==

    [TestMethod]
    public void C3_ProducerSymbolUsage_ReturnsConsumersFromDefaultHeads()
    {
        var provider = SeedProvider(ProviderCommit, dirty: false);
        SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            provider, providerCommit: ProviderCommit, line: 10, usageFile: "src/A/UseA.cs");
        SeedConsumerWithUsage("https://github.com/org/appB", "appB_head", "logical_appB",
            provider, providerCommit: ProviderCommit, line: 20, usageFile: "src/B/UseB.cs");

        var usages = new SnapshotDependencyStore(_conn)
            .FindCrossRepositoryUsages(ProviderUrl, ProviderKey, CrossRepoUsageScope.DefaultHeads, authorizedConsumerRepositoryIds: null);

        Assert.AreEqual(2, usages.Count, "both consumers' default-branch heads report a usage");
        CollectionAssert.AreEquivalent(
            new[] { "https://github.com/org/appA", "https://github.com/org/appB" },
            usages.Select(u => u.ConsumerRepositoryUrl).ToList(),
            "results carry each consumer repository");
        var a = usages.Single(u => u.ConsumerRepositoryUrl.EndsWith("appA"));
        Assert.AreEqual("main", a.ConsumerBranch, "the consumer branch is reported");
        Assert.AreEqual("appA_head", a.ConsumerCommitSha, "the consumer commit is reported");
        Assert.AreEqual("src/A/UseA.cs", a.FilePath, "the consumer file location is reported");
        Assert.AreEqual(10, a.Line);
        Assert.AreEqual(ProviderCommit, a.ProviderCommitSha, "the pinned producer version is reported");
    }

    // ==== Cross-repo usage dedup: a call site emits BOTH a pure-reference and a call-graph occurrence
    //      at the same location, so the usage query must count it ONCE (not double). Regression for the
    //      real-git integration finding that two rows were returned for a single consumer call site. ==

    [TestMethod]
    public void CrossRepoUsage_CallSite_WithRefAndCallOccurrence_IsCountedOnce()
    {
        var provider = SeedProvider(ProviderCommit, dirty: false);
        var consumer = SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            provider, providerCommit: ProviderCommit, line: 10, usageFile: "src/A/UseA.cs");

        // The document extractor emits a SECOND occurrence for the same call site: the call-graph edge
        // (source_symbol_id = the enclosing caller) at the identical (file, line). Before the pure-ref
        // filter this inflated the usage count to 2 for one call site.
        var caller = InsertSymbol(consumer.projectId, "M:AppA.Program.Run", "global::AppA.Program.Run");
        AddCallOccurrence(consumer.projectId, provider.symbolId, caller, consumer.fileVersionId, line: 10);

        var usages = new SnapshotDependencyStore(_conn)
            .FindCrossRepositoryUsages(ProviderUrl, ProviderKey, CrossRepoUsageScope.DefaultHeads, authorizedConsumerRepositoryIds: null);

        Assert.AreEqual(1, usages.Count, "a single call site is one usage, even though it emits a ref AND a call occurrence");
        Assert.AreEqual("src/A/UseA.cs", usages[0].FilePath);
        Assert.AreEqual(10, usages[0].Line);
    }

    // ==== Criterion 4 (store contract): the authorized-id filter is fail-closed =====================

    [TestMethod]
    public void C4_AuthorizationFilter_IsFailClosed()
    {
        var provider = SeedProvider(ProviderCommit, dirty: false);
        var a = SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            provider, providerCommit: ProviderCommit, line: 10);
        SeedConsumerWithUsage("https://github.com/org/appB", "appB_head", "logical_appB",
            provider, providerCommit: ProviderCommit, line: 20);

        var deps = new SnapshotDependencyStore(_conn);

        // Empty authorized set = deny all: NO results and NO existence/count metadata leaks.
        var denied = deps.FindCrossRepositoryUsages(ProviderUrl, ProviderKey, CrossRepoUsageScope.DefaultHeads, authorizedConsumerRepositoryIds: []);
        Assert.AreEqual(0, denied.Count, "an empty authorization set contributes nothing (fail closed)");

        // Only A authorized: exactly A's usage, never B's — B contributes neither a row nor a count.
        var repoA = RepoId("https://github.com/org/appA");
        var onlyA = deps.FindCrossRepositoryUsages(ProviderUrl, ProviderKey, CrossRepoUsageScope.DefaultHeads, authorizedConsumerRepositoryIds: [repoA]);
        Assert.AreEqual(1, onlyA.Count, "only the authorized consumer contributes");
        Assert.AreEqual("https://github.com/org/appA", onlyA[0].ConsumerRepositoryUrl);

        // null = allow all (a scope-less/unauthenticated local read stays backward compatible).
        var all = deps.FindCrossRepositoryUsages(ProviderUrl, ProviderKey, CrossRepoUsageScope.DefaultHeads, authorizedConsumerRepositoryIds: null);
        Assert.AreEqual(2, all.Count);
        _ = a;
    }

    // ==== Criterion 5: explicit historical scope is versioned and never conflated with the head =====

    [TestMethod]
    public void C5_DefaultScope_IsHeadOnly_ExplicitCommitScope_ReturnsHistory()
    {
        var provider = SeedProvider(ProviderCommit, dirty: false);

        // One consumer repo with TWO generations: an older historical commit and the current head. The
        // branch "main" points at the head only; the historical generation retains its edge + usage but
        // is not a branch head.
        var repoUrl = "https://github.com/org/appA";
        var repoId = new SnapshotStore(_conn).EnsureRepository(repoUrl, now: 1);
        var old = SeedConsumerGeneration(repoId, repoUrl, "old_commit", "logical_appA", "src/A/UseA.cs", line: 7);
        AddUsage(old.projectId, provider.symbolId, old.fileVersionId, line: 7);
        AddEdge(old.snapshotId, old.projectId, provider, ProviderCommit);

        var head = SeedConsumerGeneration(repoId, repoUrl, "head_commit", "logical_appA", "src/A/UseA.cs", line: 9);
        AddUsage(head.projectId, provider.symbolId, head.fileVersionId, line: 9);
        AddEdge(head.snapshotId, head.projectId, provider, ProviderCommit);
        var branchId = new SnapshotStore(_conn).EnsureBranch(repoId, "main", isDefault: true, now: 2);
        new SnapshotStore(_conn).SetBranchPointer(branchId, head.snapshotId, now: 3);

        var deps = new SnapshotDependencyStore(_conn);

        // Default scope = head only: the historical generation is NOT double-counted.
        var headScope = deps.FindCrossRepositoryUsages(ProviderUrl, ProviderKey, CrossRepoUsageScope.DefaultHeads, null);
        Assert.AreEqual(1, headScope.Count, "the default head scope reports one usage, not the retained history too");
        Assert.AreEqual(9, headScope[0].Line, "it is the head generation's usage");

        // Explicit historical commit scope returns the versioned older result.
        var historyScope = deps.FindCrossRepositoryUsages(
            ProviderUrl, ProviderKey, new CrossRepoUsageScope { ConsumerCommitSha = "old_commit" }, null);
        Assert.AreEqual(1, historyScope.Count, "explicit commit scope returns the historical usage");
        Assert.AreEqual(7, historyScope[0].Line, "it is the older generation's usage");
        Assert.AreEqual("old_commit", historyScope[0].ConsumerCommitSha);
    }

    // ==== Criterion 5 hardening: an unpublished (staging) consumer snapshot never leaks =============

    [TestMethod]
    public void C5_ExplicitCommitScope_ExcludesUnpublishedConsumerSnapshot()
    {
        var provider = SeedProvider(ProviderCommit, dirty: false);
        var repoUrl = "https://github.com/org/appA";
        var repoId = new SnapshotStore(_conn).EnsureRepository(repoUrl, now: 1);

        // A consumer generation at a specific commit that references the provider symbol and records its
        // edge, but is still STAGING (e.g. an in-flight or crashed run sharing that commit) — never
        // published. It has a real edge + occurrence, so only the published-status gate can exclude it.
        var staging = SeedConsumerGeneration(repoId, repoUrl, "wip_commit", "logical_appA", "src/A/UseA.cs", line: 5);
        AddUsage(staging.projectId, provider.symbolId, staging.fileVersionId, line: 5);
        AddEdge(staging.snapshotId, staging.projectId, provider, ProviderCommit);
        Exec($"UPDATE snapshots SET status = '{SnapshotStatus.Pending}' WHERE id = {staging.snapshotId};");

        var deps = new SnapshotDependencyStore(_conn);
        var scope = new CrossRepoUsageScope { ConsumerCommitSha = "wip_commit" };

        Assert.AreEqual(0, deps.FindCrossRepositoryUsages(ProviderUrl, ProviderKey, scope, null).Count,
            "an unpublished (staging) consumer snapshot never leaks into an explicit commit-scope usage query");
        Assert.AreEqual(0, deps.GetConsumersByProviderRepository(ProviderUrl, null, scope).Count,
            "nor into the reverse-dependency listing under that commit scope");

        // Publishing the very same snapshot makes its historical usage visible — proving the status gate,
        // not a missing edge/occurrence, is what excluded it.
        Exec($"UPDATE snapshots SET status = '{SnapshotStatus.Complete}' WHERE id = {staging.snapshotId};");
        var published = deps.FindCrossRepositoryUsages(ProviderUrl, ProviderKey, scope, null);
        Assert.AreEqual(1, published.Count, "once published, the historical usage is returned");
        Assert.AreEqual(5, published[0].Line);
    }

    // ==== Criterion 6: updating one parent's pin does NOT mutate another parent's edge ==============

    [TestMethod]
    public void C6_RepinningOneParent_LeavesTheOtherParentsEdgeUnchanged()
    {
        var providerV1 = SeedProvider(ProviderCommit, dirty: false);
        var parentA = SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            providerV1, providerCommit: ProviderCommit, line: 10);
        var parentB = SeedConsumerWithUsage("https://github.com/org/appB", "appB_head", "logical_appB",
            providerV1, providerCommit: ProviderCommit, line: 20);

        var deps = new SnapshotDependencyStore(_conn);
        var bEdgeBefore = deps.GetByConsumerSnapshot(parentB.snapshotId).Single();

        // Parent A bumps its submodule pin to a NEW provider commit: a new provider snapshot/version and a
        // NEW consumer generation for A (immutable append), leaving B's original edge byte-for-byte intact.
        var providerV2 = SeedProvider("prov_commit_bbbb", dirty: false);
        var repoA = RepoId("https://github.com/org/appA");
        var aNext = SeedConsumerGeneration(repoA, "https://github.com/org/appA", "appA_head2", "logical_appA", "src/A/UseA.cs", line: 11);
        AddUsage(aNext.projectId, providerV2.symbolId, aNext.fileVersionId, line: 11);
        AddEdge(aNext.snapshotId, aNext.projectId, providerV2, "prov_commit_bbbb");

        var bEdgeAfter = deps.GetByConsumerSnapshot(parentB.snapshotId).Single();
        Assert.AreEqual(bEdgeBefore.Id, bEdgeAfter.Id, "parent B's edge row is the same identity");
        Assert.AreEqual(providerV1.projectId, bEdgeAfter.ProviderProjectId, "parent B still pins provider v1");
        Assert.AreEqual(ProviderCommit, bEdgeAfter.ProviderCommitSha, "parent B's pin is untouched");
        Assert.AreEqual(bEdgeBefore.ProviderSnapshotId, bEdgeAfter.ProviderSnapshotId, "parent B's provider snapshot is untouched");

        // Parent A's own edges reflect BOTH pins across its two generations (append-only history).
        var aEdges = deps.GetByConsumerSnapshot(aNext.snapshotId);
        Assert.AreEqual(1, aEdges.Count);
        Assert.AreEqual(providerV2.projectId, aEdges[0].ProviderProjectId, "parent A's new generation pins provider v2");

        // Parent A's ORIGINAL generation is itself immutable: the re-pin appended a new generation and did
        // not rewrite the first one, which still pins provider v1 at the original commit.
        var aOriginalEdge = deps.GetByConsumerSnapshot(parentA.snapshotId).Single();
        Assert.AreEqual(providerV1.projectId, aOriginalEdge.ProviderProjectId, "parent A's original generation still pins provider v1");
        Assert.AreEqual(ProviderCommit, aOriginalEdge.ProviderCommitSha, "parent A's original pin is untouched by the re-pin");
    }

    // ==== #32: two repos sharing an FQN but distinct stable keys are NEVER conflated ================

    [TestMethod]
    public void Issue32_SameFqn_DistinctStableKeys_AreNotConflated()
    {
        // Provider P exposes FQN X under stable key K1. A DIFFERENT provider repo Q exposes the SAME FQN X
        // under a DISTINCT key K2 (two repos genuinely defining the same fully-qualified name).
        var providerP = SeedProvider(ProviderCommit, dirty: false); // key = ProviderKey, fqn = ProviderFqn
        var qUrl = "https://github.com/org/other-mix";
        var providerQ = SeedProviderRepo(qUrl, "logical_otherMix", "otherProv_commit",
            fqn: ProviderFqn, symbolKey: "M:Other.Combiner.Combine", dirty: false);

        // A consumer of P references P's symbol; a consumer of Q references Q's symbol.
        SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            providerP, providerCommit: ProviderCommit, line: 10);
        SeedConsumerWithUsage("https://github.com/org/appQ", "appQ_head", "logical_appQ",
            providerQ, providerCommit: "otherProv_commit", line: 30, providerUrl: qUrl);

        var deps = new SnapshotDependencyStore(_conn);

        // FQN X in provider P resolves to EXACTLY P's stable key — never Q's, even though both share the FQN.
        var keysInP = deps.ResolveProviderSymbolKeysByFqn(ProviderUrl, ProviderFqn);
        CollectionAssert.AreEqual(new[] { ProviderKey }, keysInP, "the FQN resolves to only P's stable key within P");

        // A usage query bound to P's stable key returns only P's consumer — Q's identical-FQN usage is not conflated.
        var usages = deps.FindCrossRepositoryUsages(ProviderUrl, ProviderKey, CrossRepoUsageScope.DefaultHeads, null);
        Assert.AreEqual(1, usages.Count, "only the provider whose STABLE KEY was queried contributes");
        Assert.AreEqual("https://github.com/org/appA", usages[0].ConsumerRepositoryUrl);

        // And Q's key, queried within Q, returns only Q's consumer.
        var qUsages = deps.FindCrossRepositoryUsages(qUrl, "M:Other.Combiner.Combine", CrossRepoUsageScope.DefaultHeads, null);
        Assert.AreEqual(1, qUsages.Count);
        Assert.AreEqual("https://github.com/org/appQ", qUsages[0].ConsumerRepositoryUrl);
    }

    // ==== #48: a dirty submodule pin is a DISTINCT provider snapshot + a dirty-flagged edge ==========

    [TestMethod]
    public void Issue48_DirtySubmodule_HasDistinctProviderIdentity_AndDirtyEdge()
    {
        // The clean pin and the dirty pin of the same submodule commit must not collide: their provider
        // snapshot identity hashes differ (the dirty tree folds a "submodule-dirty" working-tree delta),
        // mirroring the orchestrator's EnsureProviderSnapshot construction.
        var clean = ProviderIdentity(ProviderCommit, dirty: false);
        var dirty = ProviderIdentity(ProviderCommit, dirty: true);
        Assert.AreNotEqual(clean.Hash, dirty.Hash, "a dirty submodule tree is never the clean pinned commit's identity (#48)");
        Assert.IsNull(clean.WorkingTreeDelta, "the clean pin has no working-tree delta");
        Assert.AreEqual("submodule-dirty", dirty.WorkingTreeDelta, "the dirty pin carries the delta marker");

        // Two provider snapshots exist (clean + dirty), never one; and a dirty edge round-trips its flag.
        var provider = SeedProviderRepo(ProviderUrl, ProviderCanonical, ProviderCommit, ProviderFqn, ProviderKey, dirty: true);
        var parent = SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            provider, providerCommit: ProviderCommit, line: 10, submoduleDirty: true);

        var edge = new SnapshotDependencyStore(_conn).GetByConsumerSnapshot(parent.snapshotId).Single();
        Assert.IsTrue(edge.SubmoduleDirty, "the edge records the submodule's dirty state (#48)");

        var usages = new SnapshotDependencyStore(_conn)
            .FindCrossRepositoryUsages(ProviderUrl, ProviderKey, CrossRepoUsageScope.DefaultHeads, null);
        Assert.AreEqual(1, usages.Count);
        Assert.IsTrue(usages[0].SubmoduleDirty, "a cross-repo usage surfaces the dirty pin so it is not read as the clean commit");
    }

    // ==== Reverse-dependency listing (find_submodule_consumers) =====================================

    [TestMethod]
    public void ReverseDependency_ListsConsumersOfProviderRepository()
    {
        var provider = SeedProvider(ProviderCommit, dirty: false);
        SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            provider, providerCommit: ProviderCommit, line: 10);
        SeedConsumerWithUsage("https://github.com/org/appB", "appB_head", "logical_appB",
            provider, providerCommit: ProviderCommit, line: 20);

        var consumers = new SnapshotDependencyStore(_conn)
            .GetConsumersByProviderRepository(ProviderUrl, providerCommitSha: null, CrossRepoUsageScope.DefaultHeads);

        Assert.AreEqual(2, consumers.Count, "both parents that consume the submodule are listed");
        CollectionAssert.AreEquivalent(
            new[] { "https://github.com/org/appA", "https://github.com/org/appB" },
            consumers.Select(c => c.ConsumerRepositoryUrl).ToList());
        Assert.IsTrue(consumers.All(c => c.ProviderCommitSha == ProviderCommit), "each lists its exact pin");
    }

    // ==== Retention (Todo F): a branch-pointed consumer edge PROTECTS the shared provider generation ==

    [TestMethod]
    public void Retention_SubmoduleProvider_ProtectsProviderGeneration_WhileAParentPinsIt()
    {
        // Give the provider snapshot a real generation (index_runs) so protection can pin it.
        var runStore = new IndexRunStore(_conn);
        var runId = runStore.BeginRun("full", startedAt: 1, IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, completedAt: 1, projects: 1);

        var provider = SeedProviderRepo(ProviderUrl, ProviderCanonical, ProviderCommit, ProviderFqn, ProviderKey, dirty: false, runId: runId);
        var parent = SeedConsumerWithUsage("https://github.com/org/appA", "appA_head", "logical_appA",
            provider, providerCommit: ProviderCommit, line: 10);

        // While parent A's consumer snapshot is branch-pointed, the provider generation + commit are protected.
        var protectedSet = BuildSubmoduleProtection();
        Assert.IsTrue(protectedSet.IsGenerationProtected(runId), "the shared provider generation is protected while a parent pins it");
        Assert.IsTrue(protectedSet.IsCommitProtected(ProviderCommit), "the provider commit's history is protected too");

        // Remove the only consumer branch pointer: nothing pins the provider, so it is no longer protected.
        Exec($"UPDATE branches SET snapshot_id = NULL WHERE snapshot_id = {parent.snapshotId};");
        var afterUnpin = BuildSubmoduleProtection();
        Assert.IsFalse(afterUnpin.IsGenerationProtected(runId), "with no parent pin the provider generation is no longer protected");
    }

    // ==== helpers ===============================================================================

    private RetentionProtectionSet BuildSubmoduleProtection()
    {
        var builder = new RetentionProtectionBuilder();
        new SubmoduleProviderProtection().Contribute(_conn, builder);
        return builder.Build();
    }

    private SnapshotIdentity ProviderIdentity(string commitSha, bool dirty) => new()
    {
        RepositoryRemoteUrl = ProviderUrl,
        CommitSha = commitSha,
        TreeSha = null,
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc",
        WorkingTreeDelta = dirty ? "submodule-dirty" : null,
        IsOverlay = false
    };

    private static SnapshotIdentity ConsumerIdentity(string repoUrl, string commitSha) => new()
    {
        RepositoryRemoteUrl = repoUrl,
        CommitSha = commitSha,
        TreeSha = $"tree_{commitSha}",
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc"
    };

    private static ProjectIdentity ProjectIdentityFor(string repoUrl, string canonical, string relPath) => new()
    {
        CanonicalId = canonical,
        GitRemoteUrl = repoUrl,
        RepoRelativePath = relPath,
        TargetFramework = "net10.0"
    };

    private readonly record struct ProviderSeed(long snapshotId, long projectId, long repoId, long symbolId);
    private readonly record struct ConsumerSeed(long snapshotId, long projectId, long fileVersionId);

    private ProviderSeed SeedProvider(string commitSha, bool dirty) =>
        SeedProviderRepo(ProviderUrl, ProviderCanonical, commitSha, ProviderFqn, ProviderKey, dirty);

    /// <summary>Seeds a complete PROVIDER snapshot: provider repo + snapshot + logical/physical project +
    /// one symbol with a stable key. Optionally attached to a generation for retention tests.</summary>
    private ProviderSeed SeedProviderRepo(
        string providerUrl, string canonical, string commitSha, string fqn, string symbolKey, bool dirty, long? runId = null)
    {
        var store = new SnapshotStore(_conn);
        var repoId = store.EnsureProviderRepository(providerUrl, now: 1);
        var commitId = store.EnsureCommit(repoId, commitSha, null, now: 1);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = providerUrl,
            CommitSha = commitSha,
            TreeSha = null,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
            ConfigHash = "cfg",
            ToolchainFingerprint = "tc",
            WorkingTreeDelta = dirty ? "submodule-dirty" : null,
            IsOverlay = false
        };
        var (snapId, _, _) = store.BeginPending(identity, repoId, commitId, runId, now: 1, isProvider: true);
        var logicalId = store.EnsureLogicalProject(repoId, canonical, "lib/Mix.csproj", "net10.0", now: 1);
        var projectId = new ProjectStore(_conn).UpsertSnapshotProject(
            ProjectIdentityFor(providerUrl, canonical, "lib/Mix.csproj"), snapId, logicalId, lastIndexedAt: 1);
        store.MapProject(snapId, projectId);
        var symbolId = InsertSymbol(projectId, symbolKey, fqn);
        store.MarkComplete(snapId, publishedAt: 1);
        return new ProviderSeed(snapId, projectId, repoId, symbolId);
    }

    /// <summary>Seeds a complete consumer generation (repo may be new or reused): snapshot + logical/
    /// physical project + one file version. Does NOT add a usage/edge/branch — callers compose those.</summary>
    private ConsumerSeed SeedConsumerGeneration(
        long repoId, string repoUrl, string commitSha, string canonical, string usageFile, int line)
    {
        var store = new SnapshotStore(_conn);
        var commitId = store.EnsureCommit(repoId, commitSha, $"tree_{commitSha}", now: 1);
        var (snapId, _, _) = store.BeginPending(ConsumerIdentity(repoUrl, commitSha), repoId, commitId, null, now: 1);
        var logicalId = store.EnsureLogicalProject(repoId, canonical, "src/App/App.csproj", "net10.0", now: 1);
        var projectId = new ProjectStore(_conn).UpsertSnapshotProject(
            ProjectIdentityFor(repoUrl, canonical, "src/App/App.csproj"), snapId, logicalId, lastIndexedAt: 1);
        store.MapProject(snapId, projectId);
        var fileVersionId = InsertFileVersion(projectId, usageFile);
        store.MarkComplete(snapId, publishedAt: 1);
        return new ConsumerSeed(snapId, projectId, fileVersionId);
    }

    /// <summary>End-to-end consumer seed: a fresh repo with a default-branch head that references the
    /// provider symbol once and records its dependency edge.</summary>
    private ConsumerSeed SeedConsumerWithUsage(
        string repoUrl, string commitSha, string canonical, ProviderSeed provider, string providerCommit,
        int line, string usageFile = "src/App/Use.cs", bool submoduleDirty = false, string? providerUrl = null)
    {
        var store = new SnapshotStore(_conn);
        var repoId = store.EnsureRepository(repoUrl, now: 1);
        var consumer = SeedConsumerGeneration(repoId, repoUrl, commitSha, canonical, usageFile, line);
        AddUsage(consumer.projectId, provider.symbolId, consumer.fileVersionId, line);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 2);
        store.SetBranchPointer(branchId, consumer.snapshotId, now: 3);
        AddEdge(consumer.snapshotId, consumer.projectId, provider, providerCommit, submoduleDirty);
        return consumer;
    }

    private void AddEdge(long consumerSnapshotId, long consumerProjectId, ProviderSeed provider, string providerCommit, bool dirty = false) =>
        new SnapshotDependencyStore(_conn).Insert(new SnapshotDependencyEdge
        {
            ConsumerSnapshotId = consumerSnapshotId,
            ConsumerProjectId = consumerProjectId,
            ProviderSnapshotId = provider.snapshotId,
            ProviderProjectId = provider.projectId,
            ProviderRepositoryId = provider.repoId,
            ProviderCommitSha = providerCommit,
            ReferenceKind = "submodule_ref",
            SubmoduleDirty = dirty,
            CreatedAt = 1
        });

    private long RepoId(string url)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM repositories WHERE remote_url = @u;";
        cmd.Parameters.AddWithValue("@u", url);
        return (long)cmd.ExecuteScalar()!;
    }

    private long InsertSymbol(long projectId, string symbolKey, string fqn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 line_start, line_end, last_indexed_at)
            VALUES (@p, @k, @fqn, @dn, 0, 0, 1, 1, 1)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@k", symbolKey);
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@dn", fqn.Split('.').Last());
        return (long)cmd.ExecuteScalar()!;
    }

    private long InsertFileVersion(long projectId, string repoRelativePath)
    {
        long fileId;
        using (var f = _conn.CreateCommand())
        {
            f.CommandText = "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
            f.Parameters.AddWithValue("@p", projectId);
            f.Parameters.AddWithValue("@path", repoRelativePath);
            fileId = (long)f.ExecuteScalar()!;
        }
        using var fv = _conn.CreateCommand();
        fv.CommandText = """
            INSERT INTO file_versions (file_id, content_hash, last_indexed_at)
            VALUES (@f, @hash, 1) RETURNING id;
            """;
        fv.Parameters.AddWithValue("@f", fileId);
        fv.Parameters.AddWithValue("@hash", System.Text.Encoding.UTF8.GetBytes($"hash_{repoRelativePath}"));
        return (long)fv.ExecuteScalar()!;
    }

    private void AddUsage(long inProjectId, long targetSymbolId, long fileVersionId, int line)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
            VALUES (@p, @t, NULL, @fv, @line, 5, @kind, 0);
            """;
        cmd.Parameters.AddWithValue("@p", inProjectId);
        cmd.Parameters.AddWithValue("@t", targetSymbolId);
        cmd.Parameters.AddWithValue("@fv", fileVersionId);
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@kind", (int)ReferenceKind.Invocation);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts the call-graph occurrence a call site emits IN ADDITION to its pure reference —
    /// same target/file/line, but with a non-null source_symbol_id (the enclosing caller). The usage
    /// query must ignore this row so a call site counts once.</summary>
    private void AddCallOccurrence(long inProjectId, long targetSymbolId, long sourceSymbolId, long fileVersionId, int line)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
            VALUES (@p, @t, @src, @fv, @line, 5, @kind, 0);
            """;
        cmd.Parameters.AddWithValue("@p", inProjectId);
        cmd.Parameters.AddWithValue("@t", targetSymbolId);
        cmd.Parameters.AddWithValue("@src", sourceSymbolId);
        cmd.Parameters.AddWithValue("@fv", fileVersionId);
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@kind", (int)ReferenceKind.Invocation);
        cmd.ExecuteNonQuery();
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
