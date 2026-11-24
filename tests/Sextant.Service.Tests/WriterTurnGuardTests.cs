using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

[TestClass]
public class WriterTurnGuardTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task SharedWriterRequiresItsOwnTurnAcrossAsyncWorkerContinuations()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var previouslyCaptured = db.GetConnection();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new FakeSnapshotWorker(db)
        {
            AsyncBehavior = async (self, request, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                var snapshot = ServiceTestFixtures.PublishComplete(self.Database, request);
                return SnapshotWorkResult.Complete(snapshot);
            }
        };
        using var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(dbPath), worker, db);
        service.EnableWriterTurnGuardForTesting();
        var ensure = service.EnsureSnapshotAsync(ServiceTestFixtures.Request());
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.ThrowsExactly<InvalidOperationException>(() => db.GetConnection());
            using (var command = previouslyCaptured.CreateCommand())
            {
                command.CommandText = "SELECT 1;";
                command.ExecuteScalar();
            }
            Assert.AreEqual(1, service.WriterTraceViolationsForTesting);
            release.TrySetResult();
            var result = await ensure.WaitAsync(Timeout);
            Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
            Assert.AreEqual(1, service.WriterTraceViolationsForTesting);
            Assert.ThrowsExactly<InvalidOperationException>(() => db.GetConnection());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ControlWaitUsesInjectedClockAfterDurableAdmission()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(dbPath);
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new FakeSnapshotWorker(db)
        {
            AsyncBehavior = async (self, request, ct) =>
            {
                if (request.CommitSha == "held")
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(self.Database, request));
            }
        };
        var options = ServiceTestFixtures.NewOptions(dbPath) with { ControlWriteWait = TimeSpan.FromSeconds(5) };
        using var service = SnapshotService.Start(options, worker, db, timeProvider: clock);
        var held = service.EnsureSnapshotAsync(ServiceTestFixtures.Request(commit: "held"));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            service.ControlWaitStartedForTesting = () => waiting.TrySetResult();
            var pending = service.BeginEnsureSnapshotAsync(ServiceTestFixtures.Request(commit: "next"));
            await waiting.Task.WaitAsync(Timeout);
            Assert.IsFalse(pending.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(5));
            var result = await pending.WaitAsync(Timeout);
            Assert.AreEqual(SnapshotJobStatus.Queued, result.Status);
        }
        finally
        {
            release.TrySetResult();
            await held.WaitAsync(Timeout);
        }
    }

    [TestMethod]
    public async Task GateRejectsAPreviousTurnWhileTheNextTurnIsHeld()
    {
        var gate = new WriteGate();
        var first = gate.WaitAsync();
        await first;
        var second = gate.WaitAsync();
        Assert.IsTrue(gate.Owns(first));
        gate.Release();
        await second;
        Assert.IsFalse(gate.Owns(first));
        Assert.IsTrue(gate.Owns(second));
        gate.Release();
    }
}
