using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Search;
using Sextant.Store;

namespace Sextant.Service;

// SVC-F: the grant-scoped, federated `search_symbols`. One call runs in ONE ReadCatalog transaction, so the caller's
// grants, the branch pointers and every snapshot page it reads come from the same committed catalog state. The
// caller's visible snapshots are resolved again on EVERY call and the cursor can only narrow them: a snapshot the
// caller can no longer see (a revoked grant, a forged hash) is never read.
public sealed partial class SnapshotService
{
    /// <summary>Test seam: runs with the identity hash before each snapshot page is read (a throw marks it unavailable).</summary>
    internal Action<string>? SearchSnapshotReadHook { get; set; }

    // One page of one snapshot: rows with id > @after whose display_name starts with the prefix, in id order. Constant
    // SQL: the prefix is a bound LIKE pattern with its wildcards escaped, and the kind filter is a bound nullable value.
    private const string SearchPageSql = """
        SELECT s.id, s.symbol_key, s.fully_qualified_name, s.display_name, s.kind, s.accessibility,
               COALESCE(lp.canonical_id, p.canonical_id)
        FROM snapshot_projects sp
        JOIN symbols s ON s.project_id = sp.project_id
        JOIN projects p ON p.id = s.project_id
        LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
        WHERE sp.snapshot_id = @snapshot
          AND s.id > @after
          AND s.display_name LIKE @pattern ESCAPE '\'
          AND (@kind IS NULL OR s.kind = @kind)
        ORDER BY s.id
        LIMIT @take;
        """;

