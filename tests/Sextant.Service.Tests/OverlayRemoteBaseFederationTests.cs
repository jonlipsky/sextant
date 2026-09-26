using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #108 — the overlay planner resolves its committed BASE from a remote peer so a thin machine that
/// indexed ONLY its working-tree diff can still answer <c>find_symbol</c> by federating
/// <c>overlay ⊕ remote-base</c>, with NO full local base index. These end-to-end tests drive the real
/// <see cref="FindSymbolTool"/> over a <see cref="DatabaseProvider"/> whose local catalog holds ONLY a
/// baseless overlay (<c>is_overlay = 1</c>, <c>base_snapshot_id IS NULL</c>) selected on the default
/// branch, with the committed base served ONLY by an in-process peer addressed by the SAME immutable
/// identity hash the read planner recomputes. They cover the six acceptance criteria:
/// <list type="bullet">
///   <item>1 — a symbol living only in the remote base (an UNTOUCHED project) is federated in;</item>
///   <item>2 — a base symbol whose project the overlay TOUCHED is shadowed, never resurrected;</item>
///   <item>3 — an unreachable/rejected peer degrades to the local overlay with an actionable reason;</item>
///   <item>4 — with NO peers the read is byte-identical to the pure-local path;</item>
///   <item>5 — a remote fetch never widens local authorization (a rejected token yields no symbols);</item>
///   <item>6 — <c>meta.snapshot</c> states origin (local/remote), base identity hash, completeness,
///   freshness.</item>
/// </list>
/// </summary>
[TestClass]
public class OverlayRemoteBaseFederationTests
{
    private const string RepoUrl = "https://github.com/org/thin-repo";
    private const string Commit = "commit_head";
    private const string Tree = "tree_head";
    private const string QueryToken = "peer-query-secret";

    // ---- criterion 1 + 6: a base-only symbol is federated in with remote provenance -----------------

    [TestMethod]
    public async Task FindSymbol_Exact_FederatesRemoteBase_WhenNoLocalBase()
    {
        using var local = new ThinMachine();
        await using var peer = await PeerServer.StartAsync(token: QueryToken);
        using var node = local.FederatedTo(peer.Client, QueryToken);

        // BaseType lives ONLY in the committed base's UNTOUCHED project on the peer — never indexed locally.
        var json = await FindSymbolTool.FindSymbol(node, "global::Shared.BaseType");
        using var doc = JsonDocument.Parse(json);

        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(1, results.GetArrayLength(),
            "find_symbol federated the committed base from the peer with no full local index (criterion 1)");
        Assert.AreEqual("global::Shared.BaseType", results[0].GetProperty("fully_qualified_name").GetString());

        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("remote", snapshot.GetProperty("origin").GetString(), "the base row's origin is the peer (criterion 6)");
        Assert.AreEqual(BaseHash(), snapshot.GetProperty("base_identity_hash").GetString(),
            "meta states the immutable base identity hash the federated read rests on (criterion 6)");
        Assert.IsTrue(snapshot.GetProperty("is_overlay").GetBoolean(), "the selected generation is the overlay");
    }

