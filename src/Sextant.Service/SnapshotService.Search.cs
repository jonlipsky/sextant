using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Grants;
using Sextant.Service.Search;
using Sextant.Store;

namespace Sextant.Service;

// SVC-F: the grant-scoped, federated `search_symbols`. One call runs in ONE ReadCatalog transaction, so the caller's
// grants, the branch pointers and every snapshot page it reads come from the same committed catalog state. The
// caller's visible snapshots are resolved again on EVERY call and the cursor can only narrow them: a snapshot the
// caller can no longer see (a revoked grant, a forged hash) is never read.
//
// Issue #196 bounds what one call costs. Each snapshot page is an index seek (ix_symbols_project_name_nocase, migration
// 025) over the prefix's NOCASE key range, so it reads only the rows it returns. The call returns at most
// ServiceOptions.SearchMaxHits symbols in all. The caller's repositories are found by one indexed query over the
// caller's own keys. And the request's cancellation stops the call between statements and rows and interrupts the
// statement running.
public sealed partial class SnapshotService
{
    /// <summary>Test seam: runs with the identity hash before each snapshot page is read (a throw marks it unavailable).</summary>
    internal Action<string>? SearchSnapshotReadHook { get; set; }

    /// <summary>
    /// Test seam: runs with the identity hash and the call's cancellation token after each row of a snapshot page is
    /// read, while its statement runs. The row loop checks the token only as each row arrives, before this hook, so a
    /// cancellation raised here is stopped by SQLite's interrupt at the statement's next step.
    /// </summary>
    internal Action<string, CancellationToken>? SearchRowReadHook { get; set; }

    /// <summary>
    /// With a <c>kind</c> filter, the most rows one call examines in all. The kind is filtered as rows are read, so a
    /// rare kind never makes a call walk every match of a broad prefix: the snapshots a page reads share this evenly,
    /// like the hit budget, and a snapshot that runs out of its share resumes on the next page after the last row it
    /// examined, possibly having returned nothing on this one.
    /// </summary>
    internal const int DefaultSearchKindExamineRowsPerCall = 8192;

    /// <summary>Test seam: <see cref="DefaultSearchKindExamineRowsPerCall"/>, lowered to exercise sparse pages.</summary>
    internal int SearchKindExamineRowsPerCall { get; set; } = DefaultSearchKindExamineRowsPerCall;

    // Where a snapshot's page resumes: the project and name of the row the position last returned (it must be a row of
    // this snapshot), and whether that name is before or past the prefix's range (only for a forged position).
    internal const string SearchResumeSql = """
        SELECT s.project_id, s.display_name,
               s.display_name COLLATE NOCASE < @lo,
               s.display_name COLLATE NOCASE >= @hi
        FROM symbols s
        JOIN snapshot_projects sp ON sp.snapshot_id = @snapshot AND sp.project_id = s.project_id
        WHERE s.id = @after;
        """;

    // One page of one snapshot, in (project id, NOCASE name, id) order: the order of the index, so SQLite seeks to the
    // prefix's range in each of the snapshot's projects and stops after @take rows, without sorting. The resumed
    // project starts after (@from, @after), the following ones at @lo. The range is the prefix's NOCASE key range and
    // the escaped LIKE pattern is the exact residual (both fold ASCII letters only). @hi is an empty BLOB when the
    // range has no upper bound (every TEXT value sorts below a BLOB). Constant SQL: every value is bound. The join
    // order is forced (CROSS JOIN, INDEXED BY) so the plan never depends on statistics.
    internal const string SearchPageSql = """
        SELECT s.id, s.symbol_key, s.fully_qualified_name, s.display_name, s.kind, s.accessibility,
               COALESCE(lp.canonical_id, p.canonical_id)
        FROM snapshot_projects sp
        CROSS JOIN symbols s INDEXED BY ix_symbols_project_name_nocase
        CROSS JOIN projects p ON p.id = s.project_id
        LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
        WHERE sp.snapshot_id = @snapshot
          AND sp.project_id >= @project
          AND s.project_id = sp.project_id
          AND s.display_name COLLATE NOCASE >= (CASE WHEN sp.project_id = @project THEN @from ELSE @lo END)
          AND s.display_name COLLATE NOCASE < @hi
          AND (sp.project_id > @project OR s.display_name COLLATE NOCASE > @from OR s.id > @after)
          AND s.display_name LIKE @pattern ESCAPE '\'
        ORDER BY sp.project_id, s.display_name COLLATE NOCASE, s.id
        LIMIT @take;
        """;

