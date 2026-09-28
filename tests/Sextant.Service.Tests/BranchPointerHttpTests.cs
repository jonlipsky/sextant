using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-6/7 at the HTTP boundary: malformed branch guards on <c>POST /control/ensure</c> are a 400 before any
/// job exists (audited as denied); <c>POST /control/branches/retire</c> deletes a branch row under the SVC-5
/// URL policy, idempotently, refusing the default branch and a head-CAS mismatch with 409; and
/// <c>/control/resolve</c> reports the head's <c>commit_sha</c>, the resolved <c>branch</c>, <c>is_default</c>
/// and <c>head_sequence</c>.
/// </summary>
[TestClass]
public class BranchPointerHttpTests
{
    private const string ControlToken = "control-secret";
    private const string Repo = "https://github.com/acme/widgets";

    [TestMethod]
    [DataRow("{\"expected_head_commit\":\"commit-A\",\"branch_head_sequence\":3}", BranchGuardReason.ConflictingBranchGuards)]
    [DataRow("{\"expected_head_commit\":\"\",\"branch_head_sequence\":3,\"branch_update\":\"advance\"}", BranchGuardReason.ConflictingBranchGuards)]
    [DataRow("{\"branch_update\":\"sometimes\"}", BranchGuardReason.InvalidBranchUpdate)]
    public async Task MalformedGuards_Are400_CreateNoJob_AndAreAudited(string guards, string reason)
    {
        await using var host = await Harness.StartAsync();

        var response = await host.PostJsonAsync("/control/ensure", MergeEnsure("commit-A", guards));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("rejected", json.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
        }
        Assert.AreEqual(0, host.Worker.Calls);
        Assert.AreEqual(0L, host.Scalar("SELECT COUNT(*) FROM snapshot_jobs;"));
        var audit = host.Service.RecentAudit(action: AuditAction.Ensure);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Denied, audit[0].Outcome);
        Assert.AreEqual(reason, audit[0].Detail);
        Assert.IsNull(audit[0].RepositoryScope);
    }

    [TestMethod]
    public async Task Ensure_ReportsBranchAdvanced()
    {
        await using var host = await Harness.StartAsync();

        var created = await host.EnsureAsync("commit-A", expected: "");
        var stale = await host.EnsureAsync("commit-B", expected: "commit-X");
        var matched = await host.EnsureAsync("commit-B", expected: "commit-A", forced: true);

        Assert.IsTrue(created.GetProperty("branch_advanced").GetBoolean());
        Assert.IsFalse(stale.GetProperty("branch_advanced").GetBoolean());
        Assert.IsTrue(matched.GetProperty("branch_advanced").GetBoolean());
        var head = await host.ResolveAsync("main");
        Assert.AreEqual("commit-B", head!.Value.GetProperty("commit_sha").GetString());
    }

    [TestMethod]
    public async Task Resolve_ReportsTheHeadCommitAndItsBranch()
    {
        await using var host = await Harness.StartAsync();
        await host.EnsureAsync("commit-A", sequence: 7);
        await host.EnsureAsync("commit-B", branch: "feature", expected: "");

        var main = (await host.ResolveAsync(null))!.Value;
        Assert.AreEqual("commit-A", main.GetProperty("commit_sha").GetString());
        Assert.AreEqual("main", main.GetProperty("branch").GetString(), "the default branch resolves to its own name");
        Assert.IsTrue(main.GetProperty("is_default").GetBoolean());
        Assert.AreEqual(7L, main.GetProperty("head_sequence").GetInt64());
        Assert.AreEqual(SnapshotStatus.Complete, main.GetProperty("status").GetString(), "the snapshot row is still served");

        var feature = (await host.ResolveAsync("feature"))!.Value;
        Assert.AreEqual("commit-B", feature.GetProperty("commit_sha").GetString());
        Assert.AreEqual("feature", feature.GetProperty("branch").GetString());
        Assert.IsFalse(feature.GetProperty("is_default").GetBoolean());
        Assert.IsFalse(feature.TryGetProperty("head_sequence", out _), "an absent sequence is omitted");

        Assert.IsNull(await host.ResolveAsync("missing"));
    }

    [TestMethod]
    public async Task Retire_WithoutCas_DeletesTheBranch_AndKeepsItsSnapshot()
    {
        await using var host = await Harness.StartAsync();
        await host.EnsureAsync("commit-A");
        var feature = await host.EnsureAsync("commit-B", branch: "feature", expected: "");
        var snapshotId = feature.GetProperty("snapshot_id").GetInt64();

        var response = await host.RetireAsync("feature");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("{\"retired\":true}", await response.Content.ReadAsStringAsync());
        Assert.IsNull(await host.ResolveAsync("feature"));
        Assert.AreEqual(1L, host.Scalar("SELECT COUNT(*) FROM branches;"), "only the default branch remains");
        Assert.AreEqual(SnapshotStatus.Complete, new SnapshotStore(host.Db.GetConnection()).GetById(snapshotId)!.Status,
            "the retired branch's snapshot stays for retention");
        var audit = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Complete, audit[0].Outcome);
        Assert.AreEqual("retired", audit[0].Detail);
        Assert.AreEqual(Repo, audit[0].RepositoryScope);
        Assert.AreEqual(AuditLogStore.HashActor(ControlToken), audit[0].Actor);
    }

    [TestMethod]
    public async Task Retire_WithAMatchingCas_DeletesTheBranch()
    {
        await using var host = await Harness.StartAsync();
        await host.EnsureAsync("commit-A");
        await host.EnsureAsync("commit-B", branch: "feature", expected: "");

        var response = await host.RetireAsync("feature", expected: "COMMIT-B");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("{\"retired\":true}", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Retire_WithAStaleCas_Is409_AndKeepsTheBranch()
    {
        // A newer push re-created the branch at C after the delete push (whose before was B) was sent.
        await using var host = await Harness.StartAsync();
        await host.EnsureAsync("commit-A");
        await host.EnsureAsync("commit-B", branch: "feature", expected: "");
        await host.EnsureAsync("commit-C", branch: "feature", expected: "commit-B");

        var response = await host.RetireAsync("feature", expected: "commit-B");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        await AssertRejected(response, BranchGuardReason.HeadMismatch);
        Assert.AreEqual("commit-C", (await host.ResolveAsync("feature"))!.Value.GetProperty("commit_sha").GetString());
        var audit = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(AuditOutcome.Denied, audit[0].Outcome);
        Assert.AreEqual(BranchGuardReason.HeadMismatch, audit[0].Detail);
    }

    [TestMethod]
    public async Task Retire_TheDefaultBranch_Is409()
    {
        await using var host = await Harness.StartAsync();
        await host.EnsureAsync("commit-A");

        var response = await host.RetireAsync("main", expected: "commit-A");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        await AssertRejected(response, BranchGuardReason.DefaultBranch);
        Assert.IsNotNull(await host.ResolveAsync("main"));
        Assert.AreEqual(BranchGuardReason.DefaultBranch, host.Service.RecentAudit(action: AuditAction.Retire)[0].Detail);
    }

    [TestMethod]
    public async Task Retire_IsIdempotent()
    {
        await using var host = await Harness.StartAsync();
        await host.EnsureAsync("commit-A");
        await host.EnsureAsync("commit-B", branch: "feature", expected: "");

        Assert.AreEqual("{\"retired\":true}", await (await host.RetireAsync("feature")).Content.ReadAsStringAsync());
        var again = await host.RetireAsync("feature", expected: "commit-B");
        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, "a missing branch is not an error, even under a CAS");
        Assert.AreEqual("{\"retired\":false}", await again.Content.ReadAsStringAsync());
        var unknownRepo = await host.RetireAsync("feature", repository: "https://github.com/acme/other");
        Assert.AreEqual("{\"retired\":false}", await unknownRepo.Content.ReadAsStringAsync());

        var audit = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(3, audit.Count);
        Assert.AreEqual("absent", audit[0].Detail);
        Assert.AreEqual(AuditOutcome.Complete, audit[0].Outcome);
    }

    [TestMethod]
    public async Task Retired_Branch_IsNotRecreatedByAStalePush()
    {
        await using var host = await Harness.StartAsync();
        await host.EnsureAsync("commit-A");
        await host.EnsureAsync("commit-B", branch: "feature", expected: "");
        await host.RetireAsync("feature", expected: "commit-B");

        // A delayed push that was sent before the delete (before = A, after = B) arrives after it.
        var stale = await host.EnsureAsync("commit-B", branch: "feature", expected: "commit-A");

        Assert.IsFalse(stale.GetProperty("branch_advanced").GetBoolean());
        Assert.IsNull(await host.ResolveAsync("feature"), "a failed CAS never re-creates a retired branch");
    }

    [TestMethod]
    [DataRow("https://localhost/acme/widgets", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("http://github.com/acme/widgets", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("https://github.com/acme/../widgets", RepositoryUrlRejection.PathNotAllowed)]
    public async Task Retire_AppliesTheRepositoryUrlPolicy(string url, string reason)
    {
        await using var host = await Harness.StartAsync();

        var response = await host.RetireAsync("feature", repository: url);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        await AssertRejected(response, reason);
        Assert.IsFalse(body.Contains(url, StringComparison.Ordinal), "the refused URL is never echoed");
        var audit = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Denied, audit[0].Outcome);
        Assert.AreEqual(reason, audit[0].Detail);
        Assert.IsNull(audit[0].RepositoryScope, "the untrusted URL is never stored as a repository scope");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("  ")]
    public async Task Retire_RequiresABranch(string branch)
    {
        await using var host = await Harness.StartAsync();

        var response = await host.RetireAsync(branch);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRejected(response, BranchGuardReason.BranchRequired);
        Assert.AreEqual(BranchGuardReason.BranchRequired, host.Service.RecentAudit(action: AuditAction.Retire)[0].Detail);
    }

    [TestMethod]
    public async Task Retire_RequiresTheControlToken()
    {
        await using var host = await Harness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/control/branches/retire")
        {
            Content = JsonContent.Create(new { repository = Repo, branch = "feature" })
        };

        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Retire).Count);
    }

    private static async Task AssertRejected(HttpResponseMessage response, string reason)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(new[] { "status", "reason" },
            json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual("rejected", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
    }

    private static string MergeEnsure(string commit, string guards)
    {
        var body = JsonSerializer.SerializeToNode(
            new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = commit }, ServiceJson.Options)!.AsObject();
        foreach (var (key, value) in System.Text.Json.Nodes.JsonNode.Parse(guards)!.AsObject())
            body[key] = value?.DeepClone();
        return body.ToJsonString();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HttpClient Client { get; private init; } = null!;
        public SnapshotService Service { get; private init; } = null!;
        public FakeSnapshotWorker Worker { get; private init; } = null!;
        public IndexDatabase Db { get; private init; } = null!;
        private WebApplication App { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<Harness> StartAsync()
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken);
            // Every commit is pre-published, so each ensure is a service-side branch decision (the reuse path);
            // the worker path's convergence with it is covered by EnsureBranchCasConvergenceTests.
            foreach (var commit in new[] { "commit-A", "commit-B", "commit-C" })
                ServiceTestFixtures.PublishComplete(
                    db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = commit }, recordCommit: true);
            var worker = new FakeSnapshotWorker(db, FakeSnapshotWorker.Throws("every commit is pre-published"));
            var service = SnapshotService.Start(options, worker, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness { Client = app.GetTestClient(), App = app, Service = service, Worker = worker, Db = db, DbPath = dbPath };
        }

        public async Task<JsonElement> EnsureAsync(
            string commit, string? branch = null, string? expected = null, long? sequence = null, bool? forced = null)
        {
            var request = new EnsureSnapshotRequest
            {
                RepositoryRemoteUrl = Repo,
                CommitSha = commit,
                BranchName = branch,
                ExpectedHeadCommit = expected,
                BranchHeadSequence = sequence,
                Forced = forced
            };
            var response = await PostJsonAsync("/control/ensure", JsonSerializer.Serialize(request, ServiceJson.Options));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        public Task<HttpResponseMessage> RetireAsync(string branch, string? expected = null, string repository = Repo) =>
            PostJsonAsync("/control/branches/retire", JsonSerializer.Serialize(
                new RetireBranchRequest { Repository = repository, Branch = branch, ExpectedHeadCommit = expected },
                ServiceJson.Options));

        public async Task<JsonElement?> ResolveAsync(string? branch)
        {
            var url = $"/control/resolve?repository={Uri.EscapeDataString(Repo)}"
                      + (branch is null ? "" : $"&branch={Uri.EscapeDataString(branch)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            var response = await Client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        public async Task<HttpResponseMessage> PostJsonAsync(string url, string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            return await Client.SendAsync(request);
        }

        public long Scalar(string sql)
        {
            using var cmd = Db.GetConnection().CreateCommand();
            cmd.CommandText = sql;
            return (long)cmd.ExecuteScalar()!;
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
