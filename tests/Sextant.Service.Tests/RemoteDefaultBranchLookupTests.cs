using System.Diagnostics;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #199: <see cref="RemoteDefaultBranchLookup"/> bounds the service's outbound remote-default lookups.
/// Concurrent lookups of one repository share a call, answers are remembered briefly, at most
/// <see cref="RemoteDefaultBranchLookup.MaxConcurrentLookups"/> calls run at once (beyond that it fails closed without
/// a call), a throw answers <c>null</c>, and the table stays bounded.
/// </summary>
[TestClass]
public class RemoteDefaultBranchLookupTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [TestMethod]
    public async Task ConcurrentLookupsOfOneRepository_ShareOneCall()
    {
        var resolver = new GatedResolver(_ => "main");
        resolver.Gate.Reset();
        var lookup = new RemoteDefaultBranchLookup(resolver);

        var first = lookup.ResolveAsync("https://github.com/acme/widgets");
        var second = lookup.ResolveAsync("https://github.com/acme/widgets.git");
        resolver.Gate.Set();

        Assert.AreEqual("main", await first.WaitAsync(Wait));
        Assert.AreEqual("main", await second.WaitAsync(Wait));
        Assert.AreEqual(1, resolver.Calls, "both spellings of the repository shared one call");
    }

    [TestMethod]
    [DataRow("main")]
    [DataRow(null)]
    public async Task AFinishedAnswer_IsReusedForItsLifetime_ThenLookedUpAgain(string? answer)
    {
        var time = new ManualTime();
        var resolver = new GatedResolver(_ => answer);
        var lookup = new RemoteDefaultBranchLookup(resolver, time);

        Assert.AreEqual(answer, await lookup.ResolveAsync("https://github.com/acme/widgets").WaitAsync(Wait));
        time.Advance(RemoteDefaultBranchLookup.ResultLifetime - TimeSpan.FromSeconds(1));
        Assert.AreEqual(answer, await lookup.ResolveAsync("https://github.com/acme/widgets").WaitAsync(Wait));
        Assert.AreEqual(1, resolver.Calls, "remembered");

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(answer, await lookup.ResolveAsync("https://github.com/acme/widgets").WaitAsync(Wait));
        Assert.AreEqual(2, resolver.Calls, "looked up again once the answer expired");
    }

    [TestMethod]
    public async Task WhenEveryLookupSlotIsBusy_AnotherRepositoryFailsClosedWithoutACall()
    {
        var resolver = new GatedResolver(_ => "main");
        resolver.Gate.Reset();
        var lookup = new RemoteDefaultBranchLookup(resolver);

        var running = Enumerable.Range(0, RemoteDefaultBranchLookup.MaxConcurrentLookups)
            .Select(i => lookup.ResolveAsync($"https://github.com/acme/r{i}"))
            .ToList();
        var saturated = lookup.ResolveAsync("https://github.com/acme/other");
        var joined = lookup.ResolveAsync("https://github.com/acme/r0");

        Assert.IsTrue(saturated.IsCompletedSuccessfully, "answered at once");
        Assert.IsNull(await saturated);
        Assert.IsFalse(joined.IsCompleted, "a running lookup of the same repository is still shared");

        resolver.Gate.Set();
        foreach (var task in running)
            Assert.AreEqual("main", await task.WaitAsync(Wait));
        Assert.AreEqual("main", await joined.WaitAsync(Wait));
        Assert.AreEqual(RemoteDefaultBranchLookup.MaxConcurrentLookups, resolver.Calls, "the saturated lookup made no call");

        Assert.AreEqual("main", await lookup.ResolveAsync("https://github.com/acme/other").WaitAsync(Wait),
            "the saturated answer was not remembered");
        Assert.AreEqual(RemoteDefaultBranchLookup.MaxConcurrentLookups + 1, resolver.Calls);
    }

    [TestMethod]
    public async Task AThrowingResolver_AnswersNull()
    {
        var lookup = new RemoteDefaultBranchLookup(new GatedResolver(_ => throw new InvalidOperationException("boom")));

        Assert.IsNull(await lookup.ResolveAsync("https://github.com/acme/widgets").WaitAsync(Wait));
    }

    [TestMethod]
    public async Task TheTable_StaysBounded()
    {
        var lookup = new RemoteDefaultBranchLookup(new GatedResolver(_ => "main"), new ManualTime());

        for (var i = 0; i < RemoteDefaultBranchLookup.MaxRememberedRepositories + 100; i++)
        {
            await lookup.ResolveAsync($"https://github.com/acme/r{i}").WaitAsync(Wait);
            Assert.IsTrue(lookup.RememberedCount <= RemoteDefaultBranchLookup.MaxRememberedRepositories, $"{lookup.RememberedCount} remembered");
        }
    }

    [TestMethod]
    public async Task CancellationIgnoringResolversAreAbandonedWithoutStarvingAnotherRepository()
    {
        var resolver = new CancellationIgnoringResolver();
        var lookup = new RemoteDefaultBranchLookup(
            resolver, resolverTimeout: TimeSpan.FromMilliseconds(50));
        const string stuck = "https://github.com/acme/stuck";

        Assert.IsNull(await lookup.ResolveAsync(stuck).WaitAsync(Wait));
        await WaitUntilAsync(() => lookup.AbandonedCountForTesting() == 1);
        Assert.IsNull(await lookup.ResolveAsync(stuck).WaitAsync(Wait));
        await WaitUntilAsync(() => lookup.AbandonedCountForTesting() == 2);

        Assert.AreEqual("main", await lookup.ResolveAsync("https://github.com/acme/healthy").WaitAsync(Wait),
            "timed-out stuck resolver calls release their lookup slots");
        Assert.AreEqual(1, resolver.HealthyCalls);

        for (var i = 2; i < RemoteDefaultBranchLookup.MaxConcurrentLookupsTotal; i++)
            Assert.IsNull(await lookup.ResolveFreshAsync(stuck, CancellationToken.None).WaitAsync(Wait));
        Assert.AreEqual(RemoteDefaultBranchLookup.MaxConcurrentLookupsTotal, lookup.AbandonedCountForTesting());
        var capped = lookup.ResolveAsync("https://github.com/acme/another");
        Assert.IsTrue(capped.IsCompletedSuccessfully);
        Assert.IsNull(await capped, "outstanding calls are globally bounded");
        await WaitUntilAsync(() => resolver.StuckCalls == RemoteDefaultBranchLookup.MaxConcurrentLookupsTotal);

        resolver.ReleaseStuck();
        await WaitUntilAsync(() => lookup.AbandonedCountForTesting() == 0);
        Assert.AreEqual("main", await lookup.ResolveFreshAsync(stuck, CancellationToken.None).WaitAsync(Wait));
    }

    [TestMethod]
    public async Task CancellingDefaultLookupWaiterReleasesItsAnswerAtOnce()
    {
        var resolver = new CancellationIgnoringResolver();
        var lookup = new RemoteDefaultBranchLookup(
            resolver, resolverTimeout: TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var pending = lookup.ResolveFreshAsync("https://github.com/acme/stuck", cancellation.Token);
        await resolver.StuckStarted.Task.WaitAsync(Wait);

        cancellation.Cancel();
        Assert.IsNull(await pending.WaitAsync(Wait));
        await WaitUntilAsync(() => lookup.AbandonedCountForTesting() == 1);

        resolver.ReleaseStuck();
        await WaitUntilAsync(() => lookup.AbandonedCountForTesting() == 0);
    }

    [TestMethod]
    public async Task CancellationCallbackCanRaceCompletedResolverDisposal()
    {
        var resolver = new GatedResolver(_ => "main");
        resolver.Gate.Reset();
        using var callbackStarted = new ManualResetEventSlim();
        using var resumeCallback = new ManualResetEventSlim();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new RemoteDefaultBranchLookup(resolver)
        {
            BeforeWaiterCancelForTesting = () =>
            {
                callbackStarted.Set();
                resumeCallback.Wait(Wait);
            },
            AfterCancellationDisposedForTesting = () => disposed.TrySetResult()
        };
        using var cancellation = new CancellationTokenSource();
        var answer = lookup.ResolveFreshAsync("https://github.com/acme/widgets", cancellation.Token);
        var cancelling = Task.Run(cancellation.Cancel);
        try
        {
            Assert.IsTrue(callbackStarted.Wait(Wait));
            resolver.Gate.Set();
            await disposed.Task.WaitAsync(Wait);
        }
        finally
        {
            resolver.Gate.Set();
            resumeCallback.Set();
        }
        await cancelling.WaitAsync(Wait);
        Assert.AreEqual("main", await answer.WaitAsync(Wait));
    }

    [TestMethod]
    public async Task RunningCallsReserveTheGlobalOutstandingCapacity()
    {
        var resolver = new CancellationIgnoringResolver();
        var lookup = new RemoteDefaultBranchLookup(resolver, resolverTimeout: TimeSpan.FromSeconds(1));
        try
        {
            var calls = Enumerable.Range(0, RemoteDefaultBranchLookup.MaxConcurrentLookupsTotal)
                .Select(i => lookup.ResolveAsync($"https://github.com/acme/stuck{i}", $"tenant{i}"))
                .ToArray();
            Assert.IsNull(await lookup.ResolveAsync("https://github.com/acme/healthy", "another"));
            await Task.WhenAll(calls).WaitAsync(Wait);
            Assert.AreEqual(RemoteDefaultBranchLookup.MaxConcurrentLookupsTotal, lookup.AbandonedCountForTesting());
        }
        finally
        {
            resolver.ReleaseStuck();
        }
        await WaitUntilAsync(() => lookup.AbandonedCountForTesting() == 0);
    }

    [TestMethod]
    public async Task HealthyBranchRecoversWhenOneStuckCallReturns()
    {
        var resolver = new BranchResolver();
        var lookup = new RemoteDefaultBranchLookup(resolver, heads: resolver, resolverTimeout: TimeSpan.FromMilliseconds(100));
        const string repository = "https://github.com/acme/stuck";
        try
        {
            Assert.IsNull(await lookup.ResolveBranchHeadAsync(repository, "one").WaitAsync(Wait));
            Assert.IsNull(await lookup.ResolveBranchHeadAsync(repository, "two").WaitAsync(Wait));
            Assert.AreEqual("head", await lookup.ResolveBranchHeadAsync("https://github.com/acme/other", "healthy").WaitAsync(Wait));
            resolver.First.Set();
            await WaitUntilAsync(() => lookup.AbandonedCountForTesting() == 1);
            Assert.AreEqual("head", await lookup.ResolveBranchHeadAsync(repository, "healthy").WaitAsync(Wait));
        }
        finally
        {
            resolver.First.Set();
            resolver.Second.Set();
        }
        await WaitUntilAsync(() => lookup.AbandonedCountForTesting() == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > Wait)
                Assert.Fail("Timed out waiting for the remote lookup state.");
            await Task.Delay(10);
        }
    }

    private sealed class GatedResolver(Func<string, string?> answer) : IRemoteDefaultBranchResolver
    {
        public string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken) =>
            ResolveDefaultBranch(repositoryRemoteUrl);

        private int _calls;

        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        public int Calls => Volatile.Read(ref _calls);

        public string? ResolveDefaultBranch(string repositoryRemoteUrl)
        {
            Interlocked.Increment(ref _calls);
            Gate.Wait(Wait);
            return answer(repositoryRemoteUrl);
        }
    }

    private sealed class CancellationIgnoringResolver : IRemoteDefaultBranchResolver
    {
        public string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken) =>
            ResolveDefaultBranch(repositoryRemoteUrl);

        private int _stuckCalls;
        private int _healthyCalls;

        public ManualResetEventSlim StuckGate { get; } = new(initialState: false);
        public TaskCompletionSource StuckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StuckCalls => Volatile.Read(ref _stuckCalls);
        public int HealthyCalls => Volatile.Read(ref _healthyCalls);

        public string? ResolveDefaultBranch(string repositoryRemoteUrl)
        {
            if (repositoryRemoteUrl.Contains("/stuck", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _stuckCalls);
                StuckStarted.TrySetResult();
                StuckGate.Wait();
                return "main";
            }

            Interlocked.Increment(ref _healthyCalls);
            return "main";
        }

        public void ReleaseStuck() => StuckGate.Set();
    }

    private sealed class BranchResolver : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver
    {
        public RemoteBranchHeadObservation? ObserveBranchHead(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) =>
            ResolveBranchHead(repositoryRemoteUrl, branch, cancellationToken) is { } observed && !string.IsNullOrWhiteSpace(observed)
                ? new RemoteBranchHeadObservation(observed)
                : null;

        // This double cannot observe the default and head atomically (the refresh retries and stays supersedable).
        public RemoteBranchStateObservation? ObserveBranchState(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) => null;

        public string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken) =>
            ResolveDefaultBranch(repositoryRemoteUrl);

        public ManualResetEventSlim First { get; } = new(false);
        public ManualResetEventSlim Second { get; } = new(false);
        public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";
        public string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default)
        {
            if (branch == "one")
                First.Wait();
            else if (branch == "two")
                Second.Wait();
            return "head";
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
