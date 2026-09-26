using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Core;
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
    public async Task PartialCoverageSnapshot_IsPartialOnEnsure_PageAndResolve()
    {
        // Issue #119 acceptance: a published snapshot with partial coverage answers partial everywhere a
        // client can see it — ensure (status + reason + coverage), the symbol page (complete=false,
        // published=true, coverage), and resolve (additive coverage block).
        var coverage = new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            Reasons = ["1 of 2 discovered solution(s) were not selected"],
            SolutionsDiscovered = 2,
            SolutionsNotSelected = 1
        };
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: true, coverage);

        using var ensure = new HttpRequestMessage(HttpMethod.Post, "/control/ensure")
        {
            Content = JsonContent.Create(ServiceTestFixtures.Request(branch: "main"), options: ServiceJson.Options)
        };
        ensure.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
        var ensureResponse = await host.Client.SendAsync(ensure);
        var ensureJson = await ensureResponse.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, ensureResponse.StatusCode, ensureJson);
        using (var doc = System.Text.Json.JsonDocument.Parse(ensureJson))
        {
            Assert.AreEqual("partial", doc.RootElement.GetProperty("status").GetString());
            StringAssert.Contains(doc.RootElement.GetProperty("reason").GetString(), "not selected");
            Assert.AreEqual("partial", doc.RootElement.GetProperty("coverage").GetProperty("verdict").GetString());
            Assert.AreEqual(1, doc.RootElement.GetProperty("coverage").GetProperty("solutions_not_selected").GetInt32());
        }

        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, $"/query/snapshots/{host.SeededIdentityHash}/symbols");
        pageRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", QueryToken);
        var pageJson = await (await host.Client.SendAsync(pageRequest)).Content.ReadAsStringAsync();
        using (var doc = System.Text.Json.JsonDocument.Parse(pageJson))
        {
            Assert.IsTrue(doc.RootElement.GetProperty("symbols").GetArrayLength() > 0, "partial rows are still served");
            Assert.IsFalse(doc.RootElement.GetProperty("complete").GetBoolean());
            Assert.IsTrue(doc.RootElement.GetProperty("published").GetBoolean());
            Assert.AreEqual("partial", doc.RootElement.GetProperty("coverage").GetProperty("verdict").GetString());
        }

        using var resolve = new HttpRequestMessage(HttpMethod.Get,
            $"/control/resolve?repository={Uri.EscapeDataString(ServiceTestFixtures.Request().RepositoryRemoteUrl)}&branch=main");
        resolve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
        var resolveResponse = await host.Client.SendAsync(resolve);
        var resolveJson = await resolveResponse.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resolveResponse.StatusCode, resolveJson);
        using (var doc = System.Text.Json.JsonDocument.Parse(resolveJson))
            Assert.AreEqual("partial", doc.RootElement.GetProperty("coverage").GetProperty("verdict").GetString());
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
        // access exists without ProcessStack.
        using var authed = BuildInitialize(QueryToken);
        var response = await host.Client.SendAsync(authed);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, "a valid query token authorizes the MCP plane");
        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode, "the MCP endpoint is mapped");
    }

    /// <summary>
    /// Regression for the production gateway scenario: ProcessStack's Sextant query gateway is a "faithful
    /// JSON-RPC proxy" that handles <c>initialize</c>/<c>notifications/initialized</c> LOCALLY and forwards
    /// only <c>tools/list</c> + <c>tools/call</c> verbatim — it never performs an MCP session handshake and
    /// never sends an <c>Mcp-Session-Id</c> header. With the default STATEFUL Streamable-HTTP transport a
    /// bare <c>tools/list</c> was rejected with HTTP 400 ("A new session can only be created by an
    /// initialize request. Include a valid Mcp-Session-Id header for non-initialize requests."), breaking
    /// the whole cross-repo gateway. The stateless transport must answer a SINGLE bare <c>tools/list</c>
    /// POST — no prior initialize, no session header — with 200 and the RemoteQueryTools allowlist.
    /// </summary>
    [TestMethod]
    public async Task Mcp_BareToolsList_NoInitialize_NoSession_ReturnsToolsOverStatelessTransport()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: true);

        using var request = BuildJsonRpc("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", QueryToken);
        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            $"a bare tools/list must succeed statelessly; got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var doc = await ReadJsonRpcAsync(response);
        var root = doc.RootElement;
        Assert.AreEqual(1, root.GetProperty("id").GetInt32());
        Assert.IsFalse(root.TryGetProperty("error", out var err1), $"a stateless tools/list is not a JSON-RPC error: {(err1.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "" : err1.GetRawText())}");

        var toolNames = root.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        CollectionAssert.Contains(toolNames, "find_references",
            "tools/list enumerates the RemoteQueryTools allowlist without an initialize handshake");
        CollectionAssert.DoesNotContain(toolNames, "get_source_context",
            "the local-only tools stay excluded from the remote allowlist under stateless mode");
    }

    /// <summary>
    /// The gateway's other forwarded verb: a bare <c>tools/call</c> (no initialize, no session header) must
    /// dispatch and execute a RemoteQueryTool statelessly, routing through the fail-closed authorizer.
    /// </summary>
    [TestMethod]
    public async Task Mcp_BareToolsCall_NoInitialize_NoSession_DispatchesToolStatelessly()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: true);

        using var request = BuildJsonRpc(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_index_status","arguments":{}}}""",
            QueryToken);
        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            $"a bare tools/call must succeed statelessly; got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var doc = await ReadJsonRpcAsync(response);
        var root = doc.RootElement;
        Assert.AreEqual(2, root.GetProperty("id").GetInt32());
        Assert.IsFalse(root.TryGetProperty("error", out _), "a stateless tools/call is not a JSON-RPC error");
        Assert.IsTrue(root.GetProperty("result").TryGetProperty("content", out _),
            "the tool executed and returned MCP content statelessly");
    }

    /// <summary>The bare gateway verbs are still auth-gated: no query token ⇒ 401, never a tool result.</summary>
    [TestMethod]
    public async Task Mcp_BareToolsList_WithoutToken_IsRejected()
    {
        await using var host = await ServiceHttpHarness.StartAsync(withWorker: true, seedComplete: true);

        using var request = BuildJsonRpc("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", token: null);
        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "stateless mode must not weaken the query-token auth gate on /mcp");
    }

    private static HttpRequestMessage BuildInitialize(string? token) =>
        BuildJsonRpc(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""",
            token);

    private static HttpRequestMessage BuildJsonRpc(string body, string? token)
    {
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

    /// <summary>
    /// Reads a JSON-RPC response from the Streamable-HTTP transport, tolerating either a plain
    /// <c>application/json</c> body or an <c>text/event-stream</c> (SSE) framing whose <c>data:</c> line
    /// carries the JSON-RPC message.
    /// </summary>
    private static async Task<System.Text.Json.JsonDocument> ReadJsonRpcAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        var payload = raw;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream" ||
            raw.StartsWith("event:", StringComparison.Ordinal) ||
            raw.Contains("\ndata:", StringComparison.Ordinal) ||
            raw.StartsWith("data:", StringComparison.Ordinal))
        {
            payload = string.Concat(raw
                .Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l["data:".Length..].Trim()));
        }
        return System.Text.Json.JsonDocument.Parse(payload);
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

        public static async Task<ServiceHttpHarness> StartAsync(
            bool withWorker, bool seedComplete, SnapshotCoverage? seedCoverage = null)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            var request = ServiceTestFixtures.Request();
            if (seedComplete)
            {
                var snapId = ServiceTestFixtures.PublishComplete(db, request);
                if (seedCoverage is not null)
                    new SnapshotCoverageStore(db.GetConnection()).Record(snapId, seedCoverage, 1);
            }

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