    [TestMethod]
    public async Task FindSymbol_Fuzzy_UnionsLocalOverlayAndRemoteBase()
    {
        using var local = new ThinMachine();
        await using var peer = await PeerServer.StartAsync(token: QueryToken);
        using var node = local.FederatedTo(peer.Client, QueryToken);

        // "Base" is a full FTS token of the local overlay symbol (display "Base") AND a substring of the
        // base's untouched "BaseType" — so one fuzzy query unions the local overlay and the remote base.
        var json = await FindSymbolTool.FindSymbol(node, "Base", fuzzy: true);
        using var doc = JsonDocument.Parse(json);

        var fqns = doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString()).ToHashSet();
        Assert.IsTrue(fqns.Contains("global::Touched.Base"), "the local overlay symbol is served");
        Assert.IsTrue(fqns.Contains("global::Shared.BaseType"),
            "the untouched base symbol is federated from the peer and unioned with the overlay (criterion 1)");

        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("remote", snapshot.GetProperty("origin").GetString());
    }

    // ---- criterion 2: shadowing across the remote boundary ------------------------------------------

    [TestMethod]
    public async Task FindSymbol_ShadowsTouchedProject_AcrossRemoteBoundary()
    {
        using var local = new ThinMachine();
        await using var peer = await PeerServer.StartAsync(token: QueryToken);
        using var node = local.FederatedTo(peer.Client, QueryToken);

        // The peer's base ALSO contains a symbol in the TOUCHED project (StaleType, canonical "logical_touched").
        // The overlay re-extracted that project, so the base's stale row must NEVER resurface.
        var json = await FindSymbolTool.FindSymbol(node, "global::Touched.StaleType");
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength(),
            "a base symbol from a project the overlay touched is shadowed across the remote boundary (criterion 2)");

        // Differential proof: the peer really does publish StaleType — it is shadowed, not merely absent.
        var onPeer = await peer.FetchAll(BaseHash());
        CollectionAssert.Contains(onPeer, "global::Touched.StaleType",
            "the committed base genuinely publishes the stale row (shadowing is doing the work, not a missing seed)");
    }

    // ---- criterion 5: a remote fetch never widens local authorization -------------------------------

    [TestMethod]
    public async Task FindSymbol_WrongPeerToken_FailsClosed_NoRemoteSymbolsLeak()
    {
        using var local = new ThinMachine();
        await using var peer = await PeerServer.StartAsync(token: QueryToken);
        // The thin machine presents the WRONG token; the peer authorizes against its own policy (401/403).
        using var node = local.FederatedTo(peer.Client, token: "wrong-token");

        var json = await FindSymbolTool.FindSymbol(node, "global::Shared.BaseType");
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength(),
            "a rejected peer token yields no federated symbols — a remote fetch never widens authorization (criterion 5)");
        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("local", snapshot.GetProperty("origin").GetString(), "a failed fetch degrades to local origin");
        StringAssert.Contains(snapshot.GetProperty("fallback_reason").GetString()!, "unavailable",
            "the failure is an actionable reason, never a silent success (criterion 3/6)");
    }

    // ---- criterion 3: offline / unreachable peer degrades with an actionable reason -----------------

    [TestMethod]
    public async Task FindSymbol_PeerUnreachable_DegradesToLocalOverlay_WithReason()
    {
        using var local = new ThinMachine();
        using var peerDb = new PeerCatalog();
        var handler = new ControllablePeerHandler(peerDb.Db) { Online = false };
        using var node = local.FederatedTo(new HttpClient(handler), QueryToken);

        // A base-only symbol cannot be served while the peer is down and nothing is cached: the local
        // overlay answers (empty here) with an actionable reason rather than faulting.
        var json = await FindSymbolTool.FindSymbol(node, "global::Shared.BaseType");
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength());
        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("local", snapshot.GetProperty("origin").GetString());
        StringAssert.Contains(snapshot.GetProperty("fallback_reason").GetString()!, "unavailable",
            "an unreachable peer surfaces an actionable fallback reason (criterion 3)");
        // The base identity hash is still stamped so the caller can see WHICH base could not be reached.
        Assert.AreEqual(BaseHash(), snapshot.GetProperty("base_identity_hash").GetString());
    }

    // ---- criterion 4: with NO peers the read path is byte-identical to pure-local -------------------

    [TestMethod]
    public void FindSymbol_NoPeers_ByteIdentical_ToProviderWithoutFederationInfra()
    {
        // A NORMAL full local index (not a remote-base overlay). The presence or absence of the federation
        // infrastructure must make ZERO difference to the bytes find_symbol returns.
        using var plain = new PlainLocalIndex();

        using var bare = new DatabaseProvider(plain.Path);
        using var withInfra = new DatabaseProvider(plain.Path);
        withInfra.AttachRemoteFederation(RemoteBaseSnapshotFederation.Create(new SextantConfiguration())); // no peers
        Assert.IsNull(withInfra.RemoteBaseSource, "no peers configured ⇒ no remote source is constructed (criterion 4)");

        var bareExact = Normalize(FindSymbolTool.FindSymbol(bare, "global::App.Thing").GetAwaiter().GetResult());
        var infraExact = Normalize(FindSymbolTool.FindSymbol(withInfra, "global::App.Thing").GetAwaiter().GetResult());
        Assert.AreEqual(bareExact, infraExact, "peers-unset exact lookup is byte-identical to the pure-local path (criterion 4)");

        var bareFuzzy = Normalize(FindSymbolTool.FindSymbol(bare, "Thing", fuzzy: true).GetAwaiter().GetResult());
        var infraFuzzy = Normalize(FindSymbolTool.FindSymbol(withInfra, "Thing", fuzzy: true).GetAwaiter().GetResult());
        Assert.AreEqual(bareFuzzy, infraFuzzy, "peers-unset fuzzy search is byte-identical to the pure-local path (criterion 4)");
    }

    [TestMethod]
    public async Task FindSymbol_RemoteBaseOverlay_NoPeers_ServesLocalOverlay_NoRemoteMetaLeak()
    {
        // A baseless overlay exists locally but NO peer is configured: the read still serves the local
        // overlay and records that the base is unresolvable, never touching the network.
        using var local = new ThinMachine();
        using var node = new DatabaseProvider(local.Path); // no federation attached
        Assert.IsNull(node.RemoteBaseSource);

        var json = await FindSymbolTool.FindSymbol(node, "global::Touched.LocalType");
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(1, doc.RootElement.GetProperty("results").GetArrayLength(), "the local overlay symbol is served with no peers");
        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("local", snapshot.GetProperty("origin").GetString());
        StringAssert.Contains(snapshot.GetProperty("fallback_reason").GetString()!, "no peers are configured",
            "an unresolvable base with no peers records an actionable reason (criterion 3/6)");
    }

    // ---- provenance freshness (criterion 6) ---------------------------------------------------------

    [TestMethod]
    public async Task FindSymbol_RemoteFederated_StampsFreshnessAndCompleteness()
    {
        using var local = new ThinMachine();
        await using var peer = await PeerServer.StartAsync(token: QueryToken);
        using var node = local.FederatedTo(peer.Client, QueryToken);

        var json = await FindSymbolTool.FindSymbol(node, "global::Shared.BaseType");
        using var doc = JsonDocument.Parse(json);
        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");

        Assert.AreEqual("complete", snapshot.GetProperty("completeness").GetString(), "a fully served base is complete (criterion 6)");
        Assert.IsTrue(snapshot.GetProperty("freshness").GetInt64() > 0, "the served generation's freshness is stamped (criterion 6)");
        Assert.IsTrue(snapshot.GetProperty("dirty").GetBoolean(), "the overlay carries a working-tree delta so the read is dirty");
    }

    // ================================================================================================
    //  helpers
    // ================================================================================================

    /// <summary>The immutable identity hash of the committed base a remote-base overlay layers on.</summary>
    private static string BaseHash() => new SnapshotIdentity
    {
        RepositoryRemoteUrl = RepoUrl,
        CommitSha = Commit,
        TreeSha = Tree,
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = null,
        ToolchainFingerprint = ToolchainFingerprint.Current,
        WorkingTreeDelta = null,
        IsOverlay = false,
        // A real service peer publishes committed bases under the producing node's default capability;
        // the read planner addresses them with this SAME (same-platform) fingerprint (issue #108).
        CapabilityFingerprint = WorkerCapability.LocalDefault.Fingerprint
    }.Hash;

    /// <summary>Strips the always-varying <c>queried_at</c> so two responses can be compared byte-for-byte.</summary>
    private static string Normalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteExcept(doc.RootElement, writer, "queried_at");
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteExcept(JsonElement element, Utf8JsonWriter writer, string skipProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == skipProperty) continue;
                    writer.WritePropertyName(prop.Name);
                    WriteExcept(prop.Value, writer, skipProperty);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteExcept(item, writer, skipProperty);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// A thin machine's local catalog: ONLY a baseless overlay (is_overlay=1, base_snapshot_id NULL)
    /// selected on the default branch, holding the touched project's re-extracted symbol. Its committed
    /// base is absent locally and must be federated from a peer.
    /// </summary>
    private sealed class ThinMachine : IDisposable
    {
        public string Path { get; }
        private readonly IndexDatabase _db;

        public ThinMachine()
        {
            Path = ServiceTestFixtures.NewDbPath();
            _db = new IndexDatabase(Path);
            _db.RunMigrations();
            SeedBaselessOverlay(_db.GetConnection());
        }

        public DatabaseProvider FederatedTo(HttpClient peerClient, string? token)
        {
            var config = new SextantConfiguration
            {
                Peers = ["http://peer.local"],
                PeerQueryToken = token,
                RemoteFetchTimeoutSeconds = 5
            };
            var provider = new DatabaseProvider(Path);
            provider.AttachRemoteFederation(RemoteBaseSnapshotFederation.Create(config, peerClient));
            return provider;
        }

        private static void SeedBaselessOverlay(SqliteConnection conn)
        {
            var store = new SnapshotStore(conn);
            var repoId = store.EnsureRepository(RepoUrl, now: 1);
            var commitId = store.EnsureCommit(repoId, Commit, Tree, now: 1);
            var logicalTouched = store.EnsureLogicalProject(repoId, "logical_touched", "src/Touched/Touched.csproj", "net10.0", now: 1);

            var overlayIdentity = new SnapshotIdentity
            {
                RepositoryRemoteUrl = RepoUrl,
                CommitSha = Commit,
                TreeSha = Tree,
                SchemaVersion = IndexDatabase.LatestSchemaVersion,
                AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
                ConfigHash = null,
                ToolchainFingerprint = ToolchainFingerprint.Current,
                WorkingTreeDelta = "delta_edit",
                IsOverlay = true
            };
            var (overlayId, _, _) = store.BeginPending(
                overlayIdentity, repoId, commitId, runId: null, now: 2, baseSnapshotId: null);

            var touchedProject = new ProjectStore(conn).UpsertSnapshotProject(
                new ProjectIdentity
                {
                    CanonicalId = "logical_touched",
                    GitRemoteUrl = RepoUrl,
                    RepoRelativePath = "src/Touched/Touched.csproj",
                    TargetFramework = "net10.0"
                }, overlayId, logicalTouched, 2);
            store.MapProject(overlayId, touchedProject);
            InsertSymbol(conn, touchedProject, "K:Touched.LocalType", "global::Touched.LocalType", "LocalType");
            // A second overlay symbol whose display name is a full FTS token ("Base") that also substring-
            // matches the base's "BaseType" — so ONE fuzzy query proves the local⊕remote union.
            InsertSymbol(conn, touchedProject, "K:Touched.Base", "global::Touched.Base", "Base");
            store.MarkComplete(overlayId, publishedAt: 2);

            var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 2);
            store.SetBranchPointer(branchId, overlayId, now: 2);
        }

        public void Dispose() => SqliteTestDatabase.Delete(Path, _db);
    }

    /// <summary>A plain, non-overlay full local index (the pre-#108 pure-local shape).</summary>
    private sealed class PlainLocalIndex : IDisposable
    {
        public string Path { get; }
        private readonly IndexDatabase _db;

        public PlainLocalIndex()
        {
            Path = ServiceTestFixtures.NewDbPath();
            _db = new IndexDatabase(Path);
            _db.RunMigrations();
            var conn = _db.GetConnection();
            var request = ServiceTestFixtures.Request(repo: "https://github.com/org/plain", commit: "commit_plain");
            var snapId = ServiceTestFixtures.PublishComplete(_db, request, symbolCount: 0);

            // Point the default branch at the published (non-overlay) snapshot so it is genuinely selected.
            var store = new SnapshotStore(conn);
            var repoId = store.EnsureRepository(request.RepositoryRemoteUrl, now: 1);
            var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
            store.SetBranchPointer(branchId, snapId, now: 1);

            // One deterministically-named symbol so exact + fuzzy both have a stable hit under the selected scope.
            var projectId = ScalarLong(conn, "SELECT id FROM projects WHERE snapshot_id = @s ORDER BY id LIMIT 1;", snapId);
            InsertSymbol(conn, projectId, "K:App.Thing", "global::App.Thing", "Thing");
        }

        public void Dispose() => SqliteTestDatabase.Delete(Path, _db);
    }

    /// <summary>
    /// A peer catalog publishing the committed base under <see cref="BaseHash"/>: a SHARED (untouched)
    /// project holding BaseType, and the TOUCHED project holding a stale row that must be shadowed.
    /// </summary>
    private sealed class PeerCatalog : IDisposable
    {
        public string Path { get; }
        public IndexDatabase Db { get; }

        public PeerCatalog()
        {
            Path = ServiceTestFixtures.NewDbPath();
            Db = new IndexDatabase(Path);
            Db.RunMigrations();
            PublishBase(Db.GetConnection());
        }

        private static void PublishBase(SqliteConnection conn)
        {
            var store = new SnapshotStore(conn);
            var repoId = store.EnsureRepository(RepoUrl, now: 1);
            var commitId = store.EnsureCommit(repoId, Commit, Tree, now: 1);
            var runStore = new IndexRunStore(conn);
            var runId = runStore.BeginRun("full", 1,
                IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
            runStore.MarkComplete(runId, 1, 1);

            var logicalShared = store.EnsureLogicalProject(repoId, "logical_shared", "src/Shared/Shared.csproj", "net10.0", now: 1);
            var logicalTouched = store.EnsureLogicalProject(repoId, "logical_touched", "src/Touched/Touched.csproj", "net10.0", now: 1);

            var baseIdentity = new SnapshotIdentity
            {
                RepositoryRemoteUrl = RepoUrl,
                CommitSha = Commit,
                TreeSha = Tree,
                SchemaVersion = IndexDatabase.LatestSchemaVersion,
                AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
                ConfigHash = null,
                ToolchainFingerprint = ToolchainFingerprint.Current,
                WorkingTreeDelta = null,
                IsOverlay = false,
                // Publish the base as a real service node does — under its default worker capability — so
                // this peer is addressable only by the capability-folded hash the planner recomputes.
                CapabilityFingerprint = WorkerCapability.LocalDefault.Fingerprint
            };
            var (baseId, _, _) = store.BeginPending(baseIdentity, repoId, commitId, runId, now: 1);

            var sharedProject = new ProjectStore(conn).UpsertSnapshotProject(
                Ident("logical_shared", "src/Shared/Shared.csproj"), baseId, logicalShared, 1);
            var touchedProject = new ProjectStore(conn).UpsertSnapshotProject(
                Ident("logical_touched", "src/Touched/Touched.csproj"), baseId, logicalTouched, 1);
            store.MapProject(baseId, sharedProject);
            store.MapProject(baseId, touchedProject);

            InsertSymbol(conn, sharedProject, "K:Shared.BaseType", "global::Shared.BaseType", "BaseType");
            // A STALE row in the touched project — the overlay re-extracted this project, so it must be shadowed.
            InsertSymbol(conn, touchedProject, "K:Touched.StaleType", "global::Touched.StaleType", "StaleType");
            store.MarkComplete(baseId, publishedAt: 1);
        }

        private static ProjectIdentity Ident(string canonical, string path) => new()
        {
            CanonicalId = canonical,
            GitRemoteUrl = RepoUrl,
            RepoRelativePath = path,
            TargetFramework = "net10.0"
        };

        public void Dispose() => SqliteTestDatabase.Delete(Path, Db);
    }

    /// <summary>A real in-process peer service over TestServer, serving <see cref="PeerCatalog"/>.</summary>
    private sealed class PeerServer : IAsyncDisposable
    {
        public HttpClient Client { get; private init; } = null!;
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private PeerCatalog Catalog { get; init; } = null!;

        public static async Task<PeerServer> StartAsync(string? token)
        {
            var catalog = new PeerCatalog();
            var options = ServiceTestFixtures.NewOptions(catalog.Path, queryToken: token);
            var service = SnapshotService.Start(options, worker: null, catalog.Db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new PeerServer { Client = app.GetTestClient(), App = app, Service = service, Catalog = catalog };
        }

        /// <summary>Every FQN the peer publishes for the base (differential proof the seed exists).</summary>
        public async Task<List<string>> FetchAll(string identityHash)
        {
            var all = new List<string>();
            string? cursor = null;
            var src = new LocalBaseSnapshotSource(Catalog.Db.GetConnection());
            do
            {
                var page = await src.FetchSymbolsAsync(
                    new SnapshotPageRequest { IdentityHash = identityHash, Cursor = cursor, Limit = 100 }, default);
                all.AddRange(page.Symbols.Select(s => s.FullyQualifiedName));
                cursor = page.NextCursor;
            } while (cursor is not null);
            return all;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Service.Dispose();
            Catalog.Dispose();
        }
    }

    /// <summary>An in-memory peer handler serving genuine pages from a catalog, flippable offline.</summary>
    private sealed class ControllablePeerHandler(IndexDatabase db) : HttpMessageHandler
    {
        public bool Online { get; set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Online)
                throw new HttpRequestException("peer offline");

            var uri = request.RequestUri!;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var hash = Uri.UnescapeDataString(segments[^2]);
            var query = ParseQuery(uri.Query);

            var page = await new LocalBaseSnapshotSource(db.GetConnection()).FetchSymbolsAsync(new SnapshotPageRequest
            {
                IdentityHash = hash,
                Cursor = query.GetValueOrDefault("cursor"),
                Limit = int.TryParse(query.GetValueOrDefault("limit"), out var l) ? l : 500
            }, ct).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(page, options: SnapshotWireJson.Options)
            };
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) result[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return result;
        }
    }

    private static void InsertSymbol(SqliteConnection conn, long projectId, string symbolKey, string fqn, string displayName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 line_start, line_end, last_indexed_at)
            VALUES (@p, @k, @fqn, @dn, 0, 0, 1, 1, 1);
            """;
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@k", symbolKey);
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@dn", displayName);
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection conn, string sql, long snapshotId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@s", snapshotId);
        return (long)cmd.ExecuteScalar()!;
    }
}
