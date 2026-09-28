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

    private sealed class GatedResolver(Func<string, string?> answer) : IRemoteDefaultBranchResolver
    {
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

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
