using Sextant.Service.Observability;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #333: maintenance writes were starved by continuous indexing. The write gate was one FIFO semaphore and every
/// production took its turn at admission, so an idle-branch retire (and the scheduled retention) queued behind every
/// production admitted before it, for hours, while the maintenance loop awaited it unbounded. The gate now has a priority
/// lane for maintenance writes, and every maintenance step is bounded. Everything here is deterministic: productions are
/// held by a worker double, time comes from a <see cref="ManualClock"/>.
/// </summary>
[TestClass]
public class WriteGatePriorityTests
{
    private const string Repo = "https://github.com/acme/idle";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset ClockStart = new(2026, 3, 10, 0, 30, 0, TimeSpan.Zero);

    // ---- the gate ------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Gate_ANormalTurnIsFifo_AndAPriorityTurnTakesTheNextTurnAfterTheHolder()
    {
        var gate = new WriteGate();
        await gate.WaitAsync();
        var order = new List<string>();
        var a = Take(gate, "a", order, priority: false);
        var b = Take(gate, "b", order, priority: false);
        var p1 = Take(gate, "p1", order, priority: true);
        var p2 = Take(gate, "p2", order, priority: true);

        gate.Release();
        await Task.WhenAll(a, b, p1, p2).WaitAsync(Settle);

        CollectionAssert.AreEqual(new[] { "p1", "p2", "a", "b" }, order, "priority first (FIFO inside it), then normal FIFO");
    }

    [TestMethod]
    public async Task Gate_ProductionsKeepFifoOrderAmongThemselves()
    {
        var gate = new WriteGate();
        await gate.WaitAsync();
        var order = new List<string>();
        var turns = Enumerable.Range(0, 20).Select(i => Take(gate, $"n{i}", order, priority: false)).ToArray();

        gate.Release();
        await Task.WhenAll(turns).WaitAsync(Settle);

        CollectionAssert.AreEqual(Enumerable.Range(0, 20).Select(i => $"n{i}").ToArray(), order);
    }

    [TestMethod]
    public async Task Gate_AFreeGateIsTakenAtOnce_AndNeverGrantedTwice()
    {
        var gate = new WriteGate();
        Assert.AreEqual(1, gate.CurrentCount);
        await gate.WaitAsync(priority: true);
        Assert.AreEqual(0, gate.CurrentCount);
        var next = gate.WaitAsync();
        Assert.IsFalse(next.IsCompleted);
        gate.Release();
        await next.WaitAsync(Settle);
        gate.Release();
        Assert.AreEqual(1, gate.CurrentCount);
        Assert.ThrowsExactly<SemaphoreFullException>(gate.Release);
    }

    [TestMethod]
    public async Task Gate_PriorityIsBounded_ANormalWaiterGetsATurnAfterTheBurstBudget()
    {
        var clock = new ManualClock();
        var gate = new WriteGate(clock, TimeSpan.FromSeconds(30));
        await gate.WaitAsync(priority: true);
        var order = new List<string>();
        var normal = Take(gate, "normal", order, priority: false, autoRelease: false);
        var p1 = Take(gate, "p1", order, priority: true, autoRelease: false);
        var p2 = Take(gate, "p2", order, priority: true, autoRelease: false);

        clock.Advance(TimeSpan.FromSeconds(31));
        gate.Release();
        await Settled(order, 1);
        Assert.AreEqual("normal", order[0], "the priority holder used the burst budget while a production waited");

        gate.Release();
        await Task.WhenAll(normal, p1).WaitAsync(Settle);
        Assert.AreEqual("p1", order[1], "priority resumes after the production's turn");
        gate.Release();
        await p2.WaitAsync(Settle);
        gate.Release();
    }

