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

        var conn = db.GetConnection();

        var results = new List<object>();
        long freshness = 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.canonical_id, p.git_remote_url, p.repo_relative_path,
                   p.assembly_name, p.is_test_project, p.last_indexed_at,
                   (SELECT COUNT(*) FROM symbols WHERE project_id = p.id) as symbol_count,
                   (SELECT COUNT(*) FROM occurrences WHERE in_project_id = p.id AND source_symbol_id IS NULL) as reference_count
            FROM projects p
            ORDER BY p.last_indexed_at DESC;
            """;

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

        var index = BuildIndexInfo(conn);
        return ResponseBuilder.BuildStatus(results, freshness, index);
    }

    private static object BuildIndexInfo(Microsoft.Data.Sqlite.SqliteConnection conn)
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
            storage = BuildStorageInfo(conn)
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
