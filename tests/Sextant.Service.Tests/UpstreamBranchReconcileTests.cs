using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #338: the maintenance loop's upstream-branch reconcile retires non-default branch pointers whose branch no
/// longer exists on the remote, only on a complete listing and only after two consecutive confirmations, through the
/// audited retire path (actor <c>upstream_policy</c>, no retired head recorded).
/// </summary>
[TestClass]
public class UpstreamBranchReconcileTests
{
    private const string Repo = "https://github.com/acme/upstream";
    private const string OtherRepo = "https://github.com/acme/other";
    private static readonly DateTimeOffset ClockStart = new(2026, 3, 10, 0, 30, 0, TimeSpan.Zero);

    // ---- retirement ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task PointerAbsentOnTwoConsecutiveListings_IsRetired_WithoutARetiredHead()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main", "other");
        h.Start();

        var first = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.IsTrue(first.Enabled);
        Assert.AreEqual(1, first.Checked);
        Assert.AreEqual(1, first.AwaitingConfirmation);
        Assert.AreEqual(0, first.Retired);
        Assert.IsTrue(h.BranchExists(Repo, "gone"), "one listing is not enough");

        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var second = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(1, second.Retired);
        Assert.IsFalse(h.BranchExists(Repo, "gone"));
        Assert.IsTrue(h.BranchExists(Repo, "main"));
        Assert.IsNull(h.RetiredHead(Repo, "gone"), "the branch is gone from git too: nothing may resurrect it");
        var audit = h.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual("retired;source=upstream_policy", audit[0].Detail);
        Assert.AreEqual(AuditLogStore.HashActor("upstream_policy"), audit[0].Actor);
        Assert.AreEqual(1, h.Service.Metrics.Maintenance.UpstreamBranchesRetiredTotal);
    }

    [TestMethod]
    public async Task PointerPresentUpstream_IsKept_AndOneThatReappearsBetweenListingsStartsOver()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "kept", false, "c-kept");
        h.Seed(Repo, "flaky", false, "c-flaky");
        h.Start();

        h.Lister.Set(Repo, "main", "kept");
        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        h.Lister.Set(Repo, "main", "kept", "flaky");
        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        h.Lister.Set(Repo, "main", "kept");
        var third = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(0, third.Retired);
        Assert.AreEqual(1, third.AwaitingConfirmation, "absent once again, not twice in a row");
        Assert.IsTrue(h.BranchExists(Repo, "kept"));
        Assert.IsTrue(h.BranchExists(Repo, "flaky"));
    }

    [TestMethod]
    public async Task MovedPointer_StartsConfirmationOver()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();

        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Seed(Repo, "gone", false, "c-gone-2");
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var second = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(0, second.Retired);
        Assert.AreEqual(1, second.AwaitingConfirmation);
        Assert.IsTrue(h.BranchExists(Repo, "gone"));
    }

    [TestMethod]
    public async Task ListingFailure_Throw_OrEmptyListing_RetiresNothing_AndCountsAFailure()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Start();

        // Two prior confirmations would retire it, but none of these listings is trustworthy.
        var failures = 0;
        foreach (var mode in new[] { "null", "throw", "empty", "null" })
        {
            switch (mode)
            {
                case "null": h.Lister.Fail(Repo); break;
                case "throw": h.Lister.Throw(Repo); break;
                default: h.Lister.Set(Repo); break;
            }
            h.Clock.Advance(TimeSpan.FromMinutes(61));
            var report = await h.Service.ReconcileUpstreamBranchesAsync();
            Assert.AreEqual(1, report.Checked, mode);
            Assert.AreEqual(1, report.ListingFailures, mode);
            Assert.AreEqual(0, report.Retired, mode);
            failures++;
        }

        Assert.IsTrue(h.BranchExists(Repo, "gone"));
        Assert.AreEqual(4, failures);
        Assert.AreEqual(0, h.Service.RecentAudit(action: AuditAction.Retire).Count);
    }

    [TestMethod]
    public async Task AFailedListingBetweenTwoGoodOnes_ResetsTheConfirmation()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Start();

        h.Lister.Set(Repo, "main");
        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Lister.Fail(Repo);
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Lister.Set(Repo, "main");
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var report = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(0, report.Retired);
        Assert.AreEqual(1, report.AwaitingConfirmation);
        Assert.IsTrue(h.BranchExists(Repo, "gone"));
    }

    [TestMethod]
    public async Task TwoAbsentListingsMoreThanThreeIntervalsApart_DoNotConfirm()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();

        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(60 * 3 + 1));
        var late = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.AreEqual(0, late.Retired);
        Assert.AreEqual(1, late.AwaitingConfirmation);

        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var next = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.AreEqual(1, next.Retired, "the late listing started a fresh confirmation");
    }

    [TestMethod]
    public async Task TwoAbsentListingsExactlyThreeIntervalsApart_Confirm()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();

        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(60 * 3));

        Assert.AreEqual(1, (await h.Service.ReconcileUpstreamBranchesAsync()).Retired);
    }

    [TestMethod]
    public async Task RefsPrefixedOrInvalidNames_AreNeverRetired()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "refs/heads/odd", false, "c-1");
        h.Seed(Repo, "bad~name", false, "c-2");
        h.Seed(Repo, "feature..x", false, "c-3");
        h.Lister.Set(Repo, "main");
        h.Start();

        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var report = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(3, report.Unverifiable);
        Assert.AreEqual(0, report.Retired);
        Assert.AreEqual(0, report.Attempted);
        Assert.IsTrue(h.BranchExists(Repo, "refs/heads/odd"));
    }

    [TestMethod]
    public void IsVerifiableBranchName_AcceptsPlainNames_AndRefusesRefShapes()
    {
        foreach (var ok in new[] { "main", "feature/x", "release-1.2", "a.b" })
            Assert.IsTrue(RemoteBranchHeads.IsVerifiableBranchName(ok), ok);
        foreach (var bad in new[] { "refs/heads/x", "refs/tags/v1", "a b", "a:b", "a?b", "a*b", "a[b", "a\\b", "a..b", "a@{b", "a//b", "/a", "a/", "a.", "x.lock", "a/.b", "-a", "@", "" })
            Assert.IsFalse(RemoteBranchHeads.IsVerifiableBranchName(bad), bad);
    }

    [TestMethod]
    public async Task DefaultBranch_IsNeverRetired_EvenWhenAbsentUpstream()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "feature", false, "c-feature");
        h.Lister.Set(Repo, "feature");
        h.Start();

        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var report = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(0, report.Retired);
        Assert.IsTrue(h.BranchExists(Repo, "main"));
    }

    [TestMethod]
    public async Task BusyBranches_QueuedJobOrHandover_AreSkipped()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "queued", false, "c-q");
        h.Seed(Repo, "handed-over", false, "c-h");
        h.Seed(Repo, "idle", false, "c-i");
        h.Lister.Set(Repo, "main");
        h.Start();
        var jobs = new SnapshotJobStore(h.Db.GetConnection());
        jobs.EnsureJob("identity-queued", Repo + ".git", "c-q2", "queued");
        jobs.RecordHandover(RemoteUrlIdentity.Normalize(Repo), "handed-over", "c-x", "c-h", "c-y");

        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var report = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(2, report.Busy);
        Assert.AreEqual(1, report.Retired);
        Assert.IsTrue(h.BranchExists(Repo, "queued"));
        Assert.IsTrue(h.BranchExists(Repo, "handed-over"));
        Assert.IsFalse(h.BranchExists(Repo, "idle"));
    }

    [TestMethod]
    public async Task PointerMovedBetweenListingAndRetirement_IsSkipped()
    {
        await using var h = Harness.Create(gatedWorker: true);
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();
        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));

        // A production of another repository holds the single writer, so the retirement queues behind it.
        var blocker = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = OtherRepo, CommitSha = "b-1" });
        await WaitUntilAsync(() => h.Worker.Calls == 1);
        var admitted = h.Service.WriteAdmissions;
        var pass = h.Service.ReconcileUpstreamBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteAdmissions > admitted);
        h.Execute("UPDATE branches SET updated_at = updated_at + 1 WHERE name = 'gone';");
        h.Worker.Gate.TrySetResult();
        await blocker.WaitAsync(TimeSpan.FromSeconds(10));
        var report = await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, report.Attempted);
        Assert.AreEqual(0, report.Retired);
        Assert.AreEqual(1, report.Skipped);
        Assert.IsTrue(h.BranchExists(Repo, "gone"));
        Assert.AreEqual("pointer_moved;source=upstream_policy", h.Service.RecentAudit(action: AuditAction.Retire)[0].Detail);
    }

    [TestMethod]
    public async Task EnsureOfTheBranchBetweenListingAndRetirement_SkipsTheRetirement()
    {
        await using var h = Harness.Create(gatedWorker: true);
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();
        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));

        var blocker = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = OtherRepo, CommitSha = "b-1" });
        await WaitUntilAsync(() => h.Worker.Calls == 1);
        var admitted = h.Service.WriteAdmissions;
        var pass = h.Service.ReconcileUpstreamBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteAdmissions > admitted);
        // The branch was re-created upstream and its ensure completed after the confirming listing started, leaving the
        // pointer's snapshot and updated_at as they were.
        var at = h.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        h.Execute($"INSERT INTO snapshot_jobs (identity_hash, repository_url, commit_sha, branch_name, status, created_at, updated_at, completed_at) " +
                  $"VALUES ('recreated', '{Repo}', 'c-new', 'gone', 'complete', {at}, {at}, {at});");
        h.Worker.Gate.TrySetResult();
        await blocker.WaitAsync(TimeSpan.FromSeconds(10));
        var report = await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, report.Attempted);
        Assert.AreEqual(0, report.Retired);
        Assert.AreEqual(1, report.Skipped);
        Assert.IsTrue(h.BranchExists(Repo, "gone"));
        Assert.AreEqual("upstream_activity;source=upstream_policy", h.Service.RecentAudit(action: AuditAction.Retire)[0].Detail);
    }

    [TestMethod]
    public async Task OldJobActivity_BeforeTheListing_DoesNotBlockTheRetirement()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();
        var before = h.Clock.GetUtcNow().ToUnixTimeMilliseconds() - 1000;
        h.Execute($"INSERT INTO snapshot_jobs (identity_hash, repository_url, commit_sha, branch_name, status, created_at, updated_at, completed_at) " +
                  $"VALUES ('old', '{Repo}', 'c-gone', 'gone', 'complete', {before}, {before}, {before});");

        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));

        Assert.AreEqual(1, (await h.Service.ReconcileUpstreamBranchesAsync()).Retired);
    }

    [TestMethod]
    public async Task UpstreamRetire_OfABranchThatWentAway_NeverClearsItsRetiredHead()
    {
        await using var h = Harness.Create(gatedWorker: true);
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();
        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));

        var blocker = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = OtherRepo, CommitSha = "b-1" });
        await WaitUntilAsync(() => h.Worker.Calls == 1);
        var admitted = h.Service.WriteAdmissions;
        var pass = h.Service.ReconcileUpstreamBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteAdmissions > admitted);
        // The branch row went away (idle-retired, head recorded) before the queued retirement's turn.
        h.Execute("DELETE FROM branches WHERE name = 'gone';");
        h.Execute($"INSERT INTO retired_branch_heads (repository_id, branch_name, commit_sha, retired_at, cause) VALUES ({h.RepositoryId(Repo)}, 'gone', 'c-gone', 1, 'idle');");
        h.Worker.Gate.TrySetResult();
        await blocker.WaitAsync(TimeSpan.FromSeconds(10));
        var report = await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, report.Retired);
        Assert.AreEqual("c-gone", h.RetiredHead(Repo, "gone"));
        Assert.AreEqual("absent;source=upstream_policy", h.Service.RecentAudit(action: AuditAction.Retire)[0].Detail);
    }

    // ---- tick deadline -------------------------------------------------------------------------------

    [TestMethod]
    public async Task ListingThatOutlastsTheStepBudget_IsDeferred_RetiresNothing_AndIsNotListedAgainWhileRunning()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Start();
        h.Service.MaintenanceStepLimit = TimeSpan.FromMinutes(10);
        using var release = new ManualResetEventSlim(false);
        h.Lister.Block(Repo, release, "main");

        var pass = h.Service.ReconcileUpstreamBranchesAsync();
        await WaitUntilAsync(() => h.Lister.Calls.Count == 1);
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        var report = await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, report.Checked);
        Assert.AreEqual(0, report.Retired);
        Assert.AreEqual(0, report.AwaitingConfirmation);
        Assert.AreEqual(1, h.Service.CollectMetrics().Maintenance.MaintenanceDeferrals["upstream_branch_check"]);

        var again = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.AreEqual(0, again.Checked, "its listing is still running");
        Assert.AreEqual(1, h.Lister.Calls.Count);

        release.Set();
        await WaitUntilAsync(() => !h.Service.IsListingUpstreamForTesting(Repo));
        var third = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.AreEqual(1, third.Checked);
        Assert.AreEqual(2, h.Lister.Calls.Count);
    }

    [TestMethod]
    public async Task RetireWaitBeyondTheStepBudget_IsDeferred_AndAppliesWhenItsTurnComes()
    {
        await using var h = Harness.Create(gatedWorker: true);
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();
        h.Service.MaintenanceStepLimit = TimeSpan.FromMinutes(10);
        await h.Service.ReconcileUpstreamBranchesAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(61));

        var blocker = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = OtherRepo, CommitSha = "b-1" });
        await WaitUntilAsync(() => h.Worker.Calls == 1);
        var admitted = h.Service.WriteAdmissions;
        var pass = h.Service.ReconcileUpstreamBranchesAsync();
        await WaitUntilAsync(() => h.Service.WriteAdmissions > admitted);
        // The step's timer is created just after the retirement is admitted, so keep the clock moving until it fires.
        while (!pass.IsCompleted)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(11));
            await Task.Delay(20);
        }
        var report = await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, report.Attempted);
        Assert.AreEqual(1, report.Deferred);
        Assert.AreEqual(0, report.Skipped);
        Assert.AreEqual(1, h.Service.CollectMetrics().Maintenance.MaintenanceDeferrals["upstream_branch_retire"]);

        h.Worker.Gate.TrySetResult();
        await blocker.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !h.BranchExists(Repo, "gone"));
    }

    [TestMethod]
    public async Task ListingsRunInParallel_NoMoreThanThreeAtOnce()
    {
        await using var h = Harness.Create();
        var urls = Enumerable.Range(0, 6).Select(i => $"https://github.com/acme/par{i}").ToArray();
        foreach (var url in urls)
        {
            h.Seed(url, "main", true, "m-" + url[^1]);
            h.Seed(url, "side", false, "s-" + url[^1]);
            h.Lister.Set(url, "main", "side");
        }
        h.Lister.DelayMs = 100;
        h.Start();

        var report = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(6, report.Checked);
        Assert.AreEqual(3, h.Lister.MaxConcurrent);
    }

    [TestMethod]
    public async Task TwoUrlSpellingsOfOneRepository_AreListedOnce()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo + ".git", "side", false, "c-side");
        h.Seed(Repo.ToUpperInvariant().Replace("HTTPS", "https"), "other", false, "c-other");
        h.Lister.Set(Repo, "main", "side", "other");
        h.Lister.Set(Repo + ".git", "main", "side", "other");
        h.Lister.Set(Repo.ToUpperInvariant().Replace("HTTPS", "https"), "main", "side", "other");
        h.Start();

        var report = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.AreEqual(1, report.Due);
        Assert.AreEqual(1, report.Checked);
        Assert.AreEqual(1, h.Lister.Calls.Count);
    }

    // ---- caps, interval, switch ---------------------------------------------------------------------

    [TestMethod]
    public async Task PerTickRepositoryCap_AndPerRepositoryInterval_AreRespected()
    {
        await using var h = Harness.Create(policy => policy with { BranchUpstreamCheckMaxRepositoriesPerTick = 1 });
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "a", false, "c-a");
        h.Seed(OtherRepo, "main", true, "o-main");
        h.Seed(OtherRepo, "b", false, "o-b");
        h.Lister.Set(Repo, "main", "a");
        h.Lister.Set(OtherRepo, "main", "b");
        h.Start();

        var first = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.AreEqual(2, first.Due);
        Assert.AreEqual(1, first.Checked);
        var second = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.AreEqual(1, second.Checked, "the repository not yet checked goes next, the checked one is not due");
        Assert.AreEqual(2, h.Lister.Calls.Count);
        CollectionAssert.AreEquivalent(new[] { Repo, OtherRepo }, h.Lister.Calls.ToArray());

        var third = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.AreEqual(0, third.Due, "both were checked within the interval");

        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var fourth = await h.Service.ReconcileUpstreamBranchesAsync();
        Assert.AreEqual(2, fourth.Due);
        Assert.AreEqual(1, fourth.Checked);
        Assert.AreEqual(Repo == h.Lister.Calls[0] ? Repo : OtherRepo, h.Lister.Calls[2], "least recently checked first");
    }

    [TestMethod]
    public async Task ZeroMinutes_DisablesTheStep()
    {
        await using var h = Harness.Create(policy => policy with { BranchUpstreamCheckMinutes = 0 });
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();

        var report = await h.Service.ReconcileUpstreamBranchesAsync();

        Assert.IsFalse(report.Enabled);
        Assert.AreEqual(0, h.Lister.Calls.Count);
    }

    [TestMethod]
    public async Task NoLister_DisablesTheStep()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", true, "c-main");
        h.Start(withLister: false);

        Assert.IsFalse((await h.Service.ReconcileUpstreamBranchesAsync()).Enabled);
    }

    [TestMethod]
    public async Task MaintenanceTick_RunsTheStep_AfterIdleBranches_AndRecordsMetrics()
    {
        await using var h = Harness.Create(policy => policy with { BranchIdleRetireDays = 0, RetentionHourUtc = 5 });
        h.Seed(Repo, "main", true, "c-main");
        h.Seed(Repo, "gone", false, "c-gone");
        h.Lister.Set(Repo, "main");
        h.Start();

        var first = await h.Service.RunMaintenanceTickAsync();
        Assert.IsNotNull(first.UpstreamBranches);
        Assert.AreEqual(1, first.UpstreamBranches.Checked);
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var second = await h.Service.RunMaintenanceTickAsync();

        Assert.AreEqual(1, second.UpstreamBranches!.Retired);
        var metrics = h.Service.Metrics.Maintenance;
        Assert.AreEqual(2, metrics.UpstreamRepositoriesCheckedTotal);
        Assert.AreEqual(1, metrics.UpstreamBranchesRetiredTotal);
        StringAssert.Contains(Sextant.Service.Observability.PrometheusExposition.Render(h.Service.CollectMetrics()),
            "sextant_upstream_branches_retired_total 1");
    }

    // ---- parsing and settings ------------------------------------------------------------------------

    [TestMethod]
    public void ParseHeads_ReadsBranchNamesWithSlashes_AndIgnoresOtherRefs()
    {
        var sha = new string('a', 40);
        var output = $"{sha}\trefs/heads/main\n{sha}\trefs/heads/feature/x/y\n{sha}\trefs/tags/v1\r\n{sha}\trefs/pull/5/head\n";

        var heads = RemoteBranchHeads.Parse(output)!;

        CollectionAssert.AreEquivalent(new[] { "main", "feature/x/y" }, heads.ToArray());
    }

    [TestMethod]
    public void ParseHeads_EmptyOutputIsEmptySet_AndDamagedOutputIsNull()
    {
        Assert.AreEqual(0, RemoteBranchHeads.Parse("")!.Count);
        Assert.IsNull(RemoteBranchHeads.Parse("fatal: something broke\n"));
        Assert.IsNull(RemoteBranchHeads.Parse(new string('a', 40) + "\trefs/heads/bad name\n"));
        Assert.IsNull(RemoteBranchHeads.Parse("zzzz\trefs/heads/main\n"));
    }

    [TestMethod]
    public void ParseMaintenance_UpstreamSettings_DefaultClampAndDisable()
    {
        var d = ServiceOptions.ParseMaintenance(null, null, null, null, null);
        Assert.AreEqual(60, d.BranchUpstreamCheckMinutes);
        Assert.AreEqual(10, d.BranchUpstreamCheckMaxRepositoriesPerTick);

        var off = ServiceOptions.ParseMaintenance(null, null, null, null, null, upstreamCheckMinutes: "0", upstreamCheckMaxRepositories: "3");
        Assert.AreEqual(0, off.BranchUpstreamCheckMinutes);
        Assert.AreEqual(3, off.BranchUpstreamCheckMaxRepositoriesPerTick);

        var big = ServiceOptions.ParseMaintenance(null, null, null, null, null, upstreamCheckMinutes: "99999999", upstreamCheckMaxRepositories: "99999");
        Assert.AreEqual(MaintenancePolicy.MaxBranchUpstreamCheckMinutes, big.BranchUpstreamCheckMinutes);
        Assert.AreEqual(MaintenancePolicy.MaxBranchUpstreamCheckMaxRepositoriesPerTick, big.BranchUpstreamCheckMaxRepositoriesPerTick);

        var bad = ServiceOptions.ParseMaintenance(null, null, null, null, null, upstreamCheckMinutes: "x", upstreamCheckMaxRepositories: "0");
        Assert.AreEqual(60, bad.BranchUpstreamCheckMinutes);
        Assert.AreEqual(10, bad.BranchUpstreamCheckMaxRepositoriesPerTick);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10);
        }
    }

    private sealed class FakeLister : IRemoteBranchLister
    {
        private readonly Dictionary<string, Func<IReadOnlySet<string>?>> _answers = new(StringComparer.Ordinal);
        private int _running;
        public List<string> Calls { get; } = [];
        public int DelayMs { get; set; }
        public int MaxConcurrent { get; private set; }

        public void Block(string url, ManualResetEventSlim release, params string[] branches) => _answers[url] = () =>
        {
            release.Wait(TimeSpan.FromSeconds(20));
            return branches.ToHashSet();
        };

        public void Set(string url, params string[] branches) => _answers[url] = () => branches.ToHashSet();
        public void Fail(string url) => _answers[url] = () => null;
        public void Throw(string url) => _answers[url] = () => throw new InvalidOperationException("boom");

        public IReadOnlySet<string>? ListBranches(string repositoryRemoteUrl)
        {
            lock (Calls)
            {
                Calls.Add(repositoryRemoteUrl);
                MaxConcurrent = Math.Max(MaxConcurrent, ++_running);
            }
            try
            {
                if (DelayMs > 0)
                    Thread.Sleep(DelayMs);
                return _answers.TryGetValue(repositoryRemoteUrl, out var answer) ? answer() : null;
            }
            finally
            {
                lock (Calls)
                    _running--;
            }
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
        public FakeLister Lister { get; } = new();

        public static Harness Create(Func<MaintenancePolicy, MaintenancePolicy>? configure = null, bool gatedWorker = false)
        {
            var h = new Harness { _gatedWorker = gatedWorker };
            h.Db = new IndexDatabase(h._dbPath);
            h.Db.RunMigrations();
            h.Clock = new ManualClock(ClockStart);
            var policy = new MaintenancePolicy { Interval = TimeSpan.Zero, BranchIdleRetireDays = 0 };
            h._options = ServiceTestFixtures.NewOptions(h._dbPath) with { Maintenance = configure?.Invoke(policy) ?? policy };
            return h;
        }

        public long Seed(string url, string branch, bool isDefault, string commit)
        {
            var snapshot = ServiceTestFixtures.PublishComplete(
                Db, new EnsureSnapshotRequest { RepositoryRemoteUrl = url, CommitSha = commit }, recordCommit: true);
            var movedAt = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            var store = new SnapshotStore(Db.GetConnection());
            var repoId = store.EnsureRepository(url, movedAt);
            var branchId = store.EnsureBranch(repoId, branch, isDefault, movedAt);
            store.SetBranchPointer(branchId, snapshot, movedAt);
            return snapshot;
        }

        public void Start(bool withLister = true)
        {
            Worker = new FakeSnapshotWorker(Db) { UseGate = _gatedWorker };
            Service = SnapshotService.Start(_options, Worker, Db, timeProvider: Clock, remoteBranches: withLister ? Lister : null);
        }

        public long RepositoryId(string url)
        {
            using var conn = Open();
            return new SnapshotStore(conn).GetRepositoryId(url)!.Value;
        }

        public string? RetiredHead(string url, string name)
        {
            using var conn = Open();
            var store = new SnapshotStore(conn);
            return store.GetRepositoryId(url) is long repoId ? store.GetRetiredHead(repoId, name) : null;
        }

        public bool BranchExists(string url, string name)
        {
            using var conn = Open();
            var store = new SnapshotStore(conn);
            return store.GetRepositoryId(url) is long repoId && store.GetBranch(repoId, name) is not null;
        }

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
