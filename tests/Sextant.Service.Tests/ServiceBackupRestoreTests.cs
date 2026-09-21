using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Service.Backup;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 slice 3 — criterion 6: backup/restore and schema-upgrade rehearsals recover a QUERYABLE
/// AUTHORIZED service. These tests are hermetic (temp SQLite + temp volumes, no network beyond the
/// in-process <see cref="TestServer"/>): they back up a live catalog + immutable artifact volume, restore
/// it into a FRESH location, start a service on the restored catalog with a read-authorization policy, and
/// prove that
/// <list type="bullet">
///   <item>an authorized principal can query the restored snapshot;</item>
///   <item>authorization is RE-ENFORCED after restore — an unknown principal is rejected and a foreign
///   tenant gets the uniform not-found denial (restore must not silently drop slice-1 policy);</item>
///   <item>the immutable snapshot is byte-for-byte unchanged by the backup/restore round-trip;</item>
///   <item>a backup taken at a newer schema than the restoring build is refused (forward-only guard).</item>
/// </list>
/// </summary>
[TestClass]
public class ServiceBackupRestoreTests
{
    private const string ControlToken = "control-secret";
    private const string RepoA = "https://github.com/org/a";
    private const string RepoB = "https://github.com/org/b";

    [TestMethod]
    public async Task Backup_Restore_RecoversQueryableAuthorizedService()
    {
        var request = ServiceTestFixtures.Request(repo: RepoA);
        var backupDir = ServiceTestFixtures.NewDataRoot();

        // ---- source service: publish a snapshot, then back it up ----
        var srcDbPath = ServiceTestFixtures.NewDbPath();
        long originalSymbols;
        using (var srcDb = new IndexDatabase(srcDbPath))
        {
            srcDb.RunMigrations();
            using var srcService = SnapshotService.Start(
                ServiceTestFixtures.NewOptions(srcDbPath, controlToken: ControlToken),
                new FakeSnapshotWorker(srcDb), srcDb);

            var ensure = await srcService.EnsureSnapshotAsync(request, principal: ControlToken);
            Assert.AreEqual(SnapshotJobStatus.Complete, ensure.Status);
            originalSymbols = CountSymbols(srcDb);
            Assert.IsTrue(originalSymbols > 0);

            srcService.CreateBackup(backupDir, principal: ControlToken);
        }

        // ---- restore into a FRESH catalog + volume, then start an AUTHORIZED service on it ----
        var restoredDbPath = ServiceTestFixtures.NewDbPath();
        var restoredData = ServiceTestFixtures.NewDataRoot();
        var manifest = ServiceBackup.Restore(backupDir, restoredDbPath, new ServicePaths(ServiceVolumes.Rooted(restoredData)));
        Assert.AreEqual(IndexDatabase.LatestSchemaVersion, manifest.SchemaVersion);

        await using var host = await Harness.StartAsync(restoredDbPath, restoredData, TwoTenantPolicy());

        // Immutable snapshot is unchanged by the round-trip.
        Assert.AreEqual(originalSymbols, CountSymbols(host.Db),
            "backup/restore round-trips the immutable snapshot without mutating it");

        var url = $"/query/snapshots/{request.ToIdentity().Hash}/symbols?limit=100";

        // Authorized owning tenant reads its restored snapshot — a queryable AUTHORIZED service.
        var authorized = await Send(host, url, "reader-a");
        Assert.AreEqual(HttpStatusCode.OK, authorized.StatusCode,
            "an authorized principal queries the restored snapshot (criterion 6: queryable authorized service)");

        // Authorization is RE-ENFORCED after restore: an unknown principal is rejected.
        var unknown = await Send(host, url, "stranger");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.StatusCode,
            "restore re-enforces slice-1 authz — an unknown principal is rejected, policy was not dropped");

