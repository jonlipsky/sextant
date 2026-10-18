using Sextant.Service.Observability;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #330: a worker's async chain completes inline, so the thread that finished it kept every frame of the job on
/// its stack while the post-job reclaim collected on another thread, and the whole Solution stayed reachable. The
/// production must continue on a fresh stack: the job's state is unreachable while that thread is still inside the
/// completion (no reclaim wait: the collection runs at the moment the production Task completes), for every outcome.
/// </summary>
[TestClass]
public class JobStackReleaseTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

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
    }

    public enum Outcome { Published, FailedPublish, Threw }

    // Holds the job's state in a hoisted local across an await that completes inline on the thread calling Gate.SetResult.
    private sealed class InlineCompletingWorker(IndexDatabase db, Outcome outcome, List<WeakReference> weak) : ISnapshotWorker
    {
        public TaskCompletionSource<bool> Gate { get; } = new();
        public int Started;
        public ManualResetEventSlim Resumed { get; } = new();

        // The job's frame completes the result Task itself and keeps using its state afterwards, as a pipeline that
        // completes a channel does: the service's continuation runs inline inside that frame.
        public Task<SnapshotWorkResult> ProduceAsync(
            EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<SnapshotWorkResult>();
            _ = Task.Run(async () =>
            {
                var state = new JobState();
                weak.Add(new WeakReference(state));
                weak.Add(new WeakReference(state.Large));
                Volatile.Write(ref Started, 1);
                await Gate.Task.ConfigureAwait(false);
                Resumed.Set();
                if (outcome == Outcome.Threw)
                    result.SetException(new InvalidOperationException("the worker failed"));
                else if (outcome == Outcome.FailedPublish)
                    result.SetResult(SnapshotWorkResult.Failed("indexing finished but no complete snapshot was published"));
                else
                    result.SetResult(SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(db, request)));
                GC.KeepAlive(state);
            }, CancellationToken.None);
            return result.Task;
        }
    }

    private static EnsureSnapshotRequest Request(char commit) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/stack.git", CommitSha = new string(commit, 40), BranchName = "main"
    };

    [TestMethod]
    [DataRow(Outcome.Published)]
    [DataRow(Outcome.FailedPublish)]
    [DataRow(Outcome.Threw)]
    public async Task TheJobsState_IsUnreachable_WhileTheCompletingThreadIsStillInsideTheProduction(Outcome outcome)
    {
        var weak = new List<WeakReference>();
        var worker = new InlineCompletingWorker(_db, outcome, weak);
        using var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);

        var ensure = service.EnsureSnapshotAsync(Request('a'));
        var deadline = DateTime.UtcNow + Settle;
        while (Volatile.Read(ref worker.Started) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.AreEqual(1, Volatile.Read(ref worker.Started), "the worker started");

        // The production deregisters itself under this lock as its last step. Holding it parks the thread that
        // completed the worker right there: with an inline chain every frame of the job is still on that stack.
        var inFlightLock = service.InFlightLockForTesting;
        bool alive;
        lock (inFlightLock)
        {
            _ = Task.Run(() => worker.Gate.SetResult(true));
            Assert.IsTrue(worker.Resumed.Wait(Settle), "the worker resumed");
            Thread.Sleep(1000);
            for (var i = 0; i < 2; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            alive = weak.Any(w => w.IsAlive);
        }
        // A worker exception is a failed result, never a throw out of the ensure.
        var result = await ensure.WaitAsync(Settle);
        var expected = outcome == Outcome.Published ? SnapshotJobStatus.Complete : SnapshotJobStatus.Failed;
        Assert.AreEqual(expected, result.Status);
        if (outcome == Outcome.Threw)
            Assert.AreEqual("the worker failed", result.Reason);
        else if (outcome == Outcome.FailedPublish)
            Assert.IsFalse(string.IsNullOrEmpty(result.Reason), "the failure reason is kept");
        Assert.IsTrue(weak.Count > 0, "the worker allocated the job's state");
        Assert.IsFalse(alive, "the job's state is unreachable while the thread that completed the worker is still inside the production");
    }

    private SnapshotService StartWithReclaim(Func<JobMemoryReclaimer.Result> action, out int[] ran)
    {
        var counter = new int[1];
        ran = counter;
        var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), new FakeSnapshotWorker(_db), _db);
        service.MemoryReclaimMinWorkingSetBytes = 0;
        service.MemoryReclaimResidualRetryDelay = TimeSpan.FromMilliseconds(5);
        service.MemoryReclaimLog = _ => { };
        service.MemoryReclaimAction = () =>
        {
            Interlocked.Increment(ref counter[0]);
            return action();
        };
        return service;
    }

    [TestMethod]
    public async Task AReclaimThatLeavesALargeHeap_RunsAgainLater_UntilTheHeapIsClean()
    {
        var calls = 0;
        using var service = StartWithReclaim(
            () => new JobMemoryReclaimer.Result(0, 0, Interlocked.Increment(ref calls) < 2 ? 1L << 30 : 40L << 20, TimeSpan.Zero), out var ran);

        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 1);
        await service.PendingMemoryReclaim.WaitAsync(Settle);

        Assert.AreEqual(2, Volatile.Read(ref ran[0]), "the first run had no baseline and was residual, the retry found a clean heap");
        Assert.AreEqual(40L << 20, service.ReclaimHeapBaselineForTesting);
    }

    [TestMethod]
    public async Task ALargeSteadyHeap_BecomesTheBaseline_AndDoesNotTriggerRetries()
    {
        using var service = StartWithReclaim(() => new JobMemoryReclaimer.Result(0, 0, 1L << 30, TimeSpan.Zero), out var ran);

        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 1);
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        Assert.AreEqual(2, Volatile.Read(ref ran[0]), "only the first reclaim, which had no baseline, retried once");

        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 2);
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        Assert.AreEqual(3, Volatile.Read(ref ran[0]), "a heap at its baseline is clean at once");
    }

    [TestMethod]
    public async Task AJobSizedExcessOverTheBaseline_IsResidual_ButASteadyHeapIsNot()
    {
        var heaps = new Queue<long>([40L << 20, 1L << 30, 40L << 20]);
        using var service = StartWithReclaim(
            () => new JobMemoryReclaimer.Result(0, 0, heaps.Count > 0 ? heaps.Dequeue() : 40L << 20, TimeSpan.Zero), out var ran);

        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 1);
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        Assert.AreEqual(1, Volatile.Read(ref ran[0]), "a small first heap is clean");

        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 2);
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        Assert.AreEqual(3, Volatile.Read(ref ran[0]), "1 GiB over a 40 MiB baseline retried; the retry found it clean");
    }

    [TestMethod]
    public async Task AReclaimThatKeepsALargeHeap_RetriesAreBounded_AndRebaseTheHeap()
    {
        var calls = 0;
        using var service = StartWithReclaim(
            () => new JobMemoryReclaimer.Result(0, 0, (long)Interlocked.Increment(ref calls) << 30, TimeSpan.Zero), out var ran);
        service.MemoryReclaimResidualRetries = 2;

        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 1);
        await service.PendingMemoryReclaim.WaitAsync(Settle);

        Assert.AreEqual(3, Volatile.Read(ref ran[0]), "the first run plus the bounded retries");
        Assert.AreEqual(3L << 30, service.ReclaimHeapBaselineForTesting, "the exhausted heap is accepted as the new steady state");
        Assert.IsTrue(service.CollectMetrics().Maintenance.MemoryReclaimsSkipped.GetValueOrDefault(MemoryReclaimSkipReasons.Deferred) >= 1);
    }

    [TestMethod]
    public async Task AResidualOutcome_LeavesMemoryNotClean_SoTheNextProductionReclaimsBeforeItsWorker()
    {
        var ranWhenWorkerStarted = new List<int>();
        var ran = 0;
        var worker = new FakeSnapshotWorker(_db, (self, request) =>
        {
            lock (ranWhenWorkerStarted)
                ranWhenWorkerStarted.Add(Volatile.Read(ref ran));
            return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(self.Database, request));
        });
        using var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        service.MemoryReclaimMinWorkingSetBytes = 0;
        service.MemoryReclaimResidualRetries = 0;
        service.MemoryReclaimLog = _ => { };
        service.MemoryReclaimAction = () =>
        {
            Interlocked.Increment(ref ran);
            return new JobMemoryReclaimer.Result(0, 0, 1L << 30, TimeSpan.Zero);
        };

        await service.EnsureSnapshotAsync(Request('a'));
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        await service.EnsureSnapshotAsync(Request('b'));
        await service.PendingMemoryReclaim.WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { 0, 2 }, ranWhenWorkerStarted,
            "job A's residual post-job reclaim left the debt, so job B reclaimed again before its worker");
    }

    [TestMethod]
    public async Task ANewerProduction_SupersedesAResidualRetry_WaitingOutItsDelay()
    {
        var calls = 0;
        using var service = StartWithReclaim(
            () => new JobMemoryReclaimer.Result(0, 0, Interlocked.Increment(ref calls) == 1 ? 1L << 30 : 40L << 20, TimeSpan.Zero), out var ran);
        service.MemoryReclaimResidualRetryDelay = TimeSpan.FromMilliseconds(500);

        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 1);
        var first = service.PendingMemoryReclaim;
        var deadline = DateTime.UtcNow + Settle;
        while (Volatile.Read(ref ran[0]) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        service.ScheduleJobMemoryReclaim(Task.CompletedTask, 2);
        var second = service.PendingMemoryReclaim;
        await Task.WhenAll(first, second).WaitAsync(Settle);

        Assert.AreEqual(2, Volatile.Read(ref ran[0]), "job 1's first run and job 2's run; job 1's retry never ran");
        Assert.IsTrue(service.CollectMetrics().Maintenance.MemoryReclaimsSkipped.GetValueOrDefault(MemoryReclaimSkipReasons.Superseded) >= 1);
    }

    private sealed class ScriptedWorker(Func<EnsureSnapshotRequest, Task<SnapshotWorkResult>> script) : ISnapshotWorker
    {
        public Task<SnapshotWorkResult> ProduceAsync(
            EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken) => script(request);
    }

    [TestMethod]
    [DataRow("generic")]
    [DataRow("sync-throw")]
    [DataRow("transient")]
    [DataRow("cancelled")]
    [DataRow("completed-sync")]
    public async Task TheWorkersOutcome_IsHandledExactlyAsWhenItWasAwaitedDirectly(string kind)
    {
        var worker = new ScriptedWorker(request => kind switch
        {
            "generic" => Task.FromException<SnapshotWorkResult>(new InvalidOperationException("boom")),
            "sync-throw" => throw new InvalidOperationException("boom"),
            "transient" => Task.FromException<SnapshotWorkResult>(new TransientProvisioningException("fetch timed out")),
            "cancelled" => Task.FromException<SnapshotWorkResult>(new OperationCanceledException("worker stopped")),
            _ => Task.FromResult(SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(_db, request)))
        });
        using var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        var request = Request('c');
        var hash = request.ToIdentity().Hash;

        switch (kind)
        {
            case "cancelled":
                await Assert.ThrowsAsync<OperationCanceledException>(() => service.EnsureSnapshotAsync(request).WaitAsync(Settle));
                Assert.AreEqual(SnapshotJobStatus.Queued, service.GetStatusByIdentity(hash)!.Job.Status, "a cancelled worker is requeued");
                break;
            case "transient":
                var queued = await service.EnsureSnapshotAsync(request).WaitAsync(Settle);
                Assert.AreEqual(SnapshotJobStatus.Queued, queued.Status);
                Assert.AreEqual(SnapshotJobStatus.Queued, service.GetStatusByIdentity(hash)!.Job.Status, "a transient failure is requeued");
                break;
            case "completed-sync":
                var done = await service.EnsureSnapshotAsync(request).WaitAsync(Settle);
                Assert.AreEqual(SnapshotJobStatus.Complete, done.Status);
                break;
            default:
                var failed = await service.EnsureSnapshotAsync(request).WaitAsync(Settle);
                Assert.AreEqual(SnapshotJobStatus.Failed, failed.Status);
                Assert.AreEqual("boom", failed.Reason, "the worker's own message");
                Assert.AreEqual("boom", service.GetStatusByIdentity(hash)!.Job.LastError);
                break;
        }
    }
}
