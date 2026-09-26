using Sextant.Core;

namespace Sextant.Store;

/// <summary>
/// Builds the remote base-snapshot source from configuration, in <c>Sextant.Store</c> so BOTH the live MCP
/// query planner (<c>Sextant.Mcp</c>) and the daemon's overlay reconciler (<c>Sextant.Daemon</c>, which
/// must NOT depend on <c>Sextant.Mcp</c>) can construct the same per-peer transport without inverting the
/// dependency graph. This is the single place that maps <see cref="SextantConfiguration"/> peers onto the
/// tested <see cref="RemoteHttpBaseSnapshotSource"/> / <see cref="CompositeBaseSnapshotSource"/> stack.
/// <para>
/// When no peers are configured the result is empty (<see cref="Result.Source"/> is null) and NOTHING is
/// constructed — no <see cref="HttpClient"/>, no request ever touches the network — so a peer-less path
/// stays byte-identical to the pure-local behavior (issue #108, criterion 4).
/// </para>
/// </summary>
public static class RemoteBaseSourceFactory
{
    /// <summary>
    /// The constructed source plus the <see cref="HttpClient"/> the caller must dispose (non-null only when
    /// the factory created one; an injected client is owned by the caller and echoed back as null here).
    /// </summary>
    public readonly record struct Result(IBaseSnapshotSource? Source, HttpClient? OwnedHttp);

    /// <summary>
    /// Constructs one <see cref="RemoteHttpBaseSnapshotSource"/> per configured peer over one shared
    /// <see cref="HttpClient"/>, each with its OWN <see cref="SnapshotPageCache"/> (the paging cursor is a
    /// peer-local <c>symbols.id</c>, so a shared cache could serve one peer's page under another peer's
    /// cursor). Multiple peers are fronted by a <see cref="CompositeBaseSnapshotSource"/>.
    /// <paramref name="httpClient"/> lets a test inject a peer-backed client (e.g. an ASP.NET
    /// <c>TestServer</c> handler); when null and peers exist, a real client is created and returned in
    /// <see cref="Result.OwnedHttp"/> for the caller to dispose.
    /// </summary>
    public static Result Create(SextantConfiguration config, HttpClient? httpClient = null)
    {
        var peers = (config.Peers ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();

        if (peers.Count == 0)
            return new Result(null, null);

        var ownsHttp = httpClient is null;
        // A process-lifetime singleton HttpClient must refresh pooled connections so peer DNS changes are
        // honored in a long-running host (peers are addressed by hostname). Bound the pooled connection
        // lifetime; an injected client (e.g. a test's TestServer handler) is used as-is.
        var http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });
        var timeout = TimeSpan.FromSeconds(config.RemoteFetchTimeoutSeconds > 0 ? config.RemoteFetchTimeoutSeconds : 10);

        var sources = peers
            .Select(peer => (IBaseSnapshotSource)new RemoteHttpBaseSnapshotSource(
                http, peer, config.PeerQueryToken, timeout, new SnapshotPageCache()))
            .ToList();

        IBaseSnapshotSource source = sources.Count == 1
            ? sources[0]
            : new CompositeBaseSnapshotSource(sources);

        return new Result(source, ownsHttp ? http : null);
    }
}
