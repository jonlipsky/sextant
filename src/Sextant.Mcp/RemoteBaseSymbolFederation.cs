using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Pages a committed BASE snapshot from a configured remote peer and folds it into a local read, for the
/// remote-base overlay path (issue #108). A thin machine that indexed only its working-tree diff has the
/// touched-closure project-versions locally (the overlay) but NOT the unchanged base; this helper fetches
/// the base by its immutable identity hash through the tested transport
/// (<see cref="IBaseSnapshotSource"/> — cache-first, per-request timeout, transparent offline fallback) and
/// applies the Phase-11 shadowing rule ACROSS the remote boundary: a base symbol whose logical project the
/// local overlay re-extracted (a TOUCHED project) is dropped, because the overlay's freshly-extracted
/// version is authoritative (so a symbol deleted/renamed/changed in a touched project never resurfaces from
/// the base).
/// <para>
/// The transport pages ALL symbols of the base with no server-side name filter, so this scans the base
/// client-side. That is correct but O(base size) per cold query; the pages are cached by immutable snapshot
/// id, so repeated queries reuse them. A server-side name query that bounds this is tracked as the
/// find_usages/relationships federation follow-up (issue #112).
/// </para>
/// </summary>
internal static class RemoteBaseSymbolFederation
{
    /// <summary>The result of paging the remote base: the matched rows plus how the fetch resolved.</summary>
    internal sealed record Outcome
    {
        /// <summary>Matched base rows (touched-project rows already shadowed out), in cursor order.</summary>
        public required IReadOnlyList<SnapshotSymbolRow> Rows { get; init; }

        /// <summary>True once any page (peer or offline cache) was served for the base.</summary>
        public bool ServedRemote { get; init; }

        /// <summary>The completeness of the last served page (false ⇒ the base itself is partial upstream).</summary>
        public bool Complete { get; init; } = true;

        /// <summary>The base snapshot's durable checkout coverage as reported by the peer (issue #119), or null.</summary>
        public SnapshotCoverage? Coverage { get; init; }

        /// <summary>True when at least one served page came from the transparent offline cache.</summary>
        public bool FromOfflineCache { get; init; }

        /// <summary>
        /// Non-null when the base could not be fully federated: the peer was unreachable with nothing cached,
        /// or no configured peer publishes the base. The caller then serves the local overlay only and
        /// stamps this as an actionable <c>meta.snapshot.fallback_reason</c>.
        /// </summary>
        public string? UnavailableReason { get; init; }
    }

