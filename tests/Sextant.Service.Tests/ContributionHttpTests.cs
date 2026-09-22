using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Service.Contributions;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// The Phase-16 contribution transport end-to-end over HTTP, fully in-process on <see cref="TestServer"/>
/// (no real network): the <c>POST /control/contribute</c> endpoint (200 accept / 422 reject / 401 auth
/// gate) driven through the real <see cref="ContributionUploader"/>. Together with
/// <see cref="ContributionUploaderTests"/> (the service-unavailable fail-safe), this proves the client→
/// service protocol for acceptance criteria 1, 2, 3, and 5.
/// </summary>
[TestClass]
public class ContributionHttpTests
{
    private const string Repo = "https://github.com/octo/app";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Cap = "linux-x64|net8.0|sdk-8.0.400";
    private const string ControlToken = "control-secret";

    [TestMethod]
    public async Task Upload_of_a_clean_contribution_publishes_over_http()
    {
        await using var host = await Harness.StartAsync();
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, Cap, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var outcome = await ContributionUploader.UploadAsync(host.Client, artifact, host.UploadOptions());

        Assert.IsTrue(outcome.Accepted, outcome.Message);
        Assert.AreEqual(ContributionIngestStatus.Complete, outcome.Result!.Status);
    }

    [TestMethod]
    public async Task Re_upload_over_http_is_a_duplicate()
    {
        await using var host = await Harness.StartAsync();
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, Cap, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        await ContributionUploader.UploadAsync(host.Client, artifact, host.UploadOptions());
        var second = await ContributionUploader.UploadAsync(host.Client, artifact, host.UploadOptions());

        Assert.IsTrue(second.Accepted);
        Assert.AreEqual(ContributionIngestStatus.Duplicate, second.Result!.Status);
    }

    [TestMethod]
    public async Task Rejected_contribution_returns_422_with_structured_reason()
    {
        await using var host = await Harness.StartAsync();
        var dirty = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, Cap, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")], workingTreeDirty: true);

        var outcome = await ContributionUploader.UploadAsync(host.Client, dirty, host.UploadOptions());

        Assert.IsFalse(outcome.Accepted);
        Assert.AreEqual(ContributionIngestStatus.Rejected, outcome.Result!.Status);
        Assert.AreEqual(ContributionRejectionCode.DirtyTree, outcome.Result.RejectionCode);
    }

    [TestMethod]
    public async Task Contribute_without_control_token_is_401()
    {
        await using var host = await Harness.StartAsync();
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, Cap, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/control/contribute")
        {
            Content = new ByteArrayContent(artifact.ToArray())
        };
        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "the contribution endpoint is control-token gated (a contribution is an untrusted external input)");
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HttpClient Client { get; init; } = null!;
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public ContributionUploadOptions UploadOptions() => new()
        {
            ServiceUrl = Client.BaseAddress!,
            Token = ControlToken
        };

        public static async Task<Harness> StartAsync()
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken, queryToken: "query-secret");
            var service = SnapshotService.Start(options, new FakeSnapshotWorker(db), db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            var client = app.GetTestClient();
            return new Harness { Client = client, App = app, Service = service, Db = db, DbPath = dbPath };
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
