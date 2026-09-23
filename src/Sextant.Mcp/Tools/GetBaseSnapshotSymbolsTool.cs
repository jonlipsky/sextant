using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetBaseSnapshotSymbolsTool
{
    [McpServerTool(Name = "get_base_snapshot_symbols"),
     Description("Page the symbols of a BASE snapshot addressed by its immutable identity hash. Serves the " +
                 "snapshot from the LOCAL catalog when present; otherwise, when remote peers are configured " +
                 "(sextant.json 'peers' / SEXTANT_PEERS), transparently federates the fetch to a peer that " +
                 "publishes that snapshot — the marquee cross-repo path where a base snapshot lives in another " +
                 "repository's service. Falls back to a cached page when a warmed peer later goes offline. " +
                 "Provenance (meta.snapshot.origin = local|remote, base_identity_hash) records " +
                 "where the rows came from. Results are stable and cursor-paged; pass meta.next_cursor to continue.")]
    public static async Task<string> GetBaseSnapshotSymbols(
        DatabaseProvider dbProvider,
        [Description("The immutable identity hash of the base snapshot to page (Phase-9 SnapshotIdentity.Hash).")]
        string identity_hash,
        [Description("Optional: resume cursor from a previous page's meta.next_cursor. Omit to start at the beginning.")]
        string? cursor = null,
        [Description("Optional: max symbols per page (default 500, clamped 1..5000).")]
        int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(identity_hash))
            return ResponseBuilder.BuildEmpty("An identity_hash is required.");

        // Fail-closed top-level read gate FIRST (Phase 17): authorization + Phase-7 readiness + the #57
        // per-request read connection. A denied/unprovisioned local caller gets the uniform not-found and
        // NEVER triggers a remote fetch — so federation can never widen what the caller is authorized to
        // read locally. The remote peer independently authorizes the presented query token against its own
        // read policy, so the effective grant is (local read allowed) AND (peer grants this token).
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var failure))
            return failure;

        var request = new SnapshotPageRequest
        {
            IdentityHash = identity_hash,
            Cursor = cursor,
            Limit = limit ?? 500
        };

        using var conn = db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        var local = snapshots.GetByIdentityHash(identity_hash);

        // Local-complete: serve from the durable catalog, no network. Byte-identical to the pure-local path.
        if (local is { Status: SnapshotStatus.Complete })
        {
            var localPage = await new LocalBaseSnapshotSource(conn).FetchSymbolsAsync(request, CancellationToken.None);
            var localProvenance = new SnapshotProvenance
            {
                BaseSnapshotId = local.Id,
                BaseCommit = snapshots.GetCommitSha(local.CommitId),
                BaseIdentityHash = identity_hash,
                Origin = "local",
                Completeness = SnapshotStatus.Complete,
                Scope = "federated",
                Freshness = local.PublishedAt ?? local.CreatedAt
            };
            return BuildPage(localPage, localProvenance);
        }

        // Not resolvable locally. Federate to a configured peer when one exists; otherwise say so plainly
        // (never imply the snapshot has zero symbols).
        if (dbProvider.RemoteBaseSource is not { } remote)
            return ResponseBuilder.BuildEmpty(
                $"Base snapshot '{identity_hash}' is not in the local catalog and no remote peers are configured " +
                "(set 'peers' in sextant.json or SEXTANT_PEERS to federate).",
                readContext.Provenance);

        try
        {
            var remotePage = await remote.FetchSymbolsAsync(request, CancellationToken.None);

            // A reachable peer that simply does NOT publish this snapshot returns an empty FIRST page.
            // Collapse that to the same structured "not found" as the no-peer case rather than a silent
            // zero-symbol answer an agent would misread as "no such symbols". (An empty page on a RESUMED
            // cursor is a normal end-of-stream and is served as an ordinary terminal page.)
            if (cursor is null && remotePage.Symbols.Count == 0)
                return ResponseBuilder.BuildEmpty(
                    $"Base snapshot '{identity_hash}' is not in the local catalog and no configured peer publishes it.",
                    readContext.Provenance);

            var remoteProvenance = new SnapshotProvenance
            {
                BaseIdentityHash = identity_hash,
                Origin = "remote",
                Completeness = remotePage.Complete ? SnapshotStatus.Complete : SnapshotStatus.Partial,
                Scope = "federated",
                Freshness = readContext.Provenance?.Freshness ?? 0
            };
            return BuildPage(remotePage, remoteProvenance);
        }
        catch (Exception ex) when (ex is RemoteSnapshotUnavailableException or HttpRequestException)
        {
            // Two remote-failure shapes collapse to ONE uniform structured explanation so the tool never
            // faults the call or leaks a raw transport exception, and the caller never reads it as "no such
            // symbols":
            //  * RemoteSnapshotUnavailableException — peer unreachable/timed out and nothing cached.
            //  * HttpRequestException with an HTTP status the remote source deliberately propagates — a 404
            //    (the peer returns an identical 404 for an unknown OR unauthorized-for-this-token snapshot,
            //    so it is never an existence/authorization oracle) or a transient 5xx.
            return ResponseBuilder.BuildEmpty(
                $"Base snapshot '{identity_hash}' is not local and the remote peer is unavailable or does not publish it: {ex.Message}",
                readContext.Provenance);
        }
    }

    private static string BuildPage(SnapshotSymbolPage page, SnapshotProvenance provenance)
    {
        var results = page.Symbols.Select(s => (object)new
        {
            symbol_key = s.SymbolKey,
            fully_qualified_name = s.FullyQualifiedName,
            display_name = s.DisplayName,
            kind = s.Kind,
            accessibility = s.Accessibility,
            cursor = s.Cursor
        }).ToList();

        return ResponseBuilder.Build(results, provenance.Freshness, ambiguity: null, provenance, page.NextCursor);
    }
}
