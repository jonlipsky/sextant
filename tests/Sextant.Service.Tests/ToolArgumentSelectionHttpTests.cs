using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-2: per-call repository/branch selection through the reserved <c>repository</c> and <c>branch</c> tool
/// arguments on the service's stateless <c>/mcp</c>. The catalog holds two published repositories whose symbols
/// share a fully qualified name; <c>acme/widgets</c> also has a second branch pointing at its own snapshot, so
/// the project a <c>find_symbol</c> answers from shows exactly which (repository, branch) the call read.
/// </summary>
[TestClass]
public class ToolArgumentSelectionHttpTests
{
    private const string QueryToken = "query-secret";
    private const string ReaderToken = "widgets-reader";
    private const string Widgets = "https://github.com/acme/widgets";
    private const string Gadgets = "https://github.com/acme/gadgets";
    private const string FeatureBranch = "feature/tuning";

    // ==== tools/list ===============================================================================

    [TestMethod]
    public async Task ToolsList_AdvertisesReservedArguments_OnRepositoryScopedTools()
    {
        await using var host = await Harness.StartAsync();

        var tools = (await host.RpcAsync("tools/list", "{}")).GetProperty("tools").EnumerateArray().ToList();

        Assert.AreEqual(ServiceApp.RepositoryScopedTools.Count + ToolSelectionFilters.SelectionExemptTools.Count, tools.Count,
            "every remote tool is repository-scoped except the selection-exempt list_repositories");
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            if (ToolSelectionFilters.SelectionExemptTools.Contains(name))
            {
                var exemptProperties = tool.GetProperty("inputSchema").TryGetProperty("properties", out var p) ? p : default;
                Assert.IsFalse(exemptProperties.ValueKind == JsonValueKind.Object && exemptProperties.TryGetProperty("repository", out _), name);
                continue;
            }
            Assert.IsTrue(ServiceApp.RepositoryScopedTools.Contains(name), name);
            var schema = tool.GetProperty("inputSchema");
            var properties = schema.GetProperty("properties");
            Assert.IsTrue(IsStringSchema(properties.GetProperty("repository")), name);
            Assert.IsTrue(IsStringSchema(properties.GetProperty("branch")), name);
            if (schema.TryGetProperty("required", out var required))
            {
                var names = required.EnumerateArray().Select(r => r.GetString()).ToList();
                CollectionAssert.DoesNotContain(names, "repository", name);
                CollectionAssert.DoesNotContain(names, "branch", name);
            }
        }

