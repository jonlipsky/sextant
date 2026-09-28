using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-5 at the HTTP boundary: <c>POST /control/ensure</c> applies <see cref="RepositoryUrlPolicy"/> BEFORE any
/// job row exists. A refused URL is a <c>400 {"status":"rejected","reason":"&lt;code&gt;"}</c> that never echoes
/// the URL, runs no worker, creates no job, and leaves one <c>ensure</c>/<c>denied</c> audit row whose detail is
/// the reason code. An allowed URL is ensured under its SUBMITTED spelling (identity hashes it), and a
/// <c>file://</c> fixture passes the route only under the test-only transport flag.
/// </summary>
[TestClass]
public class EnsureIntakePolicyHttpTests
{
    private const string ControlToken = "control-secret";

    [TestMethod]
    [DataRow("https://169.254.169.254/latest/meta-data", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://localhost/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://[::1]/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://gitlab.com/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://x-access-token:hunter2@github.com/org/app", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com:8443/org/app", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("http://github.com/org/app", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("git@github.com:org/app.git", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("file:///srv/secret/repo.git", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("https://github.com/org/../app", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/-org/app", RepositoryUrlRejection.PathNotAllowed)]
    public async Task RefusedUrl_Is400_CreatesNoJob_AndIsAuditedAsDenied(string url, string reason)
    {
        await using var host = await Harness.StartAsync();

        var response = await host.EnsureAsync(url);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using (var json = JsonDocument.Parse(body))
        {
            var props = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            CollectionAssert.AreEquivalent(new[] { "status", "reason" }, props, "the refusal carries only status + reason");
            Assert.AreEqual("rejected", json.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
        }
        Assert.IsFalse(body.Contains(url, StringComparison.Ordinal), "the refused URL is never echoed");

        Assert.AreEqual(0, host.Worker.Calls, "a refused URL never reaches the worker");
        Assert.AreEqual(0L, host.JobCount(), "a refused URL is refused BEFORE any job row exists");

        var audit = host.Service.RecentAudit(action: AuditAction.Ensure);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Denied, audit[0].Outcome);
        Assert.AreEqual(reason, audit[0].Detail);
        Assert.IsNull(audit[0].RepositoryScope, "the untrusted URL is never stored as a repository scope");
        Assert.AreEqual(AuditLogStore.HashActor(ControlToken), audit[0].Actor);

        var auditBody = await (await host.SendAsync(HttpMethod.Get, "/control/audit?action=ensure")).Content.ReadAsStringAsync();
        StringAssert.Contains(auditBody, "\"outcome\":\"denied\"");
        StringAssert.Contains(auditBody, $"\"detail\":\"{reason}\"");
        Assert.IsFalse(auditBody.Contains("hunter2", StringComparison.Ordinal), "a credential in a refused URL never reaches the audit log");
        Assert.IsFalse(auditBody.Contains(ControlToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RefusedUrl_WithWaitFalse_IsAlso400()
    {
        await using var host = await Harness.StartAsync();
        var response = await host.EnsureAsync("https://127.0.0.1/org/app", wait: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), RepositoryUrlRejection.HostNotAllowed);
        Assert.AreEqual(0L, host.JobCount());
    }

    [TestMethod]
    public async Task OwnerAllowList_RefusesUnlistedOwner()
    {
        var policy = new RepositoryUrlPolicy(["github.com"], ["github.com/org"]);
        await using var host = await Harness.StartAsync(policy);

        var refused = await host.EnsureAsync("https://github.com/other/app");
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), RepositoryUrlRejection.OwnerNotAllowed);
        Assert.AreEqual(0L, host.JobCount());

        var allowed = await host.EnsureAsync("https://github.com/org/app");
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
    }

    [TestMethod]
    public async Task AllowedUrl_IsEnsuredUnderItsSubmittedSpelling()
    {
        // Identity hashes the raw spelling, so the canonical form must NOT be substituted: rewriting would
        // re-key (and re-index) every repository already ensured under another spelling.
        const string submitted = "https://GitHub.com/Org/App.git";
        await using var host = await Harness.StartAsync();

        var response = await host.EnsureAsync(submitted);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, host.Worker.Calls);
        CollectionAssert.AreEqual(new[] { submitted }, host.SeenUrls.ToArray());
        Assert.AreEqual(1L, host.JobCount());
    }

    [TestMethod]
    public async Task FileFixture_PassesTheRouteOnlyUnderTheTestFlag()
    {
        const string fixture = "file:///tmp/sextant-fixtures/app.git";

        await using (var strict = await Harness.StartAsync())
        {
            var refused = await strict.EnsureAsync(fixture);
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(), RepositoryUrlRejection.SchemeNotAllowed);
        }

        var testing = new RepositoryUrlPolicy([RepositoryUrlPolicy.DefaultHost]) { AllowFileTransportForTesting = true };
        await using var host = await Harness.StartAsync(testing);
        var response = await host.EnsureAsync(fixture);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { fixture }, host.SeenUrls.ToArray());
    }

    [TestMethod]
    public async Task RefusedUrl_IsNotHeldBehindARunningProduction()
    {
        // A production holds the single writer for its whole worker run. The refusal is a pure decision, so its
        // 400 must not wait that run out; the denied audit write stays queued and lands once the writer frees.
        await using var host = await Harness.StartAsync(gated: true, deniedAuditWait: TimeSpan.FromMilliseconds(100));
        var production = host.EnsureAsync("https://github.com/org/app");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.Worker.Calls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.AreEqual(1, host.Worker.Calls, "the production is running and holds the writer");

        var refused = await host.EnsureAsync("https://localhost/org/app").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), RepositoryUrlRejection.HostNotAllowed);
        Assert.AreEqual(0, host.DeniedAuditCount(), "the write is still queued behind the production");

        host.Worker.Gate.SetResult();
        Assert.AreEqual(HttpStatusCode.OK, (await production.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.DeniedAuditCount() == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.AreEqual(1, host.DeniedAuditCount(), "the queued denied audit lands once the writer frees");
    }

    [TestMethod]
    public async Task QueuedDeniedAudit_SurvivesShutdown()
    {
        // The caller already has its 400 while the row is queued behind a production, so shutdown (which cancels
        // the production and the service lifetime) must not drop it: it lands once the cancelled production
        // gives the writer back.
        await using var host = await Harness.StartAsync(gated: true, deniedAuditWait: TimeSpan.FromMilliseconds(100));
        var production = host.EnsureAsync("https://github.com/org/app");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.Worker.Calls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.AreEqual(1, host.Worker.Calls, "the production is running and holds the writer");

        var refused = await host.EnsureAsync("https://127.0.0.1/org/app").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual(0, host.DeniedAuditCount());

        host.Service.StopProduction();
        await production.WaitAsync(TimeSpan.FromSeconds(30));
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.DeniedAuditCount() == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.AreEqual(1, host.DeniedAuditCount(), "shutdown never drops a refusal the caller was already told about");
    }

    [TestMethod]
    public async Task DeniedAudit_IsRecordedEvenWhenTheCallerHasGoneAway()
    {
        await using var host = await Harness.StartAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        try
        {
            await host.Service.RecordEnsureDeniedAsync(RepositoryUrlRejection.HostNotAllowed, ControlToken, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The caller stopped waiting; the service-owned write still lands.
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.Service.RecentAudit(action: AuditAction.Ensure).Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        var audit = host.Service.RecentAudit(action: AuditAction.Ensure);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Denied, audit[0].Outcome);
    }

    [TestMethod]
    public async Task DeniedAudit_DuringShutdown_IsRefusedAs503()
    {
        await using var host = await Harness.StartAsync();
        host.Service.StopProduction();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => host.Service.RecordEnsureDeniedAsync(RepositoryUrlRejection.HostNotAllowed, ControlToken));
        var response = await host.EnsureAsync("https://localhost/org/app");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
            "a refusal the stopping service cannot audit is reported like any other shutdown-stopped ensure");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<string> _seen;

        public HttpClient Client { get; private init; } = null!;
        public SnapshotService Service { get; private init; } = null!;
        public FakeSnapshotWorker Worker { get; private init; } = null!;
        private WebApplication App { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public IReadOnlyList<string> SeenUrls
        {
            get
            {
                lock (_seen)
                    return [.. _seen];
            }
        }

        public static async Task<Harness> StartAsync(
            RepositoryUrlPolicy? policy = null, bool gated = false, TimeSpan? deniedAuditWait = null)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken) with
            {
                RepositoryUrlPolicy = policy ?? RepositoryUrlPolicy.Default
            };
            if (deniedAuditWait is { } wait)
                options = options with { DeniedAuditWait = wait };
            var seen = new List<string>();
            var worker = new FakeSnapshotWorker(db, (self, request) =>
            {
                lock (seen)
                    seen.Add(request.RepositoryRemoteUrl);
                return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(self.Database, request));
            }) { UseGate = gated };
            var service = SnapshotService.Start(options, worker, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness(seen)
            {
                Client = app.GetTestClient(), App = app, Service = service, Worker = worker, Db = db, DbPath = dbPath
            };
        }

        private Harness(List<string> seen) => _seen = seen;

        public async Task<HttpResponseMessage> EnsureAsync(string url, bool wait = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, wait ? "/control/ensure" : "/control/ensure?wait=false")
            {
                Content = JsonContent.Create(ServiceTestFixtures.Request(repo: url), options: ServiceJson.Options)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            return await Client.SendAsync(request);
        }

        public int DeniedAuditCount() =>
            Service.RecentAudit(action: AuditAction.Ensure).Count(a => a.Outcome == AuditOutcome.Denied);

        public long JobCount()
        {
            // The shared writer connection (owned by the database; never dispose it here).
            using var cmd = Db.GetConnection().CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshot_jobs;";
            return (long)cmd.ExecuteScalar()!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Service.Dispose();
            SqliteTestDatabase.Delete(DbPath, Db);
        }
    }
}
