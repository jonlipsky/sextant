using System.Net;
using Sextant.Service.Grants;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;
using static Sextant.Service.Tests.UserEnsureBoundsHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #203. A user caller's branch advance must target the remote's CURRENT <c>refs/heads/&lt;branch&gt;</c>
/// (the service asks the remote itself in clone mode, failing closed), the lookups are capped per tenant, and the
/// worker's snapshot context always carries the implicit-default decision explicitly. Application and assertion-less
/// callers, <c>branch_update: none</c> and locate mode are unchanged.
/// </summary>
[TestClass]
public class UserAdvanceHeadCheckTests
{
    private const string Ensure = "/control/ensure";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    // ==== HTTP: the user advance target ============================================================

    [TestMethod]
    public async Task UserCas_ToACommitThatIsNotTheRemoteHead_Is409_BeforeAnyJob_AndAudited()
    {
        var remote = new FakeRemote(head: "commit-w3");
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        await GrantAsync(host, Widgets);
        var branchesBefore = host.BranchRows();
        var headBefore = host.Service.ResolveBranchHead(Widgets, null)!;

        using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
            EnsureOf(Widgets, "commit-w2", """{"branch_name":"main","expected_head_commit":"commit-w1"}"""));

        await AssertRejectedAsync(response, HttpStatusCode.Conflict, BranchGuardReason.CommitNotRemoteHead);
        Assert.AreEqual(("main", Widgets), remote.LastHeadRequest);
        Assert.AreEqual(0L, host.JobRows());
        Assert.AreEqual(0, host.Worker.Calls);
        Assert.AreEqual(branchesBefore, host.BranchRows());
        Assert.AreEqual(headBefore.Snapshot.Id, host.Service.ResolveBranchHead(Widgets, null)!.Snapshot.Id, "the pointer did not move");
        var audit = host.Service.RecentAudit(action: AuditAction.Ensure).Single();
        Assert.AreEqual(AuditOutcome.Denied, audit.Outcome);
        StringAssert.StartsWith(audit.Detail, BranchGuardReason.CommitNotRemoteHead);
        Assert.AreEqual(RepositoryGrantKey.Of(Widgets), audit.RepositoryScope);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("throw")]
    public async Task UserCas_WhenTheRemoteHeadCannotBeDetermined_FailsClosed(string? head)
    {
        var remote = new FakeRemote(head: head);
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        await GrantAsync(host, Widgets);
        var headBefore = host.Service.ResolveBranchHead(Widgets, null)!;

        using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
            EnsureOf(Widgets, "commit-w2", """{"branch_name":"main","expected_head_commit":"commit-w1"}"""));

        await AssertRejectedAsync(response, HttpStatusCode.Conflict, BranchGuardReason.CommitNotRemoteHead);
        Assert.AreEqual(headBefore.Snapshot.Id, host.Service.ResolveBranchHead(Widgets, null)!.Snapshot.Id);
        Assert.AreEqual(0L, host.JobRows());
    }

    [TestMethod]
    public async Task UserCas_ToTheRemoteHead_AdvancesItsBranch()
    {
        var remote = new FakeRemote(head: "COMMIT-W2");
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        await GrantAsync(host, Widgets);

        var result = await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w2",
            """{"branch_name":"feature","expected_head_commit":""}""");

        Assert.IsTrue(result.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual("commit-w2", host.Service.ResolveBranchHead(Widgets, "feature")!.CommitSha);
        Assert.AreEqual(("feature", Widgets), remote.LastHeadRequest);
    }

    [TestMethod]
    public async Task UserBranchUpdateNone_IsNotChecked_AgainstTheRemoteHead_ButAgainstBranchReachability()
    {
        var remote = new FakeRemote(head: "unrelated", reachable: "true");
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        await GrantAsync(host, Widgets);

        await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w2", """{"branch_name":"refs/pull/1/head","branch_update":"none"}""");

        Assert.AreEqual(0, remote.HeadCalls);
        Assert.AreEqual(1, remote.ReachabilityCalls);
        Assert.AreEqual((Widgets, "commit-w2"), remote.LastReachabilityRequest);
    }

    // ==== HTTP: a user's pinned commit must be on a branch (issue #209) ============================

    [TestMethod]
    [DataRow("false")]
    [DataRow("throw")]
    public async Task UserBranchUpdateNone_ForACommitNotOnABranch_Is409_BeforeAnyJob_AndAudited(string reachable)
    {
        var remote = new FakeRemote(reachable: reachable);
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        await GrantAsync(host, Widgets);
        var branchesBefore = host.BranchRows();

        using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
            EnsureOf(Widgets, "forkcommit", """{"branch_update":"none"}"""));

        await AssertRejectedAsync(response, HttpStatusCode.Conflict, BranchGuardReason.CommitNotOnBranch);
        Assert.AreEqual(1, remote.ReachabilityCalls);
        Assert.AreEqual(0L, host.JobRows());
        Assert.AreEqual(0, host.Worker.Calls, "no evaluation of the unreachable commit");
        Assert.AreEqual(branchesBefore, host.BranchRows());
        var audit = host.Service.RecentAudit(action: AuditAction.Ensure).Single();
        Assert.AreEqual(AuditOutcome.Denied, audit.Outcome);
        StringAssert.StartsWith(audit.Detail, BranchGuardReason.CommitNotOnBranch);
        Assert.AreEqual(RepositoryGrantKey.Of(Widgets), audit.RepositoryScope);
    }

    [TestMethod]
    public async Task UserBranchUpdateNone_WithNoCommit_FailsClosed_WithoutAskingTheRemote()
    {
        var remote = new FakeRemote(reachable: "true");
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        await GrantAsync(host, Widgets);

        using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
            EnsureOf(Widgets, "  ", """{"branch_update":"none"}"""));

        await AssertRejectedAsync(response, HttpStatusCode.Conflict, BranchGuardReason.CommitNotOnBranch);
        Assert.AreEqual(0, remote.ReachabilityCalls);
        Assert.AreEqual(0L, host.JobRows());
    }

    [TestMethod]
    public async Task UserBranchUpdateNone_InLocateMode_WithNoChecker_IsUnchanged()
    {
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: new DefaultsOnly());
        await GrantAsync(host, Widgets);

        await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w2", """{"branch_update":"none"}""");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ApplicationAndAssertionLessNoneEnsure_AreNeverCheckedForBranchReachability(bool application)
    {
        var remote = new FakeRemote(reachable: "false");
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        var assertion = application ? host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow)) : null;

        await EnsureOkAsync(host, assertion, Widgets, "forkcommit", """{"branch_name":"refs/pull/1/head","branch_update":"none"}""");

        Assert.AreEqual(0, remote.ReachabilityCalls);
    }

    [TestMethod]
    public async Task UserCas_IsNotChecked_ForBranchReachability()
    {
        var remote = new FakeRemote(head: "commit-w2", reachable: "false");
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        await GrantAsync(host, Widgets);

        await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w2", """{"branch_name":"feature","expected_head_commit":""}""");

        Assert.AreEqual(0, remote.ReachabilityCalls, "the remote-head check already proves the commit is a branch head");
    }

    [TestMethod]
    public async Task BranchReachability_SharesInFlightCalls_RespectsTenantSlots_AndIsNeverRemembered()
    {
        var resolver = new FakeRemote(reachable: "true");
        resolver.Gate.Reset();
        var lookup = new RemoteDefaultBranchLookup(resolver, new ManualClock(), resolver, resolver);

        var first = lookup.IsCommitOnBranchAsync("https://github.com/acme/widgets", "ABC", "t");
        var second = lookup.IsCommitOnBranchAsync("https://github.com/acme/widgets.git", "abc", "t");
        resolver.Gate.Set();
        Assert.IsTrue(await first.WaitAsync(Wait));
        Assert.IsTrue(await second.WaitAsync(Wait));
        Assert.AreEqual(1, resolver.ReachabilityCalls, "concurrent checks shared one call");
        Assert.IsTrue(await lookup.IsCommitOnBranchAsync("https://github.com/acme/widgets", "abc", "t").WaitAsync(Wait));
        Assert.AreEqual(2, resolver.ReachabilityCalls, "a finished answer is checked again");

        resolver.Gate.Reset();
        var busy = Enumerable.Range(0, RemoteDefaultBranchLookup.MaxConcurrentLookups)
            .Select(i => lookup.IsCommitOnBranchAsync($"https://github.com/acme/a{i}", "abc", "t")).ToList();
        await Assert.ThrowsExactlyAsync<RemoteLookupBusyException>(
            () => lookup.IsCommitOnBranchAsync("https://github.com/acme/more", "abc", "t").WaitAsync(Wait));
        await Assert.ThrowsExactlyAsync<RemoteLookupBusyException>(
            () => lookup.ResolveBranchHeadAsync("https://github.com/acme/more", "main", "t").WaitAsync(Wait));
        resolver.Gate.Set();
        foreach (var task in busy)
            Assert.IsTrue(await task.WaitAsync(Wait));
    }

    [TestMethod]
    public async Task ACancelledLookup_KillsTheRemoteCall_AndFreesItsSlot_OnlyWhenEveryWaiterLeft()
    {
        var resolver = new FakeRemote(head: "abc", reachable: "true");
        resolver.Gate.Reset();
        var lookup = new RemoteDefaultBranchLookup(resolver, new ManualClock(), resolver, resolver);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        var a = lookup.IsCommitOnBranchAsync("https://github.com/acme/widgets", "abc", "t", first.Token);
        var b = lookup.IsCommitOnBranchAsync("https://github.com/acme/widgets", "abc", "t", second.Token);
        await resolver.WaitUntilStartedAsync();
        first.Cancel();
        Assert.IsFalse(resolver.ReachabilityToken!.Value.IsCancellationRequested, "another waiter still needs the call");
        second.Cancel();
        Assert.IsTrue(resolver.ReachabilityToken!.Value.IsCancellationRequested, "the last waiter left: the call is cancelled");
        Assert.IsFalse(await a.WaitAsync(Wait));
        Assert.IsFalse(await b.WaitAsync(Wait));

        var head = lookup.ResolveBranchHeadAsync("https://github.com/acme/widgets", "main", "t", first.Token);
        Assert.IsNull(await head.WaitAsync(Wait), "a cancelled head lookup answers null");
        Assert.IsTrue(resolver.HeadToken!.Value.IsCancellationRequested);

        resolver.Gate.Set();
        for (var i = 0; i <= RemoteDefaultBranchLookup.MaxConcurrentLookups; i++)
            Assert.IsTrue(await lookup.IsCommitOnBranchAsync($"https://github.com/acme/s{i}", "abc", "t").WaitAsync(Wait), "every slot was released");
    }

    [TestMethod]
    public async Task ACancelledLookup_IsNotJoinedByALaterCaller()
    {
        var resolver = new FakeRemote(reachable: "true");
        resolver.Gate.Reset();
        var lookup = new RemoteDefaultBranchLookup(resolver, new ManualClock(), resolver, resolver);
        using var cts = new CancellationTokenSource();

        var cancelled = lookup.IsCommitOnBranchAsync("https://github.com/acme/widgets", "abc", "t", cts.Token);
        await resolver.WaitUntilStartedAsync();
        cts.Cancel();
        var later = lookup.IsCommitOnBranchAsync("https://github.com/acme/widgets", "abc", "t");
        resolver.Gate.Set();

        Assert.IsFalse(await cancelled.WaitAsync(Wait));
        Assert.IsTrue(await later.WaitAsync(Wait), "the later caller got its own call, not the cancelled answer");
        Assert.AreEqual(2, resolver.ReachabilityCalls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UserEnsure_WhenEveryLookupSlotIsBusy_Is503LookupBusy_NotAVerdictOnTheCommit(bool pinned)
    {
        var remote = new FakeRemote(head: "commit-w2", reachable: "true");
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        await GrantAsync(host, Widgets);
        remote.Gate.Reset();
        string Options(int i) => pinned
            ? """{"branch_update":"none"}"""
            : $$"""{"branch_name":"b{{i}}","expected_head_commit":""}""";

        var running = Enumerable.Range(0, RemoteDefaultBranchLookup.MaxConcurrentLookups)
            .Select(i => host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
                EnsureOf(Widgets, $"commit-w2-{i}", Options(i))))
            .ToList();
        await remote.WaitUntilStartedAsync(RemoteDefaultBranchLookup.MaxConcurrentLookups);
        var jobsBefore = host.JobRows();

        using var busy = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
            EnsureOf(Widgets, "commit-w2-busy", Options(99)));

        await AssertRejectedAsync(busy, HttpStatusCode.ServiceUnavailable, BranchGuardReason.LookupBusy);
        Assert.IsTrue(busy.Headers.RetryAfter is not null, "a retryable refusal says when to retry");
        Assert.AreEqual(jobsBefore, host.JobRows(), "no job row for the refused ensure");
        Assert.IsTrue(host.Service.RecentAudit(action: AuditAction.Ensure)
            .Any(a => a.Outcome == AuditOutcome.Denied && a.Detail.StartsWith(BranchGuardReason.LookupBusy, StringComparison.Ordinal)));

        remote.Gate.Set();
        foreach (var task in running)
            (await task).Dispose();
    }

    [TestMethod]
    public async Task BranchReachability_WithoutAChecker_IsFalse()
    {
        var lookup = new RemoteDefaultBranchLookup(new FakeRemote());
        Assert.IsFalse(lookup.HasCommitBranchChecker);
        Assert.IsFalse(await lookup.IsCommitOnBranchAsync("https://github.com/acme/widgets", "abc").WaitAsync(Wait));
    }

    [TestMethod]
    public async Task UserCas_InLocateMode_WithNoHeadResolver_IsUnchanged()
    {
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: new DefaultsOnly());
        await GrantAsync(host, Widgets);

        var result = await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w2",
            """{"branch_name":"feature","expected_head_commit":""}""");

        Assert.IsTrue(result.GetProperty("branch_advanced").GetBoolean());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ApplicationAndAssertionLessEnsure_AreNeverCheckedAgainstTheRemote(bool application)
    {
        var remote = new FakeRemote(head: "unrelated");
        await using var host = await Harness.StartAsync(seed: Seed, remoteDefaults: remote);
        var assertion = application ? host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow)) : null;

        var result = await EnsureOkAsync(host, assertion, Widgets, "commit-w2",
            """{"branch_name":"feature","expected_head_commit":""}""");

        Assert.IsTrue(result.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual(0, remote.HeadCalls);
    }

    // ==== lookup: per-tenant slots, heads never remembered =========================================

    [TestMethod]
    public async Task OneTenantsBusyLookups_DoNotBlockAnotherTenant()
    {
        var resolver = new FakeRemote(branch: "main");
        resolver.Gate.Reset();
        var lookup = new RemoteDefaultBranchLookup(resolver);

        var busy = Enumerable.Range(0, RemoteDefaultBranchLookup.MaxConcurrentLookups)
            .Select(i => lookup.ResolveAsync($"https://github.com/acme/a{i}", "tenant-a"))
            .ToList();
        var sameTenant = lookup.ResolveAsync("https://github.com/acme/more", "tenant-a");
        var otherTenant = lookup.ResolveAsync("https://github.com/acme/b0", "tenant-b");

        Assert.IsNull(await sameTenant.WaitAsync(Wait), "the saturated tenant fails closed");
        resolver.Gate.Set();
        Assert.AreEqual("main", await otherTenant.WaitAsync(Wait), "another tenant still gets a slot");
        foreach (var task in busy)
            Assert.AreEqual("main", await task.WaitAsync(Wait));
    }

    [TestMethod]
    public async Task TheGlobalLookupCap_StillBoundsManyTenants()
    {
        var resolver = new FakeRemote(branch: "main");
        resolver.Gate.Reset();
        var lookup = new RemoteDefaultBranchLookup(resolver);

        var running = Enumerable.Range(0, RemoteDefaultBranchLookup.MaxConcurrentLookupsTotal)
            .Select(i => lookup.ResolveAsync($"https://github.com/acme/r{i}", $"tenant-{i}"))
            .ToList();
        var over = lookup.ResolveAsync("https://github.com/acme/over", "tenant-new");

        Assert.IsNull(await over.WaitAsync(Wait));
        resolver.Gate.Set();
        foreach (var task in running)
            await task.WaitAsync(Wait);
    }

    [TestMethod]
    public async Task BranchHeads_ShareInFlightCalls_ButAreNeverRemembered()
    {
        var resolver = new FakeRemote(head: "abc");
        resolver.Gate.Reset();
        var lookup = new RemoteDefaultBranchLookup(resolver, new ManualClock(), resolver);

        var first = lookup.ResolveBranchHeadAsync("https://github.com/acme/widgets", "main");
        var second = lookup.ResolveBranchHeadAsync("https://github.com/acme/widgets.git", "main");
        resolver.Gate.Set();
        Assert.AreEqual("abc", await first.WaitAsync(Wait));
        Assert.AreEqual("abc", await second.WaitAsync(Wait));
        Assert.AreEqual(1, resolver.HeadCalls, "concurrent lookups shared one call");

        Assert.AreEqual("abc", await lookup.ResolveBranchHeadAsync("https://github.com/acme/widgets", "main").WaitAsync(Wait));
        Assert.AreEqual(2, resolver.HeadCalls, "a finished head is looked up again: a push moves it");
    }

    [TestMethod]
    public async Task BranchHeads_WithoutAResolver_AreNull()
    {
        var lookup = new RemoteDefaultBranchLookup(new FakeRemote(branch: "main"));
        Assert.IsFalse(lookup.HasBranchHeadResolver);
        Assert.IsNull(await lookup.ResolveBranchHeadAsync("https://github.com/acme/widgets", "main").WaitAsync(Wait));
    }

    // ==== parsing ==================================================================================

    [TestMethod]
    [DataRow("0123456789012345678901234567890123456789\trefs/heads/main\n", "main", "0123456789012345678901234567890123456789")]
    [DataRow("0123456789012345678901234567890123456789\trefs/heads/main\r\n", "main", "0123456789012345678901234567890123456789")]
    [DataRow("", "main", null)]
    [DataRow("0123456789012345678901234567890123456789\trefs/heads/other\n", "main", null)]
    [DataRow("0123456789012345678901234567890123456789\trefs/pull/1/head\n", "main", null)]
    [DataRow("0123456789012345678901234567890123456789\trefs/heads/x/main\n", "main", null)]
    [DataRow("0123456789012345678901234567890123456789\trefs/heads/main\n0123456789012345678901234567890123456780\trefs/heads/main\n", "main", null)]
    [DataRow("not-an-id\trefs/heads/main\n", "main", null)]
    [DataRow("garbage\n", "main", null)]
    public void ParseBranchHead_AcceptsOnlyOneExactRef(string output, string branch, string? expected) =>
        Assert.AreEqual(expected, RemoteBranchHead.Parse(output, branch));

    // ==== worker context ===========================================================================

    [TestMethod]
    [DataRow(false, null, "main")]
    [DataRow(true, null, "main")]
    [DataRow(true, "main", "main")]
    [DataRow(true, "main", null)]
    public void Worker_AlwaysCarriesTheImplicitDefaultDecision_Explicitly(bool restricted, string? verified, string? branch)
    {
        var request = ServiceTestFixtures.Request(branch: branch) with
        {
            RestrictsImplicitDefault = restricted,
            VerifiedRemoteDefaultBranch = verified
        };

        Assert.IsNotNull(LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null).AllowImplicitDefault,
            "a null would fall back to allowed if the [JsonIgnore] restriction were dropped across a serialization boundary");
    }

    private sealed class DefaultsOnly : IRemoteDefaultBranchResolver
    {
        public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";
    }

    private sealed class FakeRemote(string? branch = null, string? head = null, string? reachable = null)
        : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver, IRemoteCommitBranchChecker
    {
        private int _headCalls;
        private int _reachabilityCalls;
        private (string, string)? _last;
        private (string, string)? _lastReach;

        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        public int HeadCalls => Volatile.Read(ref _headCalls);

        public int ReachabilityCalls => Volatile.Read(ref _reachabilityCalls);

        public (string Repository, string Commit)? LastReachabilityRequest => _lastReach;

        public (string Branch, string Repository)? LastHeadRequest => _last;

        public CancellationToken? ReachabilityToken { get; private set; }

        public CancellationToken? HeadToken { get; private set; }

        public async Task WaitUntilStartedAsync(int calls = 1)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (HeadCalls + ReachabilityCalls < calls)
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, "the lookups did not start");
                await Task.Delay(10);
            }
        }

        public bool IsCommitOnBranch(string repositoryRemoteUrl, string commit, CancellationToken cancellationToken = default)
        {
            ReachabilityToken = cancellationToken;
            Interlocked.Increment(ref _reachabilityCalls);
            _lastReach = (repositoryRemoteUrl, commit);
            WaitGate(cancellationToken);
            return reachable == "throw" ? throw new InvalidOperationException("boom") : reachable == "true";
        }

        public string? ResolveDefaultBranch(string repositoryRemoteUrl)
        {
            Gate.Wait(Wait);
            return branch;
        }

        // A cancelled call stops waiting and answers like a killed git: nothing.
        private void WaitGate(CancellationToken cancellationToken)
        {
            try
            {
                Gate.Wait(Wait, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("cancelled");
            }
        }

        public string? ResolveBranchHead(string repositoryRemoteUrl, string branchName, CancellationToken cancellationToken = default)
        {
            HeadToken = cancellationToken;
            Interlocked.Increment(ref _headCalls);
            _last = (branchName, repositoryRemoteUrl);
            WaitGate(cancellationToken);
            return head == "throw" ? throw new InvalidOperationException("boom") : head;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
