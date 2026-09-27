using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #162 on the SERVICE reuse path (no worker run). A null-sequence ensure that reuses an
/// already-published snapshot must reach the branch state the worker path would have reached for the same
/// request, whenever that cannot regress history:
/// <list type="bullet">
/// <item>(a) a same-commit re-ensure under a NEW identity (an AnalyzerVersion bump, or a Phase-12 provider
/// snapshot the monorepo ensure already built) re-points the branch and supersedes the old head. An
/// older-commit null-sequence reuse still declines (attach-if-unset, #62).</item>
/// <item>(b) a direct ensure that reuses a PROVIDER snapshot reports the coverage recorded when the provider
/// was published: ensure, status, resolve, and the multi-tenant selector all see that verdict.</item>
/// </list>
/// Driven end-to-end through <see cref="SnapshotService.EnsureSnapshotAsync"/> with a worker that must never run.
/// </summary>
[TestClass]
public class EnsureNullSequenceReuseTests
{
    private const string Repo = "https://github.com/org/app";
    private const string Mix = "https://github.com/org/MixAndMatch";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService? _service;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task SameCommit_NewIdentity_NullSequence_RepointsBranchAndSupersedesOldHead()
    {
        var old = Publish(Repo, "commit-A", "cfg-av1");
        var upgraded = Publish(Repo, "commit-A", "cfg-av4");
        StartService();

        await Service.EnsureSnapshotAsync(Ensure(Repo, "commit-A", "cfg-av1", "main"));
        Assert.AreEqual(old, Service.ResolveBranch(Repo, "main")?.Id, "the first attach sets the pointer");

        var result = await Service.EnsureSnapshotAsync(Ensure(Repo, "commit-A", "cfg-av4", "main"));

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(upgraded, result.SnapshotId);
        Assert.AreEqual(upgraded, Service.ResolveBranch(Repo, "main")?.Id,
            "a same-commit identity upgrade re-points the branch without a worker run (#162)");
        Assert.AreEqual(SnapshotStatus.Superseded, Store.GetById(old)!.Status,
            "the old head is superseded exactly as the worker path's advance would");
        Assert.AreEqual(upgraded, Store.GetSelectedSnapshotIdForRepository(Repo));
    }

    [TestMethod]
    public async Task OlderCommit_NullSequence_Reuse_StillDeclines()
    {
        Publish(Repo, "commit-A", "cfg-av4");
        var headOnB = Publish(Repo, "commit-B", "cfg-av4");
        StartService();

        await Service.EnsureSnapshotAsync(Ensure(Repo, "commit-B", "cfg-av4", "main"));
        await Service.EnsureSnapshotAsync(Ensure(Repo, "commit-A", "cfg-av4", "main"));

        Assert.AreEqual(headOnB, Service.ResolveBranch(Repo, "main")?.Id,
            "without a sequence an older-commit reuse never regresses the branch (#62 attach-if-unset)");
        Assert.AreEqual(SnapshotStatus.Complete, Store.GetById(headOnB)!.Status);
    }

    [TestMethod]
    public async Task SequenceBearingReuse_StaysOnForwardOnlyGate()
    {
        // #84: a same-commit identity change carrying a LOWER sequence than the recorded head never moves the
        // pointer, even though the null-sequence path would re-point it.
        var old = Publish(Repo, "commit-A", "cfg-av1");
        Publish(Repo, "commit-A", "cfg-av4");
        StartService();

        await Service.EnsureSnapshotAsync(Ensure(Repo, "commit-A", "cfg-av1", "main") with { BranchHeadSequence = 10 });
        await Service.EnsureSnapshotAsync(Ensure(Repo, "commit-A", "cfg-av4", "main") with { BranchHeadSequence = 5 });

        Assert.AreEqual(old, Service.ResolveBranch(Repo, "main")?.Id, "a lower sequence declines (#84 unchanged)");
        Assert.AreEqual(SnapshotStatus.Complete, Store.GetById(old)!.Status);
    }

