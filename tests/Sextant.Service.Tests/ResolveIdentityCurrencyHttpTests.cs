using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// <c>/control/resolve</c> reports whether the snapshot a branch points at is CURRENT under the node's present
/// identity (<c>identity_current</c>, <c>current_identity_hash</c>), so a control plane re-indexes a quiet branch after
/// an identity change (an <c>AnalyzerVersion</c>/schema bump, a policy flip), and a same-commit
/// <c>expected_head_commit</c> ensure re-points the branch at the new-identity snapshot, which queries then serve. The
/// branch decision is the real orchestrator's (<see cref="EnsureBranchCasConvergenceTests.IndexWithOrchestrator"/>).
/// </summary>
[TestClass]
public class ResolveIdentityCurrencyHttpTests
{
    private const string ControlToken = "control-secret";
    // The grant's stored spelling (what the app's reconcile resolves and ensures with) and GitHub's clone_url (what
    // the app's push path ensures with). The snapshot identity folds the raw spelling.
    private const string Repo = "https://github.com/acme/widgets";
    private const string PushRepo = "https://github.com/acme/widgets.git";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string GadgetSource =
        "namespace App { public class Widget { public int Size() => 1; } public class Gadget { } }";

    private string _root = "";
    private string _dbPath = "";
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_identity_current_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "catalog.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task IdentityChange_ResolveIsStale_ASameCommitCasEnsureRepoints_AndQueriesServeTheNewSnapshot()
    {
        long oldSnapshotId;
        string oldHash;
        await using (var before = await Host.StartAsync(_db, Options(packageRestore: true), _root))
        {
            var created = await before.EnsureAsync(Repo, expected: "");
            Assert.IsTrue(created.GetProperty("branch_advanced").GetBoolean());

            var head = await before.ResolveAsync(Repo);
            oldSnapshotId = head.GetProperty("id").GetInt64();
            oldHash = head.GetProperty("identity_hash").GetString()!;
            Assert.IsTrue(head.GetProperty("identity_current").GetBoolean(), head.ToString());
            Assert.AreEqual(oldHash, head.GetProperty("current_identity_hash").GetString());
        }

        // The SAME catalog under a node whose identity differs (package restore off folds restore=off into every
        // identity, as an AnalyzerVersion bump folds a new analyzer). Its worker now also sees a new type.
        await using var after = await Host.StartAsync(_db, Options(packageRestore: false), _root, GadgetSource);
        var newHash = new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = Commit }
            .ToIdentity(IndexProfileDescriptor.Full.ConfigurationHash, restorePolicy: "off").Hash;
        Assert.AreNotEqual(oldHash, newHash);

        var stale = await after.ResolveAsync(Repo);
        Assert.IsFalse(stale.GetProperty("identity_current").GetBoolean(), stale.ToString());
        Assert.AreEqual(oldHash, stale.GetProperty("identity_hash").GetString());
        Assert.AreEqual(newHash, stale.GetProperty("current_identity_hash").GetString(),
            "the identity the caller's own ensure of the commit will produce");
        Assert.AreEqual(oldSnapshotId, stale.GetProperty("id").GetInt64(), "the old snapshot is still served");
        Assert.AreEqual(Commit, stale.GetProperty("commit_sha").GetString());
        Assert.AreEqual(SymbolNotFound, (await after.FindGadgetAsync()).Code);
        Assert.AreEqual(oldSnapshotId, after.ServedSnapshotId());

        // What the app's reconcile sends for a stale branch: the same head commit, advance, CAS on that commit.
        var refreshed = await after.EnsureAsync(Repo, expected: Commit, update: BranchUpdateMode.Advance);
        Assert.AreEqual(SnapshotJobStatus.Complete, refreshed.GetProperty("status").GetString(), refreshed.ToString());
        Assert.AreEqual(newHash, refreshed.GetProperty("identity_hash").GetString());
        Assert.IsTrue(refreshed.GetProperty("branch_advanced").GetBoolean(), "the CAS on the same commit passes");
        Assert.AreEqual(1, after.Worker.Calls);
        var newSnapshotId = refreshed.GetProperty("snapshot_id").GetInt64();
        Assert.AreNotEqual(oldSnapshotId, newSnapshotId);

