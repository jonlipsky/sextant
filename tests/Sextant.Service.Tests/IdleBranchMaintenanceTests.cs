using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #299: the service-owned maintenance loop. Its idle-branch rule retires non-default branch pointers that have
/// not moved for more than N days through the audited retire path (never a default branch, compare-and-swap on the
/// pointer read, never a branch with pending work, bounded per tick), and its daily scheduled retention runs the bounded
/// retention pass at the configured UTC hour unless indexing holds the writer. Time comes from a <see cref="ManualClock"/>.
/// </summary>
[TestClass]
public class IdleBranchMaintenanceTests
{
    private const string Repo = "https://github.com/acme/idle";
    private const string BlockerRepo = "https://github.com/acme/blocker";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    // 2026-03-10 00:30 UTC: before the default 03:00 retention hour.
    private static readonly DateTimeOffset ClockStart = new(2026, 3, 10, 0, 30, 0, TimeSpan.Zero);

    // ---- idle-branch rule ----------------------------------------------------------------------------

    [TestMethod]
    public async Task IdleNonDefaultBranch_PastTheThreshold_IsRetired_AndAudited()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(30));
        var featureSnapshot = h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(8));
        h.Start();

        var report = await h.Service.RetireIdleBranchesAsync();

        Assert.IsTrue(report.Enabled);
        Assert.AreEqual(1, report.Retired);
        Assert.IsFalse(h.BranchExists("feature"));
        Assert.IsTrue(h.BranchExists("main"), "a default branch is never retired, however idle");
        Assert.IsTrue(h.SnapshotExists(featureSnapshot), "retiring deletes only the pointer row, never a snapshot");

        var audit = h.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Complete, audit[0].Outcome);
        Assert.AreEqual(Repo, audit[0].RepositoryScope);
        Assert.AreEqual("retired;source=idle_policy", audit[0].Detail);
        Assert.AreEqual(AuditLogStore.HashActor("idle_policy"), audit[0].Actor);
    }

    [TestMethod]
    public async Task BranchMovedWithinTheThreshold_IsKept()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(6));
        h.Start();

        var report = await h.Service.RetireIdleBranchesAsync();

        Assert.AreEqual(0, report.Idle);
        Assert.AreEqual(0, report.Retired);
        Assert.IsTrue(h.BranchExists("feature"));
    }

    [TestMethod]
    public async Task ZeroDays_DisablesTheRule()
    {
        await using var h = Harness.Create(maintenance: NoLoop() with { BranchIdleRetireDays = 0 });
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(300));
        h.Start();

        var report = await h.Service.RetireIdleBranchesAsync();

        Assert.IsFalse(report.Enabled);
        Assert.IsTrue(h.BranchExists("feature"));
        Assert.AreEqual(0, h.Service.RecentAudit(action: AuditAction.Retire).Count);
    }

    [TestMethod]
    public async Task BranchWithAQueuedJob_OrAPendingHandover_IsKept()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("queued", isDefault: false, "commit-q", idleFor: TimeSpan.FromDays(20));
        h.Seed("handed-over", isDefault: false, "commit-h", idleFor: TimeSpan.FromDays(20));
        h.Seed("idle", isDefault: false, "commit-i", idleFor: TimeSpan.FromDays(20));
        var jobs = new SnapshotJobStore(h.Db.GetConnection());
        jobs.EnsureJob("identity-queued", Repo + ".git", "commit-q2", "queued");
        jobs.RecordHandover(RemoteUrlIdentity.Normalize(Repo), "handed-over", "commit-x", "commit-h", "commit-y");
        h.Start();

        var report = await h.Service.RetireIdleBranchesAsync();

        Assert.AreEqual(3, report.Idle);
        Assert.AreEqual(2, report.Busy);
        Assert.AreEqual(1, report.Retired);
        Assert.IsTrue(h.BranchExists("queued"), "a queued job names the branch (matched across URL spellings)");
        Assert.IsTrue(h.BranchExists("handed-over"), "a pending handover is unfinished work on the branch");
        Assert.IsFalse(h.BranchExists("idle"));
    }

    [TestMethod]
    public async Task PerPassCap_RetiresTheLeastRecentlyMovedFirst_AndTheRestNextPass()
    {
        await using var h = Harness.Create(maintenance: NoLoop() with { BranchIdleRetireMaxPerPass = 2 });
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        for (var i = 1; i <= 5; i++)
            h.Seed($"agent-{i}", isDefault: false, $"commit-{i}", idleFor: TimeSpan.FromDays(10 + i));
        h.Start();

        var first = await h.Service.RetireIdleBranchesAsync();

        Assert.AreEqual(5, first.Idle);
        Assert.AreEqual(2, first.Attempted);
        Assert.AreEqual(2, first.Retired);
        Assert.IsFalse(h.BranchExists("agent-5"));
        Assert.IsFalse(h.BranchExists("agent-4"));
        Assert.IsTrue(h.BranchExists("agent-3"));

        var second = await h.Service.RetireIdleBranchesAsync();
        Assert.AreEqual(2, second.Retired);
        Assert.IsTrue(h.BranchExists("agent-1"));
        Assert.IsFalse(h.BranchExists("agent-2"));
    }

    [TestMethod]
    public async Task PointerMovedBetweenSelectionAndRetirement_IsSkipped()
    {
        await using var h = Harness.Create(maintenance: NoLoop(), gatedWorker: true);
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(10));
        h.Start();

        // A production of another repository holds the single writer, so the retirement queues behind it.
        var blocker = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = BlockerRepo, CommitSha = "b-1" });
        await WaitUntilAsync(() => h.Worker.Calls == 1);
        var admitted = h.Service.WriteAdmissions;
        var pass = h.Service.RetireIdleBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteAdmissions > admitted);

        // The branch pointer moves after the rule read it (a push that landed meanwhile).
        h.Execute("UPDATE branches SET updated_at = updated_at + 1 WHERE name = 'feature';");
        h.Worker.Gate.TrySetResult();
        await blocker.WaitAsync(Settle);
        var report = await pass.WaitAsync(Settle);

        Assert.AreEqual(1, report.Attempted);
        Assert.AreEqual(0, report.Retired);
        Assert.AreEqual(1, report.Skipped);
        Assert.IsTrue(h.BranchExists("feature"));
        var audit = h.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(AuditOutcome.Denied, audit[0].Outcome);
        Assert.AreEqual("pointer_moved;source=idle_policy", audit[0].Detail);
    }

    [TestMethod]
    public async Task RetiredBranch_IsRecreatedByItsNextPush_WhichExpectsTheRetiredHead()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(10));
        var next = h.Publish("commit-feature-2");
        h.Start();
        Assert.AreEqual(1, (await h.Service.RetireIdleBranchesAsync()).Retired);
        Assert.AreEqual("commit-feature", h.RetiredHead("feature"), "the retired head is recorded with the retirement");

        // The branch still exists in git, so its next push sends before = the retired head, exactly as
        // on-repository-change.yaml does (expected_head_commit = before).
        var result = await h.Service.EnsureSnapshotAsync(Push("commit-feature-2", expected: "commit-feature")).WaitAsync(Settle);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.IsTrue(result.BranchAdvanced, "the push re-created the branch");
        var branch = h.Branch("feature")!;
        Assert.IsFalse(branch.IsDefault);
        Assert.AreEqual(next, branch.SnapshotId);
        Assert.IsNull(h.RetiredHead("feature"), "re-creating the branch consumes its retired head");
    }

    [TestMethod]
    public async Task RetiredBranch_PushExpectingAnotherCommit_IsStillRefused()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(10));
        h.Publish("commit-feature-2");
        h.Start();
        Assert.AreEqual(1, (await h.Service.RetireIdleBranchesAsync()).Retired);

        var stale = await h.Service.EnsureSnapshotAsync(Push("commit-feature-2", expected: "commit-older")).WaitAsync(Settle);

        Assert.AreNotEqual(true, stale.BranchAdvanced);
        Assert.IsFalse(h.BranchExists("feature"), "a stale or reordered push still cannot create the branch");
        Assert.AreEqual("commit-feature", h.RetiredHead("feature"), "the refused push leaves the retired head in place");
    }

    [TestMethod]
    public async Task RetiredBranch_DeletedUpstream_IsNotResurrected()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(10));
        h.Start();
        Assert.AreEqual(1, (await h.Service.RetireIdleBranchesAsync()).Retired);

        // The branch is deleted upstream: the delete push retires it under the CAS on its before (the retired head).
        var delete = await h.Service.RetireBranchAsync(
            new RetireBranchRequest { Repository = Repo, Branch = "feature", ExpectedHeadCommit = "commit-feature" });
        Assert.IsFalse(delete.Retired, "already absent");
        Assert.IsNull(delete.Reason);
        Assert.IsNull(h.RetiredHead("feature"), "the delete clears the retired head");
        Assert.AreEqual("absent;retired_head_cleared", h.Service.RecentAudit(action: AuditAction.Retire)[0].Detail);

        // A reconcile or a redelivered push that still expects the old head no longer re-creates it.
        var replay = await h.Service.EnsureSnapshotAsync(Push("commit-feature", expected: "commit-feature")).WaitAsync(Settle);

        Assert.AreNotEqual(true, replay.BranchAdvanced);
        Assert.IsFalse(h.BranchExists("feature"));
    }

    [TestMethod]
    public async Task ManualRetire_RecordsNoRetiredHead()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(10));
        h.Start();

        // A branch-delete push: the branch is gone from git too, so nothing may re-create it from its old head.
        var retired = await h.Service.RetireBranchAsync(
            new RetireBranchRequest { Repository = Repo, Branch = "feature", ExpectedHeadCommit = "commit-feature" });

        Assert.IsTrue(retired.Retired);
        Assert.IsNull(h.RetiredHead("feature"));
        var replay = await h.Service.EnsureSnapshotAsync(Push("commit-feature", expected: "commit-feature")).WaitAsync(Settle);
        Assert.AreNotEqual(true, replay.BranchAdvanced, "a stale push after a delete stays refused, as before");
        Assert.IsFalse(h.BranchExists("feature"));
    }

    [TestMethod]
    public async Task RetiredHeads_ArePrunedAfterTheirWindow()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(10));
        h.Start();
        var first = await h.Service.RetireIdleBranchesAsync();
        Assert.AreEqual(1, first.Retired);
        Assert.AreEqual(0, first.RetiredHeadsPruned);

        h.Clock.Advance(MaintenancePolicy.RetiredHeadRetention - TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, (await h.Service.RetireIdleBranchesAsync()).RetiredHeadsPruned);
        Assert.IsNotNull(h.RetiredHead("feature"));

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(1, (await h.Service.RetireIdleBranchesAsync()).RetiredHeadsPruned);
        Assert.IsNull(h.RetiredHead("feature"));
    }

    [TestMethod]
    public async Task BranchWithNoKnownHeadCommit_IsNeverRetired()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("commitless", isDefault: false, "commit-x", idleFor: TimeSpan.FromDays(10), recordCommit: false);
        h.Start();

        var report = await h.Service.RetireIdleBranchesAsync();

        Assert.AreEqual(0, report.Idle, "its next push could not be matched to a retired head");
        Assert.IsTrue(h.BranchExists("commitless"));
    }

    [TestMethod]
    public async Task SnapshotSharedWithARetainedBranch_SurvivesRetireAndRetention()
    {
        await using var h = Harness.Create(maintenance: NoLoop());
        var shared = h.Seed("main", isDefault: true, "commit-shared", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-shared", idleFor: TimeSpan.FromDays(10));
        h.Start();

        Assert.AreEqual(1, (await h.Service.RetireIdleBranchesAsync()).Retired);
        await h.Service.RunRetentionAsync(execute: true);

        Assert.IsTrue(h.SnapshotExists(shared), "main still points at the snapshot the retired branch shared");
        Assert.AreEqual(shared, h.Branch("main")!.SnapshotId);
    }

    // ---- maintenance loop ----------------------------------------------------------------------------

    [TestMethod]
    public async Task Loop_TicksEveryInterval_AndRetiresIdleBranches()
    {
        await using var h = Harness.Create(maintenance: Loop() with { ScheduledRetention = false });
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(6) + TimeSpan.FromMinutes(90));
        h.Start();

        await h.TickAsync(); // 6 d 2.5 h idle: one day short.
        Assert.IsTrue(h.BranchExists("feature"));

        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.IsTrue(await h.Service.WaitForMaintenanceTickAsync(Settle));
        Assert.IsFalse(h.BranchExists("feature"));

        var metrics = h.Service.CollectMetrics().Maintenance;
        Assert.AreEqual(2, metrics.Ticks);
        Assert.AreEqual(1, metrics.IdleBranchesRetiredTotal);
        Assert.AreEqual(1, metrics.LastIdleBranchesRetired);
        Assert.AreEqual("disabled", h.Service.LastMaintenanceTick!.Retention);
    }

    [TestMethod]
    public async Task ZeroInterval_StartsNoLoop()
    {
        await using var h = Harness.Create(maintenance: Loop() with { Interval = TimeSpan.Zero });
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(30));
        h.Start();

        h.Clock.Advance(TimeSpan.FromDays(2));

        Assert.IsFalse(await h.Service.WaitForMaintenanceTickAsync(TimeSpan.FromMilliseconds(300)));
        Assert.IsTrue(h.BranchExists("feature"));
        Assert.AreEqual(0, h.Service.CollectMetrics().Maintenance.Ticks);
    }

    [TestMethod]
    public async Task FailedTick_IsLogged_AndTheLoopCarriesOn()
    {
        await using var h = Harness.Create(maintenance: Loop() with { ScheduledRetention = false });
        h.Seed("main", isDefault: true, "commit-main", idleFor: TimeSpan.FromDays(1));
        h.Seed("feature", isDefault: false, "commit-feature", idleFor: TimeSpan.FromDays(30));
        var calls = 0;
        h.Start(service => service.BeforeMaintenanceTick = () =>
            Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask);

        await h.TickAsync();
        Assert.IsTrue(h.BranchExists("feature"), "the failed tick did nothing");
        Assert.AreEqual(1, h.Service.CollectMetrics().Maintenance.Failures);

        await h.TickAsync();
        Assert.IsFalse(h.BranchExists("feature"), "the next tick ran normally");
        Assert.AreEqual(1, h.Service.CollectMetrics().Maintenance.Ticks);
    }

    [TestMethod]
    public async Task ScheduledRetention_RunsOncePerDay_FromTheConfiguredUtcHour()
    {
        await using var h = Harness.Create(maintenance: Loop() with { BranchIdleRetireDays = 0, RetentionHourUtc = 3 });
        h.Start();

        await h.TickAsync(); // 01:30
        await h.TickAsync(); // 02:30
        Assert.AreEqual(0, h.Service.CollectMetrics().Maintenance.ScheduledRetentionRuns);
        Assert.AreEqual("not_due", h.Service.LastMaintenanceTick!.Retention);

        await h.TickAsync(); // 03:30
        Assert.AreEqual("ran", h.Service.LastMaintenanceTick!.Retention);
        Assert.IsFalse(h.Service.LastMaintenanceTick.RetentionReport!.DryRun);
        Assert.AreEqual(1, h.Service.CollectMetrics().Maintenance.ScheduledRetentionRuns);

        for (var i = 0; i < 23; i++) // 04:30 … 02:30 next day: already ran today, then not yet due.
            await h.TickAsync();
        Assert.AreEqual(1, h.Service.CollectMetrics().Maintenance.ScheduledRetentionRuns);

        await h.TickAsync(); // 03:30 next day
        Assert.AreEqual(2, h.Service.CollectMetrics().Maintenance.ScheduledRetentionRuns);

        var audit = h.Service.RecentAudit(action: AuditAction.Retention);
        Assert.IsTrue(audit.Count > 0);
        Assert.IsTrue(audit.All(e => e.Actor == AuditLogStore.HashActor("retention_schedule")
                                     && e.Detail == "execute_batch;source=retention_schedule"));
    }

    [TestMethod]
    public async Task ScheduledRetention_AlreadyRunToday_IsNotRepeatedAfterARestart()
    {
        await using var h = Harness.Create(
            maintenance: NoLoop() with { BranchIdleRetireDays = 0 },
            // The audit log's timestamps are wall-clock, so this test's clock is too.
            start: DateTimeOffset.UtcNow, retentionHour: 0);
        h.Start();
        Assert.AreEqual("ran", (await h.Service.RunMaintenanceTickAsync()).Retention);

        h.Restart();

        Assert.AreEqual("not_due", (await h.Service.RunMaintenanceTickAsync()).Retention,
            "the audit log shows today's scheduled pass already ran");
    }

    [TestMethod]
    public async Task ScheduledRetention_WhileIndexingHoldsTheWriter_IsSkippedAndRetried()
    {
        await using var h = Harness.Create(
            maintenance: NoLoop() with { BranchIdleRetireDays = 0, RetentionHourUtc = 0 }, gatedWorker: true);
        h.Start();
        var blocker = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = BlockerRepo, CommitSha = "b-1" });
        await WaitUntilAsync(() => h.Worker.Calls == 1);

        var busy = await h.Service.RunMaintenanceTickAsync().WaitAsync(Settle);
        Assert.AreEqual("busy", busy.Retention);
        Assert.AreEqual(1, h.Service.CollectMetrics().Maintenance.ScheduledRetentionSkippedBusy);
        Assert.AreEqual(0, h.Service.RecentAudit(action: AuditAction.Retention).Count, "nothing ran");

        h.Worker.Gate.TrySetResult();
        await blocker.WaitAsync(Settle);

        var retried = await h.Service.RunMaintenanceTickAsync().WaitAsync(Settle);
        Assert.AreEqual("ran", retried.Retention);
    }

    // ---- options -------------------------------------------------------------------------------------

    [TestMethod]
    public void ParseMaintenance_Defaults()
    {
        var policy = ServiceOptions.ParseMaintenance(null, null, null, null, null);

        Assert.AreEqual(TimeSpan.FromMinutes(60), policy.Interval);
        Assert.AreEqual(7, policy.BranchIdleRetireDays);
        Assert.AreEqual(20, policy.BranchIdleRetireMaxPerPass);
        Assert.IsTrue(policy.ScheduledRetention);
        Assert.AreEqual(3, policy.RetentionHourUtc);
    }

    [TestMethod]
    public void ParseMaintenance_ZeroDisables_AndInvalidValuesKeepTheDefaults()
    {
        var off = ServiceOptions.ParseMaintenance("0", "0", "5", false, "22");
        Assert.AreEqual(TimeSpan.Zero, off.Interval);
        Assert.AreEqual(0, off.BranchIdleRetireDays);
        Assert.AreEqual(5, off.BranchIdleRetireMaxPerPass);
        Assert.IsFalse(off.ScheduledRetention);
        Assert.AreEqual(22, off.RetentionHourUtc);

        var bad = ServiceOptions.ParseMaintenance("-1", "x", "0", null, "24");
        Assert.AreEqual(TimeSpan.FromMinutes(60), bad.Interval);
        Assert.AreEqual(7, bad.BranchIdleRetireDays);
        Assert.AreEqual(20, bad.BranchIdleRetireMaxPerPass);
        Assert.AreEqual(3, bad.RetentionHourUtc);

        Assert.AreEqual(TimeSpan.FromMinutes(MaintenancePolicy.MaxIntervalMinutes),
            ServiceOptions.ParseMaintenance("100000", null, null, null, null).Interval, "a period PeriodicTimer accepts");
        Assert.AreEqual(MaintenancePolicy.MaxBranchIdleRetireMaxPerPass,
            ServiceOptions.ParseMaintenance(null, null, "999999", null, null).BranchIdleRetireMaxPerPass);
    }

    // ---- harness -------------------------------------------------------------------------------------

    // A push's branch-advance ensure, as on-repository-change.yaml sends it (expected_head_commit = the push's before).
    private static EnsureSnapshotRequest Push(string commit, string expected) => new()
    {
        RepositoryRemoteUrl = Repo, CommitSha = commit, BranchName = "feature", ExpectedHeadCommit = expected
    };

    private static MaintenancePolicy NoLoop() => new() { Interval = TimeSpan.Zero };

    private static MaintenancePolicy Loop() => new() { Interval = TimeSpan.FromMinutes(60) };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Settle;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition did not become true in time.");
            await Task.Delay(10);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _dbPath = ServiceTestFixtures.NewDbPath();
        private ServiceOptions _options = null!;
        private bool _gatedWorker;

        public IndexDatabase Db { get; private set; } = null!;
        public ManualClock Clock { get; private set; } = null!;
        public FakeSnapshotWorker Worker { get; private set; } = null!;
        public SnapshotService Service { get; private set; } = null!;

        public static Harness Create(
            MaintenancePolicy maintenance, bool gatedWorker = false, DateTimeOffset? start = null, int? retentionHour = null)
        {
            var h = new Harness { _gatedWorker = gatedWorker };
            h.Db = new IndexDatabase(h._dbPath);
            h.Db.RunMigrations();
            h.Clock = new ManualClock(start ?? ClockStart);
            h._options = ServiceTestFixtures.NewOptions(h._dbPath) with
            {
                Maintenance = retentionHour is int hour ? maintenance with { RetentionHourUtc = hour } : maintenance
            };
            return h;
        }

        // A complete snapshot of `commit` and a branch pointing at it whose pointer last moved `idleFor` before the clock.
        public long Seed(string branch, bool isDefault, string commit, TimeSpan idleFor, bool recordCommit = true)
        {
            var snapshot = ServiceTestFixtures.PublishComplete(
                Db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = commit }, recordCommit: recordCommit);
            var movedAt = (Clock.GetUtcNow() - idleFor).ToUnixTimeMilliseconds();
            var store = new SnapshotStore(Db.GetConnection());
            var repoId = store.EnsureRepository(Repo, movedAt);
            var branchId = store.EnsureBranch(repoId, branch, isDefault, movedAt);
            store.SetBranchPointer(branchId, snapshot, movedAt);
            return snapshot;
        }

        // A complete snapshot of `commit` that no branch points at yet (a push's ensure then reuses it).
        public long Publish(string commit) => ServiceTestFixtures.PublishComplete(
            Db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = commit }, recordCommit: true);

        public string? RetiredHead(string name)
        {
            using var conn = Open();
            var store = new SnapshotStore(conn);
            return store.GetRepositoryId(Repo) is long repoId ? store.GetRetiredHead(repoId, name) : null;
        }

        public void Start(Action<SnapshotService>? configure = null)
        {
            Worker = new FakeSnapshotWorker(Db) { UseGate = _gatedWorker };
            Service = SnapshotService.Start(_options, Worker, Db, timeProvider: Clock);
            configure?.Invoke(Service);
        }

        public void Restart()
        {
            Service.Dispose();
            Start();
        }

        // Advances the clock one loop interval and waits for that tick.
        public async Task TickAsync()
        {
            Clock.Advance(_options.Maintenance.Interval);
            Assert.IsTrue(await Service.WaitForMaintenanceTickAsync(Settle), "the maintenance loop ticked");
        }

        public BranchRow? Branch(string name)
        {
            using var conn = Open();
            var store = new SnapshotStore(conn);
            return store.GetRepositoryId(Repo) is long repoId ? store.GetBranch(repoId, name) : null;
        }

        public bool BranchExists(string name) => Branch(name) is not null;

        public bool SnapshotExists(long snapshotId)
        {
            using var conn = Open();
            return new SnapshotStore(conn).GetById(snapshotId) is not null;
        }

        // A write through an independent connection, as another process would make it.
        public void Execute(string sql)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 5000;";
            cmd.ExecuteNonQuery();
            return conn;
        }

        public ValueTask DisposeAsync()
        {
            Worker?.Gate.TrySetResult();
            Service?.Dispose();
            SqliteTestDatabase.Delete(_dbPath, Db);
            return ValueTask.CompletedTask;
        }
    }
}