    /// <summary>
    /// One page of <c>search_symbols</c> for <paramref name="caller"/> (SVC-F). The targets are the caller's visible
    /// grants (narrowed by the query's repository and branch) resolved to their branch's complete snapshot; the
    /// snapshots are deduplicated by identity hash, up to <see cref="ServiceOptions.SearchMaxWidthCeiling"/> of them are
    /// tracked in hash order, and at most <see cref="ServiceOptions.SearchMaxWidth"/> of those are read per call,
    /// round-robin. <paramref name="resume"/> is honored only for snapshots still visible. A failure to read the grants
    /// propagates: it never becomes an empty or unscoped result.
    /// </summary>
    internal SymbolSearchOutcome SearchSymbols(CallerPrincipal caller, SymbolSearchQuery query, SymbolSearchCursorState? resume)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(query);
        var width = Math.Clamp(_options.SearchMaxWidth, 1, ServiceOptions.SearchMaxWidthCeiling);
        var bounded = query with { Limit = Math.Clamp(query.Limit, 1, SymbolSearchQuery.MaxLimit) };
        return ReadCatalog(conn => SearchOn(conn, caller, bounded, resume, width));
    }

    private SymbolSearchOutcome SearchOn(
        SqliteConnection conn, CallerPrincipal caller, SymbolSearchQuery query, SymbolSearchCursorState? resume, int width)
    {
        var grants = new RepositoryGrantStore(conn).ListVisible(caller.TenantId, VisibilitySubject(caller));
        var targets = ResolveSearchTargets(conn, grants, query);
        if (targets.Count == 0)
            return new SymbolSearchOutcome { NoTargets = true };

        var pending = new List<SymbolSearchTarget>();
        var unavailable = new List<SymbolSearchTarget>();
        var snapshots = new SortedDictionary<string, SearchSnapshot>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (target.Snapshot is not { } snapshot)
                pending.Add(target.Target);
            else if (!SymbolSearchCursor.IsIdentityHash(snapshot.IdentityHash))
                unavailable.Add(target.Target);
            else if (snapshots.TryGetValue(snapshot.IdentityHash, out var shared))
                shared.Targets.Add(target.Target);
            else
                snapshots[snapshot.IdentityHash] = new SearchSnapshot(snapshot, [target.Target]);
        }

        var (tracked, watermark, start) = Admit(snapshots, resume, ServiceOptions.SearchMaxWidthCeiling);
        var hits = new List<(SymbolSearchHit Hit, long Id)>();
        var next = new List<SymbolSearchPosition>();
        var deferred = new List<string>();
        string? rotation = null;
        var read = 0;
        long freshness = 0;
        foreach (var position in tracked)
        {
            if (read >= width || (start is not null && string.CompareOrdinal(position.IdentityHash, start) <= 0))
            {
                // Not this page's turn: kept unread, for a later page of this round or for the next round.
                next.Add(position);
                deferred.Add(position.IdentityHash);
                continue;
            }

            read++;
            rotation = position.IdentityHash;
            var snapshot = snapshots[position.IdentityHash];
            List<(SymbolSearchHit Hit, long Id)> rows;
            try
            {
                SearchSnapshotReadHook?.Invoke(position.IdentityHash);
                rows = ReadSearchPage(conn, snapshot, position.AfterId, query);
            }
            catch (SqliteException)
            {
                unavailable.AddRange(snapshot.Targets);
                next.Add(position);
                continue;
            }

            freshness = Math.Max(freshness, snapshot.Row.PublishedAt ?? 0L);
            if (rows.Count > query.Limit)
            {
                rows.RemoveRange(query.Limit, rows.Count - query.Limit);
                next.Add(position with { AfterId = rows[^1].Id });
            }
            hits.AddRange(rows);
        }

        var unadmitted = snapshots.Keys.Where(h => watermark is null || string.CompareOrdinal(h, watermark) > 0).ToList();
        deferred.AddRange(unadmitted);
        return new SymbolSearchOutcome
        {
            Symbols = hits
                .OrderBy(h => h.Hit.Name, StringComparer.Ordinal)
                .ThenBy(h => h.Hit.IdentityHash, StringComparer.Ordinal)
                .ThenBy(h => h.Id)
                .Select(h => h.Hit)
                .ToList(),
            Next = next.Count == 0 && unadmitted.Count == 0
                ? null
                : new SymbolSearchCursorState(
                    next.OrderBy(p => p.IdentityHash, StringComparer.Ordinal).ToList(), watermark, rotation),
            Pending = Ordered(pending),
            Unavailable = Ordered(unavailable),
            Truncated = Ordered(deferred.SelectMany(h => snapshots[h].Targets)),
            IndexFreshness = freshness
        };
    }

    // The snapshots this page tracks, in hash order, and the hash its turn starts after (issue #176 fairness). Pages read
    // the tracked snapshots round-robin: each page reads, in hash order, at most `width` of those after the previous
    // page's last read (`r`), never wrapping past the end, and once none is left after `r` the next round starts from
    // the lowest hash. The cursor's positions are kept only while their snapshot is still visible (anything else,
    // revoked or forged, is dropped without a trace). The next visible hashes after the watermark join, up to
    // `capacity` (the most positions a cursor can carry), only while no tracked snapshot is waiting at or before `r` for
    // the next round, so they always join at the tail of a round. With V visible snapshots, each tracked one is then read
    // at least once every ceil(min(V, capacity) / width) pages.
    private static (List<SymbolSearchPosition> Tracked, string? Watermark, string? Start) Admit(
        SortedDictionary<string, SearchSnapshot> snapshots, SymbolSearchCursorState? resume, int capacity)
    {
        var tracked = resume?.Active.Where(p => snapshots.ContainsKey(p.IdentityHash)).ToList() ?? [];
        var watermark = resume?.Watermark;
        var start = resume?.Rotation;
        if (start is not null && !tracked.Exists(p => string.CompareOrdinal(p.IdentityHash, start) > 0))
            start = null;
        if (start is not null && tracked.Exists(p => string.CompareOrdinal(p.IdentityHash, start) <= 0))
            return (tracked, watermark, start);

        foreach (var hash in snapshots.Keys)
        {
            if (tracked.Count >= capacity)
                break;
            if (watermark is not null && string.CompareOrdinal(hash, watermark) <= 0)
                continue;
            tracked.Add(new SymbolSearchPosition(hash, 0));
            watermark = hash;
        }
        return (tracked, watermark, start);
    }

    // The caller's search targets: one per (repository, branch), ordered by repository key then branch. With no branch
    // argument, every granted branch of each visible repository (a default-branch grant resolves to the default branch's
    // name when the catalog knows it). With one, that branch of each visible repository that grants it (directly or as
    // its default branch) or whose catalog has it, since visibility is repository-level (SVC-4).
    private static List<SearchTargetState> ResolveSearchTargets(
        SqliteConnection conn, IReadOnlyList<RepositoryGrantRow> grants, SymbolSearchQuery query)
    {
        var catalog = new GrantCatalogReader(conn);
        var store = new SnapshotStore(conn);
        var targets = new Dictionary<(string Key, string Branch), SearchTargetState>();
        foreach (var group in grants.GroupBy(g => g.RepositoryKey, StringComparer.Ordinal))
        {
            if (query.RepositoryKey is { } wanted && group.Key != wanted)
                continue;
            var repository = GrantCatalogReader.GrantedSpelling(group);
            var repositoryId = catalog.RepositoryId(group.Key);

            if (query.Branch is { } branch)
            {
                var row = repositoryId is long id ? catalog.BranchFor(id, branch) : null;
                var defaultName = repositoryId is long d ? catalog.BranchFor(d, "")?.Name : null;
                var granted = group.Any(g => g.Branch == branch || (g.Branch.Length == 0 && defaultName == branch));
                if (granted || row is not null)
                    targets.TryAdd((group.Key, branch), Target(repository, branch, row));
                continue;
            }

            foreach (var granted in group.Select(g => g.Branch).Distinct(StringComparer.Ordinal))
            {
                var row = repositoryId is long id ? catalog.BranchFor(id, granted) : null;
                var name = row?.Name ?? granted;
                targets.TryAdd((group.Key, name), Target(repository, name, row));
            }
        }
        return targets
            .OrderBy(t => t.Key.Key, StringComparer.Ordinal)
            .ThenBy(t => t.Key.Branch, StringComparer.Ordinal)
            .Select(t => t.Value)
            .ToList();

        SearchTargetState Target(string repository, string branch, BranchRow? row) => new(
            new SymbolSearchTarget { Repository = repository, Branch = branch },
            row is { SnapshotId: long snapshotId } && store.GetById(snapshotId) is { Status: SnapshotStatus.Complete } snapshot
                ? snapshot
                : null);
    }

    private static List<(SymbolSearchHit Hit, long Id)> ReadSearchPage(
        SqliteConnection conn, SearchSnapshot snapshot, long afterId, SymbolSearchQuery query)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SearchPageSql;
        cmd.Parameters.AddWithValue("@snapshot", snapshot.Row.Id);
        cmd.Parameters.AddWithValue("@after", afterId);
        cmd.Parameters.AddWithValue("@pattern", LikePrefix(query.NamePrefix));
        cmd.Parameters.AddWithValue("@kind", query.Kind is { } kind ? (int)kind : DBNull.Value);
        cmd.Parameters.AddWithValue("@take", query.Limit + 1);

        var target = snapshot.Targets[0];
        var rows = new List<(SymbolSearchHit, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((new SymbolSearchHit
            {
                Repository = target.Repository,
                Branch = target.Branch,
                IdentityHash = snapshot.Row.IdentityHash,
                SymbolKey = reader.GetString(1),
                FullyQualifiedName = reader.GetString(2),
                Name = reader.GetString(3),
                Kind = ((SymbolKind)reader.GetInt32(4)).ToString().ToLowerInvariant(),
                Accessibility = SymbolStore.FormatAccessibility((Accessibility)reader.GetInt32(5)),
                Project = reader.IsDBNull(6) ? null : reader.GetString(6)
            }, reader.GetInt64(0)));
        }
        return rows;
    }

    // A LIKE pattern matching values that start with `prefix` literally: the escape character and both wildcards are
    // escaped. SQLite's LIKE folds ASCII letters only, so the match is case-insensitive for ASCII.
    private static string LikePrefix(string prefix) =>
        prefix.Replace(@"\", @"\\", StringComparison.Ordinal)
              .Replace("%", @"\%", StringComparison.Ordinal)
              .Replace("_", @"\_", StringComparison.Ordinal) + "%";

    private static List<SymbolSearchTarget> Ordered(IEnumerable<SymbolSearchTarget> targets) =>
        targets
            .Distinct()
            .OrderBy(t => t.Repository, StringComparer.Ordinal)
            .ThenBy(t => t.Branch, StringComparer.Ordinal)
            .ToList();

    private sealed record SearchTargetState(SymbolSearchTarget Target, SnapshotRow? Snapshot);

    // One visible complete snapshot and every target that resolves to it (the first, in target order, labels its rows).
    private sealed record SearchSnapshot(SnapshotRow Row, List<SymbolSearchTarget> Targets);
}
