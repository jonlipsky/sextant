using System.Net;
using System.Net.Http.Json;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #51 — the REMOTE half of Phase 11's snapshot federation, provided by this service: result paging
/// over a peer, caching-by-snapshot-id (an immutable published snapshot never goes stale), a bounded
/// per-fetch timeout, and a transparent offline fallback to the cached base. A never-cached page against
/// an unreachable/unauthorized peer surfaces a structured <see cref="RemoteSnapshotUnavailableException"/>.
/// Exercised against an in-memory stub peer backed by a real <see cref="LocalBaseSnapshotSource"/>, so
/// paging is genuine while staying hermetic (no real network).
/// </summary>
[TestClass]
public class RemoteFederationTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private string _hash = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var request = ServiceTestFixtures.Request();
        ServiceTestFixtures.PublishComplete(_db, request, symbolCount: 5);
        _hash = request.ToIdentity().Hash;
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public async Task RemotePaging_StreamsAllSymbols_AcrossPages()
    {
        var handler = new PeerStubHandler(_db);
        var source = NewSource(handler, new SnapshotPageCache());

        var collected = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await source.FetchSymbolsAsync(
                new SnapshotPageRequest { IdentityHash = _hash, Cursor = cursor, Limit = 2 }, CancellationToken.None);
            collected.AddRange(page.Symbols.Select(s => s.SymbolKey));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.AreEqual(5, collected.Count, "paging streams every symbol of the base snapshot (#51)");
        Assert.AreEqual(5, collected.Distinct().Count(), "no page overlap or duplication");
        Assert.IsTrue(pages >= 3, "the 5 symbols were served across multiple pages of 2");
    }

    [TestMethod]
    public async Task CachedPage_IsServedWithoutContactingPeer()
    {
        var handler = new PeerStubHandler(_db);
        var source = NewSource(handler, new SnapshotPageCache());
        var request = new SnapshotPageRequest { IdentityHash = _hash, Cursor = null, Limit = 2 };

        var first = await source.FetchSymbolsAsync(request, CancellationToken.None);
        var second = await source.FetchSymbolsAsync(request, CancellationToken.None);

        Assert.AreEqual(first.Symbols.Count, second.Symbols.Count);
        Assert.AreEqual(1, handler.Calls, "the immutable page is served from cache on the second fetch (#51)");
    }

    [TestMethod]
    public async Task OfflineFallback_ServesCachedPage_WhenPeerUnreachable()
    {
        var handler = new PeerStubHandler(_db);
        var cache = new SnapshotPageCache();
        var source = NewSource(handler, cache);
        var warmed = new SnapshotPageRequest { IdentityHash = _hash, Cursor = null, Limit = 2 };

        // Warm the cache, then take the peer offline.
        await source.FetchSymbolsAsync(warmed, CancellationToken.None);
        handler.Online = false;

        // A cached page keeps working transparently even though the peer is down.
        var offlinePage = await source.FetchSymbolsAsync(warmed, CancellationToken.None);
        Assert.IsTrue(offlinePage.Symbols.Count > 0, "the cached base is served transparently while offline (#51)");

        // A never-cached page against the down peer surfaces a structured unavailability.
        await Assert.ThrowsAsync<RemoteSnapshotUnavailableException>(
            () => source.FetchSymbolsAsync(
                new SnapshotPageRequest { IdentityHash = _hash, Cursor = "2", Limit = 2 }, CancellationToken.None),
            "an uncached page against an unreachable peer fails with a structured error (#51)");
    }

    [TestMethod]
    public async Task Timeout_SurfacesStructuredUnavailable()
    {
        var handler = new PeerStubHandler(_db) { Delay = TimeSpan.FromSeconds(30) };
        var source = NewSource(handler, new SnapshotPageCache(), timeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<RemoteSnapshotUnavailableException>(
            () => source.FetchSymbolsAsync(
                new SnapshotPageRequest { IdentityHash = _hash, Cursor = null, Limit = 2 }, CancellationToken.None),
            "a slow peer fetch is bounded by the timeout and surfaces a structured error (#51)");
    }

    [TestMethod]
    public async Task UnauthorizedPeer_SurfacesStructuredUnavailable()
    {
        var handler = new PeerStubHandler(_db) { ForceStatus = HttpStatusCode.Unauthorized };
        var source = NewSource(handler, new SnapshotPageCache());

        await Assert.ThrowsAsync<RemoteSnapshotUnavailableException>(
            () => source.FetchSymbolsAsync(
                new SnapshotPageRequest { IdentityHash = _hash, Cursor = null, Limit = 2 }, CancellationToken.None),
            "a peer that rejects the query token surfaces a structured error (#51)");
    }

    [TestMethod]
    public async Task ServerError_PropagatesAsProtocolError_NotSilentOfflineFallback()
    {
        var handler = new PeerStubHandler(_db) { ForceStatus = HttpStatusCode.InternalServerError };
        var source = NewSource(handler, new SnapshotPageCache());

        // A 5xx is a REAL protocol error (raised by EnsureSuccessStatusCode with a StatusCode), not an
        // unreachable peer. It must NOT be masked as a transparent offline fallback that silently hides a
        // broken peer; it propagates as an HttpRequestException, distinct from RemoteSnapshotUnavailable.
        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => source.FetchSymbolsAsync(
                new SnapshotPageRequest { IdentityHash = _hash, Cursor = null, Limit = 2 }, CancellationToken.None),
            "a 5xx protocol error propagates instead of being swallowed as an offline fallback (#51)");
        Assert.AreEqual(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    private static RemoteHttpBaseSnapshotSource NewSource(
        PeerStubHandler handler, SnapshotPageCache cache, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://peer.local") };
        return new RemoteHttpBaseSnapshotSource(
            http, "http://peer.local", queryToken: "peer-token", timeout ?? TimeSpan.FromSeconds(10), cache);
    }

    /// <summary>
    /// An in-memory stand-in for a remote peer's <c>/query/snapshots/{hash}/symbols</c> endpoint. It serves
    /// genuine pages from a local catalog via <see cref="LocalBaseSnapshotSource"/>, and can simulate a slow
    /// peer (<see cref="Delay"/>), an offline peer (<see cref="Online"/>=false), or an auth rejection
    /// (<see cref="ForceStatus"/>). It counts invocations so a test can prove a cached fetch never reached it.
    /// </summary>
    private sealed class PeerStubHandler(IndexDatabase db) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Online { get; set; } = true;
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;
        public HttpStatusCode? ForceStatus { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            if (!Online)
                throw new HttpRequestException("peer offline");
            if (ForceStatus is { } forced)
                return new HttpResponseMessage(forced);

            var uri = request.RequestUri!;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var hash = Uri.UnescapeDataString(segments[^2]); // .../snapshots/{hash}/symbols
            var query = ParseQuery(uri.Query);

            var page = await new LocalBaseSnapshotSource(db.GetConnection()).FetchSymbolsAsync(new SnapshotPageRequest
            {
                IdentityHash = hash,
                Cursor = query.GetValueOrDefault("cursor"),
                Limit = int.TryParse(query.GetValueOrDefault("limit"), out var l) ? l : 500
            }, cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(page, options: ServiceJson.Options)
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
}