    /// <summary>
    /// Pages the base snapshot addressed by <paramref name="identityHash"/> from <paramref name="source"/>,
    /// excluding rows whose <see cref="SnapshotSymbolRow.ProjectCanonicalId"/> is in
    /// <paramref name="touchedCanonicalIds"/> (cross-boundary shadowing) — and, when the touched set is
    /// non-empty, also excluding rows with NO canonical id (a legacy peer) since they cannot be proven
    /// untouched, degrading <see cref="Outcome.Complete"/> to false. Keeps rows for which
    /// <paramref name="match"/> is true, deduped by <c>symbol_key</c>. Stops early once
    /// <paramref name="maxMatches"/> matches are collected (0 = page to completion). A transport failure is
    /// caught and surfaced as <see cref="Outcome.UnavailableReason"/> rather than thrown, so the read
    /// degrades to overlay-only.
    /// </summary>
    internal static async Task<Outcome> FetchAsync(
        IBaseSnapshotSource source,
        string identityHash,
        ISet<string> touchedCanonicalIds,
        Func<SnapshotSymbolRow, bool> match,
        int maxMatches,
        CancellationToken cancellationToken)
    {
        var matched = new List<SnapshotSymbolRow>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;
        SnapshotCoverage? coverage = null;
        var droppedUnattributed = false;
        var fromCache = false;
        string? cursor = null;

        try
        {
            while (true)
            {
                var page = await source.FetchSymbolsAsync(
                    new SnapshotPageRequest { IdentityHash = identityHash, Cursor = cursor, Limit = 500 },
                    cancellationToken).ConfigureAwait(false);

                complete = page.Complete;
                coverage = page.Coverage ?? coverage;
                fromCache |= page.FromOfflineCache;

                // A reachable source that returns an empty, terminal FIRST page without proof of publication
                // (a pre-#119 peer never sends that proof) does not publish this base (mirrors
                // CompositeBaseSnapshotSource's "skip a 404/empty peer" contract). Report it so the read serves
                // the overlay only with a clear reason instead of a silent zero-symbol answer. A base the
                // source affirmatively publishes but that is empty is a legitimate (empty) answer.
                if (cursor is null && page.Symbols.Count == 0 && page.NextCursor is null && !page.IsProvenPublished)
                    return new Outcome
                    {
                        Rows = matched,
                        ServedRemote = true,
                        Complete = false,
                        FromOfflineCache = fromCache,
                        UnavailableReason = "no configured peer publishes the committed base snapshot"
                    };

                foreach (var row in page.Symbols)
                {
                    if (row.ProjectCanonicalId is { } cid)
                    {
                        if (touchedCanonicalIds.Contains(cid))
                            continue; // shadowed: the local overlay owns this project-version
                    }
                    else if (touchedCanonicalIds.Count > 0)
                    {
                        // Fail-closed shadowing (criterion 2): a base row we cannot attribute to a logical
                        // project — an older peer that omits the canonical id — MIGHT belong to a touched
                        // project, so with a non-empty touched set we cannot prove it is safe to surface.
                        // Drop it rather than risk resurfacing a shadowed symbol, and flag the federation as
                        // incomplete so provenance degrades to partial. A current peer always sends the
                        // canonical id, so this only guards legacy peers.
                        droppedUnattributed = true;
                        continue;
                    }
                    if (!match(row))
                        continue;
                    if (!seenKeys.Add(row.SymbolKey))
                        continue;
                    matched.Add(row);
                    if (maxMatches > 0 && matched.Count >= maxMatches)
                        return new Outcome
                        {
                            Rows = matched, ServedRemote = true, Complete = complete && !droppedUnattributed,
                            FromOfflineCache = fromCache, Coverage = coverage
                        };
                }

                if (page.NextCursor is null)
                    break;
                cursor = page.NextCursor;
            }

            return new Outcome
            {
                Rows = matched, ServedRemote = true, Complete = complete && !droppedUnattributed,
                FromOfflineCache = fromCache, Coverage = coverage
            };
        }
        catch (Exception ex) when (ex is RemoteSnapshotUnavailableException or HttpRequestException)
        {
            // The peer was unreachable / timed out with nothing cached, or propagated an HTTP error. Serve
            // whatever we already matched (possibly none) as overlay-only + a reason — never fault the read.
            return new Outcome
            {
                Rows = matched,
                ServedRemote = matched.Count > 0 || fromCache,
                Complete = false,
                FromOfflineCache = fromCache,
                UnavailableReason = $"remote base peer unavailable ({ex.Message})"
            };
        }
    }

    /// <summary>
    /// Maps a base symbol row to the <c>find_symbol</c> result shape. A base row carries no file/line/
    /// signature (the symbol-paging transport is declaration-identity only — issue #112), so those fields
    /// are null; kind/accessibility integer ordinals are formatted with the same enums as a local row so a
    /// federated result is shape-identical to a local one.
    /// </summary>
    internal static object MapRemoteSymbol(SnapshotSymbolRow row) => new Dictionary<string, object?>
    {
        ["fully_qualified_name"] = row.FullyQualifiedName,
        ["display_name"] = row.DisplayName,
        ["kind"] = ((SymbolKind)row.Kind).ToString().ToLowerInvariant(),
        ["project_id"] = row.ProjectCanonicalId,
        ["file_path"] = null,
        ["line_start"] = null,
        ["line_end"] = null,
        ["accessibility"] = SymbolStore.FormatAccessibility((Accessibility)row.Accessibility),
        ["signature"] = null
    };
}