        var findSymbol = tools.Single(t => t.GetProperty("name").GetString() == "find_symbol");
        Assert.AreEqual(
            "owner/repo",
            findSymbol.GetProperty("inputSchema").GetProperty("properties").GetProperty("repository")
                .GetProperty("description").GetString(),
            "the single allow-listed host enables the owner/repo short form");
    }

    [TestMethod]
    public async Task ToolsList_RepositoryDescription_SaysRequired_WhenTheServiceRequiresASelection()
    {
        // A client may drop the server instructions, so the tool schema alone must tell the agent to name the
        // repository; still one short line, since it repeats on every repository-scoped tool.
        await using var host = await Harness.StartAsync(requireSelection: true);

        var tools = (await host.RpcAsync("tools/list", "{}")).GetProperty("tools").EnumerateArray()
            .Where(t => ServiceApp.RepositoryScopedTools.Contains(t.GetProperty("name").GetString()!))
            .ToList();

        Assert.AreEqual(ServiceApp.RepositoryScopedTools.Count, tools.Count);
        foreach (var tool in tools)
        {
            Assert.AreEqual("Required: owner/repo",
                tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("repository")
                    .GetProperty("description").GetString(),
                tool.GetProperty("name").GetString());
        }
    }

    // ==== tools/call: repository ====================================================================

    [TestMethod]
    public async Task Call_RepositoryArgument_PinsRepository_AndIsStripped()
    {
        await using var host = await Harness.StartAsync(requireSelection: true);

        var call = await host.CallAsync("find_symbol", new() { ["name"] = "global::App.Type0", ["repository"] = Gadgets });

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual($"proj_{host.GadgetsMain}", ProjectOf(call.Body), "the argument selected the repository");
        CollectionAssert.AreEquivalent(new[] { "name" }, host.Probe.ArgumentNames,
            "the reserved argument is removed before the tool binds its arguments");
        Assert.AreEqual(new ToolCallSelection(Gadgets, null, ToolSelectionSource.Argument), host.Probe.Selection);
    }

    [TestMethod]
    [DataRow("github.com/acme/gadgets", DisplayName = "host/owner/repo")]
    [DataRow("acme/gadgets", DisplayName = "owner/repo on the single host")]
    [DataRow("https://GitHub.com/Acme/Gadgets.git", DisplayName = "full URL, other spelling")]
    public async Task Call_RepositoryArgument_AcceptsEveryForm(string repository)
    {
        await using var host = await Harness.StartAsync(requireSelection: true);

        var call = await host.CallAsync("find_symbol", new() { ["name"] = "global::App.Type0", ["repository"] = repository });

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual($"proj_{host.GadgetsMain}", ProjectOf(call.Body));
        Assert.AreEqual(Gadgets, host.Probe.Selection?.Repository, "the selector is the canonical URL");
    }

    [TestMethod]
    public async Task Call_ArgumentOverridesNothing_WhenHeaderNamesTheSameRepository()
    {
        await using var host = await Harness.StartAsync();

        var call = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = "acme/widgets" },
            header: "https://github.com/Acme/Widgets.git");

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual($"proj_{host.WidgetsMain}", ProjectOf(call.Body));
    }

    [TestMethod]
    public async Task Call_ArgumentAndHeaderDisagree_IsSelectorConflict()
    {
        await using var host = await Harness.StartAsync();

        var call = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = Widgets }, header: Gadgets);

        Assert.IsTrue(call.IsError, "a conflict is a tool error");
        Assert.AreEqual(ToolSelectionFilters.SelectorConflictCode, ErrorCode(call.Body));
        Assert.AreEqual(0, call.Body.GetProperty("meta").GetProperty("result_count").GetInt32());
        var text = call.Body.ToString();
        Assert.IsFalse(text.Contains("widgets") || text.Contains("gadgets"), "the error names neither repository");
        Assert.IsNull(host.Probe.ArgumentNames, "the tool never ran");
    }

    [TestMethod]
    [DataRow("\"http://github.com/acme/widgets\"", "scheme_not_allowed")]
    [DataRow("\"https://example.org/acme/widgets\"", "host_not_allowed")]
    [DataRow("\"https://github.com/acme/widgets/tree/main\"", "path_not_allowed")]
    [DataRow("\"widgets\"", "path_not_allowed")]
    [DataRow("42", null)]
    public async Task Call_InvalidRepository_IsInvalidSelector(string repositoryJson, string? reason)
    {
        await using var host = await Harness.StartAsync();

        var call = await host.CallRawAsync("find_symbol",
            $$"""{"name":"global::App.Type0","repository":{{repositoryJson}}}""");

        Assert.IsTrue(call.IsError);
        Assert.AreEqual(ToolSelectionFilters.InvalidSelectorCode, ErrorCode(call.Body));
        if (reason is not null)
            StringAssert.Contains(call.Body.GetProperty("message").GetString(), reason);
        Assert.IsFalse(call.Body.ToString().Contains("widgets"), "the rejected selector is not echoed");
    }

    [TestMethod]
    public async Task Call_NullOrBlankRepository_IsAbsent()
    {
        await using var host = await Harness.StartAsync(requireSelection: true);

        var nullCall = await host.CallRawAsync("find_symbol", """{"name":"global::App.Type0","repository":null}""");
        var blankCall = await host.CallRawAsync("find_symbol", """{"name":"global::App.Type0","repository":"  "}""");

        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, ErrorCode(nullCall.Body));
        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, ErrorCode(blankCall.Body));
    }

    // ==== tools/call: branch ========================================================================

    [TestMethod]
    public async Task Call_BranchArgument_PinsThatBranchSnapshot()
    {
        await using var host = await Harness.StartAsync(requireSelection: true);

        var feature = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = Widgets, ["branch"] = FeatureBranch });
        Assert.IsFalse(feature.IsError, feature.Body.ToString());
        Assert.AreEqual($"proj_{host.WidgetsFeature}", ProjectOf(feature.Body), "the named branch is read");
        Assert.AreEqual(new ToolCallSelection(Widgets, FeatureBranch, ToolSelectionSource.Argument), host.Probe.Selection);

        var main = await host.CallAsync("find_symbol", new() { ["name"] = "global::App.Type0", ["repository"] = Widgets });
        Assert.AreEqual($"proj_{host.WidgetsMain}", ProjectOf(main.Body), "no branch reads the default branch");
    }

    [TestMethod]
    public async Task Call_BranchArgument_WithHeaderRepository_PinsThatBranchSnapshot()
    {
        await using var host = await Harness.StartAsync();

        var call = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["branch"] = FeatureBranch }, header: Widgets);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual($"proj_{host.WidgetsFeature}", ProjectOf(call.Body));
    }

    [TestMethod]
    public async Task Call_BranchWithoutRepository_IsRepositoryRequired()
    {
        // Even with the requirement off: a branch alone never widens to the unselected default.
        await using var host = await Harness.StartAsync(requireSelection: false);

        var call = await host.CallAsync("find_symbol", new() { ["name"] = "global::App.Type0", ["branch"] = FeatureBranch });

        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, ErrorCode(call.Body), call.Body.ToString());
        Assert.AreEqual(0, call.Body.GetProperty("meta").GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public async Task Call_UnknownBranch_WithoutReadPolicy_IsActionableAndNotEchoed()
    {
        await using var host = await Harness.StartAsync(requireSelection: true);

        var call = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = Widgets, ["branch"] = "no-such-branch" });

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual(ResponseBuilder.RepositoryNotFoundCode, ErrorCode(call.Body));
        Assert.AreEqual(0, call.Body.GetProperty("meta").GetProperty("result_count").GetInt32());
        Assert.AreEqual(
            JsonDocument.Parse(ResponseBuilder.BuildBranchSelectionUnresolved()).RootElement.GetProperty("message").GetString(),
            call.Body.GetProperty("message").GetString());
        var text = call.Body.ToString();
        Assert.IsFalse(text.Contains("no-such-branch") || text.Contains("widgets"), "nothing requested is echoed");
    }

    [TestMethod]
    public async Task Call_UnindexedRepository_WithoutReadPolicy_IsActionableAndNotEchoed()
    {
        // #163: naming a repository with no complete snapshot is a tool error, never an empty success that a
        // client reads as "no matches".
        await using var host = await Harness.StartAsync(requireSelection: true);

        var call = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = "https://github.com/acme/not-indexed" });

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual(ResponseBuilder.RepositoryNotFoundCode, ErrorCode(call.Body));
        Assert.AreEqual(0, call.Body.GetProperty("meta").GetProperty("result_count").GetInt32());
        Assert.AreEqual(
            JsonDocument.Parse(ResponseBuilder.BuildSelectionUnresolved()).RootElement.GetProperty("message").GetString(),
            call.Body.GetProperty("message").GetString());
        Assert.IsFalse(call.Body.ToString().Contains("not-indexed"), "the requested repository is not echoed");
    }

    [TestMethod]
    public async Task Call_UnknownOrIncompleteBranch_UnderReadPolicy_IsUniformNotFound()
    {
        await using var host = await Harness.StartAsync(readPolicy: true);

        var authorized = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = Widgets, ["branch"] = FeatureBranch }, token: ReaderToken);
        Assert.AreEqual($"proj_{host.WidgetsFeature}", ProjectOf(authorized.Body), "the principal reads its own branch");

        var unknownBranch = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = Widgets, ["branch"] = "no-such-branch" }, token: ReaderToken);
        var incompleteBranch = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = Widgets, ["branch"] = Harness.PendingBranch }, token: ReaderToken);
        var foreignRepository = await host.CallAsync("find_symbol",
            new() { ["name"] = "global::App.Type0", ["repository"] = Gadgets, ["branch"] = "main" }, token: ReaderToken);

        var notFound = WithoutTimestamp(JsonDocument.Parse(ResponseBuilder.BuildNotFound()).RootElement);
        Assert.AreEqual(notFound, WithoutTimestamp(unknownBranch.Body), "an unknown branch is the uniform not-found");
        Assert.AreEqual(notFound, WithoutTimestamp(incompleteBranch.Body), "a branch with no complete snapshot is too");
        Assert.AreEqual(notFound, WithoutTimestamp(foreignRepository.Body), "and so is another tenant's branch");
    }

    // ==== helpers ===================================================================================

    private static string? ProjectOf(JsonElement body) =>
        body.GetProperty("results")[0].GetProperty("project_id").GetString();

    private static string? ErrorCode(JsonElement body) =>
        body.GetProperty("meta").TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;

    private static bool IsStringSchema(JsonElement property)
    {
        var type = property.GetProperty("type");
        return type.ValueKind == JsonValueKind.String
            ? type.GetString() == "string"
            : type.EnumerateArray().Any(t => t.GetString() == "string");
    }

    private static string WithoutTimestamp(JsonElement body)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();
        node["meta"]!.AsObject().Remove("queried_at");
        return node.ToJsonString();
    }

    private readonly record struct ToolCall(bool IsError, JsonElement Body);

    /// <summary>A call filter registered AFTER the selection filter: it sees what the tool is handed.</summary>
    private sealed class CallProbe
    {
        public List<string>? ArgumentNames { get; set; }
        public ToolCallSelection? Selection { get; set; }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string PendingBranch = "release/next";

        public long WidgetsMain { get; private init; }
        public long WidgetsFeature { get; private init; }
        public long GadgetsMain { get; private init; }
        public CallProbe Probe { get; } = new();
        private HttpClient Client { get; set; } = null!;
        private WebApplication App { get; set; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";
        private int _nextId = 1;

        public static async Task<Harness> StartAsync(bool requireSelection = false, bool readPolicy = false)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();
            var widgetsMain = Publish(db, Widgets, "commit-w1", "main", isDefault: true);
            var widgetsFeature = Publish(db, Widgets, "commit-w2", FeatureBranch, isDefault: false);
            var gadgetsMain = Publish(db, Gadgets, "commit-g1", "main", isDefault: true);
            PointAtPending(db, Widgets, "commit-w3", PendingBranch);

            var options = ServiceTestFixtures.NewOptions(dbPath, queryToken: readPolicy ? null : QueryToken) with
            {
                RequireRepositorySelection = requireSelection,
                ReadPolicy = readPolicy
                    ? new ReadAuthorizationPolicy
                    {
                        Enabled = true,
                        Principals = [new ReadPrincipal { Token = ReaderToken, Repositories = new HashSet<string> { Widgets } }]
                    }
                    : new ReadAuthorizationPolicy()
            };
            var service = SnapshotService.Start(options, new FakeSnapshotWorker(db), db);

            var harness = new Harness
            {
                WidgetsMain = widgetsMain,
                WidgetsFeature = widgetsFeature,
                GadgetsMain = gadgetsMain,
                Service = service,
                Db = db,
                DbPath = dbPath
            };

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            builder.Services.Configure<McpServerOptions>(o => o.Filters.Request.CallToolFilters.Add(next => async (context, ct) =>
            {
                harness.Probe.ArgumentNames = context.Params?.Arguments?.Keys.ToList() ?? [];
                harness.Probe.Selection = ToolCallSelection.Get(
                    context.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext);
                return await next(context, ct);
            }));
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();
            harness.App = app;
            harness.Client = app.GetTestClient();
            harness.DefaultToken = readPolicy ? ReaderToken : QueryToken;
            return harness;
        }

        private string DefaultToken { get; set; } = QueryToken;

        public Task<ToolCall> CallAsync(
            string tool, Dictionary<string, string> arguments, string? header = null, string? token = null) =>
            CallRawAsync(tool, JsonSerializer.Serialize(arguments), header, token);

        public async Task<ToolCall> CallRawAsync(string tool, string argumentsJson, string? header = null, string? token = null)
        {
            var result = await RpcAsync("tools/call",
                $$"""{"name":"{{tool}}","arguments":{{argumentsJson}}}""", header, token);
            var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            return new ToolCall(isError, JsonDocument.Parse(text).RootElement.Clone());
        }

        public async Task<JsonElement> RpcAsync(string method, string paramsJson, string? header = null, string? token = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    $$"""{"jsonrpc":"2.0","id":{{_nextId++}},"method":"{{method}}","params":{{paramsJson}}}""",
                    Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? DefaultToken);
            if (header is not null)
                request.Headers.Add(ServiceApp.RepositoryHeader, header);

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
            return result.Clone();
        }

        private static long Publish(IndexDatabase db, string repo, string commit, string branch, bool isDefault)
        {
            var snapId = ServiceTestFixtures.PublishComplete(
                db, ServiceTestFixtures.Request(repo: repo, commit: commit), symbolCount: 1);
            var snapshots = new SnapshotStore(db.GetConnection());
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var branchId = snapshots.EnsureBranch(snapshots.GetRepositoryId(repo)!.Value, branch, isDefault, now);
            snapshots.SetBranchPointer(branchId, snapId, now);
            return snapId;
        }

        // A branch whose pointer targets a snapshot that never completed.
        private static void PointAtPending(IndexDatabase db, string repo, string commit, string branch)
        {
            var snapshots = new SnapshotStore(db.GetConnection());
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var repoId = snapshots.GetRepositoryId(repo)!.Value;
            var identity = ServiceTestFixtures.Request(repo: repo, commit: commit).ToIdentity();
            var (snapId, _, _) = snapshots.BeginPending(identity, repoId, null, null, now);
            snapshots.SetBranchPointer(snapshots.EnsureBranch(repoId, branch, isDefault: false, now), snapId, now);
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
