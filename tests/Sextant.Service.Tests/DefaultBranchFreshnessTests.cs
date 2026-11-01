using Sextant.Service.CallerIdentity;
using Sextant.Service.Grants;
using Sextant.Service.Host;
using Sextant.Service.Observability;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>Issue #355: how far each tracked default branch's index lags its remote head.</summary>
[TestClass]
public class DefaultBranchFreshnessTests
{
    private const string Repo = "https://github.com/acme/fresh";
    private const string Other = "https://github.com/acme/other";

    [TestMethod]
    public async Task EqualHeads_AreZeroLag()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Resolver.Heads[Repo] = "aaa";
        h.Start();

        var f = await h.ObservedAsync(Repo);
        Assert.IsFalse(f.Unknown);
        Assert.AreEqual(0, f.LagSeconds);
        StringAssert.Contains(h.Prometheus(), "sextant_default_branch_lag_seconds{repository=\"" + Repo + "\"} 0");
    }

    [TestMethod]
    public async Task DifferentHeads_LagGrowsWithTheClock()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Resolver.Heads[Repo] = "bbb";
        h.Start();
        await h.ObservedAsync(Repo);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(300, h.Find(Repo)!.LagSeconds, 0.5);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(600, h.Find(Repo)!.LagSeconds, 0.5);
    }

    [TestMethod]
    public async Task UnreadableRemote_IsUnknown_NeverZero()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Resolver.Heads[Repo] = null;
        h.Start();

        var f = await h.ObservedAsync(Repo);
        Assert.IsTrue(f.Unknown);
        var text = h.Prometheus();
        StringAssert.Contains(text, "sextant_default_branch_lag_unknown{repository=\"" + Repo + "\"} 1");
        Assert.IsFalse(text.Contains("sextant_default_branch_lag_seconds{repository=\"" + Repo, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ThrowingResolver_IsUnknown()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Resolver.Throw = true;
        h.Start();

        Assert.IsTrue((await h.ObservedAsync(Repo)).Unknown);
    }

    [TestMethod]
    public async Task NoSupportedCodeAtTheRemoteHead_IsAbsent_WhileALaggingRepositoryIsNot()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Seed(Other, "main", "ccc");
        h.Resolver.Heads[Repo] = "bbb";
        h.Resolver.Heads[Other] = "ddd";
        h.AddUnsupportedJob(Repo, "bbb", "no_supported_code_default: no solution file");
        h.Start();
        await h.ObservedAsync(Other);
        await h.ObservedAsync(Repo, expectPresent: false);

        Assert.IsNull(h.Find(Repo));
        Assert.IsNotNull(h.Find(Other));
        Assert.IsFalse(h.Prometheus().Contains("repository=\"" + Repo + "\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task IndexedHeadMovingAwayFromAnEqualObservation_StartsLagging_NotZero()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Resolver.Heads[Repo] = "aaa";
        h.Start();
        await h.ObservedAsync(Repo);

        h.Seed(Repo, "main", "ccc");
        Assert.AreEqual(0, h.Find(Repo)!.LagSeconds, 0.5);
        h.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.AreEqual(120, h.Find(Repo)!.LagSeconds, 0.5);
    }

    [TestMethod]
    public async Task RecoversToZeroAfterTheHeadIsIndexed_WithoutANewObservation()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Resolver.Heads[Repo] = "bbb";
        h.Start();
        await h.ObservedAsync(Repo);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsTrue(h.Find(Repo)!.LagSeconds > 100);

        h.Seed(Repo, "main", "bbb");

        Assert.AreEqual(0, h.Find(Repo)!.LagSeconds);
    }

    [TestMethod]
    public async Task UnobservedRepository_IsNotReported_AndNoRemoteReadWithoutAResolver()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Start(withResolver: false);
        await Task.Delay(200);
        Assert.IsNull(h.Find(Repo));
    }

    [TestMethod]
    public async Task ObservationGoingStale_BecomesUnknown()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Resolver.Heads[Repo] = "aaa";
        h.Start();
        await h.ObservedAsync(Repo);
        h.Resolver.Throw = true;

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await Harness.WaitUntilAsync(() => h.Find(Repo)?.Unknown == true);
    }

    [TestMethod]
    public async Task BranchWithoutAUsableSnapshot_IsReportedAsLag_NotDropped()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Seed(Other, "main", "ccc");
        h.SetSnapshotStatus(Repo, SnapshotStatus.Superseded);
        h.SetSnapshotStatus(Other, SnapshotStatus.Failed);
        h.Resolver.Heads[Repo] = "aaa";
        h.Resolver.Heads[Other] = "ddd";
        h.Start();

        var superseded = await h.ObservedAsync(Repo);
        var failed = await h.ObservedAsync(Other);
        Assert.IsFalse(superseded.Unknown);
        Assert.IsFalse(failed.Unknown);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.AreEqual(180, h.Find(Repo)!.LagSeconds, 0.5);
        Assert.AreEqual(180, h.Find(Other)!.LagSeconds, 0.5);
    }

    [TestMethod]
    public async Task BranchWithoutAUsableSnapshot_AndNoSupportedCode_IsAbsent()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Seed(Other, "main", "ccc");
        h.SetSnapshotStatus(Repo, SnapshotStatus.Failed);
        h.Resolver.Heads[Repo] = "bbb";
        h.Resolver.Heads[Other] = "ccc";
        h.AddUnsupportedJob(Repo, "bbb", "no_supported_code: nothing to index");
        h.Start();
        await h.ObservedAsync(Other);
        await h.ObservedAsync(Repo, expectPresent: false);

        Assert.IsNull(h.Find(Repo));
    }

    [TestMethod]
    public async Task UserinfoInARepositoryUrl_NeverReachesAMetric()
    {
        const string Leaky = "https://user:s3cr3t-token@github.com/acme/leaky";
        await using var h = Harness.Create();
        h.Seed(Leaky, "main", "aaa");
        h.Resolver.Heads[Leaky] = "bbb";
        h.Start();
        await Harness.WaitUntilAsync(() => h.Service.CollectMetrics().Freshness.Count == 1);

        var text = h.Prometheus();
        StringAssert.Contains(text, "sextant_default_branch_lag_seconds{repository=\"https://github.com/acme/leaky\"}");
        Assert.IsFalse(text.Contains("s3cr3t-token", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("user:", StringComparison.Ordinal));
        Assert.IsFalse(DefaultBranchFreshnessTracker.LabelOf("git@github.com:acme/x.git").Contains("git@", StringComparison.Ordinal));
        Assert.AreEqual("https://github.com/acme/leaky", DefaultBranchFreshnessTracker.LabelOf(Leaky));
    }

    [TestMethod]
    public void LabelOf_KeepsOnlyHostAndPath_AndIsCapped()
    {
        Assert.AreEqual("https://github.com/acme/x", DefaultBranchFreshnessTests_Label("https://user:p@ss@github.com/acme/x?token=a#frag"));
        Assert.AreEqual("https://github.com/acme/x", DefaultBranchFreshnessTests_Label("https://github.com/acme/x#frag"));
        var capped = DefaultBranchFreshnessTracker.LabelOf("https://github.com/" + new string('a', 5000));
        Assert.IsTrue(capped.Length <= DefaultBranchFreshnessTracker.MaxLabelLength);
    }

    private static string DefaultBranchFreshnessTests_Label(string url) => DefaultBranchFreshnessTracker.LabelOf(url);

    [TestMethod]
    public async Task OnlyRepositoriesInTheVisibleSet_AreReported_AndAnEmptySetReportsNone()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Seed(Other, "main", "ccc");
        h.Resolver.Heads[Repo] = "bbb";
        h.Resolver.Heads[Other] = "ddd";
        h.Start();
        await h.ObservedAsync(Repo);
        await h.ObservedAsync(Other);

        var all = h.Service.CollectMetrics().Freshness;
        var one = h.Service.CollectMetrics(visibleRepositoryKeys: new HashSet<string> { RepositoryGrantKey.Of(Repo) }).Freshness;
        var none = h.Service.CollectMetrics(visibleRepositoryKeys: new HashSet<string>()).Freshness;

        Assert.AreEqual(2, all.Count);
        CollectionAssert.AreEqual(new[] { Repo }, one.Select(f => f.Repository).ToArray());
        Assert.AreEqual(0, none.Count);
    }

    [TestMethod]
    public async Task TheMetricsCallerDecidesWhichRepositoriesItMayName()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Start();
        CallerPrincipal Caller(CallerActor actor, string tenant) => new()
        {
            TenantId = tenant, TenantSlug = tenant, Actor = actor, App = "app", Connection = "c", Via = "mcp-surface",
            KeyId = "k", Jti = "j", UserId = actor == CallerActor.User ? "u" : null
        };
        await h.Service.PutGrantAsync(Caller(CallerActor.Application, "tenant-a"), GrantScope.Tenant, Repo, RepositoryGrantKey.Of(Repo), "");

        Assert.IsNull(ServiceApp.MetricsRepositoryVisibility(null, h.Service), "the operator's own access sees all");
        CollectionAssert.AreEqual(new[] { RepositoryGrantKey.Of(Repo) },
            ServiceApp.MetricsRepositoryVisibility(Caller(CallerActor.Application, "tenant-a"), h.Service)!.ToArray());
        Assert.AreEqual(0, ServiceApp.MetricsRepositoryVisibility(Caller(CallerActor.Application, "tenant-b"), h.Service)!.Count);
        Assert.AreEqual(0, ServiceApp.MetricsRepositoryVisibility(Caller(CallerActor.Application, ""), h.Service)!.Count, "no tenant, no series");
        Assert.AreEqual(0, ServiceApp.MetricsRepositoryVisibility(Caller(CallerActor.User, "tenant-a"), h.Service)!.Count);
    }

    [TestMethod]
    public async Task TwoRowsWithOneLabel_EmitOneSeries_WithTheLargerLag()
    {
        await using var h = Harness.Create();
        const string A = "https://a@github.com/acme/dup";
        const string B = "https://b@github.com/acme/dup";
        h.Seed(A, "main", "aaa");
        h.Seed(B, "main", "ccc");
        h.Resolver.Heads[A] = "bbb";
        h.Resolver.Heads[B] = "ddd";
        h.Start();
        await h.ObservedAsync("https://github.com/acme/dup");

        var text = h.Prometheus();
        var series = text.Split('\n').Count(l => l.StartsWith("sextant_default_branch_lag_seconds{repository=\"https://github.com/acme/dup\"}", StringComparison.Ordinal));
        Assert.AreEqual(1, series);
    }

    [TestMethod]
    public async Task NoSupportedCode_IsDecidedAtObservation_NotByACollection()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Resolver.Heads[Repo] = "bbb";
        h.Start();
        await h.ObservedAsync(Repo);

        // A job recorded after the observation is read by the NEXT observation, never by a collection.
        h.AddUnsupportedJob(Repo, "bbb", "no_supported_code: nothing to index");
        Assert.IsNotNull(h.Find(Repo));
        Assert.IsNotNull(h.Find(Repo));
    }

    [TestMethod]
    public async Task PassCutShortByTheInterval_LeavesUnfinishedRepositoriesOnTheirPreviousObservation()
    {
        await using var h = Harness.Create();
        h.Seed(Repo, "main", "aaa");
        h.Seed(Other, "main", "ccc");
        h.Resolver.Heads[Repo] = "aaa";
        h.Resolver.Heads[Other] = "ccc";
        h.Start(interval: TimeSpan.FromSeconds(1));
        await h.ObservedAsync(Repo);
        await h.ObservedAsync(Other);

        h.Resolver.BlockUrl = Other;
        await h.Service.RunFreshnessPassAsync(CancellationToken.None);

        Assert.IsFalse(h.Find(Other)!.Unknown);
        Assert.IsFalse(h.Find(Repo)!.Unknown);
    }

    private sealed class FakeResolver : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver
    {
        public Dictionary<string, string?> Heads { get; } = new(StringComparer.Ordinal);
        public volatile bool Throw;
        public volatile string? BlockUrl;

        public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";

        public string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default)
        {
            if (Throw)
                throw new InvalidOperationException("remote down");
            if (BlockUrl == repositoryRemoteUrl)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
                return null;
            }
            lock (Heads)
                return Heads.GetValueOrDefault(repositoryRemoteUrl);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _dbPath = ServiceTestFixtures.NewDbPath();
        private bool _started;

        public IndexDatabase Db { get; private set; } = null!;
        public ManualClock Clock { get; } = new();
        public FakeResolver Resolver { get; } = new();
        public SnapshotService Service { get; private set; } = null!;

        public static Harness Create()
        {
            var h = new Harness();
            h.Db = new IndexDatabase(h._dbPath);
            h.Db.RunMigrations();
            return h;
        }

        public void Seed(string url, string branch, string commit)
        {
            var snapshot = ServiceTestFixtures.PublishComplete(
                Db, new EnsureSnapshotRequest { RepositoryRemoteUrl = url, CommitSha = commit }, recordCommit: true);
            var at = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            var store = new SnapshotStore(Db.GetConnection());
            var repoId = store.EnsureRepository(url, at);
            var branchId = store.EnsureBranch(repoId, branch, true, at);
            store.SetBranchPointer(branchId, snapshot, at);
        }

        public void AddUnsupportedJob(string url, string commit, string reason)
        {
            using var cmd = Db.GetConnection().CreateCommand();
            cmd.CommandText = """
                INSERT INTO snapshot_jobs(identity_hash, repository_url, commit_sha, status, created_at, updated_at, last_error)
                VALUES (@id, @url, @commit, 'unsupported', 0, 0, @err);
                """;
            cmd.Parameters.AddWithValue("@id", "unsupported-" + commit);
            cmd.Parameters.AddWithValue("@url", url);
            cmd.Parameters.AddWithValue("@commit", commit);
            cmd.Parameters.AddWithValue("@err", reason);
            cmd.ExecuteNonQuery();
        }

        public void SetSnapshotStatus(string url, string status)
        {
            using var cmd = Db.GetConnection().CreateCommand();
            cmd.CommandText = "UPDATE snapshots SET status = @s WHERE id IN (SELECT b.snapshot_id FROM branches b " +
                "JOIN repositories r ON r.id = b.repository_id WHERE r.remote_url = @url);";
            cmd.Parameters.AddWithValue("@s", status);
            cmd.Parameters.AddWithValue("@url", url);
            cmd.ExecuteNonQuery();
        }

        public void Start(bool withResolver = true, TimeSpan? interval = null)
        {
            var options = ServiceTestFixtures.NewOptions(_dbPath) with
            {
                FreshnessCheckInterval = interval ?? TimeSpan.FromMinutes(5),
                Maintenance = new MaintenancePolicy { Interval = TimeSpan.Zero, BranchIdleRetireDays = 0 }
            };
            Service = SnapshotService.Start(
                options, new FakeSnapshotWorker(Db), Db,
                remoteDefaults: withResolver ? Resolver : null, timeProvider: Clock);
            _started = true;
        }

        public DefaultBranchFreshness? Find(string url) =>
            Service.CollectMetrics().Freshness.FirstOrDefault(f => f.Repository == url);

        public string Prometheus() => PrometheusExposition.Render(Service.CollectMetrics());

        public async Task<DefaultBranchFreshness> ObservedAsync(string url, bool expectPresent = true)
        {
            if (!expectPresent)
            {
                // Give the startup pass time to observe, then the caller asserts absence.
                await Task.Delay(500);
                return new DefaultBranchFreshness { Repository = url };
            }
            DefaultBranchFreshness? found = null;
            await WaitUntilAsync(() => (found = Find(url)) is not null);
            return found!;
        }

        public static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition())
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, "condition was not met in time");
                await Task.Delay(20);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_started)
                Service.Dispose();
            SqliteTestDatabase.Delete(_dbPath, Db);
            return ValueTask.CompletedTask;
        }
    }
}
