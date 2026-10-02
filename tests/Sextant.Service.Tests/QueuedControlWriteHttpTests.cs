using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #158 at the HTTP boundary: a production holds the single writer for its whole run, so a control write
/// that needs the writer must still answer within <see cref="ServiceOptions.ControlWriteWait"/>, well under the
/// ProcessStack app's 30 s client timeout. <c>POST /control/ensure?wait=false</c> for a new identity answers
/// <c>202 {status:"queued", job_id:&lt;integer&gt;}</c> with the id its row is later inserted under, and
/// <c>POST /control/branches/retire</c> answers <c>202 {"status":"accepted"}</c> and applies once the writer frees,
/// with its guards evaluated then. Both are service-owned, survive the caller going away, and apply in submission
/// order. A blocking test worker holds the writer: every commit of <see cref="Repo"/> is pre-published (its ensures
/// are service-side branch decisions), while the blocker repository and any new commit go through the worker.
/// </summary>
[TestClass]
public class QueuedControlWriteHttpTests
{
    private const string ControlToken = "control-secret";
    private const string Repo = "https://github.com/acme/widgets";
    private const string BlockerRepo = "https://github.com/acme/blocker";

    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(400);
    // The bound plus generous scheduling slack: a write that still waits for the writer fails this, never hangs.
    private static readonly TimeSpan Answered = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task EnsureWaitFalse_NewIdentityWhileTheWriterIsBusy_Is202Queued_WithAnIntegerJobId_ThatStatusResolves()
    {
        await using var host = await Harness.StartAsync();
        await host.HoldWriterAsync();
        var request = Ensure("commit-new");
        var hash = request.ToIdentity().Hash;

        var sw = Stopwatch.StartNew();
        var response = await host.EnsureAsync(request, wait: false).WaitAsync(Answered);
        sw.Stop();

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.IsTrue(sw.Elapsed >= Bound - TimeSpan.FromMilliseconds(50), $"answered after {sw.Elapsed}: the writer was busy");
        long jobId;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var root = json.RootElement;
            CollectionAssert.AreEquivalent(new[] { "job_id", "identity_hash", "status", "attached" },
                root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.AreEqual(JsonValueKind.Number, root.GetProperty("job_id").ValueKind, "job_id stays a JSON integer");
            jobId = root.GetProperty("job_id").GetInt64();
            Assert.IsTrue(jobId > 0);
            Assert.AreEqual(SnapshotJobStatus.Queued, root.GetProperty("status").GetString());
            Assert.AreEqual(hash, root.GetProperty("identity_hash").GetString());
            Assert.IsFalse(root.GetProperty("attached").GetBoolean());
        }

        // The id resolves at once, before its row exists.
        var status = await host.StatusAsync(jobId).WaitAsync(Answered);
        Assert.IsNotNull(status);
        Assert.AreEqual(jobId, status.Value.GetProperty("job").GetProperty("id").GetInt64());
        Assert.AreEqual(SnapshotJobStatus.Queued, status.Value.GetProperty("job").GetProperty("status").GetString());
        Assert.AreEqual(hash, status.Value.GetProperty("job").GetProperty("identity_hash").GetString());
        Assert.AreEqual(Repo, status.Value.GetProperty("job").GetProperty("repository_url").GetString());

        // A second ensure of the pending identity attaches to the same id.
        var again = await host.EnsureAsync(request, wait: false).WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.Accepted, again.StatusCode);
        using (var json = JsonDocument.Parse(await again.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual(jobId, json.RootElement.GetProperty("job_id").GetInt64());
            Assert.IsTrue(json.RootElement.GetProperty("attached").GetBoolean());
        }
        Assert.AreEqual(1, host.Worker.Calls, "nothing but the blocker has run");

        host.ReleaseWriter();
        await WaitUntilAsync(async () =>
            (await host.StatusAsync(jobId))?.GetProperty("job").GetProperty("status").GetString() == SnapshotJobStatus.Complete);
        var produced = (await host.StatusAsync(jobId))!.Value.GetProperty("job");
        Assert.AreEqual(hash, produced.GetProperty("identity_hash").GetString(), "the durable row took the reserved id");
        Assert.AreEqual(2, host.Worker.Calls, "the identity produced exactly once");
        Assert.AreNotEqual(host.BlockerJobId, jobId);
    }

