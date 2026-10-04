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
/// client-specific: the connect handshake (<c>server/discover</c> by default, or <c>initialize</c> for a
/// client pinned to an older protocol revision), <c>tools/list</c> and <c>tools/call</c> all succeed, no session
/// affinity is required, and the query-token gate still rejects a wrong or missing bearer. Runs fully
/// in-process over <see cref="TestServer"/> with an ephemeral SQLite catalog.
/// </summary>
[TestClass]
public class McpClientCompatibilityTests
{
    private const string ControlToken = "control-secret";
    private const string QueryToken = "query-secret";
    private const string DelegateToken = "delegate-secret";

    /// <summary>Tools that must never be reachable remotely (they bypass or out-scope the read gate).</summary>
    private static readonly string[] LocalOnlyTools = ["get_source_context", "get_daemon_status", "get_base_snapshot_symbols"];

    [TestMethod]
    [DataRow(null, DisplayName = "SDK default protocol (server/discover)")]
    [DataRow("2025-06-18", DisplayName = "pinned older protocol (initialize)")]
    public async Task SdkClient_StaticBearer_InitializeListAndCall_Succeed(string? protocolVersion)
    {
        await using var host = await Harness.StartAsync();
        await using var client = await McpClient.CreateAsync(
            host.CreateTransport(QueryToken), new McpClientOptions { ProtocolVersion = protocolVersion });

        // connect: the handshake completed and the server advertised its tools capability.
        Assert.IsNotNull(client.NegotiatedProtocolVersion, "the client negotiated a protocol version");
        if (protocolVersion is not null)
            Assert.AreEqual(protocolVersion, client.NegotiatedProtocolVersion, "the server accepted the pinned revision");
        Assert.IsNotNull(client.ServerInfo, "the handshake returned server info");
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

    [TestMethod]
    public async Task SdkClient_RepositoryToolArgument_SelectsThatRepository()
    {
        const string gadgets = "https://github.com/acme/gadgets";
        await using var host = await Harness.StartAsync(secondRepository: gadgets);
        await using var client = await McpClient.CreateAsync(host.CreateTransport(QueryToken));

        // tools/list: the reserved selector arguments are advertised as optional string arguments.
        var findSymbol = (await client.ListToolsAsync()).Single(t => t.Name == "find_symbol");
        var properties = findSymbol.JsonSchema.GetProperty("properties");
        Assert.AreEqual("string", properties.GetProperty("repository").GetProperty("type").GetString());
        Assert.AreEqual("string", properties.GetProperty("branch").GetProperty("type").GetString());

        // tools/call: the SDK forwards the argument verbatim over the static-header connection, and it
        // selects the repository for this call only.
        var result = await client.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["name"] = "global::App.Type0", ["repository"] = "acme/gadgets" });
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.IsFalse(result.IsError is true, $"tools/call is not a tool error: {text}");
        var results = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("results");
        Assert.AreEqual(1, results.GetArrayLength(), text);
        Assert.AreEqual($"proj_{host.SecondRepositorySnapshot}", results[0].GetProperty("project_id").GetString(),
            "the repository argument pinned the read to that repository's snapshot");

        // A named branch without a complete snapshot never widens to another snapshot.
        var miss = await client.CallToolAsync(
            "find_symbol",
            new Dictionary<string, object?> { ["name"] = "global::App.Type0", ["repository"] = gadgets, ["branch"] = "nope" });
        var missText = string.Concat(miss.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.AreEqual(0, System.Text.Json.JsonDocument.Parse(missText).RootElement.GetProperty("results").GetArrayLength(),
            missText);
    }

