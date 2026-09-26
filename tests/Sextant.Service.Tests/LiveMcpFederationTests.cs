using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #60 — the LIVE half of remote base-snapshot federation: a running local MCP query planner
/// transparently reaches a configured remote peer for a base snapshot the LOCAL catalog lacks, honoring
/// the PEERS / remote-fetch-timeout config, with a transparent offline fallback to the cached base. This
/// is the end-to-end acceptance the issue names: a local MCP query resolves a base snapshot served ONLY
/// by an in-process remote peer (real <see cref="TestServer"/> exposing
/// <c>/query/snapshots/{hash}/symbols</c>), proving the wiring from config → DI → planner tool → remote
/// source. Plus the offline-fallback and peers-unset regression cases.
/// </summary>
[TestClass]
public class LiveMcpFederationTests
{
    private const string QueryToken = "query-secret";

    // ---- acceptance: end-to-end federation over a real HTTP peer ------------------------------------

    [TestMethod]
    public async Task LiveMcpQuery_ResolvesBaseSnapshot_ServedOnlyByRemotePeer()
    {
        // A base snapshot for repo B lives ONLY on the peer; the local catalog holds only repo A.
        await using var peer = await RemotePeer.StartAsync(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appB", commit: "commit-bbbb"),
            symbolCount: 4);
        using var node = LocalNode.Federated(peerUrl: "http://peer.local", peerClient: peer.Client, token: QueryToken);

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, peer.IdentityHash);
        using var doc = JsonDocument.Parse(json);

        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(4, results.GetArrayLength(),
            "the local MCP query resolved repo B's base snapshot transparently from the remote peer (#60)");

        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("remote", snapshot.GetProperty("origin").GetString(),
            "provenance stamps the remote origin so the caller can see the rows came from a peer");
        Assert.AreEqual(peer.IdentityHash, snapshot.GetProperty("base_identity_hash").GetString());
    }

    [TestMethod]
    public async Task LiveMcpQuery_PagesRemoteBase_AcrossCursors()
    {
        await using var peer = await RemotePeer.StartAsync(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appB", commit: "commit-bbbb"),
            symbolCount: 5);
        using var node = LocalNode.Federated(peerUrl: "http://peer.local", peerClient: peer.Client, token: QueryToken);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, peer.IdentityHash, cursor, limit: 2);
            using var doc = JsonDocument.Parse(json);
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                seen.Add(r.GetProperty("symbol_key").GetString()!);
            cursor = doc.RootElement.GetProperty("meta").TryGetProperty("next_cursor", out var nc)
                ? nc.GetString()
                : null;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.AreEqual(5, seen.Count, "cursor paging over the remote base streams every symbol deterministically (#60)");
        Assert.AreEqual(5, seen.Distinct().Count(), "no page overlap or duplication across the federated paging");
    }

    [TestMethod]
    public async Task WrongPeerToken_FailsClosed_NoSymbolsLeak()
    {
        await using var peer = await RemotePeer.StartAsync(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appB", commit: "commit-bbbb"),
            symbolCount: 4);
        // The local node presents the WRONG query token; the peer authorizes against its own policy.
        using var node = LocalNode.Federated(peerUrl: "http://peer.local", peerClient: peer.Client, token: "wrong-token");

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, peer.IdentityHash);
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength(),
            "a remote fetch never widens authorization — a rejected token yields no symbols (#60 auth binding)");
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString()!, "unavailable");
    }

    // ---- offline fallback: a warmed cache answers when the peer goes unreachable --------------------

    [TestMethod]
    public async Task OfflineFallback_ServesCachedBase_ThroughLiveTool_WhenPeerGoesOffline()
    {
        using var peerDb = new SeededDb(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appB", commit: "commit-bbbb"), symbolCount: 3);
        var handler = new ControllablePeerHandler(peerDb.Db);
        using var node = LocalNode.Federated(
            peerUrl: "http://peer.local", peerClient: new HttpClient(handler), token: QueryToken);

        // Warm the cache through the live tool, then take the peer offline.
        var warm = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, peerDb.IdentityHash, limit: 3);
        using (var warmDoc = JsonDocument.Parse(warm))
            Assert.AreEqual(3, warmDoc.RootElement.GetProperty("results").GetArrayLength());

        handler.Online = false;

        // The same page is still answered transparently from the cached base while the peer is down.
        var offline = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, peerDb.IdentityHash, limit: 3);
        using (var offlineDoc = JsonDocument.Parse(offline))
        {
            Assert.AreEqual(3, offlineDoc.RootElement.GetProperty("results").GetArrayLength(),
                "the cached base keeps answering the live query while the peer is unreachable (#60 offline fallback)");
            Assert.AreEqual("remote",
                offlineDoc.RootElement.GetProperty("meta").GetProperty("snapshot").GetProperty("origin").GetString());
        }

        // An un-warmed page against the down peer surfaces a structured unavailability, not a silent empty.
        var uncached = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, peerDb.IdentityHash, cursor: "1", limit: 3);
        using (var uncachedDoc = JsonDocument.Parse(uncached))
        {
            Assert.AreEqual(0, uncachedDoc.RootElement.GetProperty("results").GetArrayLength());
            StringAssert.Contains(uncachedDoc.RootElement.GetProperty("message").GetString()!, "unavailable");
        }
    }

    // ---- remote failure shapes surface as structured empties, never a tool fault ---------------------

    [TestMethod]
    public async Task RemotePeerHttpError_ThroughLiveTool_StructuredEmpty_NeverFaults()
    {
        using var peerDb = new SeededDb(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appB", commit: "commit-bbbb"), symbolCount: 3);
        // A peer that answers with a protocol error (e.g. a transient 5xx) — the remote source propagates it
        // as an HttpRequestException carrying a status; the live tool must catch it and return a structured
        // empty, not fault the MCP call or leak a raw transport exception (#60 robustness).
        var handler = new ControllablePeerHandler(peerDb.Db) { ForceStatus = HttpStatusCode.InternalServerError };
        using var node = LocalNode.Federated(
            peerUrl: "http://peer.local", peerClient: new HttpClient(handler), token: QueryToken);

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, peerDb.IdentityHash);
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength(),
            "a peer HTTP error yields a structured empty result, never a faulted tool call (#60)");
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString()!, "unavailable");
    }

    [TestMethod]
    public async Task ReachablePeerLacksSnapshot_ThroughLiveTool_SaysNotPublished()
    {
        // A reachable peer that simply does not publish the requested snapshot returns an empty first page.
        await using var peer = await RemotePeer.StartAsync(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appB", commit: "commit-bbbb"),
            symbolCount: 4);
        using var node = LocalNode.Federated(peerUrl: "http://peer.local", peerClient: peer.Client, token: QueryToken);

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, "unknown-snapshot-hash");
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength(),
            "a reachable peer that lacks the snapshot is not a silent zero-symbol answer (#60)");
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString()!, "no configured peer publishes it");
    }

    [TestMethod]
    public async Task LegacyPeer_EmptyCompletePageWithoutPublished_SaysNotPublished()
    {
        // A pre-#119 peer answered `complete: true` (no `published` field) for ANY known identity — including
        // a superseded/pending base it no longer serves — with no symbols. With a single configured peer the
        // raw remote source is used directly; that page must NOT be served as an empty "complete" base.
        const string legacyBody = """{"identity_hash":"legacy-hash","symbols":[],"complete":true}""";
        using var node = LocalNode.Federated(
            peerUrl: "http://peer.local", peerClient: new HttpClient(new RawBodyPeerHandler(legacyBody)), token: QueryToken);

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, "legacy-hash");
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength());
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString()!, "no configured peer publishes it",
            "a legacy peer's `complete` is not proof of publication (#119 review)");
    }

    // ---- regression: peers unset keeps the local path byte-identical --------------------------------

    [TestMethod]
    public async Task PeersUnset_LocalHash_ServedLocally_NoRemoteSource()
    {
        // No federation attached at all — the pure-local path.
        using var node = LocalNode.PureLocal(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appA", commit: "commit-aaaa"), symbolCount: 3);
        Assert.IsNull(node.Provider.RemoteBaseSource, "no peers configured ⇒ no remote source is constructed");

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, node.LocalIdentityHash);
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(3, doc.RootElement.GetProperty("results").GetArrayLength(),
            "a locally-complete snapshot is served from the local catalog with no peers (#60 regression)");
        Assert.AreEqual("local",
            doc.RootElement.GetProperty("meta").GetProperty("snapshot").GetProperty("origin").GetString());
    }

    [TestMethod]
    public async Task PeersUnset_NonLocalHash_SaysNoPeers_NeverTouchesNetwork()
    {
        using var node = LocalNode.PureLocal(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appA", commit: "commit-aaaa"), symbolCount: 3);

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, "hash-that-is-not-local");
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength());
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString()!, "no remote peers are configured");
    }

    // ---- issue #119: a published-but-partial base snapshot is served AND labeled partial -------------

    private static SnapshotCoverage PartialCoverage() => new()
    {
        Verdict = SnapshotCoverageVerdict.Partial,
        Reasons = ["2 of 3 discovered solution(s) were not selected for indexing (b.slnx, c.slnx)."],
        SelectionSource = "default_root",
        SolutionsDiscovered = 3,
        SolutionsSelected = 1,
        SolutionsNotSelected = 2
    };

    [TestMethod]
    public async Task PartialCoverageBase_ServedLocally_IsLabeledPartialWithCoverage()
    {
        using var node = LocalNode.PureLocal(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appA", commit: "commit-aaaa"),
            symbolCount: 3, PartialCoverage());

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, node.LocalIdentityHash);
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(3, doc.RootElement.GetProperty("results").GetArrayLength(),
            "a published partial snapshot's rows are still served (#119)");
        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("local", snapshot.GetProperty("origin").GetString());
        Assert.AreEqual("partial", snapshot.GetProperty("completeness").GetString(),
            "a partial-coverage base is never labeled complete (#119)");
        var coverage = snapshot.GetProperty("coverage");
        Assert.AreEqual("partial", coverage.GetProperty("verdict").GetString());
        Assert.AreEqual(2, coverage.GetProperty("solutions_not_selected").GetInt32());
    }

    [TestMethod]
    public async Task PartialCoverageBase_ServedByRemotePeer_CarriesCoverageAcrossTheWire()
    {
        await using var peer = await RemotePeer.StartAsync(
            ServiceTestFixtures.Request(repo: "https://github.com/org/appB", commit: "commit-bbbb"),
            symbolCount: 4, PartialCoverage());
        using var node = LocalNode.Federated(peerUrl: "http://peer.local", peerClient: peer.Client, token: QueryToken);

        var json = await GetBaseSnapshotSymbolsTool.GetBaseSnapshotSymbols(node.Provider, peer.IdentityHash);
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual(4, doc.RootElement.GetProperty("results").GetArrayLength(),
            "the peer's published partial snapshot is served, not skipped as unpublished (#119)");
        var snapshot = doc.RootElement.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("remote", snapshot.GetProperty("origin").GetString());
        Assert.AreEqual("partial", snapshot.GetProperty("completeness").GetString(),
            "the peer's coverage verdict survives the HTTP page wire (#119)");
        Assert.AreEqual("partial", snapshot.GetProperty("coverage").GetProperty("verdict").GetString());
    }

    [TestMethod]
    public void Federation_Create_WithNoPeers_ProducesNoSource()
    {
        using var federation = RemoteBaseSnapshotFederation.Create(new SextantConfiguration());
        Assert.IsFalse(federation.IsEnabled);
        Assert.IsNull(federation.Source);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// A ready local MCP node over an ephemeral catalog, optionally federated to a peer. Holds only repo A
    /// so a request for another repo's base snapshot must federate. Disposal tears down the provider (and
    /// its owned HttpClient/federation) and deletes the seeded catalog.
    /// </summary>
    private sealed class LocalNode : IDisposable
    {
        private readonly SeededDb _localDb;
        public DatabaseProvider Provider { get; }
        public string LocalIdentityHash => _localDb.IdentityHash;

        private LocalNode(SeededDb localDb, DatabaseProvider provider)
        {
            _localDb = localDb;
            Provider = provider;
        }

        public static LocalNode Federated(string peerUrl, HttpClient peerClient, string? token)
        {
            var localDb = new SeededDb(
                ServiceTestFixtures.Request(repo: "https://github.com/org/appA", commit: "commit-aaaa"), symbolCount: 2);
            var config = new SextantConfiguration
            {
                Peers = [peerUrl],
                PeerQueryToken = token,
                RemoteFetchTimeoutSeconds = 5
            };
            var provider = new DatabaseProvider(localDb.Path);
            provider.AttachRemoteFederation(RemoteBaseSnapshotFederation.Create(config, peerClient));
            return new LocalNode(localDb, provider);
        }

        public static LocalNode PureLocal(
            EnsureSnapshotRequest request, int symbolCount, SnapshotCoverage? coverage = null)
        {
            var localDb = new SeededDb(request, symbolCount, coverage);
            return new LocalNode(localDb, new DatabaseProvider(localDb.Path));
        }

        public void Dispose()
        {
            Provider.Dispose();
            _localDb.Dispose();
        }
    }

    /// <summary>An ephemeral file-backed catalog seeded with one complete snapshot.</summary>
    private sealed class SeededDb : IDisposable
    {
        public string Path { get; }
        public IndexDatabase Db { get; }
        public string IdentityHash { get; }

        public SeededDb(EnsureSnapshotRequest request, int symbolCount, SnapshotCoverage? coverage = null)
        {
            Path = ServiceTestFixtures.NewDbPath();
            Db = new IndexDatabase(Path);
            Db.RunMigrations();
            var snapId = ServiceTestFixtures.PublishComplete(Db, request, symbolCount);
            if (coverage is not null)
                new SnapshotCoverageStore(Db.GetConnection()).Record(snapId, coverage, 1);
            IdentityHash = request.ToIdentity().Hash;
        }

        public void Dispose() => SqliteTestDatabase.Delete(Path, Db);
    }

    /// <summary>A real in-process peer service over TestServer, seeded with one complete snapshot.</summary>
    private sealed class RemotePeer : IAsyncDisposable
    {
        public HttpClient Client { get; private init; } = null!;
        public string IdentityHash { get; private init; } = "";
        private WebApplication App { get; init; } = null!;
        private SnapshotService Service { get; init; } = null!;
        private IndexDatabase Db { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<RemotePeer> StartAsync(
            EnsureSnapshotRequest request, int symbolCount, SnapshotCoverage? coverage = null)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();
            var snapId = ServiceTestFixtures.PublishComplete(db, request, symbolCount);
            if (coverage is not null)
                new SnapshotCoverageStore(db.GetConnection()).Record(snapId, coverage, 1);

            var options = ServiceTestFixtures.NewOptions(dbPath, queryToken: QueryToken);
            var service = SnapshotService.Start(options, worker: null, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new RemotePeer
            {
                Client = app.GetTestClient(),
                IdentityHash = request.ToIdentity().Hash,
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

    /// <summary>
    /// An in-memory peer handler serving genuine pages from a local catalog, flippable offline to drive the
    /// offline-fallback path through the live tool. Serializes with the wire-identical service options.
    /// </summary>
    private sealed class ControllablePeerHandler(IndexDatabase db) : HttpMessageHandler
    {
        public bool Online { get; set; } = true;

        /// <summary>When set, the peer returns this HTTP status instead of a page (drives the 404/5xx arm).</summary>
        public HttpStatusCode? ForceStatus { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Online)
                throw new HttpRequestException("peer offline");

            if (ForceStatus is { } status)
                return new HttpResponseMessage(status);

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

    /// <summary>A peer that answers every page request with a fixed JSON body (e.g. a pre-#119 page shape).</summary>
    private sealed class RawBodyPeerHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
