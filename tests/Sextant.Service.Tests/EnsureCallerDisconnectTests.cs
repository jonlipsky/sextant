using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #148: an ensure's production is owned by the SERVICE, never by the caller. A caller that disconnects
/// or times out mid-index (the prod incident: a curl killed after ~20 min threw the whole monorepo index away)
/// must only end its own wait — the worker keeps running, the job stays running and later publishes, and a
/// re-ensure attaches to the in-flight production (worker runs once). Control-plane reads never wait on the
/// writer, so status/resolve/coverage answer promptly for the whole duration of a long index. Only service
/// shutdown cancels a worker, and that requeues the job.
/// </summary>
[TestClass]
public class EnsureCallerDisconnectTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService? _service;
    private FakeSnapshotWorker? _worker;
    private TaskCompletionSource? _release;

    [TestCleanup]
    public void TestCleanup()
    {
        // Never leave a gated worker parked: release it so Dispose's drain returns immediately.
        _worker?.Gate.TrySetResult();
        _release?.TrySetResult();
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task CallerCancelled_MidProduction_WorkerKeepsRunning_JobPublishes_ReEnsureAttaches()
    {
        var service = StartGated();
        var request = ServiceTestFixtures.Request();
        var hash = request.ToIdentity().Hash;

        using var caller = new CancellationTokenSource();
        var ensure = service.EnsureSnapshotAsync(request, caller.Token);
        await WaitUntilAsync(() => _worker!.Calls == 1);

        // The caller times out / disconnects mid-index.
        caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ensure);

        var during = await BoundedAsync(() => service.GetStatusByIdentity(hash));
        Assert.IsNotNull(during);
        Assert.AreEqual(SnapshotJobStatus.Running, during.Job.Status,
            "a caller disconnect must NOT cancel or requeue production (issue #148)");

        // The index finishes after the caller has gone: it still publishes normally.
        _worker!.Gate.SetResult();
        await WaitUntilAsync(() => service.GetStatusByIdentity(hash)?.Job.Status == SnapshotJobStatus.Complete);

        var settled = service.GetStatusByIdentity(hash)!;
        Assert.AreEqual(1, settled.Job.Attempts, "the job ran exactly once — never requeued by the disconnect");
        Assert.IsNotNull(settled.Job.SnapshotId, "the orphaned production published its snapshot");

        var again = await service.EnsureSnapshotAsync(request);
        Assert.AreEqual(during.Job.Id, again.JobId);
        Assert.AreEqual(SnapshotJobStatus.Complete, again.Status);
        Assert.IsTrue(again.Attached, "the re-ensure attaches to the published result");
        Assert.AreEqual(1, _worker.Calls, "the worker ran once across the disconnect and the re-ensure");
    }

    [TestMethod]
    public async Task ReEnsure_WhileProducing_AttachesToInFlightRun_WithoutWaitingOnTheWriter()
    {
        var service = StartGated();
        var request = ServiceTestFixtures.Request();

        var first = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => _worker!.Calls == 1);

        // A re-ensure (e.g. the retry of a caller that timed out) must attach to the running production at
        // once — the production holds the single writer, so an attach that waited on it would hang for the
        // whole index.
        var pending = await service.BeginEnsureSnapshotAsync(request).WaitAsync(Prompt);
        Assert.AreEqual(SnapshotJobStatus.Running, pending.Status);
        Assert.IsTrue(pending.Attached);

        var blocking = service.EnsureSnapshotAsync(request);
        await Task.Delay(100);
        Assert.IsFalse(blocking.IsCompleted, "a blocking attach waits for the shared production");

        _worker!.Gate.SetResult();
        var results = await Task.WhenAll(first, blocking).WaitAsync(Settle);

        Assert.AreEqual(1, _worker.Calls, "concurrent ensures of one identity share ONE worker run (criterion 1)");
        Assert.IsTrue(results.All(r => r.JobId == pending.JobId && r.Status == SnapshotJobStatus.Complete));
        Assert.IsFalse(results[0].Attached, "the first ensure produced the snapshot");
        Assert.IsTrue(results[1].Attached, "the re-ensure attached to it");
    }

    [TestMethod]
    public async Task ControlReads_AnswerPromptly_WhileAWorkerHoldsTheWriter()
    {
        var service = StartGated();

        // A second repository with a published snapshot + branch pointer (attached without a worker run),
        // so resolve/coverage have something to find while repository A is mid-index.
        var other = ServiceTestFixtures.Request(repo: "https://github.com/org/lib", commit: "commit-bbbb", branch: "main");
        ServiceTestFixtures.PublishComplete(_db, other);
        var otherResult = await service.EnsureSnapshotAsync(other).WaitAsync(Settle);
        Assert.AreEqual(SnapshotJobStatus.Complete, otherResult.Status);
        Assert.AreEqual(0, _worker!.Calls);

        var request = ServiceTestFixtures.Request();
        var hash = request.ToIdentity().Hash;
        var ensure = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => _worker.Calls == 1);

        var byIdentity = await TimedAsync(() => service.GetStatusByIdentity(hash));
        Assert.AreEqual(SnapshotJobStatus.Running, byIdentity?.Job.Status);

        var byId = await TimedAsync(() => service.GetStatus(byIdentity!.Job.Id));
        Assert.AreEqual(SnapshotJobStatus.Running, byId?.Job.Status);

        var resolved = await TimedAsync(() => service.ResolveBranch(other.RepositoryRemoteUrl, "main"));
        Assert.AreEqual(otherResult.SnapshotId, resolved?.Id);

        await TimedAsync(() => service.GetCoverage(otherResult.SnapshotId!.Value));

        _worker.Gate.SetResult();
        var produced = await ensure.WaitAsync(Settle);
        Assert.AreEqual(SnapshotJobStatus.Complete, produced.Status);

        // Read-your-writes: once the ensure has returned, the read connection sees its terminal state.
        Assert.AreEqual(SnapshotJobStatus.Complete, service.GetStatus(produced.JobId)?.Job.Status);
    }

    [TestMethod]
    public async Task DifferentIdentity_CallerGivesUpWhileQueuedBehindTheWriter_StillProducesLater()
    {
        var service = StartGated();
        var a = ServiceTestFixtures.Request();
        var b = ServiceTestFixtures.Request(repo: "https://github.com/org/other", commit: "commit-cccc");

        var ensureA = service.EnsureSnapshotAsync(a);
        await WaitUntilAsync(() => _worker!.Calls == 1);

        // B queues behind A's production for the single writer; its caller gives up while waiting.
        using var caller = new CancellationTokenSource();
        var ensureB = service.EnsureSnapshotAsync(b, caller.Token);
        await Task.Delay(100);
        caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ensureB);

        _worker!.Gate.SetResult();
        await ensureA.WaitAsync(Settle);

        // B's ensure was never the caller's to cancel: it registers + produces once the writer frees up.
        var hashB = b.ToIdentity().Hash;
        await WaitUntilAsync(() => service.GetStatusByIdentity(hashB)?.Job.Status == SnapshotJobStatus.Complete);
        Assert.AreEqual(2, _worker.Calls, "each identity produced exactly once");
    }

    [TestMethod]
    public async Task WaitFalse_ReturnsTheJobImmediately_ThenStatusReachesTerminal()
    {
        var service = StartGated();
        var request = ServiceTestFixtures.Request();

        var accepted = await service.BeginEnsureSnapshotAsync(request).WaitAsync(Prompt);

        Assert.IsTrue(accepted.Status is SnapshotJobStatus.Queued or SnapshotJobStatus.Running,
            $"a wait=false ensure returns a non-terminal status, got {accepted.Status}");
        Assert.IsTrue(accepted.JobId > 0);
        Assert.AreEqual(request.ToIdentity().Hash, accepted.IdentityHash);
        Assert.IsNull(accepted.SnapshotId);

        await WaitUntilAsync(() => service.GetStatus(accepted.JobId)?.Job.Status == SnapshotJobStatus.Running);
        _worker!.Gate.SetResult();
        await WaitUntilAsync(() => service.GetStatus(accepted.JobId)?.Job.Status == SnapshotJobStatus.Complete);
        Assert.AreEqual(1, _worker.Calls);

        // Already terminal: wait=false returns the settled result itself.
        var terminal = await service.BeginEnsureSnapshotAsync(request).WaitAsync(Prompt);
        Assert.AreEqual(SnapshotJobStatus.Complete, terminal.Status);
        Assert.IsTrue(terminal.Attached);
        Assert.AreEqual(1, _worker.Calls);
    }

    [TestMethod]
    public async Task Dispose_DuringProduction_CancelsTheWorker_AndRequeuesTheJob()
    {
        var service = StartGated();
        var request = ServiceTestFixtures.Request();
        var hash = request.ToIdentity().Hash;

        var ensure = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => _worker!.Calls == 1);
        var jobId = (await BoundedAsync(() => service.GetStatusByIdentity(hash)))!.Job.Id;

        service.Dispose();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ensure.WaitAsync(Settle));
        var job = new SnapshotJobStore(_db.GetConnection()).GetJob(jobId)!;
        Assert.AreEqual(SnapshotJobStatus.Queued, job.Status,
            "shutdown cancellation requeues the job for a later re-attempt (never a durable failure)");
        Assert.AreEqual(1, job.Attempts);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.EnsureSnapshotAsync(request));
    }

    [TestMethod]
    public async Task StopProduction_CancelsTheWorker_RequeuesTheJob_AndFailsLaterEnsuresFast()
    {
        var service = StartGated();
        var request = ServiceTestFixtures.Request();

        var ensure = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => _worker!.Calls == 1);

        service.StopProduction();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ensure.WaitAsync(Settle));
        Assert.AreEqual(SnapshotJobStatus.Queued, service.GetStatusByIdentity(request.ToIdentity().Hash)?.Job.Status);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.EnsureSnapshotAsync(ServiceTestFixtures.Request(commit: "commit-dddd")).WaitAsync(Settle));
    }

    [TestMethod]
    public async Task LeaseLostMidProduction_RecordsNoResult_JobStaysRunningForTheNewOwner()
    {
        var service = StartGated(leaseTtl: TimeSpan.FromSeconds(3));
        var request = ServiceTestFixtures.Request();
        var hash = request.ToIdentity().Hash;

        var ensure = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => _worker!.Calls == 1);

        // Another writer steals the lease while the worker is running (issue #38).
        StealLease();
        await WaitUntilAsync(() => service.LeaseLost);

        _worker!.Gate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ensure.WaitAsync(Settle));

        var job = service.GetStatusByIdentity(hash)!.Job;
        Assert.AreEqual(SnapshotJobStatus.Running, job.Status,
            "a service that lost the lease must not record the result; the new owner's reconcile requeues it");
        Assert.IsNull(job.SnapshotId);
    }

    [TestMethod]
    [DataRow("cancelled")]
    [DataRow("transient")]
    [DataRow("failure")]
    public async Task LeaseLostMidProduction_WorkerThrows_WritesNothing_JobStaysRunning(string kind)
    {
        // The worker FAILS after the lease was stolen (e.g. a straggler cancelled by shutdown finally stops):
        // none of the production catch arms may requeue / fail / diagnose a job the new owner now controls.
        Exception thrown = kind switch
        {
            "cancelled" => new OperationCanceledException("worker stopped"),
            "transient" => new TransientProvisioningException("fetch timed out"),
            _ => new InvalidOperationException("worker exploded")
        };
        var throwing = new StubbornWorker(thrown);
        var service = StartGated(leaseTtl: TimeSpan.FromSeconds(3), worker: throwing);
        var request = ServiceTestFixtures.Request();

        var ensure = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => throwing.Calls == 1);
        var jobId = (await BoundedAsync(() => service.GetStatusByIdentity(request.ToIdentity().Hash)))!.Job.Id;

        StealLease();
        await WaitUntilAsync(() => service.LeaseLost);
        throwing.Release.SetResult();

        var ex = await Assert.ThrowsAsync<Exception>(() => ensure.WaitAsync(Settle));
        Assert.IsInstanceOfType(ex, thrown.GetType(), "the worker's exception propagates unrecorded");

        var jobs = new SnapshotJobStore(_db.GetConnection());
        var job = jobs.GetJob(jobId)!;
        Assert.AreEqual(SnapshotJobStatus.Running, job.Status, $"a lease-lost {kind} must not requeue/fail the job");
        Assert.AreEqual(1, job.Attempts);
        Assert.IsNull(job.LastError);
        Assert.AreEqual(0, jobs.GetDiagnostics(jobId).Count, "no diagnostics are written without the lease");
    }

    [TestMethod]
    public async Task StopProduction_RefusesEnsures_EvenAnAttachToAProductionStillWindingDown()
    {
        // A worker that ignores cancellation keeps its production registered after shutdown began. An ensure
        // must then be refused with the SERVICE's cancellation (the host maps it to 503) — never answered 202
        // "running" by attaching to a production that is being torn down.
        var stubborn = new StubbornWorker();
        var service = StartGated(worker: stubborn);
        var request = ServiceTestFixtures.Request();

        var ensure = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => stubborn.Calls == 1);

        service.StopProduction();

        var attach = await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.BeginEnsureSnapshotAsync(request).WaitAsync(Prompt));
        Assert.IsFalse(attach.CancellationToken == CancellationToken.None,
            "refused on the service lifetime token, not the caller's");
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.BeginEnsureSnapshotAsync(ServiceTestFixtures.Request(commit: "commit-eeee"))
                .WaitAsync(Prompt));

        stubborn.Release.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ensure.WaitAsync(Settle));
        Assert.AreEqual(1, stubborn.Calls, "the refused ensures never started another worker");
    }

    [TestMethod]
    [DataRow("returns-failed")]
    [DataRow("throws-io")]
    public async Task Shutdown_WorkerEndsInAFailureAfterCancellation_RequeuesInsteadOfCachingIt(string kind)
    {
        // A worker that ignores (or swallows) shutdown's cancellation may then report a failure that is only an
        // artifact of the shutdown — a Failed result, or a torn-down build pipe surfacing as an IOException.
        // It must be requeued for the next owner, never cached as a terminal failure that suppresses retries.
        var stubborn = new StubbornWorker(kind == "throws-io" ? new IOException("build pipe closed") : null);
        var service = StartGated(worker: stubborn);
        var request = ServiceTestFixtures.Request();
        var hash = request.ToIdentity().Hash;

        var ensure = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => stubborn.Calls == 1);
        service.StopProduction();
        stubborn.Release.SetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ensure.WaitAsync(Settle));
        var job = (await BoundedAsync(() => service.GetStatusByIdentity(hash)))!.Job;
        Assert.AreEqual(SnapshotJobStatus.Queued, job.Status,
            "a failure reported after shutdown cancelled the worker is requeued, not recorded as terminal");
        Assert.IsNull(job.SnapshotId);
    }

    [TestMethod]
    public async Task Dispose_WhenAWorkerOutlivesTheDrain_AbandonsTheLease_AndRecordsNothing()
    {
        // A worker that ignores cancellation past ShutdownDrainTimeout may still commit through the shared
        // writer. Dispose must return (bounded) WITHOUT releasing the lease row — another writer taking over
        // now could race the straggler — and every write fails closed: the straggler's result is never
        // recorded, leaving the job running for the next owner's startup reconcile (as for a crash).
        var stubborn = new StubbornWorker();
        var service = StartGated(worker: stubborn, drain: TimeSpan.FromMilliseconds(300));
        var request = ServiceTestFixtures.Request();
        var hash = request.ToIdentity().Hash;

        var ensure = service.EnsureSnapshotAsync(request);
        await WaitUntilAsync(() => stubborn.Calls == 1);
        var jobId = (await BoundedAsync(() => service.GetStatusByIdentity(hash)))!.Job.Id;

        var sw = Stopwatch.StartNew();
        await Task.Run(service.Dispose).WaitAsync(Settle);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), $"Dispose is bounded by the drain timeout, took {sw.Elapsed}");

        Assert.IsFalse(service.ProductionDrained, "the drain timed out on the straggler");
        Assert.IsTrue(service.LeaseLost, "every write probe now fails closed");
        Assert.AreEqual(service.OwnerToken, WriterLease.GetCurrent(_db.GetConnection())?.OwnerToken,
            "the lease row is abandoned (left to expire), never released under a live straggler");
        Assert.IsNull(WriterLease.TryAcquire(_dbPath, "successor", autoHeartbeat: false),
            "no other writer can start while the straggler may still write");

        stubborn.Release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ensure.WaitAsync(Settle));
        var job = new SnapshotJobStore(_db.GetConnection()).GetJob(jobId)!;
        Assert.AreEqual(SnapshotJobStatus.Running, job.Status, "the straggler's outcome is never recorded");
        Assert.IsNull(job.SnapshotId);
    }

    // ---- harness --------------------------------------------------------------------------------

    private SnapshotService StartGated(TimeSpan? leaseTtl = null, ISnapshotWorker? worker = null, TimeSpan? drain = null)
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _worker = new FakeSnapshotWorker(_db) { UseGate = true };
        var options = ServiceTestFixtures.NewOptions(_dbPath) with
        {
            LeaseTtl = leaseTtl ?? TimeSpan.FromSeconds(30),
            ShutdownDrainTimeout = drain ?? TimeSpan.FromSeconds(30)
        };
        if (worker is StubbornWorker stubborn)
            _release = stubborn.Release;
        _service = SnapshotService.Start(options, worker ?? _worker, _db);
        return _service;
    }

    // A worker that IGNORES cancellation (e.g. a wedged MSBuild evaluation): it returns only once released,
    // then fails the run with <paramref name="throwOnRelease"/> when one is given.
    private sealed class StubbornWorker(Exception? throwOnRelease = null) : ISnapshotWorker
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SnapshotWorkResult> ProduceAsync(
            EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Release.Task.ConfigureAwait(false);
            if (throwOnRelease is not null)
                throw throwOnRelease;
            return SnapshotWorkResult.Failed("released");
        }
    }

    private void StealLease()
    {
        using var thief = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        thief.Open();
        using var cmd = thief.CreateCommand();
        cmd.CommandText = "UPDATE writer_lease SET owner_token = 'thief' WHERE id = 1;";
        cmd.ExecuteNonQuery();
    }

    // Runs a control-plane read off the test thread and asserts it answers within the prompt bound even though
    // a gated worker holds the single writer.
    private static async Task<T> TimedAsync<T>(Func<T> read)
    {
        var sw = Stopwatch.StartNew();
        var result = await Task.Run(read).WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();
        Assert.IsTrue(sw.Elapsed < Prompt, $"control-plane read took {sw.Elapsed} while a worker held the writer");
        return result;
    }

    // Runs a read off the test thread, bounded — a regression that makes it wait on the writer held by a gated
    // worker must FAIL the test rather than deadlock it.
    private static Task<T> BoundedAsync<T>(Func<T> read) => Task.Run(read).WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!await BoundedAsync(condition))
        {
            if (deadline.Elapsed > Settle)
                Assert.Fail("condition not reached within the settle bound");
            await Task.Delay(20);
        }
    }
}
