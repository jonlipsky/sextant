using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Builds and owns the remote base-snapshot source the live MCP query planner federates through (issue
/// #60). It is the composition-root seam between <see cref="SextantConfiguration"/> and the tested
/// <see cref="RemoteHttpBaseSnapshotSource"/> transport:
/// <list type="bullet">
///   <item>When no <see cref="SextantConfiguration.Peers"/> are configured, <see cref="Source"/> is null
///   and the MCP read path is byte-identical to today's pure-local behavior — nothing is constructed and
///   no request ever touches the network.</item>
///   <item>When peers are configured, it constructs one <see cref="RemoteHttpBaseSnapshotSource"/> per
///   peer over ONE shared <see cref="HttpClient"/>, each with its OWN <see cref="SnapshotPageCache"/>.
///   The cursor is a peer-LOCAL <c>symbols.id</c>, so a per-peer cache keeps one peer's page from ever
///   being served under another peer's cursor; the <see cref="CompositeBaseSnapshotSource"/> that fronts
///   multiple peers guarantees the peer that owns a cursor is the only one asked to resume it.</item>
/// </list>
/// Disposing the federation disposes the owned <see cref="HttpClient"/> (only when it created one), so the
/// singleton <see cref="DatabaseProvider"/> can clean it up on shutdown.
/// </summary>
public sealed class RemoteBaseSnapshotFederation : IDisposable
{
    private readonly HttpClient? _ownedHttp;

    private RemoteBaseSnapshotFederation(IBaseSnapshotSource? source, HttpClient? ownedHttp)
    {
        Source = source;
        _ownedHttp = ownedHttp;
    }

    /// <summary>The remote base-snapshot source, or null when no peers are configured (pure-local path).</summary>
    public IBaseSnapshotSource? Source { get; }

    /// <summary>True when at least one peer is configured and a remote source was constructed.</summary>
    public bool IsEnabled => Source is not null;

    /// <summary>
    /// Constructs the federation from configuration. <paramref name="httpClient"/> lets a test inject a
    /// peer-backed client (e.g. an ASP.NET <c>TestServer</c> handler); when null and peers are configured,
    /// a real <see cref="HttpClient"/> is created and owned (disposed with the federation).
    /// </summary>
    public static RemoteBaseSnapshotFederation Create(SextantConfiguration config, HttpClient? httpClient = null)
    {
        // Delegate source construction to the Store-level factory so the daemon's overlay reconciler (which
        // cannot depend on Sextant.Mcp) builds the identical per-peer transport (issue #108).
        var result = RemoteBaseSourceFactory.Create(config, httpClient);
        return new RemoteBaseSnapshotFederation(result.Source, result.OwnedHttp);
    }

    public void Dispose() => _ownedHttp?.Dispose();
}
