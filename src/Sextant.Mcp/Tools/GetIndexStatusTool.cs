using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetIndexStatusTool
{
    [McpServerTool(Name = "get_index_status"), Description("What is indexed, with full snapshot provenance and coverage. Call when meta.snapshot has a warning.")]
    public static string GetIndexStatus(
        DatabaseProvider dbProvider,
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        // Fail closed (criterion 1): status reveals project names, git remotes, symbol/reference counts and
        // storage — all existence/count signals. An unauthorized principal must get the uniform not-found,
        // never the status, so authorize BEFORE reading anything.
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;
        if (!Paging.TryBegin("get_index_status", limit, cursor, readContext, out var page, out var cursorError))
            return cursorError;

        using var conn = db.OpenReadConnection();

        var projects = new List<ProjectRow>();
        long freshness = 0;

        // Default to the selected snapshot's project versions (criterion 6): a scope-less status call
        // lists exactly the current snapshot, surfaces the commit-invariant logical canonical id (never
        // the per-snapshot-suffixed storage value), and never double-counts across coexisting snapshots.
        // A legacy/pre-first-publish DB (no selected snapshot) lists the mutable rows exactly as before.
        // The pinned selection comes from the read context so it honours the Phase-17 repository selector.
        var selected = readContext.SelectedSnapshotId;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(lp.canonical_id, p.canonical_id) AS canonical_id, p.git_remote_url, p.repo_relative_path,
                   p.assembly_name, p.is_test_project, p.last_indexed_at,
                   (SELECT COUNT(*) FROM symbols WHERE project_id = p.id) as symbol_count,
                   (SELECT COUNT(*) FROM occurrences WHERE in_project_id = p.id AND source_symbol_id IS NULL) as reference_count
            FROM projects p
            LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
            WHERE {(selected is long ? "p.snapshot_id = @snap" : "p.snapshot_id IS NULL")}
            ORDER BY p.last_indexed_at DESC;
            """;
        if (selected is long snap)
            cmd.Parameters.AddWithValue("@snap", snap);

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var lastIndexed = reader.GetInt64(reader.GetOrdinal("last_indexed_at"));
                if (freshness == 0 || lastIndexed < freshness)
                    freshness = lastIndexed;

                projects.Add(new ProjectRow
                {
                    CanonicalId = reader.GetString(reader.GetOrdinal("canonical_id")),
                    GitRemoteUrl = reader.GetString(reader.GetOrdinal("git_remote_url")),
                    RepoRelativePath = reader.GetString(reader.GetOrdinal("repo_relative_path")),
                    AssemblyName = reader.IsDBNull(reader.GetOrdinal("assembly_name")) ? null : reader.GetString(reader.GetOrdinal("assembly_name")),
                    IsTestProject = reader.GetInt64(reader.GetOrdinal("is_test_project")) != 0,
                    LastIndexedAt = lastIndexed,
                    SymbolCount = reader.GetInt64(reader.GetOrdinal("symbol_count")),
                    ReferenceCount = reader.GetInt64(reader.GetOrdinal("reference_count"))
                });
            }
        }

        // The totals summarize every project; the rows are one page of them, by path so a cursor resumes exactly
        // where the previous page ended (a repository with a hundred projects is otherwise one oversized answer).
        var ordered = projects
            .OrderBy(p => p.RepoRelativePath, StringComparer.Ordinal)
            .ThenBy(p => p.CanonicalId, StringComparer.Ordinal)
            .ToList();
        var totals = new
        {
            projects = projects.Count,
            test_projects = projects.Count(p => p.IsTestProject),
            symbols = projects.Sum(p => p.SymbolCount),
            references = projects.Sum(p => p.ReferenceCount)
        };
        var index = BuildIndexInfo(conn, selected, dbProvider.Authorizer.IsEnforcing, readContext.Provenance, totals);
        return ResponseBuilder.BuildStatus(page.Slice(ordered), ordered.Count, page, freshness, index);
    }

    /// <summary>One project row of <c>get_index_status</c>, serialized as its snake_case fields.</summary>
    private sealed class ProjectRow
    {
        public required string CanonicalId { get; init; }
        public required string GitRemoteUrl { get; init; }
        public required string RepoRelativePath { get; init; }
        public string? AssemblyName { get; init; }
        public bool IsTestProject { get; init; }
        public long LastIndexedAt { get; init; }
        public long SymbolCount { get; init; }
        public long ReferenceCount { get; init; }
    }

    private static object BuildIndexInfo(
        Microsoft.Data.Sqlite.SqliteConnection conn, long? selectedSnapshotId, bool policyEnforced,
        SnapshotProvenance? provenance, object totals)
    {
        // Under an enforced multi-tenant policy, scope run metadata (profile / config_hash / features) to
        // the caller's SELECTED snapshot's own index run instead of the DB-wide latest complete run, which
        // would leak another tenant's indexing profile and config hash (Phase 17, criterion 1). The
        // zero-policy local path keeps using the last-complete run (byte-identical to pre-Phase-17).
        var runStore = new IndexRunStore(conn);
        IndexRun? run;
        if (policyEnforced)
        {
            var snapRunId = selectedSnapshotId is long sid ? new SnapshotStore(conn).GetById(sid)?.RunId : null;
            run = snapRunId is long rid ? runStore.GetById(rid) : null;
        }
        else
        {
            run = runStore.GetLastCompleteRun();
        }

        // A generation with no recorded features (pre-Phase-8 index) is served with every capability.
        var features = run?.Features is { } f ? (IndexFeature)f : IndexFeature.Deep;
        var profile = run?.IndexingProfile ?? "unknown";

        return new
        {
            profile,
            config_hash = run?.ConfigHash,
            // The whole index at a glance, whichever page of project rows this response carries.
            totals,
            features = IndexProfiles.FeatureNames(features),
            overlay = BuildOverlayInfo(conn, selectedSnapshotId),
            coverage = BuildCoverageInfo(conn, selectedSnapshotId),
            // The full provenance of the served snapshot (base/overlay, completeness, compatibility, origin): the
            // detail the remote surface's lean per-response meta.snapshot points here for. Null (omitted) for a
            // legacy/pre-snapshot database, like meta.snapshot.
            snapshot = SnapshotMeta.From(provenance),
            // Storage is a DB-WIDE (all-tenant) aggregate; omit it under an enforced multi-tenant policy so
            // a per-repository authorized caller cannot read another tenant's storage/existence counts
            // (Phase 17, criterion 1). The zero-policy local path keeps reporting it (byte-identical).
            storage = policyEnforced ? null : BuildStorageInfo(conn)
        };
    }

    /// <summary>
    /// Issue #119: the durable checkout coverage of the selected generation's committed base (the selected
    /// snapshot itself, or an overlay's base; a remote-base overlay carries its peer base's coverage on its
    /// own row) — which solutions/projects/submodules it covers and, when partial, why. Null (and so
    /// omitted) when no coverage was recorded (a local index or pre-022 DB).
    /// </summary>
    private static SnapshotCoverage? BuildCoverageInfo(Microsoft.Data.Sqlite.SqliteConnection conn, long? selectedSnapshotId)
    {
        if (selectedSnapshotId is not long id || new SnapshotStore(conn).GetById(id) is not { } snap)
            return null;

        var coverageId = snap.IsOverlay ? snap.BaseSnapshotId ?? snap.Id : snap.Id;
        return new SnapshotCoverageStore(conn).Get(coverageId);
    }

    /// <summary>
    /// Phase 10 provenance: when the selected generation is a working-tree overlay, or a full LOCAL
    /// fallback that could not reuse a committed base, surfaces that fact plus the EXPLICIT reason
    /// (criterion 5) and the base it layers on. Null (and so omitted) for a clean committed base or a
    /// legacy/pre-snapshot DB, so existing status output is unchanged.
    /// </summary>
    private static object? BuildOverlayInfo(Microsoft.Data.Sqlite.SqliteConnection conn, long? selectedSnapshotId)
    {
        if (selectedSnapshotId is not long id)
            return null;

        var snap = new SnapshotStore(conn).GetById(id);
        if (snap == null)
            return null;

        // Only surface the block when there is something Phase-10-specific to report.
        if (!snap.IsOverlay && snap.FallbackReason == null)
            return null;

        return new
        {
            is_overlay = snap.IsOverlay,
            base_snapshot_id = snap.BaseSnapshotId,
            has_working_tree_delta = snap.WorkingTreeDelta != null,
            fallback_reason = snap.FallbackReason
        };
    }

    private static object BuildStorageInfo(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        return new
        {
            database_bytes = ScalarLong(conn, "SELECT (SELECT page_count FROM pragma_page_count()) * (SELECT page_size FROM pragma_page_size());"),
            api_snapshot_count = ScalarLong(conn, "SELECT COUNT(*) FROM api_surface_snapshots;"),
            file_version_count = ScalarLong(conn, "SELECT COUNT(*) FROM file_versions;"),
            complete_generation_count = ScalarLong(conn, "SELECT COUNT(*) FROM index_runs WHERE status = 'complete';"),
            total_run_count = ScalarLong(conn, "SELECT COUNT(*) FROM index_runs;")
        };
    }

    private static long ScalarLong(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? 0 : Convert.ToInt64(result);
        }
        catch
        {
            return 0;
        }
    }
}
