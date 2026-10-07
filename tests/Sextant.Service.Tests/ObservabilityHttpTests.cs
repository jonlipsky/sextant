using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 slice 3 — criterion 5 observability surface AND its criterion-1 leakage guard. The metrics,
/// audit, and pilot endpoints aggregate cross-tenant repository scopes, counts, and cost, so they MUST be
/// operator-only: reachable with the CONTROL token and NEVER with the query token (or anonymously). These
/// tests prove the gate at the HTTP boundary and that the signals actually reflect indexing activity.
/// Runs fully in-process over <see cref="TestServer"/> with an ephemeral SQLite catalog.
/// </summary>
[TestClass]
public class ObservabilityHttpTests
{
    private const string ControlToken = "control-secret";
    private const string QueryToken = "query-secret";

    [TestMethod]
    public async Task Metrics_WithQueryToken_IsDenied_LeakageGuard()
    {
        await using var host = await Harness.StartAsync();
        var response = await Send(host, "/control/metrics", QueryToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "the query token must NOT reach the operator metrics surface (criterion-1 leakage guard)");
    }

    [TestMethod]
    public async Task Metrics_Anonymous_IsDenied()
    {
        await using var host = await Harness.StartAsync();
        var response = await Send(host, "/control/metrics", token: null);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Audit_WithQueryToken_IsDenied_LeakageGuard()
    {
        await using var host = await Harness.StartAsync();
        var response = await Send(host, "/control/audit", QueryToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "the durable audit log (repo-scoped, cross-tenant counts) must never be query-plane reachable");
    }

    [TestMethod]
    public async Task Pilot_WithQueryToken_IsDenied()
    {
        await using var host = await Harness.StartAsync();
        var response = await Send(host, "/control/pilot?workload=trusted", QueryToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Metrics_WithControlToken_ReportsAllCriterion5Signals()
    {
        await using var host = await Harness.StartAsync();

        // Drive one real ensure through the control plane so there is a completed job to measure.
        await EnsureViaControl(host);

        var response = await Send(host, "/control/metrics", ControlToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Every criterion-5 signal is present in the JSON snapshot.
        foreach (var key in new[]
        {
            "indexing_latency", "queue_delay", "query_latency", "jobs", "success_rate",
            "completeness_rate", "recent_jobs", "window_start_unix_ms", "window_end_unix_ms",
            "sample_count", "worker_capacity", "storage", "cache_reuse", "alerts", "cost_by_repository"
        })
            StringAssert.Contains(body, key, $"metrics snapshot reports '{key}' (criterion 5)");
    }

    [TestMethod]
    public async Task Metrics_PrometheusFormat_IsTextExposition()
    {
        await using var host = await Harness.StartAsync();
        await EnsureViaControl(host);

        var response = await Send(host, "/control/metrics?format=prometheus", ControlToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "# TYPE sextant_job_success_rate gauge");
        StringAssert.Contains(body, "# TYPE sextant_recent_job_completeness_rate gauge");
        StringAssert.Contains(body, "sextant_recent_jobs_sample_count");
        StringAssert.Contains(body, "sextant_recent_jobs_window_start_unix_ms");
        StringAssert.Contains(body, "sextant_jobs_complete");
    }

    [TestMethod]
    public async Task Audit_WithControlToken_RecordsTheEnsure()
    {
        await using var host = await Harness.StartAsync();
        await EnsureViaControl(host);

        var response = await Send(host, "/control/audit?action=ensure", ControlToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "\"action\":\"ensure\"", "the ensure was durably audited (criterion 5)");
        StringAssert.Contains(body, "\"outcome\":\"complete\"");
        // The actor is a non-reversible hash, never the raw control token.
        Assert.IsFalse(body.Contains(ControlToken), "the raw control token must never appear in the audit log");
    }

    [TestMethod]
    public async Task Pilot_WithControlToken_EvaluatesGate()
    {
        await using var host = await Harness.StartAsync();
        var response = await Send(host, "/control/pilot?workload=untrusted", ControlToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "hard_os_isolation",
            "an untrusted pilot gate names the #76 hard-isolation precondition");
        StringAssert.Contains(body, "\"ready\":false", "untrusted is not pilot-ready without #76");
    }

    [TestMethod]
    public async Task Pilot_IgnoresRequestSuppliedCapabilityFlags()
    {
        // The #76 hard-isolation precondition (and the DR/backup signal) are SERVICE capabilities, not
        // request parameters — a caller must not be able to assert them into existence via query flags.
        await using var host = await Harness.StartAsync();
        var response = await Send(
            host, "/control/pilot?workload=untrusted&hard_isolation=true&recent_backup=true", ControlToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "\"ready\":false",
            "request-supplied hard_isolation/recent_backup flags are ignored — #76 is still not satisfied");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Completeness_ReflectsTerminalJobs_InMetricsAndPilot(bool partial)
    {
        await using var host = await Harness.StartAsync(partial);
        for (var i = 0; i < 5; i++)
            await EnsureViaControl(host, $"commit-{i}");
        host.SetTerminalJobCompletionTime(DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());

        using var metricsResponse = await Send(host, "/control/metrics", ControlToken);
        Assert.AreEqual(HttpStatusCode.OK, metricsResponse.StatusCode);
        using var metrics = JsonDocument.Parse(await metricsResponse.Content.ReadAsStringAsync());
        var jobs = metrics.RootElement.GetProperty("jobs");
        Assert.AreEqual(1.0, jobs.GetProperty("success_rate").GetDouble());
        Assert.AreEqual(partial ? 0.0 : 1.0, jobs.GetProperty("completeness_rate").GetDouble());
        var alerts = metrics.RootElement.GetProperty("alerts").EnumerateArray().ToArray();
        Assert.AreEqual(partial, alerts.Any(a => a.GetProperty("id").GetString() == "low_completeness_rate"));
        Assert.IsFalse(alerts.Any(a => a.GetProperty("id").GetString() == "low_success_rate"));
        var recentJobs = metrics.RootElement.GetProperty("recent_jobs");
        Assert.AreEqual(5, recentJobs.GetProperty("sample_count").GetInt64());
        Assert.AreEqual(partial ? 0.0 : 1.0, recentJobs.GetProperty("completeness_rate").GetDouble());
        Assert.IsTrue(recentJobs.GetProperty("window_start_unix_ms").GetInt64() <
                      recentJobs.GetProperty("window_end_unix_ms").GetInt64());

        using var pilotResponse = await Send(host, "/control/pilot?workload=trusted", ControlToken);
        Assert.AreEqual(HttpStatusCode.OK, pilotResponse.StatusCode);
        using var pilot = JsonDocument.Parse(await pilotResponse.Content.ReadAsStringAsync());
        var check = pilot.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "snapshot_completeness");
        Assert.AreEqual(!partial, check.GetProperty("passed").GetBoolean());
        Assert.IsFalse(check.GetProperty("blocking").GetBoolean());
    }

    [TestMethod]
    public async Task Pilot_WithoutTerminalJobs_CompletenessIsNotAssessed()
    {
        await using var host = await Harness.StartAsync();
        using var response = await Send(host, "/control/pilot?workload=untrusted", ControlToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var pilot = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var check = pilot.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "snapshot_completeness");
        Assert.IsFalse(check.GetProperty("passed").GetBoolean());
        Assert.IsTrue(check.GetProperty("blocking").GetBoolean());
        StringAssert.Contains(check.GetProperty("message").GetString()!, "not assessed");
    }

    private static async Task EnsureViaControl(Harness host, string commit = "commit-aaaa")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/control/ensure")
        {
            Content = JsonContent.Create(ServiceTestFixtures.Request(commit: commit), options: ServiceJson.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
        var response = await host.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Send(Harness host, string url, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HttpClient Client { get; init; } = null!;
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public void SetTerminalJobCompletionTime(long completedAt)
        {
            using var connection = Db.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE snapshot_jobs SET completed_at = @completed_at
                WHERE status IN (@complete, @partial, @failed, @unsupported, @cancelled);
                """;
            command.Parameters.AddWithValue("@completed_at", completedAt);
            command.Parameters.AddWithValue("@complete", SnapshotJobStatus.Complete);
            command.Parameters.AddWithValue("@partial", SnapshotJobStatus.Partial);
            command.Parameters.AddWithValue("@failed", SnapshotJobStatus.Failed);
            command.Parameters.AddWithValue("@unsupported", SnapshotJobStatus.Unsupported);
            command.Parameters.AddWithValue("@cancelled", SnapshotJobStatus.Cancelled);
            command.ExecuteNonQuery();
        }

        public static async Task<Harness> StartAsync(bool partial = false)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken, queryToken: QueryToken);
            var worker = new FakeSnapshotWorker(db, partial
                ? (self, request) => SnapshotWorkResult.Partial(
                    ServiceTestFixtures.PublishComplete(self.Database, request), "Some projects did not load.")
                : null);
            var service = SnapshotService.Start(options, worker, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness { Client = app.GetTestClient(), App = app, Service = service, Db = db, DbPath = dbPath };
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