    [TestMethod]
    public async Task DirectEnsureReusingProvider_RepointsOldHead_AndReportsRecordedCoverage()
    {
        // The prod shape: MixAndMatch main points at an old direct-built snapshot (AnalyzerVersion 1); the
        // monorepo ensure later built the SAME commit as a Phase-12 provider snapshot under the current
        // identity and recorded its partial provider coverage; a direct null-sequence ensure then reuses it.
        var oldHead = Publish(Mix, "commit-M", "cfg-av1");
        StartService();
        await Service.EnsureSnapshotAsync(Ensure(Mix, "commit-M", "cfg-av1", "main"));
        Assert.AreEqual(oldHead, Service.ResolveBranch(Mix, "main")?.Id);

        var provider = Publish(Mix, "commit-M", "cfg-av4", isProvider: true);
        RecordCoverage(provider, ProviderPartial);

        var result = await Service.EnsureSnapshotAsync(Ensure(Mix, "commit-M", "cfg-av4", "main"));

        Assert.AreEqual(provider, result.SnapshotId, "the direct ensure reuses the provider snapshot (same identity)");
        Assert.AreEqual(provider, Service.ResolveBranch(Mix, "main")?.Id, "main now resolves to the reused provider");
        Assert.AreEqual(SnapshotStatus.Superseded, Store.GetById(oldHead)!.Status);
        AssertPartial(result.Status, result.Reason, result.Coverage, "ensure");
        var status = Service.GetStatus(result.JobId)!;
        AssertPartial(status.Job.Status, status.Job.LastError, status.Coverage, "status");
        var resolveCoverage = Service.GetCoverage(provider);
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, resolveCoverage?.Verdict, "resolve's coverage block reads this row");
        Assert.AreEqual("parent_selection", resolveCoverage?.SelectionSource);
        Assert.AreEqual(provider, Store.GetSelectedSnapshotIdForRepository(Mix),
            "the multi-tenant selector (and so MCP meta) resolves the provider snapshot");
        var meta = Sextant.Mcp.FederatedReadContext.Resolve(_db, requestedRepository: () => Mix).Provenance;
        Assert.AreEqual("partial", meta?.Completeness, "MCP meta.snapshot.completeness reports the provider verdict");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, meta?.Coverage?.Verdict, "MCP meta.snapshot.coverage");
    }

    [TestMethod]
    public async Task DirectEnsureOfProviderOnlyRepository_MakesItSelectable()
    {
        var provider = Publish(Mix, "commit-M", "cfg-av4", isProvider: true);
        RecordCoverage(provider, ProviderPartial);
        StartService();
        Assert.IsNull(Store.GetSelectedSnapshotIdForRepository(Mix), "a provider-only repository is not selectable");

        var result = await Service.EnsureSnapshotAsync(Ensure(Mix, "commit-M", "cfg-av4", "main"));

        Assert.AreEqual(provider, result.SnapshotId);
        AssertPartial(result.Status, result.Reason, result.Coverage, "ensure");
        Assert.AreEqual(provider, Store.GetSelectedSnapshotIdForRepository(Mix),
            "an ensure in its own right makes the repository a consumer, as the worker path's EnsureRepository does");
    }

    [TestMethod]
    public async Task SequenceBearingEnsureOfProviderOnlyRepository_MakesItSelectable()
    {
        var provider = Publish(Mix, "commit-M", "cfg-av4", isProvider: true);
        StartService();

        await Service.EnsureSnapshotAsync(Ensure(Mix, "commit-M", "cfg-av4", "main") with { BranchHeadSequence = 10 });

        Assert.AreEqual(provider, Service.ResolveBranch(Mix, "main")?.Id, "the forward-only gate advances the new branch");
        Assert.AreEqual(provider, Store.GetSelectedSnapshotIdForRepository(Mix),
            "the sequence-bearing reuse path also makes the repository a consumer (parity with the worker)");
    }

    [TestMethod]
    public async Task BranchlessNullSequenceReuse_TargetsMain_LikeTheWorkerPath()
    {
        // The worker (CreateSnapshotContext) and the sequence-bearing reuse path both resolve a missing branch
        // to "main" as the default; the null-sequence reuse path must reach the same state, including the
        // same-commit upgrade and consumer promotion of a provider-only repository.
        var provider = Publish(Mix, "commit-M", "cfg-av4", isProvider: true);
        RecordCoverage(provider, ProviderPartial);
        StartService();

        var result = await Service.EnsureSnapshotAsync(Ensure(Mix, "commit-M", "cfg-av4", branch: null));

        Assert.AreEqual(provider, result.SnapshotId);
        Assert.AreEqual(provider, Service.ResolveBranch(Mix, "main")?.Id, "a branchless ensure attaches main");
        Assert.AreEqual(provider, Store.GetSelectedSnapshotIdForRepository(Mix),
            "main owns the default and the repository became a consumer, so the selector resolves it");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static readonly SnapshotCoverage ProviderPartial = new()
    {
        Verdict = SnapshotCoverageVerdict.Partial,
        Reasons = ["none of this repository's 1 solution(s) was selected; it was built only from the projects the indexing checkout's solution selection reaches, so its solution-scoped view is missing."],
        SelectionSource = "parent_selection",
        SolutionsDiscovered = 1,
        SolutionsNotSelected = 1,
        ProjectsLoaded = 1,
        ProjectFilesOnDisk = 1
    };

    private SnapshotService Service => _service!;
    private SnapshotStore Store => new(_db.GetConnection());

    private void StartService()
    {
        var worker = new FakeSnapshotWorker(_db, FakeSnapshotWorker.Throws("worker must not run on the reuse path"));
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
    }

    private long Publish(string repo, string commit, string configHash, bool isProvider = false) =>
        ServiceTestFixtures.PublishComplete(_db, Ensure(repo, commit, configHash, branch: null),
            recordCommit: true, isProvider: isProvider);

    private void RecordCoverage(long snapshotId, SnapshotCoverage coverage) =>
        Assert.IsTrue(new SnapshotCoverageStore(_db.GetConnection()).Record(snapshotId, coverage, 1));

    private static void AssertPartial(string status, string? reason, SnapshotCoverage? coverage, string surface)
    {
        Assert.AreEqual(SnapshotJobStatus.Partial, status, $"{surface}: a partial provider is never reported complete");
        StringAssert.Contains(reason, "solution-scoped view", $"{surface}: the recorded reason is surfaced");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage?.Verdict, $"{surface}: coverage block");
        Assert.AreEqual("parent_selection", coverage?.SelectionSource, $"{surface}: selection source");
    }

    private static EnsureSnapshotRequest Ensure(string repo, string commit, string configHash, string? branch) => new()
    {
        RepositoryRemoteUrl = repo,
        CommitSha = commit,
        ConfigHash = configHash,
        BranchName = branch
    };
}
