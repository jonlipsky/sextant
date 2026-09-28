using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Mcp;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-1: the service host always wires the <c>X-Sextant-Repository</c> request selector, whether or not a
/// read policy is configured, and <see cref="ServiceOptions.RequireRepositorySelection"/> makes a query
/// that names no repository fail with <c>repository_required</c>. With the requirement off (the default),
/// a legacy <c>QUERY_TOKEN</c> read with no header keeps reading the unselected default, which for a
/// multi-repository catalog is every repository. The catalog here holds two published repositories whose
/// symbols share a fully qualified name, so an unscoped <c>find_symbol</c> is ambiguous across both.
/// </summary>
[TestClass]
public class RepositorySelectionHttpTests
{
    private const string QueryToken = "query-secret";
    private const string RepoA = "https://github.com/org/a";
    private const string RepoB = "https://github.com/org/b";

    [TestMethod]
    public async Task Header_IsHonored_WithoutAReadPolicy()
    {
        await using var host = await Harness.StartAsync(requireSelection: false);

        var tool = await FindAsync(host, repository: RepoB);

        var meta = tool.GetProperty("meta");
        Assert.IsFalse(meta.TryGetProperty("error", out _), tool.ToString());
        Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32(), "only the named repository is read");
        Assert.IsFalse(meta.TryGetProperty("ambiguous", out _));
        Assert.AreEqual($"proj_{host.SnapshotB}", ProjectOf(tool));
    }

    [TestMethod]
    public async Task LegacyToken_NoHeader_StaysUnscoped()
    {
        // Regression pin: a gateway that sends no selector keeps reading across every repository until the
        // operator turns the requirement on.
        await using var host = await Harness.StartAsync(requireSelection: false);

        var tool = await FindAsync(host, repository: null);

        var meta = tool.GetProperty("meta");
        Assert.IsFalse(meta.TryGetProperty("error", out _), tool.ToString());
        Assert.IsTrue(meta.GetProperty("ambiguous").GetBoolean());
        Assert.AreEqual(2, meta.GetProperty("ambiguous_match_count").GetInt32(),
            "with no header the read spans both repositories");
    }

    [TestMethod]
    public async Task RequireSelection_NoHeader_IsRepositoryRequired()
    {
        await using var host = await Harness.StartAsync(requireSelection: true);

        var tool = await FindAsync(host, repository: null);

        var meta = tool.GetProperty("meta");
        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, meta.GetProperty("error").GetProperty("code").GetString());
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
        var text = tool.ToString();
        Assert.IsFalse(text.Contains(RepoA) || text.Contains(RepoB), "the error names no repository");
    }

    [TestMethod]
    public async Task RequireSelection_WithHeader_IsServed()
    {
        await using var host = await Harness.StartAsync(requireSelection: true);

        var tool = await FindAsync(host, repository: RepoA);

        var meta = tool.GetProperty("meta");
        Assert.IsFalse(meta.TryGetProperty("error", out _), tool.ToString());
        Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());
        Assert.AreEqual($"proj_{host.SnapshotA}", ProjectOf(tool));
    }

    [TestMethod]
    public async Task RequireSelection_UnknownRepositoryHeader_ReadsNothing()
    {
        // A header that names no indexed repository satisfies neither the requirement nor the fallback: it
        // never widens to the multi-repository unscoped read.
        await using var host = await Harness.StartAsync(requireSelection: true);

        var tool = await FindAsync(host, repository: "https://github.com/org/not-indexed");

        var meta = tool.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32(), tool.ToString());
        Assert.IsFalse(tool.ToString().Contains("not-indexed"), "the requested repository is not echoed");
    }

    private static string? ProjectOf(JsonElement tool) =>
        tool.GetProperty("results")[0].GetProperty("project_id").GetString();

    private static async Task<JsonElement> FindAsync(Harness host, string? repository)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"find_symbol","arguments":{"name":"global::App.Type0"}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", QueryToken);
        if (repository is not null)
            request.Headers.Add(ServiceApp.RepositoryHeader, repository);

        using var response = await host.Client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, raw);

        var payload = raw.Contains("data:", StringComparison.Ordinal)
            ? string.Concat(raw.Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l["data:".Length..].Trim()))
            : raw;
        using var rpc = JsonDocument.Parse(payload);
        var text = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HttpClient Client { get; private init; } = null!;
        public long SnapshotA { get; private init; }
        public long SnapshotB { get; private init; }
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<Harness> StartAsync(bool requireSelection)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();
            var snapshotA = PublishDefaultBranch(db, RepoA);
            var snapshotB = PublishDefaultBranch(db, RepoB);

            var options = ServiceTestFixtures.NewOptions(dbPath, queryToken: QueryToken) with
            {
                RequireRepositorySelection = requireSelection
            };
            Assert.IsFalse(options.ReadPolicy.Enabled, "these tests exercise the legacy query token, no read policy");
            var service = SnapshotService.Start(options, new FakeSnapshotWorker(db), db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness
            {
                Client = app.GetTestClient(),
                SnapshotA = snapshotA,
                SnapshotB = snapshotB,
                App = app,
                Service = service,
                Db = db,
                DbPath = dbPath
            };
        }

        private static long PublishDefaultBranch(IndexDatabase db, string repo)
        {
            var snapId = ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request(repo: repo), symbolCount: 1);
            var snapshots = new SnapshotStore(db.GetConnection());
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var branch = snapshots.EnsureBranch(snapshots.GetRepositoryId(repo)!.Value, "main", isDefault: true, now);
            snapshots.SetBranchPointer(branch, snapId, now);
            return snapId;
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
