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
/// Phase 17 — criterion 1 authorization-surface hardening at the HTTP boundary. Covers the least-privilege
/// contributor token (issue #71: a contributor cannot reach the control plane, the control token remains a
/// superset) and the enforced query-plane read policy (a known principal authenticates, an unknown one is
/// rejected, and the federation artifact surface returns an IDENTICAL 404 for an unauthorized snapshot as
/// for a missing one — no cross-tenant existence/artifact oracle). Runs fully in-process over TestServer.
/// </summary>
[TestClass]
public class AuthorizationHardeningHttpTests
{
    private const string ControlToken = "control-secret";
    private const string ContributeToken = "contrib-secret";
    private const string RepoA = "https://github.com/org/a";
    private const string RepoB = "https://github.com/org/b";

    // ==== #71: least-privilege contributor token ===================================================

    [TestMethod]
    public async Task ContributorToken_CannotReachControlEnsure()
    {
        await using var host = await Harness.StartAsync(o => o with
        {
            ControlToken = ControlToken, ContributeToken = ContributeToken
        });

        var response = await Send(host, HttpMethod.Post, "/control/ensure", ContributeToken,
            JsonContent.Create(ServiceTestFixtures.Request(), options: ServiceJson.Options));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "a contributor token must NOT authorize a control-plane endpoint (issue #71)");
    }

    [TestMethod]
    public async Task ControlToken_StillReachesControlEnsure()
    {
        await using var host = await Harness.StartAsync(o => o with
        {
            ControlToken = ControlToken, ContributeToken = ContributeToken
        });

        var response = await Send(host, HttpMethod.Post, "/control/ensure", ControlToken,
            JsonContent.Create(ServiceTestFixtures.Request(), options: ServiceJson.Options));

        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, "the control token still authorizes ensure");
    }

    [TestMethod]
    public async Task ContributorToken_AuthorizesContributeEndpoint()
    {
        await using var host = await Harness.StartAsync(o => o with
        {
            ControlToken = ControlToken, ContributeToken = ContributeToken
        });

        // A malformed body is rejected by the ingest pipeline (422), but the point is authentication PASSED:
        // the contributor token reaches /control/contribute (never 401).
        var response = await Send(host, HttpMethod.Post, "/control/contribute", ContributeToken,
            new ByteArrayContent([1, 2, 3]));

        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "the least-privilege contributor token authorizes the contribute endpoint");
    }

    [TestMethod]
    public async Task ControlToken_IsSuperset_AlsoAuthorizesContribute()
    {
        await using var host = await Harness.StartAsync(o => o with
        {
            ControlToken = ControlToken, ContributeToken = ContributeToken
        });

        var response = await Send(host, HttpMethod.Post, "/control/contribute", ControlToken,
            new ByteArrayContent([1, 2, 3]));

        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, "the control token is a contribution superset");
    }

    [TestMethod]
    public async Task WrongToken_IsRejectedOnContribute()
    {
        await using var host = await Harness.StartAsync(o => o with
        {
            ControlToken = ControlToken, ContributeToken = ContributeToken
        });

        var response = await Send(host, HttpMethod.Post, "/control/contribute", "not-a-real-token",
            new ByteArrayContent([1, 2, 3]));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "an unrecognized token is rejected");
    }

    // ==== query-plane read policy (criterion 1) ====================================================

    [TestMethod]
    public async Task ReadPolicy_KnownPrincipal_IsAuthenticated_UnknownIsRejected()
    {
        await using var host = await Harness.StartAsync(o => o with
        {
            QueryToken = null,
            ReadPolicy = TwoTenantPolicy()
        }, seedRepo: RepoA);

        var url = $"/query/snapshots/{host.SeededIdentityHash}/symbols?limit=10";

        var known = await Send(host, HttpMethod.Get, url, "reader-a");
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, known.StatusCode, "a known principal authenticates on the query plane");

        var unknown = await Send(host, HttpMethod.Get, url, "stranger");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.StatusCode, "an unknown principal is rejected");
    }

    [TestMethod]
    public async Task ReadPolicy_ForeignTenant_GetsIdentical404AsMissingSnapshot()
    {
        await using var host = await Harness.StartAsync(o => o with
        {
            QueryToken = null,
            ReadPolicy = TwoTenantPolicy()
        }, seedRepo: RepoA);

        // reader-b is a KNOWN principal (authenticates) but authorized only for repo B, while the seeded
        // snapshot belongs to repo A: the federation artifact surface must hide it as a 404.
        var forbidden = await Send(host, HttpMethod.Get,
            $"/query/snapshots/{host.SeededIdentityHash}/symbols?limit=10", "reader-b");
        Assert.AreEqual(HttpStatusCode.NotFound, forbidden.StatusCode,
            "a foreign-tenant principal cannot fetch another repository's artifact pages (criterion 1)");

        // A genuinely-missing snapshot returns the SAME 404 for the same principal — no existence oracle.
        var missing = await Send(host, HttpMethod.Get,
            "/query/snapshots/deadbeefdeadbeef/symbols?limit=10", "reader-b");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode,
            "an unauthorized snapshot is indistinguishable from a missing one");
    }

    [TestMethod]
    public async Task ReadPolicy_OwningTenant_CanFetchItsOwnSnapshot()
    {
        await using var host = await Harness.StartAsync(o => o with
        {
            QueryToken = null,
            ReadPolicy = TwoTenantPolicy()
        }, seedRepo: RepoA);

        var response = await Send(host, HttpMethod.Get,
            $"/query/snapshots/{host.SeededIdentityHash}/symbols?limit=10", "reader-a");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "the owning tenant reads its own snapshot pages");
    }

    private static ReadAuthorizationPolicy TwoTenantPolicy() => new()
    {
        Enabled = true,
        Principals =
        [
            new ReadPrincipal { Token = "reader-a", Repositories = new HashSet<string> { RepoA } },
            new ReadPrincipal { Token = "reader-b", Repositories = new HashSet<string> { RepoB } }
        ]
    };

    private static async Task<HttpResponseMessage> Send(
        Harness host, HttpMethod method, string url, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HttpClient Client { get; init; } = null!;
        public string SeededIdentityHash { get; init; } = "";
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<Harness> StartAsync(Func<ServiceOptions, ServiceOptions> configure, string? seedRepo = null)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            var request = ServiceTestFixtures.Request(repo: seedRepo ?? "https://github.com/org/app");
            if (seedRepo is not null)
                ServiceTestFixtures.PublishComplete(db, request);

            var options = configure(ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken, queryToken: null));
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