    [TestMethod]
    public async Task EnsureWaitFalse_CallerGoneWhileQueued_TheEnsureStillRegistersAndProduces()
    {
        await using var host = await Harness.StartAsync();
        await host.HoldWriterAsync();
        var request = Ensure("commit-new");
        var hash = request.ToIdentity().Hash;

        using var gone = new CancellationTokenSource();
        var admitted = host.Service.WriteAdmissions;
        var post = host.EnsureAsync(request, wait: false, gone.Token);
        await WaitUntilAsync(() => Task.FromResult(host.Service.WriteAdmissions > admitted));
        gone.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => post.WaitAsync(Answered));

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.GetStatusByIdentity(hash)?.Job.Status == SnapshotJobStatus.Complete));
        Assert.AreEqual(2, host.Worker.Calls);
    }

    [TestMethod]
    public async Task EnsureWaitFalse_RegistrationThatNeverCommits_LeavesTheReservedIdUnknown_NotQueuedForever()
    {
        await using var host = await Harness.StartAsync();
        await host.HoldWriterAsync();
        var response = await host.EnsureAsync(Ensure("commit-new"), wait: false).WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        long jobId;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            jobId = json.RootElement.GetProperty("job_id").GetInt64();

        // The service loses the writer lease before the queued registration's turn, so its row is never written.
        await host.StealLeaseAsync();
        host.ReleaseWriter();

        // The reservation goes with the failed registration: the id reads 404 (ensure again), never queued forever.
        await WaitUntilAsync(async () => await host.StatusAsync(jobId) is null);
        Assert.IsNull(host.Service.GetStatusByIdentity(Ensure("commit-new").ToIdentity().Hash));
    }

    [TestMethod]
    public async Task EnsureWaitTrue_WhileTheWriterIsBusy_StillWaitsForItsResult()
    {
        await using var host = await Harness.StartAsync();
        await host.HoldWriterAsync();

        var post = host.EnsureAsync(Ensure("commit-new"), wait: true);
        await Task.Delay(Bound * 2);
        Assert.IsFalse(post.IsCompleted, "the blocking ensure keeps its contract");

        host.ReleaseWriter();
        var response = await post.WaitAsync(Settle);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(SnapshotJobStatus.Complete, json.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Retire_WithTheWriterFree_KeepsTheSynchronous200Body()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();

        var response = await host.RetireAsync("feature", expected: "commit-B").WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("{\"retired\":true}", await response.Content.ReadAsStringAsync());
        Assert.IsNull(await host.ResolveCommitAsync("feature"));
    }

    [TestMethod]
    public async Task Retire_WhileTheWriterIsBusy_Is202Accepted_AndAppliesOnceItFrees()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        var sw = Stopwatch.StartNew();
        var response = await host.RetireAsync("feature", expected: "commit-B").WaitAsync(Answered);
        sw.Stop();

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual("{\"status\":\"accepted\"}", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("commit-B", await host.ResolveCommitAsync("feature"), "not applied while the writer is busy");
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Retire).Count);

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire).Count == 1));
        Assert.IsNull(await host.ResolveCommitAsync("feature"));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire)[0];
        Assert.AreEqual(AuditOutcome.Complete, audit.Outcome);
        Assert.AreEqual("retired", audit.Detail);
        Assert.AreEqual(Repo, audit.RepositoryScope);
        Assert.AreEqual(AuditLogStore.HashActor(ControlToken), audit.Actor);
    }

    [TestMethod]
    public async Task Retire_CallerGoneWhileQueued_StillApplies()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        using var gone = new CancellationTokenSource();
        var admitted = host.Service.WriteAdmissions;
        var post = host.RetireAsync("feature", expected: "commit-B", gone.Token);
        await WaitUntilAsync(() => Task.FromResult(host.Service.WriteAdmissions > admitted));
        gone.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => post.WaitAsync(Answered));

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire).Count == 1));
        Assert.IsNull(await host.ResolveCommitAsync("feature"));
    }

    [TestMethod]
    public async Task Retire_QueuedBehindAnAdvance_EvaluatesItsCasWhenItApplies()
    {
        // When it was submitted, feature was at B and the CAS would have passed; by the time it applies, the push
        // queued before it has moved feature to C, so it must not retire the branch.
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.EnsureAsync(Ensure("commit-C", "feature", expected: "commit-B"), wait: false).WaitAsync(Answered)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.RetireAsync("feature", expected: "commit-B").WaitAsync(Answered)).StatusCode);

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire).Count == 1));
        Assert.AreEqual("commit-C", await host.ResolveCommitAsync("feature"));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire)[0];
        Assert.AreEqual(AuditOutcome.Denied, audit.Outcome);
        Assert.AreEqual(BranchGuardReason.HeadMismatch, audit.Detail);
    }

    [TestMethod]
    public async Task Retire_OfTheDefaultBranch_QueuedWhileBusy_IsNeverApplied()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.RetireAsync("main", expected: "commit-A").WaitAsync(Answered)).StatusCode);

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire).Count == 1));
        Assert.AreEqual("commit-A", await host.ResolveCommitAsync("main"));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire)[0];
        Assert.AreEqual(AuditOutcome.Denied, audit.Outcome);
        Assert.AreEqual(BranchGuardReason.DefaultBranch, audit.Detail);
    }

    [TestMethod]
    public async Task PushRetireRecreate_QueuedWhileBusy_ApplyInSubmissionOrder()
    {
        // A push (B -> C), the branch's deletion (before = C) and its re-creation (before = zeros) at D, all while a
        // production holds the writer. Only submission order ends with feature at D: every other order leaves it at
        // C (the retire's CAS fails against B) or absent (the re-create's CAS fails against a still-present branch).
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.EnsureAsync(Ensure("commit-C", "feature", expected: "commit-B"), wait: false).WaitAsync(Answered)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.RetireAsync("feature", expected: "commit-C").WaitAsync(Answered)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.EnsureAsync(Ensure("commit-D", "feature", expected: ""), wait: false).WaitAsync(Answered)).StatusCode);

        host.ReleaseWriter();
        var recreate = Ensure("commit-D", "feature").ToIdentity().Hash;
        await WaitUntilAsync(() => Task.FromResult(host.Service.GetStatusByIdentity(recreate)?.Job.Status == SnapshotJobStatus.Complete));
        Assert.AreEqual("commit-D", await host.ResolveCommitAsync("feature"));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Complete, audit[0].Outcome);
        Assert.AreEqual("retired", audit[0].Detail, "the retire applied between the push and the re-create");
    }

    [TestMethod]
    public async Task IdenticalQueuedRetires_AreCoalesced_ADifferentOneIsNot()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        for (var i = 0; i < 2; i++)
            Assert.AreEqual(HttpStatusCode.Accepted,
                (await host.RetireAsync("feature", expected: "commit-B").WaitAsync(Answered)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted, (await host.RetireAsync("feature").WaitAsync(Answered)).StatusCode);

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire, repositoryScope: Repo).Count >= 2));
        await host.QuiesceAsync();
        var audit = host.Service.RecentAudit(action: AuditAction.Retire, repositoryScope: Repo);
        CollectionAssert.AreEqual(new[] { "absent", "retired" }, audit.Select(a => a.Detail).ToArray(),
            "the identical retires share one application; the unguarded one applies on its own, after them");
    }

    [TestMethod]
    public async Task Retire_AfterShutdownBegan_Is503_AndNothingIsQueued()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        host.Service.StopProduction();

        var response = await host.RetireAsync("feature").WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.AreEqual("unavailable", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("commit-B", await host.ResolveCommitAsync("feature"));
    }

    [TestMethod]
    public async Task QueuedRegistrationAndRetire_LandDuringTheShutdownDrain_TheIdSurvivesARestart()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();
        var request = Ensure("commit-new");
        var response = await host.EnsureAsync(request, wait: false).WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        long jobId;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            jobId = json.RootElement.GetProperty("job_id").GetInt64();
        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.RetireAsync("feature", expected: "commit-B").WaitAsync(Answered)).StatusCode);

        // Graceful shutdown: the blocker is cancelled and requeued, then the drain lets the queued registration and
        // the queued retire take their turns before the lease is released. Nothing new is produced.
        host.Service.Dispose();
        Assert.IsTrue(host.Service.ProductionDrained);

        using var restarted = SnapshotService.Start(host.Options, host.Worker, host.Db);
        var status = restarted.GetStatus(jobId);
        Assert.IsNotNull(status, "the id handed out while queued is the durable job's id");
        Assert.AreEqual(request.ToIdentity().Hash, status.Job.IdentityHash);
        Assert.AreEqual(SnapshotJobStatus.Queued, status.Job.Status, "registered, not produced: the next ensure produces it");
        Assert.IsNull(restarted.ResolveBranch(Repo, "feature"), "the queued retire applied during the drain");
        Assert.AreEqual(1, host.Worker.Calls);
    }

    private static EnsureSnapshotRequest Ensure(string commit, string? branch = null, string? expected = null) => new()
    {
        RepositoryRemoteUrl = Repo,
        CommitSha = commit,
        BranchName = branch,
        ExpectedHeadCommit = expected
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!await Task.Run(condition).WaitAsync(TimeSpan.FromSeconds(10)))
        {
            if (deadline.Elapsed > Settle)
                Assert.Fail("condition not reached within the settle bound");
            await Task.Delay(20);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HttpClient Client { get; private init; } = null!;
        public SnapshotService Service { get; private init; } = null!;
        public FakeSnapshotWorker Worker { get; private init; } = null!;
        public IndexDatabase Db { get; private init; } = null!;
        public ServiceOptions Options { get; private init; } = null!;
        public long BlockerJobId { get; private set; }
        private WebApplication App { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<Harness> StartAsync()
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();
            foreach (var commit in new[] { "commit-A", "commit-B", "commit-C", "commit-D" })
                ServiceTestFixtures.PublishComplete(db, Ensure(commit), recordCommit: true);

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken) with { ControlWriteWait = Bound };
            var worker = new FakeSnapshotWorker(db) { UseGate = true };
            var service = SnapshotService.Start(options, worker, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness
            {
                Client = app.GetTestClient(), App = app, Service = service, Worker = worker, Db = db, Options = options, DbPath = dbPath
            };
        }

        // main at A (the repository's first branch, so its default) and feature at B, while the writer is free.
        public async Task SeedAsync()
        {
            foreach (var (commit, branch) in new[] { ("commit-A", "main"), ("commit-B", "feature") })
            {
                var response = await EnsureAsync(Ensure(commit, branch, expected: ""), wait: true);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            Assert.AreEqual("commit-B", await ResolveCommitAsync("feature"));
        }

        // Starts a production of another repository and leaves it holding the single writer until ReleaseWriter.
        public async Task HoldWriterAsync()
        {
            var response = await EnsureAsync(
                new EnsureSnapshotRequest { RepositoryRemoteUrl = BlockerRepo, CommitSha = "blocker-1" }, wait: false);
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
            using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                BlockerJobId = json.RootElement.GetProperty("job_id").GetInt64();
            await WaitUntilAsync(() => Task.FromResult(Worker.Calls == 1));
        }

        public void ReleaseWriter() => Worker.Gate.TrySetResult();

        /// <summary>Takes the single-writer lease from this service (another owner claims it) and waits until it notices.</summary>
        public async Task StealLeaseAsync()
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                   {
                       DataSource = DbPath,
                       Pooling = false
                   }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE writer_lease SET owner_token = 'thief' WHERE id = 1;";
                cmd.ExecuteNonQuery();
            }
            await WaitUntilAsync(() => Task.FromResult(Service.LeaseLost));
        }

        // Waits until every write admitted so far has applied: a retire of an unknown branch takes the next turn.
        public async Task QuiesceAsync()
        {
            var probe = await Service.RetireBranchAsync(
                new RetireBranchRequest { Repository = "https://github.com/acme/none", Branch = "none" }).WaitAsync(Settle);
            Assert.IsFalse(probe.Retired);
        }

        public Task<HttpResponseMessage> EnsureAsync(
            EnsureSnapshotRequest request, bool wait, CancellationToken cancellationToken = default) =>
            PostJsonAsync(wait ? "/control/ensure" : "/control/ensure?wait=false",
                JsonSerializer.Serialize(request, ServiceJson.Options), cancellationToken);

        public Task<HttpResponseMessage> RetireAsync(
            string branch, string? expected = null, CancellationToken cancellationToken = default) =>
            PostJsonAsync("/control/branches/retire", JsonSerializer.Serialize(
                new RetireBranchRequest { Repository = Repo, Branch = branch, ExpectedHeadCommit = expected },
                ServiceJson.Options), cancellationToken);

        public async Task<JsonElement?> StatusAsync(long jobId)
        {
            var response = await GetAsync($"/control/status/{jobId}");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        public async Task<string?> ResolveCommitAsync(string branch)
        {
            var response = await GetAsync(
                $"/control/resolve?repository={Uri.EscapeDataString(Repo)}&branch={Uri.EscapeDataString(branch)}");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("commit_sha").GetString();
        }

        private async Task<HttpResponseMessage> GetAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            return await Client.SendAsync(request);
        }

        private async Task<HttpResponseMessage> PostJsonAsync(string url, string json, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            return await Client.SendAsync(request, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseWriter();
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Service.Dispose();
            SqliteTestDatabase.Delete(DbPath, Db);
        }
    }
}
