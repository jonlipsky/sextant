using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Service;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #145 on a REAL index: the parent-plus-submodule checkout of
/// <see cref="CloneModeEnsure_ParentWithSubmodule_IndexesTheSubmoduleProjectAsAProviderSnapshot"/>, produced by
/// the real worker inside a running service and then read over the remote <c>/mcp</c> surface the way an agent
/// reads it. The synthetic catalog the service tests use writes its own stored paths; this test pins that the
/// paths the real indexer stores answer repository-relative inputs and come back repository-relative.
/// </summary>
public partial class SubmoduleCheckoutIntegrationTests
{
    private const string QueryToken = "query-secret";

    [TestMethod]
    public async Task RemoteMcp_RepositoryRelativePaths_ResolveRealIndexedFiles_AndNoCheckoutPathLeaks()
    {
        var (providerDir, providerCommit) = CreateProviderRepo();
        var (parentUrl, parentCommit) = CreateParentRepo(providerDir, providerCommit);

        var paths = new ServicePaths(ServiceVolumes.Rooted(Path.Combine(_tempDir, "service")));
        var checkouts = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, log: _log.Add)
        {
            AllowFileTransportForTesting = true
        };
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = parentUrl,
            CommitSha = parentCommit,
            BranchName = "main",
            IsDefaultBranch = true
        };
        Assert.IsTrue(checkouts.TryResolve(request, out var resolution), string.Join("\n", _log));
        RestoreSolution(Path.Combine(resolution.CheckoutDir, "App.slnx"));

        var dbPath = Path.Combine(_tempDir, "catalog.db");
        var db = new IndexDatabase(dbPath, IndexWriteOptions.Default);
        db.RunMigrations();
        var config = new SextantConfiguration();
        var options = new ServiceOptions
        {
            CatalogDbPath = dbPath,
            Volumes = ServiceVolumes.Rooted(Path.Combine(_tempDir, "service")),
            DefaultConfigHash = IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash,
            ControlToken = "control-secret",
            QueryToken = QueryToken,
            RequireRepositorySelection = true,
            RepositoryUrlPolicy = new RepositoryUrlPolicy([RepositoryUrlPolicy.DefaultHost]) { AllowFileTransportForTesting = true }
        };
        var worker = new LocalIndexerSnapshotWorker(db, config, checkouts, _log.Add);
        var service = SnapshotService.Start(options, worker, db);
        WebApplication? app = null;
        try
        {
            var ensured = await service.EnsureSnapshotAsync(request);
            Assert.AreEqual("complete", ensured.Status, $"{ensured.Reason}\n{string.Join("\n", _log)}");

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();
            using var client = app.GetTestClient();

            // The parent's own file, by the path an agent sees in its clone.
            var program = await CallAsync(client, "get_file_symbols", parentUrl, new JsonObject { ["file_path"] = "App/Program.cs" });
            var programRows = Results(program);
            CollectionAssert.IsSubsetOf(new[] { "Program", "Run" }, programRows.Select(r => r.GetProperty("display_name").GetString()).ToArray(),
                program.GetRawText());
            Assert.IsTrue(programRows.All(r => r.GetProperty("file_path").GetString() == "App/Program.cs"), program.GetRawText());

            // The same input in another spelling (leading ./, backslashes) names the same file.
            var spelled = await CallAsync(client, "get_file_symbols", parentUrl, new JsonObject { ["file_path"] = @".\App\Program.cs" });
            Assert.AreEqual(programRows.Count, Results(spelled).Count, spelled.GetRawText());

            // A submodule project is indexed into its provider snapshot, not the parent's (Phase 12), so the
            // parent's use of it is read through find_cross_repository_usages: the consumer file comes back
            // by its path in the parent's repository, never the worker's checkout path.
            var providerUrl = new Uri(providerDir).AbsoluteUri;
            var usages = await CallAsync(client, "find_cross_repository_usages", parentUrl,
                new JsonObject { ["provider_repository_url"] = providerUrl, ["symbol_fqn"] = "global::Mix.Combiner" });
            var usageRows = Results(usages);
            Assert.IsTrue(usageRows.Count > 0, usages.GetRawText());
            Assert.IsTrue(usageRows.All(r => r.GetProperty("file_path").GetString() == "App/Program.cs"), usages.GetRawText());

            // A file: scope takes the same repository-relative form.
            var scoped = await CallAsync(client, "find_symbol", parentUrl,
                new JsonObject { ["name"] = "Program", ["fuzzy"] = true, ["scope"] = "file:App/Program.cs" });
            var scopedRows = Results(scoped);
            Assert.IsTrue(scopedRows.Count > 0, scoped.GetRawText());
            Assert.IsTrue(scopedRows.All(r => r.GetProperty("file_path").GetString() == "App/Program.cs"), scoped.GetRawText());

            // An absolute path (here the worker's real checkout path) is refused, never resolved.
            var absolute = await CallAsync(client, "get_file_symbols", parentUrl,
                new JsonObject { ["file_path"] = Path.Combine(resolution.CheckoutDir, "App", "Program.cs") });
            Assert.AreEqual(ResponseBuilder.InvalidArgumentCode, absolute.GetProperty("meta").GetProperty("error").GetProperty("code").GetString());

            // No response leaks the worker's checkout path. (meta.snapshot.repository names the repository the
            // caller asked for; this fixture's is a local file URL, a hosted one is https://host/owner/repo.)
            var checkoutVolume = Path.Combine(_tempDir, "service");
            foreach (var response in new[] { program, spelled, usages, scoped, absolute })
            {
                var text = response.GetRawText();
                Assert.IsFalse(text.Contains(checkoutVolume.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
                    || text.Contains(checkoutVolume.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), text);
            }
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            service.Dispose();
            db.Dispose();
        }
    }

    private static List<JsonElement> Results(JsonElement response) =>
        response.TryGetProperty("results", out var results)
            ? results.EnumerateArray().ToList()
            : throw new AssertFailedException("no results: " + response.GetRawText());

    // One MCP tools/call over the service's /mcp, the way a remote agent sends it.
    private static async Task<JsonElement> CallAsync(HttpClient client, string tool, string repository, JsonObject arguments)
    {
        arguments["repository"] = repository;
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", QueryToken);

        using var response = await client.SendAsync(request);
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
}
