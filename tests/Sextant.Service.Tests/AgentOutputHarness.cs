using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// An in-process service host over <see cref="AgentOutputFixture"/>, configured like the live service
/// (<c>REQUIRE_REPOSITORY_SELECTION=true</c>, a query token), that speaks MCP JSON-RPC over <c>/mcp</c>.
/// </summary>
internal sealed class AgentOutputHarness : IAsyncDisposable
{
    public const string QueryToken = "query-secret";

    public HttpClient Client { get; private init; } = null!;
    public AgentOutputFixture Fixture { get; private init; } = null!;
    private WebApplication App { get; init; } = null!;
    private SnapshotService Service { get; init; } = null!;
    private IndexDatabase Db { get; init; } = null!;
    private string DbPath { get; init; } = "";

    /// <summary>
    /// Starts the host. <paramref name="delegateCallers"/> also configures a delegate token and a caller key (the
    /// gateway deployment), which changes only what <c>tools/list</c> says about the <c>repository</c> argument; the
    /// harness itself still calls with the query token.
    /// </summary>
    public static async Task<AgentOutputHarness> StartAsync(bool delegateCallers = false)
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var fixture = AgentOutputFixture.Create(db);

        var options = ServiceTestFixtures.NewOptions(dbPath, queryToken: QueryToken) with
        {
            RequireRepositorySelection = true
        };
        if (delegateCallers)
        {
            options = options with
            {
                DelegateTokens = ["delegate-secret"],
                CallerAssertion = new Sextant.Service.CallerIdentity.CallerAssertionOptions
                {
                    Keys = Sextant.Service.CallerIdentity.CallerKeyRing.Create([("kid-a", new byte[32], "tenant-a")]),
                    Audience = CallerAssertionSigner.Audience
                }
            };
        }
        var service = SnapshotService.Start(options, new FakeSnapshotWorker(db), db);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ServiceApp.RegisterServices(builder, options, service);
        var app = builder.Build();
        ServiceApp.MapEndpoints(app, options);
        await app.StartAsync();

        return new AgentOutputHarness
        {
            Client = app.GetTestClient(),
            Fixture = fixture,
            App = app,
            Service = service,
            Db = db,
            DbPath = dbPath
        };
    }

    /// <summary>Sends one JSON-RPC request and returns its <c>result</c> object (raw JSON text).</summary>
    public async Task<string> RpcAsync(string method, JsonObject? parameters = null)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method };
        if (parameters is not null)
            body["params"] = parameters;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", QueryToken);

        using var response = await Client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, raw);

        var payload = raw.Contains("data:", StringComparison.Ordinal)
            ? string.Concat(raw.Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l["data:".Length..].Trim()))
            : raw;
        using var rpc = JsonDocument.Parse(payload);
        Assert.IsTrue(rpc.RootElement.TryGetProperty("result", out var result), payload);
        return result.GetRawText();
    }

    /// <summary>Calls a tool and returns its text content (the tool's JSON response body).</summary>
    public async Task<string> CallTextAsync(string tool, JsonObject arguments)
    {
        var result = await RpcAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() });
        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    /// <summary>Calls a tool against repository A and parses its JSON response body.</summary>
    public async Task<JsonElement> CallAsync(string tool, JsonObject arguments, string repository = AgentOutputFixture.RepoA)
    {
        arguments["repository"] ??= repository;
        var text = await CallTextAsync(tool, arguments);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Service.Dispose();
        SqliteTestDatabase.Delete(DbPath, Db);
        Fixture.Dispose();
    }
}
