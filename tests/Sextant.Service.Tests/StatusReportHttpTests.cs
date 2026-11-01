using System.Net;
using System.Text.Json;
using Sextant.Core;
using Sextant.Service.Observability;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// <c>GET /control/report</c> through the real host: tenant-scoped like <c>/control/metrics</c>, one row per default
/// branch with its verdict, fixed gap categories only, bounded by the row limit.
/// </summary>
[TestClass]
public class StatusReportHttpTests
{
    private const string Gadgets = "https://github.com/acme/gadgets";
    private const string Partial = "https://github.com/acme/partial";
    private const string NoCode = "https://github.com/acme/docs-only";
    private const string Empty = "https://github.com/acme/empty";
    private const string Verified = "https://github.com/acme/verified";
    private const string Old = "https://github.com/acme/old";
    private const string Staging = "https://github.com/acme/staging";
    private const string NoCodeLate = "https://github.com/acme/zzz-docs";

    private static Task<Harness> StartAsync(
        Func<ServiceOptions, ServiceOptions>? configure = null, IRemoteDefaultBranchResolver? resolver = null) =>
        Harness.StartAsync(seed: Seed, configure: configure ?? (o => o), remoteDefaults: resolver);

    private static void Seed(IndexDatabase db)
    {
        var widgets = GrantServiceTests.PublishOnBranch(db, Widgets, "commit-w9aaaaaaaaaaaaaaaa", "main", isDefault: true);
        var gadgets = GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true);
        var verified = GrantServiceTests.PublishOnBranch(db, Verified, "commit-v1", "main", isDefault: true);
        var old = GrantServiceTests.PublishOnBranch(db, Old, "commit-o1", "main", isDefault: true);
        var staging = GrantServiceTests.PublishOnBranch(db, Staging, "commit-s1", "main", isDefault: true);
        var partial = GrantServiceTests.PublishOnBranch(db, Partial, "commit-p1", "main", isDefault: true);
        var conn = db.GetConnection();
        Exec(conn, $"UPDATE snapshots SET status = 'superseded' WHERE id = {old};");
        Exec(conn, $"UPDATE snapshots SET status = 'pending' WHERE id = {staging};");
        var recordedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        new SnapshotCoverageStore(conn).Record(verified, new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }, recordedAt);
        new SnapshotCoverageStore(conn).Record(partial, new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            Reasons = ["submodule_unpopulated: external/secret-tools", "project_skipped: src/Hidden/Hidden.csproj"],
            SubmodulesDeclared = 1,
            SubmodulesUnpopulated = 1,
            ProjectsSkipped = 1,
            ProjectVersionsExcluded = 2
        }, recordedAt);

        var snapshots = new SnapshotStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshots.EnsureBranch(snapshots.EnsureRepository(Empty, now), "main", true, now);
        snapshots.EnsureRepository(NoCode, now);
        var jobs = new SnapshotJobStore(conn);
        foreach (var (hash, repo) in new[] { ("hash-docs-only", NoCode), ("hash-docs-late", NoCodeLate) })
        {
            var (job, _) = jobs.EnsureJob(hash, repo, "abcdef0123456789", "main");
            jobs.MarkResult(job.Id, SnapshotJobStatus.Unsupported, null, SnapshotJobStore.NoSupportedCodePrefix + "only markdown");
        }
        foreach (var (hash, repo, snap) in new[] { ("hash-done-w", Widgets, widgets), ("hash-done-g", Gadgets, gadgets), ("hash-done-p", Partial, partial) })
        {
            var (job, _) = jobs.EnsureJob(hash, repo, "abcdef0123456789", "main");
            jobs.MarkResult(job.Id, SnapshotJobStatus.Complete, snap, null);
        }
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task<JsonDocument> GetAsync(Harness host, string? assertion)
    {
        using var response = await host.ControlAsync(HttpMethod.Get, "/control/report", ControlToken, assertion);
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        return JsonDocument.Parse(text);
    }

    private static JsonElement Row(JsonDocument doc, string repository) =>
        doc.RootElement.GetProperty("repositories").EnumerateArray().Single(r => r.GetProperty("repository").GetString() == repository);

    private static List<string> Names(JsonDocument doc) =>
        doc.RootElement.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("repository").GetString()!).ToList();

    [TestMethod]
    public async Task Operator_SeesEveryRepository_WithVerdicts_AndFixedGapCategories()
    {
        await using var host = await StartAsync();
        using var doc = await GetAsync(host, null);

        CollectionAssert.AreEqual(
            new[] { Empty, NoCode, NoCodeLate, Gadgets, Partial, Verified, Old, Staging, Widgets }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Names(doc));

        var widgets = Row(doc, Widgets);
        Assert.AreEqual("unknown", widgets.GetProperty("verdict").GetString(), "a complete snapshot with no coverage row is not claimed complete");
        Assert.AreEqual("complete", Row(doc, Verified).GetProperty("verdict").GetString());
        Assert.AreEqual("main", widgets.GetProperty("branch").GetString());
        Assert.AreEqual("commit-w9aaa", widgets.GetProperty("commit").GetString());
        Assert.AreEqual(1, widgets.GetProperty("projects_indexed").GetInt64());
        Assert.AreEqual(1, widgets.GetProperty("documents").GetInt64());
        Assert.AreEqual(1, widgets.GetProperty("symbols").GetInt64());
        Assert.IsFalse(widgets.TryGetProperty("references", out _), "occurrences are never counted");
        Assert.IsTrue(widgets.TryGetProperty("lag_seconds", out var lag) && lag.ValueKind == JsonValueKind.Null);
        Assert.IsTrue(widgets.GetProperty("lag_unknown").GetBoolean());
        Assert.IsTrue(widgets.TryGetProperty("behind_remote", out var behind) && behind.ValueKind == JsonValueKind.Null);
        Assert.AreEqual(0, widgets.GetProperty("gap_reasons").GetArrayLength());

        var old = Row(doc, Old);
        Assert.AreEqual("superseded", old.GetProperty("verdict").GetString());
        Assert.AreEqual("commit-o1", old.GetProperty("commit").GetString());
        Assert.IsFalse(old.TryGetProperty("symbols", out _));
        var staging = Row(doc, Staging);
        Assert.AreEqual("pending", staging.GetProperty("verdict").GetString());
        Assert.AreEqual("commit-s1", staging.GetProperty("commit").GetString());

        var partial = Row(doc, Partial);
        Assert.AreEqual("partial", partial.GetProperty("verdict").GetString());
        CollectionAssert.AreEqual(new[] { "projects_not_loaded", "submodules_unpopulated" },
            partial.GetProperty("gap_reasons").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.AreEqual(2, partial.GetProperty("projects_excluded").GetInt64());
        var raw = doc.RootElement.GetRawText();
        Assert.IsFalse(raw.Contains("secret-tools") || raw.Contains("Hidden"), "raw reason text must never be reported");

        var noCode = Row(doc, NoCode);
        Assert.AreEqual("no_supported_code", noCode.GetProperty("verdict").GetString());
        Assert.IsFalse(noCode.GetProperty("lag_unknown").GetBoolean());

        var none = Row(doc, Empty);
        Assert.AreEqual("none", none.GetProperty("verdict").GetString());
        Assert.IsFalse(none.TryGetProperty("symbols", out _), "a repository with no snapshot has no counts");

        var totals = doc.RootElement.GetProperty("totals");
        Assert.AreEqual(9, totals.GetProperty("repositories").GetInt32());
        var by = totals.GetProperty("by_verdict");
        Assert.AreEqual(1, by.GetProperty("complete").GetInt32());
        Assert.AreEqual(1, by.GetProperty("partial").GetInt32());
        Assert.AreEqual(2, by.GetProperty("unknown").GetInt32());
        Assert.AreEqual(1, by.GetProperty("superseded").GetInt32());
        Assert.AreEqual(1, by.GetProperty("pending").GetInt32());
        Assert.AreEqual(2, by.GetProperty("no_supported_code").GetInt32());
        Assert.AreEqual(1, by.GetProperty("none").GetInt32());
        Assert.AreEqual(4, totals.GetProperty("symbols").GetInt64());
        Assert.IsTrue(totals.GetProperty("counts_complete").GetBoolean());
        Assert.IsFalse(totals.TryGetProperty("references", out _));
        Assert.IsTrue(doc.RootElement.GetProperty("counts_complete").GetBoolean());
        Assert.IsFalse(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("last_24h").TryGetProperty("jobs_by_outcome", out _));
        Assert.IsTrue(doc.RootElement.GetProperty("storage").GetProperty("catalog_bytes").GetInt64() > 0);
        Assert.IsFalse(doc.RootElement.TryGetProperty("tool_queries", out _));
    }

    [TestMethod]
    public async Task ApplicationCaller_GetsNoStorage_TheOperatorKeepsIt()
    {
        await using var host = await StartAsync();
        var a = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-a"), "kid-a");
        using (var grant = await host.ControlAsync(HttpMethod.Put, "/control/grants/tenant", ControlToken, a,
            JsonSerializer.Serialize(new { repository = Widgets })))
            Assert.IsTrue(grant.IsSuccessStatusCode);

        using var forApp = await GetAsync(host, a);
        Assert.IsFalse(forApp.RootElement.TryGetProperty("storage", out _), "catalog size is operator-only");
        Assert.IsFalse(forApp.RootElement.GetRawText().Contains("catalog_bytes"));
        using var forOperator = await GetAsync(host, null);
        Assert.IsTrue(forOperator.RootElement.GetProperty("storage").GetProperty("catalog_bytes").GetInt64() > 0);
    }

    [TestMethod]
    public async Task Last24h_CoversOnlyTheListedRepositories_AndSaysWhenTruncated()
    {
        await using var host = await StartAsync(o => o with { ReportMaxRepositories = 3 });
        using var doc = await GetAsync(host, null);
        // Listed (by label): docs-only, empty, gadgets. Only gadgets has a complete job among them.
        CollectionAssert.AreEqual(new[] { NoCode, Empty, Gadgets }.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Names(doc));
        var last = doc.RootElement.GetProperty("last_24h");
        Assert.AreEqual(1, last.GetProperty("jobs_by_outcome").GetProperty("complete").GetInt64());
        Assert.AreEqual(1, last.GetProperty("repositories_reindexed").GetInt32());
        Assert.IsTrue(last.GetProperty("truncated").GetBoolean());

        await using var whole = await StartAsync();
        using var all = await GetAsync(whole, null);
        Assert.AreEqual(3, all.RootElement.GetProperty("last_24h").GetProperty("jobs_by_outcome").GetProperty("complete").GetInt64());
        Assert.IsFalse(all.RootElement.GetProperty("last_24h").GetProperty("truncated").GetBoolean());
    }

    [TestMethod]
    public async Task BehindRemote_ComparesTheIndexedCommitWithTheLastRemoteHead()
    {
        await using var host = await StartAsync(
            o => o with { FreshnessCheckInterval = TimeSpan.FromMinutes(5) }, new HeadResolver());
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            using var metrics = await host.ControlAsync(HttpMethod.Get, "/control/metrics", ControlToken, null);
            var text = await metrics.Content.ReadAsStringAsync();
            if (text.Contains(Widgets) && text.Contains(Gadgets))
                break;
            Assert.IsTrue(DateTime.UtcNow < deadline, "the freshness pass never ran");
            await Task.Delay(100);
        }

        using var doc = await GetAsync(host, null);
        var widgets = Row(doc, Widgets);
        Assert.IsFalse(widgets.GetProperty("behind_remote").GetBoolean());
        var gadgets = Row(doc, Gadgets);
        Assert.IsTrue(gadgets.GetProperty("behind_remote").GetBoolean());
        Assert.AreEqual(0, gadgets.GetProperty("lag_seconds").GetDouble(), 1.0, "lag 0 alone reads as current: behind_remote says otherwise");
        Assert.AreEqual(JsonValueKind.Null, Row(doc, Partial).GetProperty("behind_remote").ValueKind);
    }

    private sealed class HeadResolver : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver
    {
        public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";

        public string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) =>
            repositoryRemoteUrl == Widgets ? "commit-w9aaaaaaaaaaaaaaaa" : repositoryRemoteUrl == Gadgets ? "commit-g2" : null;
    }

    [TestMethod]
    public void GapCategories_CoverPartialLoadsAndTimeBudgetShortfalls()
    {
        var partiallyLoaded = StatusReportBuilder.GapCategories(new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            ProjectsPartiallyLoaded = 1
        });
        CollectionAssert.AreEqual(new[] { ReportGapReason.ProjectsPartiallyLoaded }, partiallyLoaded.ToArray());

        var budget = StatusReportBuilder.GapCategories(new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            TimeBudget = new TimeBudgetCoverage { ProjectsNotIndexed = 2, ProjectsNotFullyExtracted = 3 }
        });
        CollectionAssert.AreEqual(
            new[] { ReportGapReason.ProjectsNotIndexed, ReportGapReason.ProjectsNotFullyExtracted, ReportGapReason.TimeBudgetExhausted },
            budget.ToArray());
    }

    [TestMethod]
    public async Task NoSupportedCode_IsOrderedAndCut_AndTheCutIsReported()
    {
        await using var host = await StartAsync(o => o with { ReportMaxNoCodeJobs = 1 });
        using var doc = await GetAsync(host, null);
        Assert.IsTrue(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.AreEqual("no_supported_code", Row(doc, NoCode).GetProperty("verdict").GetString(), "the first by URL is kept");
        Assert.IsFalse(Names(doc).Contains(NoCodeLate), "the later one is cut, and the report says so");
    }

    [TestMethod]
    public async Task Report_IsServedWhole_FromTheCache_ForTenMinutes()
    {
        await using var host = await StartAsync();
        using var first = await host.ControlAsync(HttpMethod.Get, "/control/report", ControlToken, null);
        var a = await first.Content.ReadAsStringAsync();
        host.ExecuteOnCatalog($"UPDATE snapshots SET status = 'superseded' WHERE id = (SELECT snapshot_id FROM branches b JOIN repositories r ON r.id = b.repository_id WHERE r.remote_url = '{Widgets}');");
        using var second = await host.ControlAsync(HttpMethod.Get, "/control/report", ControlToken, null);
        Assert.AreEqual(a, await second.Content.ReadAsStringAsync(), "the whole response is cached per caller scope");
        Assert.AreEqual(1, host.Service.ReportComputations);
    }

    [TestMethod]
    public async Task OneReportComputesAtATime_AndAConcurrentRequestGetsTheSameResult()
    {
        await using var host = await StartAsync();
        var a = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-a"), "kid-a");
        var b = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-b"), "kid-b");
        using (var grant = await host.ControlAsync(HttpMethod.Put, "/control/grants/tenant", ControlToken, b,
            JsonSerializer.Serialize(new { repository = Gadgets })))
            Assert.IsTrue(grant.IsSuccessStatusCode);
        var running = 0;
        var maxRunning = 0;
        host.Service.ReportComputeHook = () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref running);
        };

        var calls = new[] { null, null, null, a, b }.Select(async who =>
        {
            using var response = await host.ControlAsync(HttpMethod.Get, "/control/report", ControlToken, who);
            return await response.Content.ReadAsStringAsync();
        }).ToArray();
        var texts = await Task.WhenAll(calls);

        Assert.AreEqual(1, maxRunning, "reports for different scopes never compute together");
        Assert.AreEqual(1, texts.Take(3).Distinct().Count(), "concurrent requests of one scope share one result");
        Assert.AreEqual(3, host.Service.ReportComputations, "one computation per scope");
    }

    [TestMethod]
    public async Task AComputationOutlivesItsCaller_AndLaterRequestsAreServedFromIt()
    {
        await using var host = await StartAsync();
        var gate = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        host.Service.ReportComputeHook = () =>
        {
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        };
        using var gone = new CancellationTokenSource();
        var waiting = host.Service.BuildStatusReportAsync(null, [], gone.Token);
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)));
        await gone.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);

        gate.Set();
        var report = await host.Service.BuildStatusReportAsync(null, [], CancellationToken.None);
        Assert.IsTrue(report.CountsComplete, "the abandoned computation finished and was kept");
        Assert.AreEqual(1, host.Service.ReportComputations);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    [TestMethod]
    public void Counts_AreSeparateShortReads_ThatKeepTheirProgress_AndNeverStarveTheLaterRepositories()
    {
        var path = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(path);
        db.RunMigrations();
        GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true);
        GrantServiceTests.PublishOnBranch(db, Widgets, "commit-w1", "main", isDefault: true);
        var cache = new ReportCountCache();
        var tracker = new DefaultBranchFreshnessTracker(TimeProvider.System, TimeSpan.FromHours(1));

        StatusReport Run(int cancelAtOpen, out int opens, out int maxLive)
        {
            using var budget = new CancellationTokenSource();
            var opened = 0;
            var live = 0;
            var peak = 0;
            Microsoft.Data.Sqlite.SqliteConnection Open()
            {
                if (++opened == cancelAtOpen)
                    budget.Cancel();
                var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
                conn.Open();
                peak = Math.Max(peak, ++live);
                conn.StateChange += (_, e) =>
                {
                    if (e.CurrentState == System.Data.ConnectionState.Closed)
                        live--;
                };
                return conn;
            }
            var report = StatusReportBuilder.Build(Open, tracker, cache, TimeProvider.System, path, null, [], 500, 100, true, budget.Token);
            opens = opened;
            maxLive = peak;
            return report;
        }

        // Opens 1-2 are the catalog reads (and last_24h), then one per count: gadgets (first by label) projects, documents.
        var first = Run(cancelAtOpen: 4, out var opens, out var maxLive);
        Assert.IsFalse(first.CountsComplete);
        Assert.IsFalse(first.Totals.CountsComplete);
        Assert.AreEqual(1, maxLive, "no connection (read transaction) is held while the next count runs");
        Assert.IsTrue(opens >= 4);
        var gadgets = first.Repositories.Single(r => r.Repository == Gadgets);
        Assert.IsNotNull(gadgets.ProjectsIndexed);
        Assert.IsNotNull(gadgets.Documents, "a finished count is kept at once");
        Assert.IsNull(gadgets.Symbols);

        // The budget ends again at once: the repository not attempted yet goes first and gets its first count.
        var second = Run(cancelAtOpen: 3, out _, out _);
        var widgets = second.Repositories.Single(r => r.Repository == Widgets);
        Assert.IsNotNull(widgets.ProjectsIndexed, "the least recently attempted repository is counted first");
        Assert.IsNotNull(second.Repositories.Single(r => r.Repository == Gadgets).Documents, "an earlier report's counts carry over");

        var third = Run(cancelAtOpen: int.MaxValue, out var thirdOpens, out _);
        Assert.IsTrue(third.CountsComplete);
        Assert.IsTrue(third.Totals.CountsComplete);
        Assert.IsTrue(thirdOpens < 2 + 6, "counts already measured are not read again");
    }

    [TestMethod]
    public async Task Tenants_SeeOnlyTheirTenantWideGrants_AndUsersAreRefused()
    {
        await using var host = await StartAsync();
        var a = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-a"), "kid-a");
        var b = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-b"), "kid-b");
        // tenant-a also holds a grant on a repository the catalog has never seen.
        const string Unseen = "https://github.com/acme/unseen";
        foreach (var (assertion, repo) in new[] { (a, Widgets), (a, Unseen), (b, Gadgets), (b, NoCode) })
        {
            using var grant = await host.ControlAsync(HttpMethod.Put, "/control/grants/tenant", ControlToken, assertion,
                JsonSerializer.Serialize(new { repository = repo }));
            Assert.IsTrue(grant.IsSuccessStatusCode, await grant.Content.ReadAsStringAsync());
        }

        using var forA = await GetAsync(host, a);
        CollectionAssert.AreEqual(new[] { Unseen, Widgets }, Names(forA));
        Assert.AreEqual("none", Row(forA, Unseen).GetProperty("verdict").GetString());
        Assert.AreEqual(2, forA.RootElement.GetProperty("totals").GetProperty("repositories").GetInt32());

        using var forB = await GetAsync(host, b);
        CollectionAssert.AreEqual(new[] { NoCode, Gadgets }.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Names(forB));
        Assert.AreEqual("no_supported_code", Row(forB, NoCode).GetProperty("verdict").GetString());
        foreach (var other in new[] { Widgets, Partial, Empty })
            Assert.IsFalse(forB.RootElement.GetRawText().Contains(other, StringComparison.OrdinalIgnoreCase), other + " leaked");

        using var user = await host.ControlAsync(HttpMethod.Get, "/control/report", ControlToken,
            host.Sign(CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow), "kid-a"));
        Assert.AreEqual(HttpStatusCode.Forbidden, user.StatusCode);

        using var noToken = await host.ControlAsync(HttpMethod.Get, "/control/report", "wrong-token", null);
        Assert.IsTrue(noToken.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task ApplicationCallerWithNoGrants_GetsNoRepositories()
    {
        await using var host = await StartAsync();
        var a = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-a"), "kid-a");
        using var doc = await GetAsync(host, a);
        Assert.AreEqual(0, doc.RootElement.GetProperty("repositories").GetArrayLength());
        Assert.AreEqual(0, doc.RootElement.GetProperty("totals").GetProperty("repositories").GetInt32());
    }

    [TestMethod]
    public async Task RowLimit_CutsTheList_AndSaysSo()
    {
        await using var host = await StartAsync(o => o with { ReportMaxRepositories = 3 });
        using var doc = await GetAsync(host, null);
        Assert.AreEqual(3, doc.RootElement.GetProperty("repositories").GetArrayLength());
        Assert.IsTrue(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.AreEqual(3, doc.RootElement.GetProperty("totals").GetProperty("repositories").GetInt32());
    }

    [TestMethod]
    public async Task ExhaustedBudget_LeavesCountsOut_ButStillAnswers()
    {
        await using var host = await StartAsync(o => o with { ReportBudget = TimeSpan.FromTicks(1) });
        using var response = await host.ControlAsync(HttpMethod.Get, "/control/report", ControlToken, null);
        var text = await response.Content.ReadAsStringAsync();
        // Either the cheap reads were interrupted (503) or only the counts were skipped; never a partial body.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var doc = JsonDocument.Parse(text);
            Assert.IsFalse(doc.RootElement.GetProperty("counts_complete").GetBoolean());
        }
        else
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, text);
            StringAssert.Contains(text, "report_timeout");
        }
    }
}
