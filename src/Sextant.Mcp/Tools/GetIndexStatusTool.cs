using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetIndexStatusTool
{
    [McpServerTool(Name = "get_index_status"), Description("Check what projects are indexed, symbol/reference counts, index freshness, the active indexing profile, its enabled feature capabilities, and retained storage. Call this first to see what data is available.")]
    public static string GetIndexStatus(DatabaseProvider dbProvider)
    {
        var db = dbProvider.GetReadyDatabase(out var notReady);
        if (db == null)
            return ResponseBuilder.BuildEmpty(notReady);

        // Fail closed (criterion 1): status reveals project names, git remotes, symbol/reference counts and
        // storage — all existence/count signals. An unauthorized principal must get the structured error,
        // never the status, so authorize BEFORE reading anything.
        if (!ReadContextGate.TryResolve(db, out _, out var authError, authorizer: dbProvider.Authorizer))
            return authError;

        using var conn = db.OpenReadConnection();

        var results = new List<object>();
        long freshness = 0;

        // Default to the selected snapshot's project versions (criterion 6): a scope-less status call
        // lists exactly the current snapshot, surfaces the commit-invariant logical canonical id (never
        // the per-snapshot-suffixed storage value), and never double-counts across coexisting snapshots.
        // A legacy/pre-first-publish DB (no selected snapshot) lists the mutable rows exactly as before.
        var selected = new SnapshotStore(conn).GetSelectedSnapshotId();
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

                results.Add(new
                {
                    canonical_id = reader.GetString(reader.GetOrdinal("canonical_id")),
                    git_remote_url = reader.GetString(reader.GetOrdinal("git_remote_url")),
                    repo_relative_path = reader.GetString(reader.GetOrdinal("repo_relative_path")),
                    assembly_name = reader.IsDBNull(reader.GetOrdinal("assembly_name")) ? null : reader.GetString(reader.GetOrdinal("assembly_name")),
                    is_test_project = reader.GetInt64(reader.GetOrdinal("is_test_project")) != 0,
                    last_indexed_at = lastIndexed,
                    symbol_count = reader.GetInt64(reader.GetOrdinal("symbol_count")),
                    reference_count = reader.GetInt64(reader.GetOrdinal("reference_count"))
                });
            }
        }

        var index = BuildIndexInfo(conn, selected);
        return ResponseBuilder.BuildStatus(results, freshness, index);
    }

    private static object BuildIndexInfo(Microsoft.Data.Sqlite.SqliteConnection conn, long? selectedSnapshotId)
    {
        var run = new IndexRunStore(conn).GetLastCompleteRun();

        // A generation with no recorded features (pre-Phase-8 index) is served with every capability.
        var features = run?.Features is { } f ? (IndexFeature)f : IndexFeature.Deep;
        var profile = run?.IndexingProfile ?? "unknown";

        return new
        {
            profile,
            config_hash = run?.ConfigHash,
            features = IndexProfiles.FeatureNames(features),
            overlay = BuildOverlayInfo(conn, selectedSnapshotId),
            storage = BuildStorageInfo(conn)
        };
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