        var current = await after.ResolveAsync(Repo);
        Assert.IsTrue(current.GetProperty("identity_current").GetBoolean(), current.ToString());
        Assert.AreEqual(newSnapshotId, current.GetProperty("id").GetInt64());
        Assert.AreEqual(newHash, current.GetProperty("identity_hash").GetString());
        Assert.AreEqual(newHash, current.GetProperty("current_identity_hash").GetString());
        Assert.AreEqual(Commit, current.GetProperty("commit_sha").GetString());
        Assert.IsTrue(current.GetProperty("is_default").GetBoolean());
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf(oldSnapshotId), "the old head is superseded (#128)");

        var gadget = await after.FindGadgetAsync();
        Assert.IsNull(gadget.Code, gadget.Body.ToString());
        Assert.AreEqual(1, gadget.Body.GetProperty("meta").GetProperty("result_count").GetInt32());
        Assert.AreEqual(newSnapshotId, after.ServedSnapshotId());

        var again = await after.EnsureAsync(Repo, expected: Commit, update: BranchUpdateMode.Advance);
        Assert.IsFalse(again.GetProperty("branch_advanced").GetBoolean(), "already pointed at it");
        Assert.AreEqual(1, after.Worker.Calls, "a current branch is never re-indexed");
    }

    [TestMethod]
    public async Task AnalyzerUpgrade_PreviousAnalyzerSnapshotIsStale_UntilTheSameCommitIsReEnsured()
    {
        await using var host = await Host.StartAsync(_db, Options(packageRestore: true), _root);
        await host.EnsureAsync(Repo, expected: "");
        var head = await host.ResolveAsync(Repo);
        var snapshotId = head.GetProperty("id").GetInt64();
        var currentHash = head.GetProperty("identity_hash").GetString()!;

        // Rewrite the published snapshot (and its job) into what the previous release left behind: the SAME commit
        // indexed under the previous analyzer and snapshot schema, so its identity hash is the previous release's.
        var previous = new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = Commit }
            .ToIdentity(IndexProfileDescriptor.Full.ConfigurationHash) with
            {
                AnalyzerVersion = "4",
                SchemaVersion = 24
            };
        var rebuilt = previous with
        {
            AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
            SchemaVersion = IndexDatabase.SnapshotSchemaVersion
        };
        Assert.AreEqual(currentHash, rebuilt.Hash, "the test models exactly the analyzer/schema bump");
        Execute("UPDATE snapshots SET identity_hash = @old, analyzer_version = '4', schema_version = 24 WHERE id = @id;",
            ("@old", previous.Hash), ("@id", snapshotId));
        Execute("UPDATE snapshot_jobs SET identity_hash = @old WHERE identity_hash = @current;",
            ("@old", previous.Hash), ("@current", currentHash));

        var stale = await host.ResolveAsync(Repo);
        Assert.IsFalse(stale.GetProperty("identity_current").GetBoolean(), stale.ToString());
        Assert.AreEqual(previous.Hash, stale.GetProperty("identity_hash").GetString());
        Assert.AreEqual(currentHash, stale.GetProperty("current_identity_hash").GetString());
        Assert.AreEqual(snapshotId, host.ServedSnapshotId());
        var widget = await host.CallAsync("find_symbol", """{"name":"App.Widget"}""");
        Assert.IsNull(widget.Code, widget.Body.ToString());
        Assert.AreEqual(RemoteResponsePresenter.IncompatibleWarning,
            widget.Body.GetProperty("meta").GetProperty("snapshot").GetProperty("warning").GetString(),
            "agents already see the analyzer drift");

        var refreshed = await host.EnsureAsync(Repo, expected: Commit, update: BranchUpdateMode.Advance);
        Assert.AreEqual(currentHash, refreshed.GetProperty("identity_hash").GetString());
        Assert.IsTrue(refreshed.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual(2, host.Worker.Calls, "the current identity is built anew");

        var current = await host.ResolveAsync(Repo);
        Assert.IsTrue(current.GetProperty("identity_current").GetBoolean(), current.ToString());
        Assert.AreEqual(refreshed.GetProperty("snapshot_id").GetInt64(), current.GetProperty("id").GetInt64());
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf(snapshotId));
        Assert.AreEqual(current.GetProperty("id").GetInt64(), host.ServedSnapshotId());
    }

    [TestMethod]
    public async Task ARepositorySpellingVariant_IsNeverStale_AndAnEnsureUnderEitherSpellingConverges()
    {
        await using (var push = await Host.StartAsync(_db, Options(packageRestore: true), _root))
        {
            // A push ensures with GitHub's clone_url; the reconcile resolves with the grant's spelling.
            var built = await push.EnsureAsync(PushRepo, expected: "");
            var pushHash = built.GetProperty("identity_hash").GetString()!;

            foreach (var spelling in new[] { Repo, PushRepo, "HTTPS://GitHub.com/acme/widgets/" })
            {
                var head = await push.ResolveAsync(spelling);
                Assert.IsTrue(head.GetProperty("identity_current").GetBoolean(), $"{spelling}: {head}");
                Assert.AreEqual(pushHash, head.GetProperty("identity_hash").GetString());
                Assert.AreEqual(pushHash, head.GetProperty("current_identity_hash").GetString(), spelling);
            }
            Assert.AreEqual(1, push.Worker.Calls, "nothing was re-indexed");
        }

        await using var after = await Host.StartAsync(_db, Options(packageRestore: false), _root);
        var stale = await after.ResolveAsync(Repo);
        Assert.IsFalse(stale.GetProperty("identity_current").GetBoolean(), stale.ToString());
        var grantHash = stale.GetProperty("current_identity_hash").GetString()!;

        var refreshed = await after.EnsureAsync(Repo, expected: Commit, update: BranchUpdateMode.Advance);
        Assert.AreEqual(grantHash, refreshed.GetProperty("identity_hash").GetString(),
            "current_identity_hash is exactly what the resolving caller's ensure builds");
        Assert.IsTrue(refreshed.GetProperty("branch_advanced").GetBoolean());

        foreach (var spelling in new[] { Repo, PushRepo })
        {
            var head = await after.ResolveAsync(spelling);
            Assert.IsTrue(head.GetProperty("identity_current").GetBoolean(), $"{spelling}: {head}");
            Assert.AreEqual(grantHash, head.GetProperty("current_identity_hash").GetString(), spelling);
        }
        Assert.AreEqual(1, after.Worker.Calls);
    }

    [TestMethod]
    public async Task ASnapshotWithNoRecordedCommit_OmitsBothFields()
    {
        // A pre-published snapshot with no commits row (and so no commit_sha): currency cannot be computed, and both
        // fields are omitted together, never null, so a client reads the absence as current. The fixture publishes
        // under the null default configuration, so this node uses it too and the ensure reuses the snapshot.
        ServiceTestFixtures.PublishComplete(_db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = Commit });
        await using var host = await Host.StartAsync(
            _db, Options(packageRestore: true) with { DefaultConfigHash = null }, _root);
        await host.EnsureAsync(Repo, expected: "");

        var head = await host.ResolveAsync(Repo);
        Assert.IsFalse(head.TryGetProperty("commit_sha", out _), head.ToString());
        Assert.IsFalse(head.TryGetProperty("identity_current", out _), head.ToString());
        Assert.IsFalse(head.TryGetProperty("current_identity_hash", out _), head.ToString());
        Assert.IsTrue(head.TryGetProperty("identity_hash", out _));
        Assert.AreEqual(0, host.Worker.Calls);
    }

    [TestMethod]
    public async Task ASnapshotWithNoJob_IsCurrentUnderTheCatalogSpelling()
    {
        // A snapshot published outside the job ledger (no snapshot_jobs row) was built under the catalog's stored
        // spelling, so a resolve under another spelling still reads it as current.
        var snapshotId = ServiceTestFixtures.PublishComplete(
            _db, new EnsureSnapshotRequest { RepositoryRemoteUrl = PushRepo, CommitSha = Commit }, recordCommit: true);
        var snapshots = new SnapshotStore(_db.GetConnection());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var branchId = snapshots.EnsureBranch(snapshots.GetRepositoryId(PushRepo)!.Value, "main", isDefault: true, now);
        snapshots.SetBranchPointer(branchId, snapshotId, now);
        await using var host = await Host.StartAsync(
            _db, Options(packageRestore: true) with { DefaultConfigHash = null }, _root);

        var head = await host.ResolveAsync(Repo);
        Assert.IsTrue(head.GetProperty("identity_current").GetBoolean(), head.ToString());
        Assert.AreEqual(head.GetProperty("identity_hash").GetString(), head.GetProperty("current_identity_hash").GetString());
        Assert.IsNull(new SnapshotJobStore(_db.GetConnection()).GetJobByIdentity(
            head.GetProperty("identity_hash").GetString()!), "the snapshot has no job row");
    }

    [TestMethod]
    public async Task AContributedHead_OmitsBothFields()
    {
        // A head assembled from client contributions carries the contributor's identity (its own tree sha, no
        // toolchain), which no service ensure reproduces, so the strict comparison would read it as stale forever and
        // a reconciler would replace it with a server build. Its currency is left to its contributor: not reported.
        await using var host = await Host.StartAsync(_db, Options(packageRestore: true), _root);
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, "win-x64|net8.0-windows|sdk-8.0.400", [new PayloadProjectSpec("src/Win/Win.csproj", "net8.0-windows")]);
        var contributed = await host.Service.IngestContributionAsync(new IngestContributionRequest
        {
            Artifact = artifact.ToArray(), Finalize = true, BranchName = "main", IsDefaultBranch = true
        });
        Assert.AreEqual(ContributionIngestStatus.Complete, contributed.Status, contributed.Message);

        var head = await host.ResolveAsync(Repo);
        Assert.AreEqual(contributed.SnapshotId, head.GetProperty("id").GetInt64());
        Assert.AreEqual(Commit, head.GetProperty("commit_sha").GetString(), "the head records its commit");
        Assert.IsFalse(head.TryGetProperty("identity_current", out _), head.ToString());
        Assert.IsFalse(head.TryGetProperty("current_identity_hash", out _), head.ToString());
    }

    private const string SymbolNotFound = ResponseBuilder.SymbolNotFoundCode;

    // The service's default profile (the worker's orchestrator stamps its configuration hash), with or without
    // package restore: off folds restore=off into the request and published identity.
    private ServiceOptions Options(bool packageRestore) =>
        ServiceTestFixtures.NewOptions(_dbPath, dataRoot: Path.Combine(_root, "data"), controlToken: ControlToken) with
        {
            DefaultConfigHash = IndexProfileDescriptor.Full.ConfigurationHash,
            PackageRestore = packageRestore
        };

    private string StatusOf(long snapshotId) => new SnapshotStore(_db.GetConnection()).GetById(snapshotId)!.Status;

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        Assert.AreEqual(1, cmd.ExecuteNonQuery(), sql);
    }

    private sealed class Host : IAsyncDisposable
    {
        public HttpClient Client { get; private init; } = null!;
        public FakeSnapshotWorker Worker { get; private init; } = null!;
        public SnapshotService Service { get; private init; } = null!;
        private WebApplication App { get; init; } = null!;
        private string DbPath { get; init; } = "";

        // A service + HTTP host over the shared catalog; its worker runs the real orchestrator under the node's
        // restore policy (the published identity must equal the requested one). Disposing it releases the writer
        // lease and leaves the catalog for the next host.
        public static async Task<Host> StartAsync(IndexDatabase db, ServiceOptions options, string root, string? source = null)
        {
            var worker = new FakeSnapshotWorker(db, (self, request) => EnsureBranchCasConvergenceTests.IndexWithOrchestrator(
                self.Database, root, request, options.RestoreIdentityComponent, source));
            var service = SnapshotService.Start(options, worker, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();
            return new Host { Client = app.GetTestClient(), App = app, Service = service, Worker = worker, DbPath = db.DbPath };
        }

        public async Task<JsonElement> EnsureAsync(string repository, string? expected = null, string? update = null)
        {
            var request = new EnsureSnapshotRequest
            {
                RepositoryRemoteUrl = repository,
                CommitSha = Commit,
                ExpectedHeadCommit = expected,
                BranchUpdate = update
            };
            using var message = new HttpRequestMessage(HttpMethod.Post, "/control/ensure")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(request, ServiceJson.Options), Encoding.UTF8, "application/json")
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            using var response = await Client.SendAsync(message);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, raw);
            return JsonDocument.Parse(raw).RootElement.Clone();
        }

        public async Task<JsonElement> ResolveAsync(string repository)
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Get, $"/control/resolve?repository={Uri.EscapeDataString(repository)}");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            using var response = await Client.SendAsync(message);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, raw);
            return JsonDocument.Parse(raw).RootElement.Clone();
        }

        public Task<(JsonElement Body, string? Code)> FindGadgetAsync() =>
            CallAsync("find_symbol", """{"name":"App.Gadget"}""");

        // The snapshot a read of the repository is served from. The remote surface prints only its commit, which
        // several snapshots here share, so a local provider over the same catalog reads its id.
        public long ServedSnapshotId()
        {
            using var local = new DatabaseProvider(DbPath) { RequestedRepository = () => Repo };
            var status = JsonDocument.Parse(GetIndexStatusTool.GetIndexStatus(local)).RootElement;
            Assert.IsFalse(status.GetProperty("meta").TryGetProperty("error", out _), status.ToString());
            return status.GetProperty("index").GetProperty("snapshot").GetProperty("base_snapshot_id").GetInt64();
        }

        // One anonymous /mcp tools/call selecting the repository by header; the tool's JSON text and its error code.
        public async Task<(JsonElement Body, string? Code)> CallAsync(string tool, string arguments)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool
                    + "\",\"arguments\":" + arguments + "}}",
                    Encoding.UTF8, "application/json")
            };
            message.Headers.Accept.ParseAdd("application/json");
            message.Headers.Accept.ParseAdd("text/event-stream");
            message.Headers.Add(ServiceApp.RepositoryHeader, Repo);
            using var response = await Client.SendAsync(message);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, raw);

            var payload = raw.Contains("data:", StringComparison.Ordinal)
                ? string.Concat(raw.Split('\n')
                    .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                    .Select(l => l["data:".Length..].Trim()))
                : raw;
            using var rpc = JsonDocument.Parse(payload);
            var text = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            var body = JsonDocument.Parse(text).RootElement.Clone();
            var code = body.TryGetProperty("meta", out var meta) && meta.TryGetProperty("error", out var error)
                ? error.GetProperty("code").GetString()
                : null;
            return (body, code);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Service.Dispose();
        }
    }
}