    [TestMethod]
    public async Task Gate_APriorityBurstWithNoProductionWaiting_NeverSpendsTheBudget()
    {
        var clock = new ManualClock();
        var gate = new WriteGate(clock, TimeSpan.FromSeconds(30));
        await gate.WaitAsync(priority: true);
        clock.Advance(TimeSpan.FromMinutes(5));
        var p = gate.WaitAsync(priority: true);
        gate.Release();
        await p.WaitAsync(Settle);
        var order = new List<string>();
        var normal = Take(gate, "normal", order, priority: false, autoRelease: false);
        var p2 = Take(gate, "p2", order, priority: true, autoRelease: false);
        gate.Release();
        await Settled(order, 1);
        Assert.AreEqual("p2", order[0], "no production was waiting while the long priority turn ran, so nothing was starved");
        gate.Release();
        await Task.WhenAll(normal, p2).WaitAsync(Settle);
        gate.Release();
    }

    [TestMethod]
    public async Task Gate_CancellingAQueuedTurnRemovesIt_AndAGrantedTurnStaysTheCallers()
    {
        var gate = new WriteGate();
        await gate.WaitAsync();
        using var cts = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(cts.Token, priority: true);
        var order = new List<string>();
        var normal = Take(gate, "normal", order, priority: false, autoRelease: false);
        Assert.AreEqual(1, gate.PriorityWaiters);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        Assert.AreEqual(0, gate.PriorityWaiters);

        gate.Release();
        await normal.WaitAsync(Settle);
        using var late = new CancellationTokenSource();
        late.Cancel();
        Assert.AreEqual(1, order.Count, "cancelling after the grant changes nothing");
        gate.Release();
        Assert.AreEqual(1, gate.CurrentCount);
        await Assert.ThrowsAsync<OperationCanceledException>(() => gate.WaitAsync(late.Token));
        Assert.AreEqual(1, gate.CurrentCount, "a pre-cancelled turn never takes the gate");
    }

    // ---- the service ---------------------------------------------------------------------------------

    [TestMethod]
    public async Task UnderAStreamOfQueuedProductions_AnIdleRetireTakesTheNextTurnAfterTheHolder()
    {
        await using var h = await Harness.StartAsync();
        h.Seed("main", isDefault: true, "commit-main", TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", TimeSpan.FromDays(10));
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");
        var queued = Enumerable.Range(1, 5).Select(h.Produce).ToList();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.NormalWaiters == 5);

        var pass = h.Service.RetireIdleBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters == 1);
        h.Worker.Release("p0");
        var report = await pass.WaitAsync(Settle);

        Assert.AreEqual(1, report.Retired, "the retire got the turn right after the holder");
        await h.Worker.WaitForStartAsync("p1");
        Assert.AreEqual(1, h.Worker.RetiresAuditedWhenStarted["p1"], "the first queued production started only after the retire's turn");
        CollectionAssert.AreEqual(new[] { "p0", "p1" }, h.Worker.StartOrder);

