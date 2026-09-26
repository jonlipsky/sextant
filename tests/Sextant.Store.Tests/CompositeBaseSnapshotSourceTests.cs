using System.Net;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #60 — the multi-peer composition rules that keep federated paging deterministic and safe. The
/// paging cursor is a peer-LOCAL <c>symbols.id</c>, and a published snapshot is immutable and wholly
/// present-or-absent on any given node, so <see cref="CompositeBaseSnapshotSource"/> must:
/// <list type="bullet">
///   <item>skip a peer that DEFINITIVELY lacks the snapshot (a 404 → <see cref="HttpRequestException"/>
///   with <see cref="HttpStatusCode.NotFound"/>) on ANY page and continue to the next peer;</item>
///   <item>skip a merely UNREACHABLE peer (<see cref="RemoteSnapshotUnavailableException"/>) only on the
///   FIRST page, and on a RESUMED cursor surface the failure rather than fail it over into a different
///   peer's id-space;</item>
///   <item>propagate a real protocol error (5xx) rather than silently masking a broken peer.</item>
/// </list>
/// </summary>
[TestClass]
public class CompositeBaseSnapshotSourceTests
{
    [TestMethod]
    public async Task FirstPage_SkipsPeerThat404s_AndServesNextPeer()
    {
        var p0 = new FakeSource(_ => throw new HttpRequestException("not found", null, HttpStatusCode.NotFound));
        var p1 = new FakeSource(r => Page(r.IdentityHash, 3, next: null));
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var page = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h" }, CancellationToken.None);

        Assert.AreEqual(3, page.Symbols.Count, "a 404 from peer 0 is a definitive miss — peer 1 must be tried (#60)");
        Assert.AreEqual(1, p1.Calls);
    }

    [TestMethod]
    public async Task FirstPage_SkipsUnreachablePeer_AndServesNextPeer()
    {
        var p0 = new FakeSource(_ => throw new RemoteSnapshotUnavailableException("peer down"));
        var p1 = new FakeSource(r => Page(r.IdentityHash, 2, next: null));
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var page = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h" }, CancellationToken.None);

        Assert.AreEqual(2, page.Symbols.Count, "on the first page an unreachable peer is skipped to a healthy one (#60)");
    }

    [TestMethod]
    public async Task ResumedCursor_DoesNotFailUnreachablePeerOverToAnother()
    {
        var p0 = new FakeSource(_ => throw new RemoteSnapshotUnavailableException("peer down"));
        // Peer 1 would wrongly answer in ITS OWN id-space if consulted for peer 0's cursor.
        var p1 = new FakeSource(r => Page(r.IdentityHash, 9, next: null));
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        await Assert.ThrowsAsync<RemoteSnapshotUnavailableException>(
            () => composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h", Cursor = "5" }, CancellationToken.None),
            "a resumed cursor must never be reinterpreted in a different peer's id-space (#60)");
        Assert.AreEqual(0, p1.Calls, "the second peer is never consulted for a cursor it did not produce");
    }

    [TestMethod]
    public async Task ResumedCursor_StillSkipsDefinitive404()
    {
        // A peer that never had the snapshot 404s on every page — it never produced the resumed cursor, so
        // skipping it is always safe and the peer that DOES own the cursor answers consistently.
        var p0 = new FakeSource(_ => throw new HttpRequestException("not found", null, HttpStatusCode.NotFound));
        var p1 = new FakeSource(r => Page(r.IdentityHash, 4, next: null));
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var page = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h", Cursor = "5" }, CancellationToken.None);

        Assert.AreEqual(4, page.Symbols.Count, "a definitively-absent peer (404) is skipped on a resumed page too (#60)");
    }

