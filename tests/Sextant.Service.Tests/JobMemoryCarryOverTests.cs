using System.Collections.Concurrent;
using Sextant.Service.Observability;
using Sextant.Service.Restore;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #325: a previous job's memory is never carried into the next job (a skipped post-job reclaim is made up at
/// the start of the next production), every skipped reclaim is logged and counted by reason, and every restore
/// invocation caps MSBuild's worker processes.
/// </summary>
[TestClass]
public class JobMemoryCarryOverTests
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

    private static EnsureSnapshotRequest Request(string repo, char commit) => new()
    {
        RepositoryRemoteUrl = $"https://github.com/org/{repo}.git", CommitSha = new string(commit, 40), BranchName = "main"
    };

    private sealed class Harness
    {
        public int Ran;
        public readonly List<int> RanWhenWorkerStarted = [];
        public readonly List<string> Logs = [];
    }

    private SnapshotService Start(Harness h, ISnapshotWorker worker, long workingSet = 1L << 40)
    {
        var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        service.MemoryReclaimMinWorkingSetBytes = 1000;
        service.WorkingSetProbe = () => workingSet;
        service.MemoryReclaimBusyRetries = 0;
        service.MemoryReclaimBusyRetryDelay = TimeSpan.FromMilliseconds(1);
        service.MemoryReclaimAction = () =>
        {
            Interlocked.Increment(ref h.Ran);
            return default;
        };
        service.MemoryReclaimLog = line =>
        {
            lock (h.Logs)
                h.Logs.Add(line);
        };
        return service;
    }

    private FakeSnapshotWorker GatedWorker(Harness h) => new(_db, (self, request) =>
    {
        lock (h.RanWhenWorkerStarted)
            h.RanWhenWorkerStarted.Add(Volatile.Read(ref h.Ran));
        return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(self.Database, request));
    }) { UseGate = true };

    // Job A is held in its worker, job B is queued behind it, then A finishes: A's post-job reclaim finds B queued.
    private static async Task RunBurstAsync(SnapshotService service, FakeSnapshotWorker worker)
    {
        var a = service.EnsureSnapshotAsync(Request("burst-a", 'a'));
        var deadline = DateTime.UtcNow + Settle;
        while (worker.Calls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        var b = service.EnsureSnapshotAsync(Request("burst-b", 'b'));
        await Task.Delay(200);
        worker.Gate.SetResult();
        await Task.WhenAll(a, b).WaitAsync(Settle);
        await service.PendingMemoryReclaim.WaitAsync(Settle);
    }

    [TestMethod]
    public async Task ASkippedPostJobReclaim_LeadsToAStartOfJobReclaim_OnTheNextProduction()
    {
        var h = new Harness();
        var worker = GatedWorker(h);
        using var service = Start(h, worker);

        await RunBurstAsync(service, worker);

        Assert.AreEqual(2, h.RanWhenWorkerStarted.Count);
        Assert.AreEqual(0, h.RanWhenWorkerStarted[0], "the first job starts with nothing owed");
        Assert.AreEqual(1, h.RanWhenWorkerStarted[1], "the second job reclaimed the first job's memory before its own work");
        Assert.IsTrue(h.Logs.Any(l => l.Contains("start-of-job memory reclaimed")), string.Join("\n", h.Logs));
        var skipped = service.CollectMetrics().Maintenance.MemoryReclaimsSkipped;
        Assert.IsTrue(skipped.GetValueOrDefault(MemoryReclaimSkipReasons.Deferred) + skipped.GetValueOrDefault(MemoryReclaimSkipReasons.Superseded) >= 1,
            "the skipped post-job reclaim of job A is counted");
        Assert.IsTrue(h.Logs.Any(l => l.Contains("memory reclaim skipped (")), "and logged with its reason");
    }

    [TestMethod]
    public async Task NoStartOfJobReclaim_WhenTheWorkingSetIsBelowTheThreshold()
    {
        var h = new Harness();
        var worker = GatedWorker(h);
        using var service = Start(h, worker, workingSet: 10);

        await RunBurstAsync(service, worker);

        Assert.AreEqual(0, Volatile.Read(ref h.Ran));
        Assert.IsTrue(service.CollectMetrics().Maintenance.MemoryReclaimsSkipped.GetValueOrDefault(MemoryReclaimSkipReasons.BelowThreshold) >= 1);
        Assert.IsTrue(h.Logs.Any(l => l.Contains("below_threshold")), string.Join("\n", h.Logs));
    }

    [TestMethod]
    public async Task WhenThePostJobReclaimRan_TheNextProductionOwesNothing()
    {
        var h = new Harness();
        var worker = new FakeSnapshotWorker(_db, (self, request) =>
        {
            lock (h.RanWhenWorkerStarted)
                h.RanWhenWorkerStarted.Add(Volatile.Read(ref h.Ran));
            return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(self.Database, request));
        });
        using var service = Start(h, worker);

        await service.EnsureSnapshotAsync(Request("idle-a", 'a'));
        await service.PendingMemoryReclaim.WaitAsync(Settle);
        await service.EnsureSnapshotAsync(Request("idle-b", 'b'));
        await service.PendingMemoryReclaim.WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { 0, 1 }, h.RanWhenWorkerStarted, "no start-of-job reclaim: job A's post-job reclaim already ran");
        Assert.AreEqual(2, Volatile.Read(ref h.Ran), "one post-job reclaim per job");
        Assert.AreEqual(2, service.CollectMetrics().Maintenance.MemoryReclaimsRun);
    }

    [TestMethod]
    public void EverySkipReason_IsLoggedAndCounted_AndExposedToPrometheus()
    {
        var h = new Harness();
        var service = Start(h, new FakeSnapshotWorker(_db), workingSet: 0);

        Assert.IsFalse(service.TryReclaimJobMemory(7));
        service.Dispose();
        Assert.IsFalse(service.TryReclaimJobMemory(8));

        var skipped = service.CollectMetrics().Maintenance.MemoryReclaimsSkipped;
        Assert.AreEqual(1, skipped.GetValueOrDefault(MemoryReclaimSkipReasons.BelowThreshold));
        Assert.AreEqual(1, skipped.GetValueOrDefault(MemoryReclaimSkipReasons.ShuttingDown));
        Assert.IsTrue(h.Logs.Any(l => l.StartsWith("Job 7: memory reclaim skipped (below_threshold)")));
        Assert.IsTrue(h.Logs.Any(l => l.StartsWith("Job 8: memory reclaim skipped (shutting_down)")));

        var other = new Harness();
        var live = Start(other, new FakeSnapshotWorker(_db), workingSet: 0);
        foreach (var phase in MemoryReclaimPhases.All)
        {
            live.Metrics.RecordMemoryReclaimRun(phase);
            foreach (var reason in MemoryReclaimSkipReasons.All)
                live.Metrics.RecordMemoryReclaimSkipped(reason, phase);
        }
        var text = PrometheusExposition.Render(live.CollectMetrics());
        live.Dispose();
        foreach (var phase in MemoryReclaimPhases.All)
        {
            StringAssert.Contains(text, $"sextant_memory_reclaims_total{{phase=\"{phase}\"}} 1");
            foreach (var reason in MemoryReclaimSkipReasons.All)
                StringAssert.Contains(text, $"sextant_memory_reclaims_skipped_total{{phase=\"{phase}\",reason=\"{reason}\"}} 1");
        }
    }

    private sealed class PerCommitGateWorker(IndexDatabase db) : ISnapshotWorker
    {
        private readonly ConcurrentDictionary<char, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<char, TaskCompletionSource> _started = new();

        public TaskCompletionSource Gate(char commit) => _gates.GetOrAdd(commit, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public Task Started(char commit) => _started.GetOrAdd(commit, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public Action<char>? OnStart { get; set; }

        public async Task<SnapshotWorkResult> ProduceAsync(
            EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
        {
            var commit = request.CommitSha[0];
            OnStart?.Invoke(commit);
            _started.GetOrAdd(commit, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            await Gate(commit).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(db, request));
        }
    }

    [TestMethod]
    public async Task ALateFinishingReclaim_DoesNotMarkMemoryCleanWhileANewerProductionRuns()
    {
        var h = new Harness();
        var worker = new PerCommitGateWorker(_db);
        using var service = Start(h, worker);
        var release = new ManualResetEventSlim(false);
        var firstActionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        // The first reclaim (job A's post-job one) blocks while job B starts; later ones return at once.
        service.MemoryReclaimAction = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstActionStarted.TrySetResult();
                release.Wait(Settle);
            }
            Interlocked.Increment(ref h.Ran);
            return default;
        };
        var ranWhenStarted = new ConcurrentDictionary<char, int>();
        worker.OnStart = c => ranWhenStarted[c] = Volatile.Read(ref h.Ran);

        worker.Gate('a').SetResult();
        await service.EnsureSnapshotAsync(Request("race-a", 'a'));
        await firstActionStarted.Task.WaitAsync(Settle);

        var b = service.EnsureSnapshotAsync(Request("race-b", 'b'));
        await worker.Started('b').WaitAsync(Settle);
        var c = service.EnsureSnapshotAsync(Request("race-c", 'c'));
        await Task.Delay(200);

        release.Set();
        var deadline = DateTime.UtcNow + Settle;
        while (Volatile.Read(ref h.Ran) < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.AreEqual(1, Volatile.Read(ref h.Ran), "job A's late reclaim finished while job B was running");

        worker.Gate('b').SetResult();
        worker.Gate('c').SetResult();
        await Task.WhenAll(b, c).WaitAsync(Settle);
        await service.PendingMemoryReclaim.WaitAsync(Settle);

        Assert.AreEqual(0, ranWhenStarted['a']);
        Assert.AreEqual(0, ranWhenStarted['b'], "job B's start-of-job reclaim found A's reclaim still running");
        Assert.AreEqual(2, ranWhenStarted['c'],
            "A's late reclaim must not mark memory clean: B's post-job reclaim was deferred, so job C reclaims at its start");
    }

    [TestMethod]
    public void RestoreInvocations_AllCapMsBuildNodes_AndHonourTheOption()
    {
        var defaults = new PackageRestoreRunner();
        Assert.AreEqual(2, PackageRestoreRunner.DefaultMaxNodes);
        Assert.AreEqual(2, defaults.MaxNodes);

        var runner = new PackageRestoreRunner(maxNodes: 5);
        var perSolution = runner.CreateStartInfo(Path.Combine(Path.GetTempPath(), "All.slnx"), []).ArgumentList;
        var union = runner.CreateUnionStartInfo(Path.Combine(Path.GetTempPath(), "RestoreUnion.proj"), Path.GetTempPath(), new Dictionary<string, string>()).ArgumentList;

        CollectionAssert.Contains(perSolution.ToList(), "-maxcpucount:5");
        CollectionAssert.Contains(union.ToList(), "-maxcpucount:5");
        CollectionAssert.Contains(union.ToList(), "-nodeReuse:false");
        CollectionAssert.Contains(perSolution.ToList(), "-nodeReuse:false");
        CollectionAssert.Contains(defaults.CreateStartInfo("All.slnx", []).ArgumentList.ToList(), "-maxcpucount:2");
        Assert.AreEqual(2, new PackageRestoreRunner(maxNodes: 0).MaxNodes, "zero means the default");
        Assert.AreEqual(PackageRestoreRunner.MaxMaxNodes, new PackageRestoreRunner(maxNodes: 10_000).MaxNodes);
    }

    [TestMethod]
    public void TheOption_ReadsFromTheEnvironment_AndDefaultsToTwo()
    {
        const string name = "SEXTANT_SERVICE_RESTORE_MAX_NODES";
        var config = new Sextant.Core.SextantConfiguration { DbPath = ServiceTestFixtures.NewDbPath() };
        Environment.SetEnvironmentVariable(name, null);
        try
        {
            Assert.AreEqual(2, ServiceOptions.FromEnvironment(config).RestoreMaxNodes);
            Environment.SetEnvironmentVariable(name, "4");
            Assert.AreEqual(4, ServiceOptions.FromEnvironment(config).RestoreMaxNodes);
            Environment.SetEnvironmentVariable(name, "0");
            Assert.AreEqual(2, ServiceOptions.FromEnvironment(config).RestoreMaxNodes, "0 or malformed falls back to the default");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
