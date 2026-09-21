using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>A portable symbol row from a base snapshot, keyed by its node-independent <c>symbol_key</c>.</summary>
public sealed record SnapshotSymbolRow
{
    public required long Cursor { get; init; }
    public required string SymbolKey { get; init; }
    public required string FullyQualifiedName { get; init; }
    public required string DisplayName { get; init; }
    public required int Kind { get; init; }
    public required int Accessibility { get; init; }
}

/// <summary>
/// One immutable page of a base snapshot's symbols, addressed by the portable Phase-9 identity hash and a
/// stable cursor. Because a published snapshot is immutable, a page is safe to cache by
/// (<see cref="IdentityHash"/>, cursor) indefinitely (issue #51 caching-by-snapshot-id). A null
/// <see cref="NextCursor"/> marks the last page.
/// </summary>
public sealed record SnapshotSymbolPage
{
    public required string IdentityHash { get; init; }
    public required IReadOnlyList<SnapshotSymbolRow> Symbols { get; init; }
    public string? NextCursor { get; init; }
    public bool Complete { get; init; } = true;

    /// <summary>True when this page was served from cache after the source became unreachable (offline fallback).</summary>
    public bool FromOfflineCache { get; init; }
}

/// <summary>A stable, resumable request for one page of a base snapshot's symbols.</summary>
public sealed record SnapshotPageRequest
{
    public required string IdentityHash { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 500;

    /// <summary>Normalizes the cursor to a monotonic symbol id (0 = from the beginning).</summary>
    public long CursorId => long.TryParse(Cursor, out var v) ? v : 0;

    /// <summary>The cache key: immutable snapshot identity + page cursor.</summary>
    public string CacheKey => $"{IdentityHash}:{CursorId}:{Limit}";
}

/// <summary>
/// The seam a Phase-11 federated read uses to pull a BASE snapshot's rows from wherever they live (issue
/// #51). Phase 11 built the local planner + immutable snapshot-id pinning and a base-snapshot-source seam;
/// this is the source those slot into. A local implementation reads the durable catalog; a remote
/// implementation pages from a peer service with caching, a timeout, and a transparent offline fallback to
/// the cached base.
/// </summary>
public interface IBaseSnapshotSource
{
    Task<SnapshotSymbolPage> FetchSymbolsAsync(SnapshotPageRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a base snapshot's symbols from the local durable catalog. Pages are stable and deterministic:
/// symbols are ordered by their monotonic id and the cursor is the last id returned, so
/// <c>WHERE id &gt; cursor</c> resumes exactly where the previous page ended (immutable snapshot ⇒ stable
/// paging, issue #51). An unknown or not-yet-complete identity yields an empty, complete page.
/// </summary>
public sealed class LocalBaseSnapshotSource(SqliteConnection connection) : IBaseSnapshotSource
{
    public Task<SnapshotSymbolPage> FetchSymbolsAsync(SnapshotPageRequest request, CancellationToken cancellationToken)
    {
        var snapshots = new SnapshotStore(connection);
        var snapshot = snapshots.GetByIdentityHash(request.IdentityHash);
        if (snapshot is not { Status: SnapshotStatus.Complete })
            return Task.FromResult(new SnapshotSymbolPage
            {
                IdentityHash = request.IdentityHash,
                Symbols = [],
                NextCursor = null,
                Complete = snapshot is not null
            });

        var projectIds = snapshots.GetSnapshotProjectIds(snapshot.Id);
        if (projectIds.Count == 0)
            return Task.FromResult(new SnapshotSymbolPage { IdentityHash = request.IdentityHash, Symbols = [], NextCursor = null });

        var limit = Math.Clamp(request.Limit, 1, 5000);
        var inList = string.Join(",", projectIds);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, symbol_key, fully_qualified_name, display_name, kind, accessibility
            FROM symbols
            WHERE project_id IN ({inList}) AND id > @cursor
            ORDER BY id
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@cursor", request.CursorId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var rows = new List<SnapshotSymbolRow>(limit);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rows.Add(new SnapshotSymbolRow
                {
                    Cursor = reader.GetInt64(0),
                    SymbolKey = reader.GetString(1),
                    FullyQualifiedName = reader.GetString(2),
                    DisplayName = reader.GetString(3),
                    Kind = reader.GetInt32(4),
                    Accessibility = reader.GetInt32(5)
                });
        }

        var next = rows.Count == limit ? rows[^1].Cursor.ToString() : null;
        return Task.FromResult(new SnapshotSymbolPage
        {
            IdentityHash = request.IdentityHash,
            Symbols = rows,
            NextCursor = next
        });
    }
}

/// <summary>
/// A bounded, thread-safe cache of immutable base-snapshot pages keyed by (identity hash, cursor). A
/// published snapshot never changes, so a cached page is valid forever; the cache is bounded by a max
/// entry count and evicts the least-recently-used entry on overflow (issue #51 bounded local caches). It
/// backs both proactive caching and the transparent offline fallback: once a page is cached, a later fetch
/// can serve it even when the remote source is unreachable.
/// </summary>
public sealed class SnapshotPageCache(int capacity = 1024)
{
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new();

    private sealed record Entry(string Key, SnapshotSymbolPage Page);

    public bool TryGet(string key, out SnapshotSymbolPage page)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                page = node.Value.Page;
                return true;
            }
        }
        page = null!;
        return false;
    }

    public void Set(string key, SnapshotSymbolPage page)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }

            var node = new LinkedListNode<Entry>(new Entry(key, page));
            _lru.AddFirst(node);
            _map[key] = node;

            while (_map.Count > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }
}

/// <summary>
/// Composes an ordered set of base-snapshot sources (local first, then remote peers). The first source
/// that returns a non-empty (or known-complete) page wins; a remote source that is unreachable is skipped
/// so a healthy peer or the local catalog still answers. This is the concrete base-snapshot seam a
/// federated read resolves through (issue #51).
/// </summary>
public sealed class CompositeBaseSnapshotSource(IReadOnlyList<IBaseSnapshotSource> sources) : IBaseSnapshotSource
{
    public async Task<SnapshotSymbolPage> FetchSymbolsAsync(SnapshotPageRequest request, CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var source in sources)
        {
            try
            {
                var page = await source.FetchSymbolsAsync(request, cancellationToken).ConfigureAwait(false);
                if (page.Symbols.Count > 0 || page.NextCursor is null && page.Complete)
                    return page;
            }
            catch (RemoteSnapshotUnavailableException ex)
            {
                last = ex;
            }
        }

        if (last is not null)
            throw last;

        return new SnapshotSymbolPage { IdentityHash = request.IdentityHash, Symbols = [], NextCursor = null };
    }
}

/// <summary>A remote base-snapshot source was unreachable and no cached page was available (issue #51).</summary>
public sealed class RemoteSnapshotUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