        h.Worker.ReleaseAll();
        await Task.WhenAll(queued.Append(holder)).WaitAsync(Settle);
        CollectionAssert.AreEqual(new[] { "p0", "p1", "p2", "p3", "p4", "p5" }, h.Worker.StartOrder, "productions keep FIFO order");
        Assert.IsTrue(h.Service.CollectMetrics().Maintenance.MaintenanceWriterTurns >= 1);
    }

    [TestMethod]
    public async Task ProductionsStillProgressBetweenMaintenanceTurns()
    {
        await using var h = await Harness.StartAsync();
        h.Seed("main", isDefault: true, "commit-main", TimeSpan.FromDays(1));
        for (var i = 1; i <= 3; i++)
            h.Seed($"feature-{i}", isDefault: false, $"commit-f{i}", TimeSpan.FromDays(10 + i));
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");
        var queued = h.Produce(1);
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.NormalWaiters == 1);

        // Priority turns that hold the gate for no time never spend the burst budget (the clock does not move), so all
        // three retirements run right after the holder; the production that was queued first starts only after them.
        var pass = h.Service.RetireIdleBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters == 3);
        h.Worker.Release("p0");
        await holder.WaitAsync(Settle);
        await h.Worker.WaitForStartAsync("p1");
        Assert.AreEqual(3, h.Worker.RetiresAuditedWhenStarted["p1"], "the three retirements took the turns before the production");
        h.Worker.Release("p1");
        await queued.WaitAsync(Settle);
        var report = await pass.WaitAsync(Settle);

        Assert.AreEqual(3, report.Retired);
        CollectionAssert.AreEqual(new[] { "p0", "p1" }, h.Worker.StartOrder);
    }

    // The writer-time bound itself: a priority turn that holds the gate past the burst budget while a production waits
    // hands the next turn to that production, then takes the priority lane again.
    [TestMethod]
    public async Task APriorityHoldPastTheBurstBudget_HandsTheNextTurnToAWaitingProduction()
    {
        await using var h = await Harness.StartAsync();
        var gate = h.Service.WriteGateForTesting;
        var order = new List<string>();
        await gate.WaitAsync(priority: true);
        var production = Take(gate, "production", order, priority: false, autoRelease: false);
        var next = Take(gate, "maintenance-2", order, priority: true, autoRelease: false);
        await WaitUntilAsync(() => gate.NormalWaiters == 1 && gate.PriorityWaiters == 1);

        h.Clock.Advance(WriteGate.DefaultPriorityBurstBudget + TimeSpan.FromSeconds(1));
        gate.Release();
        await Settled(order, 1);
        Assert.AreEqual("production", order[0]);
        gate.Release();
        await Task.WhenAll(production, next).WaitAsync(Settle);
        CollectionAssert.AreEqual(new[] { "production", "maintenance-2" }, order);
        gate.Release();
    }

    // Issue #333 round 2: productions of 420 s back to back for one simulated hour with a retention backlog. The scheduled
    // pass holds the writer for one burst of B per production turn (its batches commit one by one inside the hold), every
    // production still progresses, and none waits longer than B plus one batch.
    [TestMethod]
    public async Task HeldBurst_GivesRetentionAboutBPerProductionTurn_AndEveryProductionStillProgresses()
    {
        await using var h = await Harness.StartAsync();
        var gate = h.Service.WriteGateForTesting;
        var burst = TimeSpan.FromSeconds(120);
        var batch = TimeSpan.FromSeconds(2);
        var production = TimeSpan.FromSeconds(420);
        var timer = new SnapshotService.RetentionPassTimer { Priority = true, Burst = burst, Time = h.Clock };

        var stop = false;
        var retention = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                // One batch of work inside the hold; the clock moves by the batch's duration.
                await timer.WriteAsync(h.Service, () => { h.Clock.Advance(batch); return Task.FromResult(0); }, _ => { }, CancellationToken.None);
            }
            timer.EndTurn(h.Service);
        });

        var worstWait = TimeSpan.Zero;
        var completed = 0;
        var simulationStart = h.Clock.GetUtcNow();
        while (h.Clock.GetUtcNow() - simulationStart < TimeSpan.FromHours(1))
        {
            var asked = h.Clock.GetUtcNow();
            await gate.WaitAsync();
            var waited = h.Clock.GetUtcNow() - asked;
            if (waited > worstWait) worstWait = waited;
            h.Clock.Advance(production);
            completed++;
            // The retention task is parked on the priority lane before the production lets go, so who goes next is
            // decided by the gate alone, never by task scheduling.
            await WaitUntilAsync(() => gate.PriorityWaiters == 1 || completed == 0);
            gate.Release();
        }
        Volatile.Write(ref stop, true);
        await retention.WaitAsync(Settle);

        Assert.IsTrue(completed >= 6, $"productions keep progressing ({completed} in the simulated hour)");
        Assert.IsTrue(worstWait <= burst + batch, $"a production waits at most B plus one batch, waited {worstWait}");
        var held = timer.HeldTime;
        Assert.IsTrue(held >= burst * (completed - 1), $"retention held about B per production turn, held {held} over {completed} productions");
        Assert.IsTrue(held <= (burst + batch) * (completed + 1), $"and no more than B plus one batch per turn, held {held}");
    }

    [TestMethod]
    public async Task HeldBurst_ReleasesAtB_AndDoesNotReleaseEarly()
    {
        await using var h = await Harness.StartAsync();
        var gate = h.Service.WriteGateForTesting;
        var timer = new SnapshotService.RetentionPassTimer { Priority = true, Burst = TimeSpan.FromSeconds(10), Time = h.Clock };

        for (var i = 0; i < 4; i++)
        {
            await timer.WriteAsync(h.Service, () => { h.Clock.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult(0); }, _ => { }, CancellationToken.None);
            Assert.AreEqual(0, gate.CurrentCount, $"still held after {2 * (i + 1)} s");
            Assert.AreEqual(TimeSpan.FromSeconds(2 * (i + 1)), timer.BudgetElapsed());
        }
        await timer.WriteAsync(h.Service, () => { h.Clock.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult(0); }, _ => { }, CancellationToken.None);
        Assert.AreEqual(1, gate.CurrentCount, "released at B");
        Assert.AreEqual(TimeSpan.FromSeconds(10), timer.BudgetElapsed());

        // The wait for the next turn is not spent.
        await gate.WaitAsync();
        var next = timer.WriteAsync(h.Service, () => Task.FromResult(0), _ => { }, CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        gate.Release();
        await next.WaitAsync(Settle);
        Assert.AreEqual(TimeSpan.FromSeconds(10), timer.BudgetElapsed(), "waiting for a turn costs no budget");

        // A failing write ends the turn.
        await Assert.ThrowsAsync<InvalidOperationException>(() => timer.WriteAsync<int>(
            h.Service, () => throw new InvalidOperationException("boom"), _ => { }, CancellationToken.None));
        Assert.AreEqual(1, gate.CurrentCount, "a failure releases the gate");
    }

    [TestMethod]
    public async Task APassStarvedOfItsFirstTurn_StaysDue_AndTheNextTickRuns()
    {
        await using var h = await Harness.StartAsync(NoLoop() with { BranchIdleRetireDays = 0, RetentionHourUtc = 0 });
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");

        var first = h.Service.RunMaintenanceTickAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters >= 1);
        h.Clock.Advance(h.Service.MaintenanceStepLimitForTesting + TimeSpan.FromSeconds(1));
        var starved = await first.WaitAsync(Settle);

        Assert.AreEqual("ran", starved.Retention);
        Assert.AreEqual(0, starved.RetentionReport!.Batches, "it never got the writer");
        Assert.IsTrue(starved.RetentionReport.MoreRemaining);

        h.Worker.ReleaseAll();
        await holder.WaitAsync(Settle);
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.CurrentCount == 1);
        var second = await h.Service.RunMaintenanceTickAsync().WaitAsync(Settle);
        Assert.AreEqual("ran", second.Retention, "the day stayed open, so the next tick runs the pass");
    }

    [TestMethod]
    public async Task AProductionLongerThanTheTickDeadline_AcrossManyTicks_KeepsRetentionDue_AndItRunsOnceTheWriterFrees()
    {
        await using var h = await Harness.StartAsync(NoLoop() with { BranchIdleRetireDays = 0, RetentionHourUtc = 0 });
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");

        for (var i = 0; i < SnapshotService.MaxScheduledNoProgressContinues + 3; i++)
        {
            var tick = h.Service.RunMaintenanceTickAsync();
            await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters >= 1);
            h.Clock.Advance(h.Service.MaintenanceStepLimitForTesting + TimeSpan.FromSeconds(1));
            var starved = await tick.WaitAsync(Settle);
            Assert.AreEqual("ran", starved.Retention, $"tick {i} still runs the pass");
            Assert.AreEqual("starved", starved.RetentionReport!.StopReason);
            Assert.AreEqual(0, starved.RetentionReport.Batches);
        }

        h.Worker.ReleaseAll();
        await holder.WaitAsync(Settle);
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.CurrentCount == 1);
        var next = await h.Service.RunMaintenanceTickAsync().WaitAsync(Settle);
        Assert.AreEqual("ran", next.Retention, "the day never closed, so the pass runs once the writer is free");
    }

    [TestMethod]
    public async Task AStarvedPass_NeverCountsTowardTheNoProgressCap()
    {
        await using var h = await Harness.StartAsync();
        var starved = new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = "starved", Batches = 0 };
        var noProgress = new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = "unit_budget", Batches = 1 };
        var service = h.Service;
        for (var i = 0; i < 20; i++)
            Assert.IsTrue(service.ScheduledPassContinues(starved));
        for (var i = 0; i < SnapshotService.MaxScheduledNoProgressContinues; i++)
            Assert.IsTrue(service.ScheduledPassContinues(noProgress));
        Assert.IsFalse(service.ScheduledPassContinues(noProgress), "real no-progress passes are still capped");
    }

    [TestMethod]
    public void TheShortReTick_WaitsAtLeastAsLongAsTheLastPassTook()
    {
        var quick = new MaintenanceTickReport { IdleBranches = new IdleBranchRetireReport { Enabled = true }, Retention = "ran", RetentionDurationMs = 1000 };
        var slow = quick with { RetentionDurationMs = 600_000 };
        Assert.AreEqual(TimeSpan.FromMinutes(5), SnapshotService.MinReTickDelay(TimeSpan.FromMinutes(5), quick));
        Assert.AreEqual(TimeSpan.FromMinutes(10), SnapshotService.MinReTickDelay(TimeSpan.FromMinutes(5), slow));
    }

    [TestMethod]
    public async Task AnIdleRetire_OvertakingAnEarlierAdmittedAdvance_IsBranchBusy()
    {
        await using var h = await Harness.StartAsync(NoLoop() with { ScheduledRetention = false });
        h.Seed("main", isDefault: true, "commit-main", TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", TimeSpan.FromDays(10));
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");

        var pass = h.Service.RetireIdleBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters == 1);
        var push = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Repo, CommitSha = "commit-feature-2", BranchName = "feature", ExpectedHeadCommit = "commit-feature"
        });
        h.Worker.ReleaseAll();
        var report = await pass.WaitAsync(Settle);
        await Task.WhenAll(holder, push).WaitAsync(Settle);

        Assert.AreEqual(0, report.Retired, "the push admitted before the retire applied keeps its branch");
        Assert.IsTrue(h.BranchExists("feature"));
    }

    [TestMethod]
    public async Task ASecondTick_DoesNotQueueAnotherRetireForABranchStillWaiting()
    {
        await using var h = await Harness.StartAsync(NoLoop() with { ScheduledRetention = false });
        h.Seed("main", isDefault: true, "commit-main", TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", TimeSpan.FromDays(10));
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");

        var first = h.Service.RunMaintenanceTickAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters == 1);
        h.Clock.Advance(h.Service.MaintenanceStepLimitForTesting + TimeSpan.FromSeconds(1));
        var report1 = await first.WaitAsync(Settle);
        Assert.AreEqual(1, report1.IdleBranches!.Deferred);
        Assert.IsTrue(h.Service.HasPendingIdleRetire(Repo, "feature"));

        var report2 = await h.Service.RunMaintenanceTickAsync().WaitAsync(Settle);
        Assert.AreEqual(0, report2.IdleBranches!.Attempted, "the queued retirement is not repeated");
        Assert.AreEqual(1, report2.IdleBranches.Busy);
        Assert.AreEqual(1, h.Service.WriteGateForTesting.PriorityWaiters, "still exactly one queued retirement");

        h.Worker.ReleaseAll();
        await holder.WaitAsync(Settle);
        await WaitUntilAsync(() => !h.BranchExists("feature"));
        var maintenance = h.Service.CollectMetrics().Maintenance;
        Assert.AreEqual(1, maintenance.IdleBranchesRetiredTotal, "a late-applied retirement is counted");
    }

    [TestMethod]
    public async Task TheTickDeadline_IsSharedByTheSteps_NotStackedPerStep()
    {
        await using var h = await Harness.StartAsync(NoLoop() with { ScheduledRetention = false, TickDeadlineSeconds = 100 });
        h.Seed("main", isDefault: true, "commit-main", TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", TimeSpan.FromDays(10));
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");

        var tick = h.Service.RunMaintenanceTickAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters == 1);
        h.Clock.Advance(TimeSpan.FromSeconds(99));
        Assert.IsFalse(tick.IsCompleted, "inside the deadline the step still waits");
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        var report = await tick.WaitAsync(Settle);
        Assert.AreEqual(1, report.IdleBranches!.Deferred);

        h.Worker.ReleaseAll();
        await holder.WaitAsync(Settle);
    }

    [TestMethod]
    public void AStarvedOrProgressingPass_DecidesTheShortReTick()
    {
        var progressed = new MaintenanceTickReport { IdleBranches = new IdleBranchRetireReport { Enabled = true }, Retention = "ran", ContinueSoon = true };
        var idle = new MaintenanceTickReport { IdleBranches = new IdleBranchRetireReport { Enabled = true }, Retention = "not_due" };
        Assert.IsTrue(SnapshotService.ShouldTickSoon(progressed));
        Assert.IsFalse(SnapshotService.ShouldTickSoon(idle));
    }

    [TestMethod]
    public void TheNewOptions_ParseWithDefaultsAndClamps()
    {
        var d = ServiceOptions.ParseMaintenance(null, null, null, null, null);
        Assert.AreEqual(120, d.RetentionBurstSeconds);
        Assert.AreEqual(5, d.RetentionContinueMinutes);
        Assert.AreEqual(900, d.TickDeadlineSeconds);
        var set = ServiceOptions.ParseMaintenance(null, null, null, null, null, null, null, null, "30", "0", "60");
        Assert.AreEqual(30, set.RetentionBurstSeconds);
        Assert.AreEqual(0, set.RetentionContinueMinutes);
        Assert.AreEqual(60, set.TickDeadlineSeconds);
        var big = ServiceOptions.ParseMaintenance(null, null, null, null, null, null, null, null, "999999", "999999", "999999");
        Assert.AreEqual(MaintenancePolicy.MaxRetentionBurstSeconds, big.RetentionBurstSeconds);
        Assert.AreEqual(MaintenancePolicy.MaxRetentionContinueMinutes, big.RetentionContinueMinutes);
        Assert.AreEqual(MaintenancePolicy.MaxTickDeadlineSeconds, big.TickDeadlineSeconds);
        var bad = ServiceOptions.ParseMaintenance(null, null, null, null, null, null, null, null, "x", "-1", "0");
        Assert.AreEqual(120, bad.RetentionBurstSeconds);
        Assert.AreEqual(5, bad.RetentionContinueMinutes);
        Assert.AreEqual(900, bad.TickDeadlineSeconds);
    }

    [TestMethod]
    public async Task ScheduledRetention_MakesProgressOnTheTick_WhileProductionsAreQueued()
    {
        await using var h = await Harness.StartAsync(NoLoop() with { BranchIdleRetireDays = 0, RetentionHourUtc = 0 });
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");
        var queued = Enumerable.Range(1, 3).Select(h.Produce).ToList();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.NormalWaiters == 3);

        var tick = h.Service.RunMaintenanceTickAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters >= 1);
        h.Worker.Release("p0");
        h.Worker.ReleaseAll();
        var ran = await tick.WaitAsync(Settle);

        Assert.AreEqual("ran", ran.Retention, "the tick is not skipped because indexing is busy");
        Assert.AreEqual(0, h.Service.CollectMetrics().Maintenance.ScheduledRetentionSkippedBusy);
        h.Worker.ReleaseAll();
        await Task.WhenAll(queued.Append(holder)).WaitAsync(Settle);
    }

    [TestMethod]
    public async Task TheLoopStep_IsBounded_ItIsDeferredWithMetrics_AndLaterStepsStillRun()
    {
        await using var h = await Harness.StartAsync(NoLoop() with { ScheduledRetention = false });
        h.Seed("main", isDefault: true, "commit-main", TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", TimeSpan.FromDays(10));
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");

        var tick = h.Service.RunMaintenanceTickAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters == 1);
        Assert.IsFalse(tick.IsCompleted, "the step waits for its turn within the bound");
        h.Clock.Advance(h.Service.MaintenanceStepLimitForTesting + TimeSpan.FromSeconds(1));
        var report = await tick.WaitAsync(Settle);

        Assert.AreEqual(1, report.IdleBranches!.Deferred);
        Assert.AreEqual("disabled", report.Retention, "the steps after the deferred one still ran");
        var maintenance = h.Service.CollectMetrics().Maintenance;
        Assert.IsTrue(maintenance.MaintenanceDeferrals["idle_branch_retire"] >= 1);
        StringAssert.Contains(PrometheusExposition.Render(h.Service.CollectMetrics()),
            "sextant_maintenance_deferrals_total{step=\"idle_branch_retire\"} ");

        // The retirement is service-owned: it still applies when the writer frees up.
        h.Worker.ReleaseAll();
        await holder.WaitAsync(Settle);
        await WaitUntilAsync(() => !h.BranchExists("feature"));
    }

    [TestMethod]
    public async Task APriorityTurn_SurvivesTheCallersCancellation_AndTheNextTurnsStillRun()
    {
        await using var h = await Harness.StartAsync(NoLoop() with { RepositoryReclaimDays = 14 });
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");
        using var cts = new CancellationTokenSource();

        var pass = h.Service.ReclaimInactiveRepositoriesAsync(cts.Token);
        var queued = h.Produce(1);
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.NormalWaiters == 1);
        cts.Cancel();
        try { await pass.WaitAsync(Settle); } catch (OperationCanceledException) { }

        Assert.AreEqual(0, h.Service.WriteGateForTesting.PriorityWaiters, "a cancelled maintenance turn leaves the lane");
        h.Worker.Release("p0");
        await h.Worker.WaitForStartAsync("p1");
        h.Worker.ReleaseAll();
        await Task.WhenAll(holder, queued).WaitAsync(Settle);
        Assert.AreEqual(1, h.Service.WriteGateForTesting.CurrentCount);
    }

    [TestMethod]
    public async Task APriorityRetire_QueuedAtShutdown_IsDrainedOrRequeuedWithoutHangingDispose()
    {
        var h = await Harness.StartAsync();
        h.Seed("main", isDefault: true, "commit-main", TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", TimeSpan.FromDays(10));
        var holder = h.Produce(0);
        await h.Worker.WaitForStartAsync("p0");
        var pass = h.Service.RetireIdleBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteGateForTesting.PriorityWaiters == 1);

        h.Worker.ReleaseAll();
        var disposing = Task.Run(h.Service.Dispose);
        await disposing.WaitAsync(Settle);
        try { await pass.WaitAsync(Settle); } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        try { await holder.WaitAsync(Settle); } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        await h.DisposeAsync();
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static MaintenancePolicy NoLoop() => new() { Interval = TimeSpan.Zero };

    private static Task Take(WriteGate gate, string name, List<string> order, bool priority, bool autoRelease = true) =>
        gate.WaitAsync(priority: priority).ContinueWith(_ =>
        {
            lock (order)
                order.Add(name);
            if (autoRelease)
                gate.Release();
        }, TaskScheduler.Default);

    private static async Task Settled(List<string> order, int count)
    {
        await WaitUntilAsync(() =>
        {
            lock (order)
                return order.Count >= count;
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Settle;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition did not become true in time.");
            await Task.Delay(5);
        }
    }

    // A production double: it records the order productions start in and holds each one until the test releases it.
    private sealed class ControlledWorker(IndexDatabase db) : ISnapshotWorker
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, TaskCompletionSource> _holds = [];
        private readonly List<string> _started = [];

        public Func<int> RetireAudits { get; set; } = () => 0;
        public Dictionary<string, int> RetiresAuditedWhenStarted { get; } = [];

        public string[] StartOrder
        {
            get
            {
                lock (_lock)
                    return [.. _started];
            }
        }

        private TaskCompletionSource Hold(string key)
        {
            lock (_lock)
            {
                if (!_holds.TryGetValue(key, out var hold))
                    _holds[key] = hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return hold;
            }
        }

        public void Release(string key) => Hold(key).TrySetResult();

        public void ReleaseAll()
        {
            lock (_lock)
            {
                foreach (var hold in _holds.Values)
                    hold.TrySetResult();
                _releaseAll = true;
            }
        }

        private bool _releaseAll;

        public Task WaitForStartAsync(string key) => WaitUntilAsync(() =>
        {
            lock (_lock)
                return _started.Contains(key);
        });

        public async Task<SnapshotWorkResult> ProduceAsync(
            EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
        {
            var key = request.CommitSha;
            var audits = RetireAudits();
            Task gate;
            lock (_lock)
            {
                _started.Add(key);
                RetiresAuditedWhenStarted[key] = audits;
                gate = _releaseAll ? Task.CompletedTask : Hold(key).Task;
            }
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(db, request));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _dbPath = ServiceTestFixtures.NewDbPath();

        public IndexDatabase Db { get; private set; } = null!;
        public ManualClock Clock { get; private set; } = null!;
        public ControlledWorker Worker { get; private set; } = null!;
        public SnapshotService Service { get; private set; } = null!;

        public static Task<Harness> StartAsync(MaintenancePolicy? maintenance = null)
        {
            var h = new Harness();
            h.Db = new IndexDatabase(h._dbPath);
            h.Db.RunMigrations();
            h.Clock = new ManualClock(ClockStart);
            var options = ServiceTestFixtures.NewOptions(h._dbPath) with { Maintenance = maintenance ?? NoLoop() };
            h.Worker = new ControlledWorker(h.Db);
            h.Service = SnapshotService.Start(options, h.Worker, h.Db, timeProvider: h.Clock);
            h.Worker.RetireAudits = () => h.Service.RecentAudit(action: AuditAction.Retire).Count;
            return Task.FromResult(h);
        }

        // An ensure of one repository (distinct per index), commit "p<n>"; it holds the writer until released.
        public Task Produce(int n) => Service.EnsureSnapshotAsync(
            new EnsureSnapshotRequest { RepositoryRemoteUrl = $"https://github.com/acme/p{n}", CommitSha = $"p{n}" });

        public void Seed(string branch, bool isDefault, string commit, TimeSpan idleFor)
        {
            var snapshot = ServiceTestFixtures.PublishComplete(
                Db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = commit }, recordCommit: true);
            var movedAt = (Clock.GetUtcNow() - idleFor).ToUnixTimeMilliseconds();
            var store = new SnapshotStore(Db.GetConnection());
            var repoId = store.EnsureRepository(Repo, movedAt);
            var branchId = store.EnsureBranch(repoId, branch, isDefault, movedAt);
            store.SetBranchPointer(branchId, snapshot, movedAt);
        }

        public bool BranchExists(string name)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString());
            conn.Open();
            var store = new SnapshotStore(conn);
            return store.GetRepositoryId(Repo) is long repoId && store.GetBranch(repoId, name) is not null;
        }

        public ValueTask DisposeAsync()
        {
            Worker.ReleaseAll();
            Service.Dispose();
            SqliteTestDatabase.Delete(_dbPath, Db);
            return ValueTask.CompletedTask;
        }
    }
}