    /// <summary>
    /// SVC-3: a pooled client holding a delegate token connects and lists tools with no caller assertion (one
    /// connection serves many callers), and every <c>tools/call</c> then needs one. A client that sends a verified
    /// assertion gets past the caller check, and its read is still denied until per-caller grants exist.
    /// </summary>
    [TestMethod]
    [DataRow(null, DisplayName = "SDK default protocol (server/discover)")]
    [DataRow("2025-06-18", DisplayName = "pinned older protocol (initialize)")]
    public async Task SdkClient_DelegateToken_PoolConnectsWithoutCaller_AndCallsNeedOne(string? protocolVersion)
    {
        var key = CallerAssertionSigner.NewKey();
        await using var host = await Harness.StartAsync(callerKey: key);

        await using (var pooled = await McpClient.CreateAsync(
            host.CreateTransport(DelegateToken), new McpClientOptions { ProtocolVersion = protocolVersion }))
        {
            Assert.IsNotNull(pooled.NegotiatedProtocolVersion, "a delegate client connects without a caller");
            var listed = (await pooled.ListToolsAsync()).Select(t => t.Name).ToList();
            CollectionAssert.Contains(listed, "find_symbol", "and lists tools without one");

            var call = await pooled.CallToolAsync("find_symbol", new Dictionary<string, object?> { ["name"] = "global::App.Type0" });
            var text = string.Concat(call.Content.OfType<TextContentBlock>().Select(c => c.Text));
            Assert.IsTrue(call.IsError is true, text);
            StringAssert.Contains(text, "\"caller_required\"");
        }

        var assertion = CallerAssertionSigner.Sign(key, "kid-a", CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow));
        await using var caller = await McpClient.CreateAsync(
            host.CreateTransport(DelegateToken, new Dictionary<string, string> { [Sextant.Service.CallerIdentity.CallerAssertionOptions.DefaultHeader] = assertion }),
            new McpClientOptions { ProtocolVersion = protocolVersion });
        var read = await caller.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["name"] = "global::App.Type0", ["repository"] = "org/app" });
        var readText = string.Concat(read.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.IsFalse(readText.Contains("caller_required", StringComparison.Ordinal), readText);
        Assert.AreEqual(0, System.Text.Json.JsonDocument.Parse(readText).RootElement.GetProperty("results").GetArrayLength(),
            $"a delegate read is denied until grants exist: {readText}");
    }

    /// <summary>
    /// elevenworks/ProcessStack#3258: the platform's MCP gateway keeps ONE pooled client per connection and signs
    /// each request's own caller into it. The service authorizes every <c>tools/call</c> by the caller of that
    /// request, never by the connection: the granted caller reads, another caller on the same client gets the
    /// uniform not-found, the granted caller reads again, and a request with no caller is refused, including
    /// when the calls are in flight together.
    /// </summary>
    [TestMethod]
    public async Task SdkClient_OnePooledClient_AuthorizesEachRequestByItsOwnCaller()
    {
        var key = CallerAssertionSigner.NewKey();
        await using var host = await Harness.StartAsync(callerKey: key, grantedUser: "user-1");
        var caller = new AsyncLocal<string?>();
        await using var pooled = await McpClient.CreateAsync(host.CreatePooledTransport(DelegateToken, () => caller.Value));

        async Task<(bool IsError, string Text)> FindAs(string? sub)
        {
            caller.Value = sub is null
                ? null
                : CallerAssertionSigner.Sign(key, "kid-a", CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow, sub: sub, jti: $"jti-{Guid.NewGuid():N}"));
            var result = await pooled.CallToolAsync(
                "find_symbol", new Dictionary<string, object?> { ["name"] = "global::App.Type0", ["repository"] = "org/app" });
            return (result.IsError is true, string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        }

        static int Results(string text) =>
            System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("results").GetArrayLength();

        var granted = await FindAs("user-1");
        Assert.IsFalse(granted.IsError, granted.Text);
        Assert.AreEqual(1, Results(granted.Text), $"the granted caller reads the repository: {granted.Text}");

        var other = await FindAs("user-2");
        Assert.IsTrue(other.IsError, other.Text);
        StringAssert.Contains(other.Text, "\"repository_not_found\"", "another caller gets the uniform not-found");
        Assert.AreEqual(0, Results(other.Text), $"another caller on the same client reads nothing: {other.Text}");

        var again = await FindAs("user-1");
        Assert.AreEqual(1, Results(again.Text), $"the grant still applies after another caller's request: {again.Text}");

        var anonymous = await FindAs(null);
        Assert.IsTrue(anonymous.IsError, anonymous.Text);
        StringAssert.Contains(anonymous.Text, "\"caller_required\"");

        // In flight together over the one client: each request is still decided by its own caller.
        var subjects = new[] { "user-1", "user-2", "user-1", "user-2", "user-1", "user-2" };
        var concurrent = await Task.WhenAll(subjects.Select(sub => Task.Run(() => FindAs(sub))));
        for (var i = 0; i < subjects.Length; i++)
        {
            Assert.AreEqual(subjects[i] != "user-1", concurrent[i].IsError, concurrent[i].Text);
            Assert.AreEqual(subjects[i] == "user-1" ? 1 : 0, Results(concurrent[i].Text),
                $"concurrent call {i} as {subjects[i]}: {concurrent[i].Text}");
        }
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

        /// <summary>The complete snapshot of the second repository, when one was published.</summary>
        public long? SecondRepositorySnapshot { get; private init; }

        public static async Task<Harness> StartAsync(string? secondRepository = null, byte[]? callerKey = null, string? grantedUser = null)
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
            if (grantedUser is not null)
            {
                var repository = ServiceTestFixtures.Request().RepositoryRemoteUrl;
                new RepositoryGrantStore(db.GetConnection()).Upsert(
                    "tenant-a", grantedUser, Sextant.Service.Grants.RepositoryGrantKey.Of(repository), repository, "",
                    RepositoryGrantSource.Self, now: 1);
            }
            long? secondSnapId = null;
            if (secondRepository is not null)
            {
                secondSnapId = ServiceTestFixtures.PublishComplete(
                    db, ServiceTestFixtures.Request(secondRepository, "commit-bbbb"));
                var secondRepoId = snapshots.GetById(secondSnapId.Value)!.RepositoryId;
                snapshots.SetBranchPointer(
                    snapshots.EnsureBranch(secondRepoId, "main", isDefault: true, now: 1), secondSnapId.Value, now: 1);
            }

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken, queryToken: QueryToken);
            if (callerKey is not null)
            {
                options = options with
                {
                    DelegateTokens = [DelegateToken],
                    CallerAssertion = new Sextant.Service.CallerIdentity.CallerAssertionOptions
                    {
                        Keys = Sextant.Service.CallerIdentity.CallerKeyRing.Create([("kid-a", callerKey, "tenant-a")]),
                        Audience = CallerAssertionSigner.Audience
                    }
                };
            }
            var service = SnapshotService.Start(options, null, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness
            {
                Client = app.GetTestClient(), App = app, Service = service, Db = db, DbPath = dbPath,
                SecondRepositorySnapshot = secondSnapId
            };
        }

        /// <summary>
        /// A Streamable-HTTP client transport with a static bearer header, the shape a pooled MCP client uses.
        /// The mode is pinned so a 401 surfaces as-is instead of triggering an auto-detect fallback to SSE.
        /// </summary>
        public HttpClientTransport CreateTransport(string? bearer, IReadOnlyDictionary<string, string>? extraHeaders = null)
        {
            var headers = new Dictionary<string, string>();
            if (bearer is not null)
                headers["Authorization"] = $"Bearer {bearer}";
            foreach (var (name, value) in extraHeaders ?? new Dictionary<string, string>())
                headers[name] = value;

            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Client.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers,
                Name = "generic-mcp-client"
            }, Client);
        }

        /// <summary>
        /// One Streamable-HTTP client over a static delegate bearer, whose handler stamps the caller assertion
        /// <paramref name="assertion"/> returns into each request as it is sent: the shape of the platform's pooled
        /// gateway client, which signs every forwarded call's own caller.
        /// </summary>
        public HttpClientTransport CreatePooledTransport(string bearer, Func<string?> assertion)
        {
            var client = new HttpClient(new CallerStampingHandler(assertion) { InnerHandler = App.GetTestServer().CreateHandler() })
            {
                BaseAddress = Client.BaseAddress
            };
            _pooledClients.Add(client);
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Client.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
                Name = "pooled-gateway-client"
            }, client);
        }

        private readonly List<HttpClient> _pooledClients = [];

        public async ValueTask DisposeAsync()
        {
            foreach (var pooled in _pooledClients)
                pooled.Dispose();
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Service.Dispose();
            SqliteTestDatabase.Delete(DbPath, Db);
        }
    }

    /// <summary>Stamps the current caller assertion (none when null) into each request it sends.</summary>
    private sealed class CallerStampingHandler(Func<string?> assertion) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(Sextant.Service.CallerIdentity.CallerAssertionOptions.DefaultHeader);
            if (assertion() is { } value)
                request.Headers.TryAddWithoutValidation(Sextant.Service.CallerIdentity.CallerAssertionOptions.DefaultHeader, value);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
