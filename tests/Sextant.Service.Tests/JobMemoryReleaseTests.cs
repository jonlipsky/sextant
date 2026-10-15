using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #320: a job's memory must not outlive the job. The service reclaims it between jobs; the weak
/// references below prove nothing the job allocated is still rooted, and that the reclaim actually ran
/// (a plain test run would otherwise leave the garbage uncollected). No timing or RSS assertions.
/// </summary>
[TestClass]
public class JobMemoryReleaseTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    private sealed class JobState
    {
        public byte[] Large = new byte[4 * 1024 * 1024];
        public object[] Graph = Enumerable.Range(0, 1000).Select(i => (object)new int[16]).ToArray();
    }

    [TestMethod]
    public async Task AJobsState_IsCollectedOnceTheJobHasFinished()
    {
        var weak = new List<WeakReference>();
        var worker = new FakeSnapshotWorker(_db, (self, request) =>
        {
            var state = new JobState();
            weak.Add(new WeakReference(state));
            weak.Add(new WeakReference(state.Large));
            return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(self.Database, request));
        });
        using var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        service.MemoryReclaimMinWorkingSetBytes = 0;

        var result = await service.EnsureSnapshotAsync(Request("memory", 'a'));
        await service.PendingMemoryReclaim.WaitAsync(Settle);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.IsTrue(weak.Count > 0 && weak.All(w => !w.IsAlive), "the job's objects are unreachable and were collected at job end");
    }

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    private static EnsureSnapshotRequest Request(string repo, char commit) => new()
    {
        RepositoryRemoteUrl = $"https://github.com/org/{repo}.git", CommitSha = new string(commit, 40), BranchName = "main"
    };

    private SnapshotService StartCounting(out int[] counters, Func<JobMemoryReclaimer.Result>? action = null, FakeSnapshotWorker? worker = null)
    {
        var ran = new int[1];
        counters = ran;
        var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker ?? new FakeSnapshotWorker(_db), _db);
        service.MemoryReclaimMinWorkingSetBytes = 0;
        service.MemoryReclaimAction = () =>
        {
            Interlocked.Increment(ref ran[0]);
            return action is null ? default : action();
        };
        service.MemoryReclaimLog = _ => { };
        return service;
    }

    [TestMethod]
    public async Task AReclaimOrLogThatThrows_NeverWedgesTheGate_ASecondEnsureCompletes()
    {
        using var service = StartCounting(out var ran, action: () => throw new IOException("boom"));
        service.MemoryReclaimLog = _ => throw new IOException("log boom");

        var first = await service.EnsureSnapshotAsync(Request("one", 'a'));
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        var second = await service.EnsureSnapshotAsync(Request("two", 'b')).WaitAsync(Settle);

        Assert.AreEqual(SnapshotJobStatus.Complete, first.Status);
        Assert.AreEqual(SnapshotJobStatus.Complete, second.Status);
        Assert.IsTrue(Volatile.Read(ref ran[0]) >= 1, "the failing reclaim did run");

        service.MemoryReclaimAction = () => throw new IOException("boom");
        Assert.IsFalse(service.TryReclaimJobMemory(1), "a throwing reclaim is swallowed");
    }

    [TestMethod]
    public async Task TheReclaim_IsSkipped_WhileAnotherProductionIsInFlight()
    {
        var gated = new FakeSnapshotWorker(_db) { UseGate = true };
        using var service = StartCounting(out var ran, worker: gated);

        var inFlight = service.EnsureSnapshotAsync(Request("busy", 'c'));
        var deadline = DateTime.UtcNow + Settle;
        while (gated.Calls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.IsFalse(service.TryReclaimJobMemory(1));
        Assert.AreEqual(0, Volatile.Read(ref ran[0]));

        gated.Gate.SetResult();
        await inFlight.WaitAsync(Settle);
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        Assert.AreEqual(1, Volatile.Read(ref ran[0]), "the last job of the burst reclaims");
    }

    [TestMethod]
    public async Task ABusyReclaim_RetriesUntilTheGateIsFree_ThenReclaimsOnce()
    {
        using var service = StartCounting(out var ran);
        service.MemoryReclaimBusyRetryDelay = TimeSpan.FromMilliseconds(10);
        service.MemoryReclaimBusyRetries = 1000;

        await service.WriteGateForTesting.WaitAsync();
        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 1);
        await Task.Delay(150);
        Assert.AreEqual(0, Volatile.Read(ref ran[0]), "skipped while a non-producing holder has the gate");
        Assert.IsFalse(service.PendingMemoryReclaim.IsCompleted, "still waiting to retry");

        service.WriteGateForTesting.Release();
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        Assert.AreEqual(1, Volatile.Read(ref ran[0]));
    }

    [TestMethod]
    public async Task ABusyReclaim_GivesUpQuietly_AfterTheBoundedRetries()
    {
        using var service = StartCounting(out var ran);
        service.MemoryReclaimBusyRetryDelay = TimeSpan.FromMilliseconds(5);
        service.MemoryReclaimBusyRetries = 3;

        await service.WriteGateForTesting.WaitAsync();
        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 1);
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        service.WriteGateForTesting.Release();

        Assert.AreEqual(0, Volatile.Read(ref ran[0]));
    }

    [TestMethod]
    public async Task ANewerProductionsReclaim_SupersedesAnOlderPendingOne()
    {
        using var service = StartCounting(out var ran);
        service.MemoryReclaimBusyRetryDelay = TimeSpan.FromMilliseconds(10);
        service.MemoryReclaimBusyRetries = 1000;

        await service.WriteGateForTesting.WaitAsync();
        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 1);
        var older = service.PendingMemoryReclaim;
        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 2);
        var newer = service.PendingMemoryReclaim;
        await Task.Delay(100);

        service.WriteGateForTesting.Release();
        await Task.WhenAll(older, newer).WaitAsync(Settle);
        Assert.AreEqual(1, Volatile.Read(ref ran[0]), "only the newest pending reclaim runs");
    }

    [TestMethod]
    public void TheReclaim_IsSkipped_BelowTheWorkingSetThreshold()
    {
        using var service = StartCounting(out var ran);
        service.MemoryReclaimMinWorkingSetBytes = 1000;
        service.WorkingSetProbe = () => 999;
        Assert.IsFalse(service.TryReclaimJobMemory(1));
        service.WorkingSetProbe = () => 1000;
        Assert.IsTrue(service.TryReclaimJobMemory(1));
        Assert.AreEqual(1, Volatile.Read(ref ran[0]));
    }

    [TestMethod]
    public void TheReclaim_IsSkipped_WhenShuttingDown()
    {
        var service = StartCounting(out var ran);
        service.Dispose();
        Assert.IsFalse(service.TryReclaimJobMemory(1));
        Assert.AreEqual(0, Volatile.Read(ref ran[0]));
    }

    [TestMethod]
    public async Task TwoReclaims_NeverRunConcurrently()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        using var service = StartCounting(out var ran, action: () =>
        {
            entered.TrySetResult();
            release.Wait(Settle);
            return default;
        });

        var first = Task.Run(() => service.TryReclaimJobMemory(1));
        await entered.Task.WaitAsync(Settle);
        Assert.IsFalse(service.TryReclaimJobMemory(2), "a reclaim already running makes the second a no-op");
        release.Set();
        Assert.IsTrue(await first.WaitAsync(Settle));
        Assert.AreEqual(1, Volatile.Read(ref ran[0]));
    }

    [TestMethod]
    public void Reclaim_CollectsUnreachableObjects_AndNeverThrows()
    {
        var weak = Allocate();
        var result = JobMemoryReclaimer.Reclaim();
        Assert.IsFalse(weak.IsAlive);
        Assert.IsTrue(result.WorkingSetAfter > 0);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference Allocate() => new(new JobState());

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Workspace, WeakReference Solution) ReleaseALoadedJob()
    {
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var project = workspace.AddProject("P", Microsoft.CodeAnalysis.LanguageNames.CSharp);
        workspace.AddDocument(project.Id, "A.cs", Microsoft.CodeAnalysis.Text.SourceText.From("class A { }"));
        var solution = workspace.CurrentSolution;
        var load = new Sextant.Indexer.MultiSolutionLoadResult(solution, [], []);
        solution.Projects.First().GetCompilationAsync().GetAwaiter().GetResult();
        var weak = (new WeakReference(workspace), new WeakReference(solution));
        LocalIndexerSnapshotWorker.ReleaseJobState(load);
        return weak;
    }

    [TestMethod]
    public void ReleasingAJobsState_MakesItsWorkspaceAndSolutionCollectableWithoutAnyReclaim()
    {
        var (workspace, solution) = ReleaseALoadedJob();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(workspace.IsAlive, "the disposed workspace is unreachable");
        Assert.IsFalse(solution.IsAlive, "the solution and its compilations are unreachable");
    }
}
