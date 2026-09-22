using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Read-only aggregate queries over an index used to populate <see cref="RowCountMetrics"/>.
/// All table names are compile-time constants; no user input reaches the SQL text.
/// </summary>
public sealed class IndexMetricsStore(SqliteConnection connection)
{
    /// <summary>Gathers row counts and reference-duplication metrics from the current index.</summary>
    public RowCountMetrics Collect()
    {
        var (totalRefs, distinctRefs) = ReferenceOccurrences();
        return new RowCountMetrics
        {
            Projects = Count("projects"),
            Symbols = Count("symbols"),
            References = totalRefs,
            DistinctReferenceOccurrences = distinctRefs,
            DuplicateReferenceRows = totalRefs - distinctRefs,
            Relationships = Count("relationships"),
            CallGraphEdges = Count("call_graph"),
            Comments = TableExists("comments") ? Count("comments") : 0
        };
    }

    /// <summary>Total reference rows and the number of semantically-distinct occurrences.</summary>
    public (long total, long distinct) ReferenceOccurrences()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM "references"),
                (SELECT COUNT(*) FROM (
                    SELECT DISTINCT symbol_id, file_path, line, reference_kind FROM "references"
                ));
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private long Count(string table)
    {
        // Quote the identifier so reserved words (e.g. "references") are handled;
        // table is always a constant supplied by this class.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private bool TableExists(string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() != null;
    }
}
