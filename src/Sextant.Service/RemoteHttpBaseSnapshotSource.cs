using System.Net;
using System.Net.Http.Json;

namespace Sextant.Service;

/// <summary>
/// A base-snapshot source backed by a REMOTE peer service over authenticated HTTP (issue #51). It provides
/// the four remote-federation behaviors Phase 11 deferred to the service:
/// <list type="bullet">
///   <item><b>Paging</b> — forwards the stable cursor so a large base snapshot streams page by page.</item>
///   <item><b>Caching by snapshot id</b> — every fetched page is cached by (identity hash, cursor); an
///   immutable published snapshot never changes, so the cache never goes stale.</item>
///   <item><b>Timeout</b> — each fetch is bounded by <see cref="_timeout"/> via a linked cancellation.</item>
///   <item><b>Transparent offline fallback</b> — a page already in the cache is served WITHOUT contacting
///   the peer, so once warmed a federated read keeps working when the peer is unreachable; only a
///   never-cached page against an unreachable peer surfaces a structured
///   <see cref="RemoteSnapshotUnavailableException"/>.</item>
/// </list>
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
                .ReadFromJsonAsync<SnapshotSymbolPage>(ServiceJson.Options, timeoutCts.Token)
                .ConfigureAwait(false)
                ?? new SnapshotSymbolPage { IdentityHash = request.IdentityHash, Symbols = [], NextCursor = null };

            _cache.Set(request.CacheKey, page);
            return page;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel — this was our own timeout. No cached page exists (checked above).
            throw new RemoteSnapshotUnavailableException(
                $"Remote peer '{_peerBaseUrl}' timed out after {_timeout.TotalSeconds:0.#}s and no cached page was available.");
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
