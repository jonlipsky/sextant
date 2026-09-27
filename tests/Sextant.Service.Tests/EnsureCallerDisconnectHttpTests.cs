using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #148 over HTTP (in-process TestServer): an HTTP client that times out / disconnects mid-index — the
/// ProcessStack <c>SextantServiceClient</c>'s 100 s <c>HttpClient.Timeout</c>, a proxy, an operator's curl —
/// must never cancel production; <c>/control/status</c> and <c>/control/resolve</c> answer promptly while the
/// index runs; and <c>POST /control/ensure?wait=false</c> returns 202 with the ensure-result body at once.
/// </summary>
[TestClass]
public class EnsureCallerDisconnectHttpTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private string _dbPath = "";
    private IndexDatabase? _db;
    private FakeSnapshotWorker? _worker;
    private SnapshotService? _service;

    [TestMethod]
    public async Task ClientTimesOutMidIndex_ProductionContinues_StatusStaysPrompt_JobCompletes()
    {
        await using var host = await StartAsync();
        var client = host.GetTestClient();

        // The client gives up mid-index (its own timeout), exactly like the prod incident's killed curl.
        using var clientTimeout = new CancellationTokenSource();
        var post = client.PostAsync("/control/ensure", Body(), clientTimeout.Token);
        await WaitUntilAsync(() => _worker!.Calls == 1);
        clientTimeout.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => post);

        var hash = ServiceTestFixtures.Request().ToIdentity().Hash;
        await Task.Delay(200); // let the server observe the abort
        var jobId = (await BoundedAsync(() => _service!.GetStatusByIdentity(hash)))!.Job.Id;

        // Status polls answer promptly while the worker holds the writer, and report the job still running.
        var sw = Stopwatch.StartNew();
        var status = await client.GetAsync($"/control/status/{jobId}").WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();
        Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
        Assert.IsTrue(sw.Elapsed < Prompt, $"GET /control/status took {sw.Elapsed} during production");
        Assert.AreEqual(SnapshotJobStatus.Running, await JobStatusOf(status));

        sw.Restart();
        var resolve = await client.GetAsync("/control/resolve?repository=https://github.com/org/app&branch=main")
            .WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();
        Assert.AreEqual(HttpStatusCode.NotFound, resolve.StatusCode, "no branch pointer yet — but answered");
        Assert.IsTrue(sw.Elapsed < Prompt, $"GET /control/resolve took {sw.Elapsed} during production");

        // The index finishes long after the client left: the job still completes and publishes.
        _worker!.Gate.SetResult();
        await WaitUntilAsync(() => _service!.GetStatus(jobId)?.Job.Status == SnapshotJobStatus.Complete);

        // A re-ensure attaches to the result — no second worker run.
        var again = await client.PostAsync("/control/ensure", Body());
        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        var body = await again.Content.ReadFromJsonAsync<EnsureSnapshotResult>(ServiceJson.Options);
        Assert.AreEqual(jobId, body!.JobId);
        Assert.IsTrue(body.Attached);
        Assert.AreEqual(1, _worker.Calls);
    }

    [TestMethod]
    public async Task EnsureWaitFalse_Returns202WithTheEnsureResultShape_ThenStatusReachesTerminal()
    {
        await using var host = await StartAsync();
        var client = host.GetTestClient();

        var sw = Stopwatch.StartNew();
        var response = await client.PostAsync("/control/ensure?wait=false", Body()).WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.IsTrue(sw.Elapsed < Prompt, $"wait=false took {sw.Elapsed}");

        // Same snake_case ensure-result shape a blocking ensure returns (job_id, identity_hash, status, ...).
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var jobId = root.GetProperty("job_id").GetInt64();
        Assert.IsTrue(jobId > 0);
        Assert.AreEqual(ServiceTestFixtures.Request().ToIdentity().Hash, root.GetProperty("identity_hash").GetString());
        var status = root.GetProperty("status").GetString();
        Assert.IsTrue(status is SnapshotJobStatus.Queued or SnapshotJobStatus.Running, $"non-terminal status, got {status}");
        Assert.IsTrue(root.TryGetProperty("attached", out _));

        await WaitUntilAsync(() => _worker!.Calls == 1);
        _worker!.Gate.SetResult();

        string? polled = null;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < Settle)
        {
            polled = await JobStatusOf(await client.GetAsync($"/control/status/{jobId}"));
            if (polled == SnapshotJobStatus.Complete) break;
            await Task.Delay(20);
        }
        Assert.AreEqual(SnapshotJobStatus.Complete, polled, "polling /control/status reaches the terminal state");
        Assert.AreEqual(1, _worker.Calls);
    }

    [TestMethod]
    public async Task EnsureDefault_StillBlocksUntilTerminal_Returns200()
    {
        await using var host = await StartAsync();
        var client = host.GetTestClient();

        var post = client.PostAsync("/control/ensure", Body());
        await WaitUntilAsync(() => _worker!.Calls == 1);
        await Task.Delay(100);
        Assert.IsFalse(post.IsCompleted, "the default ensure keeps the blocking contract");

        _worker!.Gate.SetResult();
        var response = await post.WaitAsync(Settle);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EnsureSnapshotResult>(ServiceJson.Options);
        Assert.AreEqual(SnapshotJobStatus.Complete, body!.Status);
    }

    [TestMethod]
    public async Task EnsureInterruptedByServiceShutdown_Returns503_AndTheJobIsRequeued()
    {
        await using var host = await StartAsync();
        var client = host.GetTestClient();

        var post = client.PostAsync("/control/ensure", Body());
        await WaitUntilAsync(() => _worker!.Calls == 1);
        _service!.StopProduction();

        var response = await post.WaitAsync(Settle);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.AreEqual("unavailable", json.RootElement.GetProperty("status").GetString(),
                "the 503 body claims no job state; the retried ensure reports the real one");
        var hash = ServiceTestFixtures.Request().ToIdentity().Hash;
        Assert.AreEqual(SnapshotJobStatus.Queued, _service.GetStatusByIdentity(hash)?.Job.Status);

        // Once shutdown began, even a non-blocking ensure is refused (never a 202 "running").
        var late = await client.PostAsync("/control/ensure?wait=false", Body()).WaitAsync(Settle);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, late.StatusCode);
    }

    [TestMethod]
    public async Task ReEnsureOfAnIndexedBranch_ResolveReturnsTheCurrentHeadWithCoverage_Promptly()
    {
        // The prod shape (2026-09-27): the repository's branch already points at a published snapshot, and a
        // re-ensure of a NEW commit on that branch is indexing. /control/resolve must return 200 with the
        // current head snapshot plus its coverage block promptly — pre-#148 it waited on the writer held by the
        // worker and stalled for the whole index — and /control/status must report the re-index running.
        await using var host = await StartAsync();
        var client = host.GetTestClient();

        var head = ServiceTestFixtures.Request(commit: "commit-aaaa", branch: "main");
        var headSnapshot = ServiceTestFixtures.PublishComplete(_db!, head);
        new SnapshotCoverageStore(_db!.GetConnection()).Record(headSnapshot, new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Complete,
            SelectionSource = "default_union"
        }, 1);
        var attached = await client.PostAsync("/control/ensure", Body(head)).WaitAsync(Settle);
        Assert.AreEqual(HttpStatusCode.OK, attached.StatusCode, "the published head attaches without a worker run");
        Assert.AreEqual(0, _worker!.Calls);

        var reEnsure = await client.PostAsync("/control/ensure?wait=false",
            Body(ServiceTestFixtures.Request(commit: "commit-bbbb", branch: "main"))).WaitAsync(Settle);
        Assert.AreEqual(HttpStatusCode.Accepted, reEnsure.StatusCode);
        var jobId = (await reEnsure.Content.ReadFromJsonAsync<EnsureSnapshotResult>(ServiceJson.Options))!.JobId;
        await WaitUntilAsync(() => _worker.Calls == 1);

        var sw = Stopwatch.StartNew();
        var resolve = await client.GetAsync("/control/resolve?repository=https://github.com/org/app&branch=main")
            .WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();
        Assert.AreEqual(HttpStatusCode.OK, resolve.StatusCode);
        Assert.IsTrue(sw.Elapsed < Prompt, $"GET /control/resolve took {sw.Elapsed} while the branch was re-indexing");
        using (var json = JsonDocument.Parse(await resolve.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual(headSnapshot, json.RootElement.GetProperty("id").GetInt64(),
                "resolve serves the branch's current head while its next commit indexes");
            Assert.AreEqual(SnapshotCoverageVerdict.Complete,
                json.RootElement.GetProperty("coverage").GetProperty("verdict").GetString(),
                "the coverage block (a second catalog read) is served off the writer too");
        }

        sw.Restart();
        var status = await client.GetAsync($"/control/status/{jobId}").WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();
        Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
        Assert.IsTrue(sw.Elapsed < Prompt, $"GET /control/status took {sw.Elapsed} while the branch was re-indexing");
        Assert.AreEqual(SnapshotJobStatus.Running, await JobStatusOf(status));

        _worker.Gate.SetResult();
        await WaitUntilAsync(() => _service!.GetStatus(jobId)?.Job.Status == SnapshotJobStatus.Complete);
    }

    // ---- harness --------------------------------------------------------------------------------

    private static JsonContent Body(EnsureSnapshotRequest? request = null) =>
        JsonContent.Create(request ?? ServiceTestFixtures.Request(), options: ServiceJson.Options);

    private static async Task<string?> JobStatusOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("job").GetProperty("status").GetString();
    }

    private async Task<WebApplication> StartAsync()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _worker = new FakeSnapshotWorker(_db) { UseGate = true };

        var options = ServiceTestFixtures.NewOptions(_dbPath);
        _service = SnapshotService.Start(options, _worker, _db);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ServiceApp.RegisterServices(builder, options, _service);
        var app = builder.Build();
        ServiceApp.MapEndpoints(app, options);
        await app.StartAsync();
        return app;
    }

    // Runs a read off the test thread, bounded — a regression that makes it wait on the writer held by a gated
    // worker must FAIL the test rather than deadlock it.
    private static Task<T> BoundedAsync<T>(Func<T> read) => Task.Run(read).WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!await BoundedAsync(condition))
        {
            if (deadline.Elapsed > Settle)
                Assert.Fail("condition not reached within the settle bound");
            await Task.Delay(20);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        _worker?.Gate.TrySetResult();
        _service?.Dispose();
        try { if (_db is not null) SqliteTestDatabase.Delete(_dbPath, _db); } catch { }
    }
}
