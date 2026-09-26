using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sextant.Store;

/// <summary>
/// A base-snapshot source backed by a REMOTE peer service over authenticated HTTP (issue #51). It provides
/// the four remote-federation behaviors Phase 11 deferred to the service:
/// <list type="bullet">
///   <item><b>Paging</b> — forwards the stable cursor so a large base snapshot streams page by page.</item>
///   <item><b>Caching by snapshot id</b> — every fetched page of a PUBLISHED snapshot is cached by (identity
///   hash, cursor); an immutable published snapshot never changes, so the cache never goes stale. An empty
///   page that does not prove publication ("not published yet") is never cached (issue #119).</item>
///   <item><b>Timeout</b> — each fetch is bounded by <see cref="_timeout"/> via a linked cancellation.</item>
///   <item><b>Transparent offline fallback</b> — a page already in the cache is served WITHOUT contacting
///   the peer, so once warmed a federated read keeps working when the peer is unreachable; only a
///   never-cached page against an unreachable peer surfaces a structured
///   <see cref="RemoteSnapshotUnavailableException"/>.</item>
/// </list>
/// Lives in <c>Sextant.Store</c> alongside the seam it implements so the live MCP query planner (issue
/// #60) can construct it directly; the wire format is <see cref="SnapshotWireJson.Options"/>, identical to
/// the service's response side.
/// </summary>
public sealed class RemoteHttpBaseSnapshotSource : IBaseSnapshotSource
{
    private readonly HttpClient _http;
    private readonly string _peerBaseUrl;
    private readonly string? _queryToken;
    private readonly TimeSpan _timeout;
    private readonly SnapshotPageCache _cache;

    public RemoteHttpBaseSnapshotSource(
        HttpClient http, string peerBaseUrl, string? queryToken, TimeSpan timeout, SnapshotPageCache? cache = null)
    {
        _http = http;
        _peerBaseUrl = peerBaseUrl.TrimEnd('/');
        _queryToken = queryToken;
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : timeout;
        _cache = cache ?? new SnapshotPageCache();
    }

    public async Task<SnapshotSymbolPage> FetchSymbolsAsync(SnapshotPageRequest request, CancellationToken cancellationToken)
    {
        // Cache-first: a warm page is served without touching the peer, which is exactly the transparent
        // offline fallback — a federated read over a cached base keeps working when the peer is down.
        if (_cache.TryGet(request.CacheKey, out var cached))
            return cached;

        var url = $"{_peerBaseUrl}/query/snapshots/{Uri.EscapeDataString(request.IdentityHash)}/symbols" +
                  $"?cursor={request.CursorId}&limit={request.Limit}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_queryToken))
                message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _queryToken);

            using var response = await _http.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new RemoteSnapshotUnavailableException(
                    $"Remote peer '{_peerBaseUrl}' rejected the query token ({(int)response.StatusCode}).");
            response.EnsureSuccessStatusCode();

            var page = await response.Content
                .ReadFromJsonAsync<SnapshotSymbolPage>(SnapshotWireJson.Options, timeoutCts.Token)
                .ConfigureAwait(false)
                ?? new SnapshotSymbolPage { IdentityHash = request.IdentityHash, Symbols = [], NextCursor = null };

            // A 2xx body that deserialized but carries a null symbol list ({"symbols": null}) is malformed —
            // treat it as an unavailable peer rather than letting a later `page.Symbols.Count` NRE the read.
            if (page.Symbols is null)
                throw new RemoteSnapshotUnavailableException(
                    $"Remote peer '{_peerBaseUrl}' returned a malformed snapshot page (null symbols).");

            // Cache only pages that are PROVEN immutable: a page with rows (a peer serves rows only for a
            // published snapshot) or one the peer affirmatively marks published. An empty unproven page —
            // "unknown / not yet published" (issue #119) — describes MUTABLE state: the same identity may
            // publish moments later, so caching it would pin a stale negative for the process lifetime.
            if (page.Symbols.Count > 0 || page.IsProvenPublished)
                _cache.Set(request.CacheKey, page);
            return page;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel — this was our own timeout. No cached page exists (checked above).
            throw new RemoteSnapshotUnavailableException(
                $"Remote peer '{_peerBaseUrl}' timed out after {_timeout.TotalSeconds:0.#}s and no cached page was available.");
        }
        catch (JsonException ex)
        {
            // A 2xx response whose body is not valid JSON / not a valid page (including a missing required
            // property). Normalize to a structured unavailable so the tool answers empty instead of faulting.
            throw new RemoteSnapshotUnavailableException(
                $"Remote peer '{_peerBaseUrl}' returned a malformed snapshot page.", ex);
        }
        catch (NotSupportedException ex)
        {
            // An unreadable/unsupported content type on a 2xx response — same structured degradation.
            throw new RemoteSnapshotUnavailableException(
                $"Remote peer '{_peerBaseUrl}' returned an unreadable snapshot page.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            // Only a CONNECTION-level failure (no HTTP status — DNS/connect/reset) means the peer is
            // unreachable and we fall back to the cached base. A protocol error (4xx/5xx from
            // EnsureSuccessStatusCode, e.g. a 404 for an unknown snapshot or a 500) carries a StatusCode
            // and is a REAL error that must propagate, not be masked as a silent offline fallback.
            throw new RemoteSnapshotUnavailableException(
                $"Remote peer '{_peerBaseUrl}' was unreachable and no cached page was available.", ex);
        }
    }
}