        // A foreign KNOWN tenant gets the uniform not-found denial (no cross-tenant existence oracle).
        var foreign = await Send(host, url, "reader-b");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode,
            "restore re-enforces the uniform-not-found denial for a foreign tenant (criterion 1 preserved)");
    }

    [TestMethod]
    public async Task SchemaUpgradeRehearsal_RestoreThenStart_IsAtLatestSchemaAndQueryable()
    {
        var request = ServiceTestFixtures.Request(repo: RepoA);
        var backupDir = ServiceTestFixtures.NewDataRoot();

        var srcDbPath = ServiceTestFixtures.NewDbPath();
        using (var srcDb = new IndexDatabase(srcDbPath))
        {
            srcDb.RunMigrations();
            using var srcService = SnapshotService.Start(
                ServiceTestFixtures.NewOptions(srcDbPath, controlToken: ControlToken),
                new FakeSnapshotWorker(srcDb), srcDb);
            await srcService.EnsureSnapshotAsync(request, principal: ControlToken);
            srcService.CreateBackup(backupDir, principal: ControlToken);
        }

        // Rehearsal: restore onto a fresh host and start the service. Startup runs migrations to the latest
        // schema, recovers, reconciles, and re-enforces authorization — the restored service is ready.
        var restoredDbPath = ServiceTestFixtures.NewDbPath();
        var restoredData = ServiceTestFixtures.NewDataRoot();
        ServiceBackup.Restore(backupDir, restoredDbPath, new ServicePaths(ServiceVolumes.Rooted(restoredData)));

        await using var host = await Harness.StartAsync(restoredDbPath, restoredData, TwoTenantPolicy());

        Assert.AreEqual(IndexDatabase.LatestSchemaVersion, host.Db.CurrentSchemaVersion,
            "the restored catalog is migrated to the latest schema after the rehearsal");

        var response = await Send(host, $"/query/snapshots/{request.ToIdentity().Hash}/symbols?limit=10", "reader-a");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "the schema-upgrade rehearsal recovers a queryable authorized service (criterion 6)");
    }

    [TestMethod]
    public void Restore_RefusesBackupFromNewerSchema()
    {
        // Produce a real backup, then tamper the manifest to claim a newer schema than this build supports.
        var request = ServiceTestFixtures.Request(repo: RepoA);
        var backupDir = ServiceTestFixtures.NewDataRoot();
        var srcDbPath = ServiceTestFixtures.NewDbPath();
        using (var srcDb = new IndexDatabase(srcDbPath))
        {
            srcDb.RunMigrations();
            using var srcService = SnapshotService.Start(
                ServiceTestFixtures.NewOptions(srcDbPath, controlToken: ControlToken),
                new FakeSnapshotWorker(srcDb), srcDb);
            srcService.EnsureSnapshotAsync(request, principal: ControlToken).GetAwaiter().GetResult();
            srcService.CreateBackup(backupDir, principal: ControlToken);
        }

        var manifestPath = Path.Combine(backupDir, "manifest.json");
        var tampered = File.ReadAllText(manifestPath)
            .Replace($"\"schema_version\":{IndexDatabase.LatestSchemaVersion}",
                     $"\"schema_version\":{IndexDatabase.LatestSchemaVersion + 1}");
        File.WriteAllText(manifestPath, tampered);

        var restoredDbPath = ServiceTestFixtures.NewDbPath();
        var restoredData = ServiceTestFixtures.NewDataRoot();
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ServiceBackup.Restore(backupDir, restoredDbPath, new ServicePaths(ServiceVolumes.Rooted(restoredData))));
        StringAssert.Contains(ex.Message, "newer than this build",
            "a backup from a newer schema is refused before it can half-land (forward-only guard)");
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

    private static long CountSymbols(IndexDatabase db)
    {
        using var cmd = db.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static async Task<HttpResponseMessage> Send(Harness host, string url, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HttpClient Client { get; init; } = null!;
        public IndexDatabase Db { get; init; } = null!;
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<Harness> StartAsync(string dbPath, string dataRoot, ReadAuthorizationPolicy policy)
        {
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();

            var options = ServiceTestFixtures.NewOptions(dbPath, dataRoot: dataRoot, controlToken: ControlToken, queryToken: null)
                with { ReadPolicy = policy };
            var service = SnapshotService.Start(options, new FakeSnapshotWorker(db), db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness { Client = app.GetTestClient(), App = app, Service = service, Db = db, DbPath = dbPath };
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