    // The consumer repositories whose remote_url falls in any of the bound NOCASE ranges (a JSON array of [lo, hi]
    // pairs), through ix_repositories_remote_url_nocase (migration 025): one seek per range, never the whole table.
    internal const string SearchRepositoriesSql = """
        SELECT r.id, r.remote_url
        FROM json_each(@ranges) j
        CROSS JOIN repositories r INDEXED BY ix_repositories_remote_url_nocase
        WHERE r.remote_url COLLATE NOCASE >= (j.value ->> 0)
          AND r.remote_url COLLATE NOCASE < (j.value ->> 1)
          AND r.is_provider = 0;
        """;

    /// <summary>
    /// One page of <c>search_symbols</c> for <paramref name="caller"/> (SVC-F). The targets are the caller's visible
    /// grants (narrowed by the query's repository and branch) resolved to their branch's complete snapshot; the
    /// snapshots are deduplicated by identity hash, up to <see cref="ServiceOptions.SearchMaxWidthCeiling"/> of them are
    /// tracked in hash order, and at most <see cref="ServiceOptions.SearchMaxWidth"/> of those are read per call,
    /// round-robin, sharing <see cref="ServiceOptions.SearchMaxHits"/>. <paramref name="resume"/> is honored only for
    /// snapshots still visible. A failure to read the grants propagates: it never becomes an empty or unscoped result.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal SymbolSearchOutcome SearchSymbols(
        CallerPrincipal caller, SymbolSearchQuery query, SymbolSearchCursorState? resume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(query);
        var bounds = new SearchBounds(
            Math.Clamp(_options.SearchMaxWidth, 1, ServiceOptions.SearchMaxWidthCeiling),
            Math.Clamp(_options.SearchMaxHits, ServiceOptions.SearchMaxHitsFloor, ServiceOptions.SearchMaxHitsCeiling));
        var bounded = query with { Limit = Math.Clamp(query.Limit, 1, SymbolSearchQuery.MaxLimit) };
        return ReadCatalogCancellable(conn => SearchOn(conn, caller, bounded, resume, bounds, cancellationToken), cancellationToken);
    }

    private SymbolSearchOutcome SearchOn(
        SqliteConnection conn, CallerPrincipal caller, SymbolSearchQuery query, SymbolSearchCursorState? resume,
        SearchBounds bounds, CancellationToken cancellationToken)
    {
        var grants = new RepositoryGrantStore(conn).ListVisible(caller.TenantId, VisibilitySubject(caller));
        var targets = ResolveSearchTargets(conn, grants, query, cancellationToken);
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
        var next = new List<SymbolSearchPosition>();
        var deferred = new List<string>();
        var turn = new List<SymbolSearchPosition>();
        foreach (var position in tracked)
        {
            if (turn.Count >= bounds.Width || (start is not null && string.CompareOrdinal(position.IdentityHash, start) <= 0))
            {
                // Not this page's turn: kept unread, for a later page of this round or for the next round.
                next.Add(position);
                deferred.Add(position.IdentityHash);
            }
            else
            {
                turn.Add(position);
            }
        }

        var page = new SearchPageInput(query, NoCasePrefixRange.Of(query.NamePrefix), LikePrefix(query.NamePrefix), cancellationToken);
        var hits = new List<(SymbolSearchHit Hit, long Id)>();
        var budget = bounds.MaxHits;
        var examineBudget = SearchKindExamineRowsPerCall;
        string? rotation = null;
        long freshness = 0;
        for (var i = 0; i < turn.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = turn[i];
            rotation = position.IdentityHash;
            var snapshot = snapshots[position.IdentityHash];
            // The snapshots still to read share what is left of the call's budget evenly. MaxHits is at least the width,
            // so every one of them gets at least one row and the round-robin turns are unchanged.
            var share = Math.Min(query.Limit, budget / (turn.Count - i));
            // Without a kind every row examined is a hit, so one row past the share tells whether there are more. With
            // one, the rows examined are shared the same way and are the only bound, so the call never examines more
            // than SearchKindExamineRowsPerCall rows (a snapshot may then return fewer hits than its share).
            var take = query.Kind is null ? share + 1 : Math.Max(1, examineBudget / (turn.Count - i));
            SnapshotPage rows;
            try
            {
                SearchSnapshotReadHook?.Invoke(position.IdentityHash);
                rows = ReadSearchPage(conn, snapshot, position.AfterId, page, share, take);
            }
            catch (SqliteException) when (!cancellationToken.IsCancellationRequested)
            {
                examineBudget -= take;
                unavailable.AddRange(snapshot.Targets);
                next.Add(position);
                continue;
            }

            budget -= rows.Hits.Count;
            examineBudget -= rows.Examined;
            freshness = Math.Max(freshness, snapshot.Row.PublishedAt ?? 0L);
            if (rows.ResumeAfter is long after)
                next.Add(position with { AfterId = after });
            hits.AddRange(rows.Hits);
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
    // its default branch) or whose catalog has it, since visibility is repository-level (SVC-4). The repositories are
    // found in one query over the caller's keys; each branch and snapshot is a point lookup, so the work grows with the
    // caller's grants (bounded by the grant limits), never with the catalog.
    private static List<SearchTargetState> ResolveSearchTargets(
        SqliteConnection conn, IReadOnlyList<RepositoryGrantRow> grants, SymbolSearchQuery query,
        CancellationToken cancellationToken)
    {
        var catalog = new GrantCatalogReader(conn);
        var store = new SnapshotStore(conn);
        var groups = grants
            .GroupBy(g => g.RepositoryKey, StringComparer.Ordinal)
            .Where(g => query.RepositoryKey is not { } wanted || g.Key == wanted)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        var repositoryIds = SearchRepositoryIds(conn, groups.Select(g => g.Key).ToList());
        var targets = new Dictionary<(string Key, string Branch), SearchTargetState>();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repository = GrantCatalogReader.GrantedSpelling(group);
            long? repositoryId = repositoryIds.TryGetValue(group.Key, out var found) ? found : null;

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

    // The lowest-id consumer repository of each key, found in ONE query over the caller's keys. A catalog row keeps the
    // spelling its ensure submitted, so each key stands for the NOCASE ranges its spellings start with: the key itself
    // and, for an https key, the key with the default port. A row in a range counts only when its RepositoryGrantKey is
    // exactly the key. A spelling outside both ranges (leading whitespace, or a non-ASCII letter in another case, which
    // the SVC-5 intake refuses) is not found, so its target reads as pending: never as another repository.
    private static Dictionary<string, long> SearchRepositoryIds(SqliteConnection conn, IReadOnlyCollection<string> keys)
    {
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        var ranges = new List<string[]>();
        foreach (var key in keys)
        {
            foreach (var spelling in CatalogSpellings(key))
            {
                if (NoCasePrefixRange.Of(spelling) is { Hi: { } hi } range)
                    ranges.Add([range.Lo, hi]);
            }
        }
        if (ranges.Count == 0)
            return found;

        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SearchRepositoriesSql;
        cmd.Parameters.AddWithValue("@ranges", JsonSerializer.Serialize(ranges));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var key = RepositoryGrantKey.Of(reader.GetString(1));
            if (wanted.Contains(key) && (!found.TryGetValue(key, out var lowest) || id < lowest))
                found[key] = id;
        }
        return found;
    }

    private static IEnumerable<string> CatalogSpellings(string key)
    {
        if (key.Length == 0)
            yield break;
        yield return key;
        const string scheme = "https://";
        var slash = key.StartsWith(scheme, StringComparison.Ordinal) ? key.IndexOf('/', scheme.Length) : -1;
        if (slash > scheme.Length)
            yield return string.Concat(key.AsSpan(0, slash), ":443", key.AsSpan(slash));
    }

    // Reads the next page of one snapshot: at most `share` hits, examining at most `take` rows (at least 1). The page
    // resumes after its last hit when there are more, or after the last row it examined when it examined `take` rows
    // first; otherwise the snapshot is exhausted.
    private SnapshotPage ReadSearchPage(
        SqliteConnection conn, SearchSnapshot snapshot, long afterId, SearchPageInput page, int share, int take)
    {
        var hi = page.Range.Hi is { } upper ? (object)upper : Array.Empty<byte>();
        var (project, from, after) = (0L, page.Range.Lo, 0L);
        if (afterId > 0)
        {
            if (ResumePoint(conn, snapshot.Row.Id, afterId, page.Range, hi) is not { } point)
                return new SnapshotPage([], null, 0);
            (project, from, after) = point;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = SearchPageSql;
        cmd.Parameters.AddWithValue("@snapshot", snapshot.Row.Id);
        cmd.Parameters.AddWithValue("@project", project);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@after", after);
        cmd.Parameters.AddWithValue("@lo", page.Range.Lo);
        cmd.Parameters.AddWithValue("@hi", hi);
        cmd.Parameters.AddWithValue("@pattern", page.Pattern);
        cmd.Parameters.AddWithValue("@take", take);

        var target = snapshot.Targets[0];
        var hits = new List<(SymbolSearchHit, long)>();
        var examined = 0;
        var lastExamined = 0L;
        using (var reader = cmd.ExecuteReader())
        {
            while (hits.Count <= share && reader.Read())
            {
                // CancellationTokenSource sets the token before it runs the interrupt callback, so a row the step
                // produced in between is dropped here rather than returned.
                page.CancellationToken.ThrowIfCancellationRequested();
                examined++;
                lastExamined = reader.GetInt64(0);
                SearchRowReadHook?.Invoke(snapshot.Row.IdentityHash, page.CancellationToken);
                var kind = (SymbolKind)reader.GetInt32(4);
                if (page.Query.Kind is { } wanted && kind != wanted)
                    continue;
                hits.Add((new SymbolSearchHit
                {
                    Repository = target.Repository,
                    Branch = target.Branch,
                    IdentityHash = snapshot.Row.IdentityHash,
                    SymbolKey = reader.GetString(1),
                    FullyQualifiedName = reader.GetString(2),
                    Name = reader.GetString(3),
                    Kind = kind.ToString().ToLowerInvariant(),
                    Accessibility = SymbolStore.FormatAccessibility((Accessibility)reader.GetInt32(5)),
                    Project = reader.IsDBNull(6) ? null : reader.GetString(6)
                }, lastExamined));
            }
        }

        if (hits.Count > share)
        {
            hits.RemoveRange(share, hits.Count - share);
            return new SnapshotPage(hits, hits[^1].Item2, examined);
        }
        return new SnapshotPage(hits, examined >= take ? lastExamined : null, examined);
    }

    // Where a position resumes: after its row in the index order, or at the start of the range (in its project, or in
    // the next one) when a forged position names a row outside it. Null when the row is not one of this snapshot's.
    private static (long Project, string From, long After)? ResumePoint(
        SqliteConnection conn, long snapshotId, long afterId, NoCasePrefixRange range, object hi)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SearchResumeSql;
        cmd.Parameters.AddWithValue("@snapshot", snapshotId);
        cmd.Parameters.AddWithValue("@after", afterId);
        cmd.Parameters.AddWithValue("@lo", range.Lo);
        cmd.Parameters.AddWithValue("@hi", hi);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var project = reader.GetInt64(0);
        if (reader.GetBoolean(3))
            return (project + 1, range.Lo, 0L);
        if (reader.GetBoolean(2))
            return (project, range.Lo, 0L);
        return (project, reader.GetString(1), afterId);
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

    // How many snapshots one call reads and how many symbols it returns in all.
    private readonly record struct SearchBounds(int Width, int MaxHits);

    // What every snapshot page of one call shares.
    private sealed record SearchPageInput(
        SymbolSearchQuery Query, NoCasePrefixRange Range, string Pattern, CancellationToken CancellationToken);

    // One snapshot's page: its hits (with their row ids), the row it resumes after (null when it is exhausted), and how
    // many rows it examined.
    private readonly record struct SnapshotPage(List<(SymbolSearchHit Hit, long Id)> Hits, long? ResumeAfter, int Examined);
}
