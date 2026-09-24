using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Verifies the <c>POST /control/ensure</c> HTTP status-code contract: a TERMINAL outcome returns 200, while
/// a non-terminal (queued) outcome — produced when a TRANSIENT provisioning failure requeues the identity
/// for a bounded retry — returns 202 Accepted so an orchestrator polls <c>/status</c> instead of treating
/// it as a settled result. Runs fully in-process over TestServer with an open (dev) control plane.
/// </summary>
[TestClass]
public class EnsureEndpointStatusCodeTests
{
    [TestMethod]
    public async Task Ensure_TerminalOutcome_Returns200()
    {
        await using var host = await StartAsync(new FakeSnapshotWorker(_db!));

        var response = await Post(host);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "a terminal (complete) ensure returns 200");
    }

    [TestMethod]
    public async Task Ensure_TransientRequeue_Returns202Accepted()
    {
        // The worker throws a transient provisioning failure, so the service requeues the identity (bounded)
        // and the ensure result is non-terminal (queued) → the endpoint must surface 202, not 200.
        var worker = new FakeSnapshotWorker(_db!,
            (_, _) => throw new TransientProvisioningException("git fetch failed transiently: timed out"));
        await using var host = await StartAsync(worker);

        var response = await Post(host);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode,
            "a transient-requeue ensure returns 202 so the orchestrator polls /status");
    }

    // ---- harness --------------------------------------------------------------------------------

    private string _dbPath = "";
    private IndexDatabase? _db;

    private static Task<HttpResponseMessage> Post(WebApplication host) =>
        host.GetTestClient().PostAsync("/control/ensure",
            JsonContent.Create(ServiceTestFixtures.Request(), options: ServiceJson.Options));

    private async Task<WebApplication> StartAsync(FakeSnapshotWorker worker)
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();

        // Open (null token) control plane keeps the test focused on the status-code mapping, not auth.
        var options = ServiceTestFixtures.NewOptions(_dbPath);
        var service = SnapshotService.Start(options, worker, _db);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ServiceApp.RegisterServices(builder, options, service);
        var app = builder.Build();
        ServiceApp.MapEndpoints(app, options);
        await app.StartAsync();
        return app;
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (_db is not null) SqliteTestDatabase.Delete(_dbPath, _db); } catch { }
    }
}
