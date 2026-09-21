using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Acceptance criterion 4 (a complete snapshot is queryable through authenticated HTTP MCP WITHOUT
/// ProcessStack) plus the control/query auth separation and the health-vs-readiness distinction. Runs
/// fully in-process over <see cref="TestServer"/> with an ephemeral SQLite catalog — no real network.
/// </summary>
[TestClass]
public class ServiceHttpTests
{
    private const string ControlToken = "control-secret";
    private const string QueryToken = "query-secret";

    [TestMethod]
    public async Task Health_IsOpen_AndReportsAvailable()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: false);
        var response = await host.Client.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "available");
    }

    [TestMethod]
    public async Task Ready_Is200_WhenWorkerCapacityPresent()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: false);
        var response = await host.Client.GetAsync("/ready");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Ready_Is503_WhenNoWorkerCapacity()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: false, seedComplete: false);
        var response = await host.Client.GetAsync("/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
            "a query-only node reports no worker capacity (readiness), distinct from availability (health)");
    }

    [TestMethod]
    public async Task Query_CompleteSnapshotSymbols_AreReturnedWithQueryToken()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: true);
        var url = $"/query/snapshots/{host.SeededIdentityHash}/symbols?limit=100";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", QueryToken);
        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<SnapshotSymbolPage>(ServiceJson.Options);
        Assert.IsNotNull(page);
        Assert.IsTrue(page!.Symbols.Count > 0,
            "a complete snapshot is queryable through authenticated HTTP without ProcessStack (criterion 4)");
    }

    [TestMethod]
    public async Task Query_WithoutToken_Is401()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: true);
        var response = await host.Client.GetAsync($"/query/snapshots/{host.SeededIdentityHash}/symbols");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Control_WithoutToken_Is401()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: false);
        var response = await host.Client.PostAsJsonAsync("/control/ensure", ServiceTestFixtures.Request(), ServiceJson.Options);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Control_Ensure_WithToken_ProducesAndPublishes()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/control/ensure")
        {
            Content = JsonContent.Create(ServiceTestFixtures.Request(), options: ServiceJson.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);

        var response = await host.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<EnsureSnapshotResult>(ServiceJson.Options);
        Assert.AreEqual(SnapshotJobStatus.Complete, result!.Status);
    }

    [TestMethod]
    public async Task Mcp_Endpoint_IsMapped_AndAuthGated()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: true);

        // Without the query token, the /mcp plane is rejected by the auth middleware.
        using (var anon = BuildInitialize(token: null))
        {
            var denied = await host.Client.SendAsync(anon);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        // With the query token, the MCP transport is reachable (mapped + authorized) — proving HTTP MCP
        // access exists without ProcessStack. The full JSON-RPC handshake is covered by McpHttpProtocolTests.
        using var authed = BuildInitialize(QueryToken);
        var response = await host.Client.SendAsync(authed);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, "a valid query token authorizes the MCP plane");
        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode, "the MCP endpoint is mapped");
    }

    private static HttpRequestMessage BuildInitialize(string? token)
    {
        const string body = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
            """;
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>An in-process service + HTTP host over TestServer, with an optional pre-published snapshot.</summary>
    private sealed class ServiceHttpHarness : IAsyncDisposable
    {
        public HttpClient Client { get; init; } = null!;
        public string SeededIdentityHash { get; init; } = "";
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<ServiceHttpHarness> StartAsync(bool withWorker, bool seedComplete)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            var request = ServiceTestFixtures.Request();
            if (seedComplete)
                ServiceTestFixtures.PublishComplete(db, request);

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken, queryToken: QueryToken);
            var worker = withWorker ? new FakeSnapshotWorker(db) : null;
            var service = SnapshotService.Start(options, worker, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new ServiceHttpHarness
            {
                Client = app.GetTestClient(),
                SeededIdentityHash = request.ToIdentity().Hash,
                App = app,
                Service = service,
                Db = db,
                DbPath = dbPath
            };
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