    [TestMethod]
    public async Task ServerError_PropagatesFromComposite_NotSkipped()
    {
        var p0 = new FakeSource(_ => throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError));
        var p1 = new FakeSource(r => Page(r.IdentityHash, 2, next: null));
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h" }, CancellationToken.None),
            "a 5xx is a real protocol error that must surface, not be masked by failing over (#60)");
        Assert.AreEqual(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.AreEqual(0, p1.Calls);
    }

    [TestMethod]
    public async Task AllPeersLackSnapshot_YieldsEmptyPage()
    {
        var p0 = new FakeSource(_ => throw new HttpRequestException("not found", null, HttpStatusCode.NotFound));
        var p1 = new FakeSource(r => new SnapshotSymbolPage
        {
            IdentityHash = r.IdentityHash, Symbols = [], NextCursor = null, Complete = false
        });
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var page = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h" }, CancellationToken.None);

        Assert.AreEqual(0, page.Symbols.Count, "no peer publishes the snapshot ⇒ a plain empty page, not a fault (#60)");
        Assert.IsFalse(page.IsPublished);
    }

    [TestMethod]
    public async Task EmptyPublishedPartialPage_IsServed_NotSkippedToTheNextPeer()
    {
        // Issue #119: a partial snapshot's final page (or a zero-symbol snapshot) is empty with
        // complete=false — but the peer still PUBLISHES it and owns the cursor, so it must be served, never
        // failed over into another peer's id-space.
        var p0 = new FakeSource(r => new SnapshotSymbolPage
        {
            IdentityHash = r.IdentityHash, Symbols = [], NextCursor = null, Complete = false, Published = true
        });
        var p1 = new FakeSource(r => Page(r.IdentityHash, 7, next: null));
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var first = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h" }, CancellationToken.None);
        var resumed = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h", Cursor = "500" }, CancellationToken.None);

        Assert.AreEqual(0, first.Symbols.Count);
        Assert.IsTrue(first.IsPublished);
        Assert.AreEqual(0, resumed.Symbols.Count, "an exact page-boundary end stays with the owning peer");
        Assert.AreEqual(0, p1.Calls, "the owning peer answered — the next peer is never consulted");
    }

    [TestMethod]
    public async Task LegacyPeerWithoutPublishedField_CompleteEmptyPage_StillOwnsTheSnapshot()
    {
        var p0 = new FakeSource(r => new SnapshotSymbolPage
        {
            IdentityHash = r.IdentityHash, Symbols = [], NextCursor = null, Complete = true
        });
        var p1 = new FakeSource(r => Page(r.IdentityHash, 2, next: null));
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var page = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h", Cursor = "9" }, CancellationToken.None);

        Assert.AreEqual(0, page.Symbols.Count, "a pre-#119 peer's complete=true empty page means 'published, last page'");
        Assert.AreEqual(0, p1.Calls);
    }

    [TestMethod]
    public async Task LegacyPeerEmptyCompleteFirstPage_DoesNotMaskALaterPublishingPeer()
    {
        // A pre-#119 peer answered complete=true (no `published`) for a pending/superseded identity with no
        // rows. On the FIRST page that is not proof of publication, so routing continues to the peer that
        // actually publishes the snapshot (#119 review).
        var p0 = new FakeSource(r => new SnapshotSymbolPage
        {
            IdentityHash = r.IdentityHash, Symbols = [], NextCursor = null, Complete = true
        });
        var p1 = new FakeSource(r => Page(r.IdentityHash, 2, next: null) with { Published = true });
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var page = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h" }, CancellationToken.None);

        Assert.AreEqual(2, page.Symbols.Count, "the publishing peer answers, not the legacy peer's empty page");
        Assert.AreEqual(1, p1.Calls);
    }

    [TestMethod]
    public async Task UnpublishedFirstPeer_FallsThroughToThePublishingPeer()
    {
        // A local catalog that only has the identity pending answers unpublished; a peer that publishes it wins.
        var p0 = new FakeSource(r => new SnapshotSymbolPage
        {
            IdentityHash = r.IdentityHash, Symbols = [], NextCursor = null, Complete = false, Published = false
        });
        var p1 = new FakeSource(r => Page(r.IdentityHash, 3, next: null) with { Complete = false, Published = true });
        var composite = new CompositeBaseSnapshotSource([p0, p1]);

        var page = await composite.FetchSymbolsAsync(new SnapshotPageRequest { IdentityHash = "h" }, CancellationToken.None);

        Assert.AreEqual(3, page.Symbols.Count);
        Assert.IsFalse(page.Complete, "the serving peer's partial coverage verdict is preserved");
        Assert.AreEqual(1, p1.Calls);
    }

    private static SnapshotSymbolPage Page(string hash, int count, string? next) => new()
    {
        IdentityHash = hash,
        NextCursor = next,
        Symbols = Enumerable.Range(0, count).Select(i => new SnapshotSymbolRow
        {
            Cursor = i,
            SymbolKey = $"k{i}",
            FullyQualifiedName = $"N.T{i}",
            DisplayName = $"T{i}",
            Kind = 1,
            Accessibility = 1
        }).ToList()
    };

    /// <summary>An in-memory base-snapshot source whose per-request behavior is driven by a delegate that
    /// may return a page or throw. Counts invocations so a test can prove a peer was (not) consulted.</summary>
    private sealed class FakeSource(Func<SnapshotPageRequest, SnapshotSymbolPage> handler) : IBaseSnapshotSource
    {
        public int Calls { get; private set; }

        public Task<SnapshotSymbolPage> FetchSymbolsAsync(SnapshotPageRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(handler(request));
        }
    }
}
