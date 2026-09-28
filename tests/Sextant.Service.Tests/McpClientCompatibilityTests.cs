using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Generic MCP-client compatibility for the remote query plane. The official MCP C# SDK client connects to
/// the service's stateless <c>/mcp</c> endpoint over Streamable HTTP with only a static bearer header. That
/// is how any pooled or proxying MCP client connects, so these tests pin that the surface needs nothing
/// client-specific: <c>initialize</c>, <c>tools/list</c> and <c>tools/call</c> all succeed, no session
/// affinity is required, and the query-token gate still rejects a wrong or missing bearer. Runs fully
/// in-process over <see cref="TestServer"/> with an ephemeral SQLite catalog.
/// </summary>
[TestClass]
public class McpClientCompatibilityTests
{
    private const string ControlToken = "control-secret";
    private const string QueryToken = "query-secret";

    /// <summary>Tools that must never be reachable remotely (they bypass or out-scope the read gate).</summary>
    private static readonly string[] LocalOnlyTools = ["get_source_context", "get_daemon_status", "get_base_snapshot_symbols"];

    [TestMethod]
    public async Task SdkClient_StaticBearer_InitializeListAndCall_Succeed()
    {
        await using var host = await Harness.StartAsync();
        await using var client = await McpClient.CreateAsync(host.CreateTransport(QueryToken));

        // initialize: the handshake completed and the server advertised its tools capability.
        Assert.IsNotNull(client.ServerInfo, "initialize returned server info");
        Assert.IsNotNull(client.ServerCapabilities.Tools, "the server advertises the tools capability");
        Assert.IsNull(client.SessionId,
            "the stateless transport issues no Mcp-Session-Id, so a pooled client needs no session affinity");

        // tools/list: every allowlisted remote query tool is listed and no local-only tool leaks.
        var listed = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        foreach (var expected in RemoteToolNames())
            CollectionAssert.Contains(listed, expected, $"tools/list includes the allowlisted tool '{expected}'");
        foreach (var localOnly in LocalOnlyTools)
            CollectionAssert.DoesNotContain(listed, localOnly, $"tools/list never exposes the local-only tool '{localOnly}'");

        // tools/call: a read tool executes against the seeded complete snapshot and returns its data.
        var result = await client.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["name"] = "global::App.Type0" });
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.IsFalse(result.IsError is true, $"tools/call is not a tool error: {text}");
        StringAssert.Contains(text, "global::App.Type0", "the tool answered from the seeded snapshot");

        // A pooled client reuses one connection for many calls; each stateless POST stands alone.
        var status = await client.CallToolAsync("get_index_status");
        Assert.IsFalse(status.IsError is true, "a second call over the same client also succeeds");
        Assert.IsTrue(status.Content.OfType<TextContentBlock>().Any(), "the second call returned MCP content");
    }

    [TestMethod]
    [DataRow(null, DisplayName = "missing bearer")]
    [DataRow("wrong-token", DisplayName = "wrong bearer")]
    [DataRow(ControlToken, DisplayName = "control token is not a query credential")]
    public async Task SdkClient_WrongOrMissingBearer_IsRejectedAtInitialize(string? token)
    {
        await using var host = await Harness.StartAsync();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await McpClient.CreateAsync(host.CreateTransport(token)));

        Assert.AreEqual(HttpStatusCode.Unauthorized, ex.StatusCode,
            "the query-token gate rejects the client before any MCP method runs");
    }

    /// <summary>The MCP tool names declared by the remote allowlist (<see cref="ServiceApp.RemoteQueryTools"/>).</summary>
    private static List<string> RemoteToolNames()
    {
        var names = ServiceApp.RemoteQueryTools
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToList();
        Assert.IsTrue(names.Count > 0, "the remote allowlist declares at least one named tool");
        return names;
    }

    /// <summary>An in-process service + HTTP host over TestServer with one pre-published complete snapshot.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private HttpClient Client { get; init; } = null!;
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<Harness> StartAsync()
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            // Publish one complete snapshot and point the repository's default branch at it, so the
            // single-repository read path selects it the way a normal ensure leaves it.
            var snapId = ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request());
            var snapshots = new SnapshotStore(db.GetConnection());
            var repoId = snapshots.GetById(snapId)!.RepositoryId;
            snapshots.SetBranchPointer(snapshots.EnsureBranch(repoId, "main", isDefault: true, now: 1), snapId, now: 1);

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken, queryToken: QueryToken);
            var service = SnapshotService.Start(options, null, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness { Client = app.GetTestClient(), App = app, Service = service, Db = db, DbPath = dbPath };
        }

        /// <summary>
        /// A Streamable-HTTP client transport with a static bearer header, the shape a pooled MCP client uses.
        /// The mode is pinned so a 401 surfaces as-is instead of triggering an auto-detect fallback to SSE.
        /// </summary>
        public HttpClientTransport CreateTransport(string? bearer)
        {
            var headers = new Dictionary<string, string>();
            if (bearer is not null)
                headers["Authorization"] = $"Bearer {bearer}";

            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Client.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers,
                Name = "generic-mcp-client"
            }, Client);
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
