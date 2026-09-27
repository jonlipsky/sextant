using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Sextant.Store.Tests;

/// <summary>
/// <c>EXPLAIN QUERY PLAN</c> helpers for the issue-#160 plan gates. Plans are read as the detail column
/// (one line per plan node, in plan order). Without <c>sqlite_stat1</c> the planner is purely heuristic,
/// so a plan is a deterministic property of the SQL and the schema, not of the seeded data.
/// </summary>
internal static partial class QueryPlan
{
    /// <summary>Explains <paramref name="command"/> exactly as the store would run it, with its parameters.</summary>
    public static List<string> Explain(SqliteCommand command)
    {
        using var eqp = command.Connection!.CreateCommand();
        eqp.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        foreach (SqliteParameter p in command.Parameters)
            eqp.Parameters.AddWithValue(p.ParameterName, p.Value);
        return Read(eqp);
    }

    /// <summary>Explains raw SQL. Unbound parameters are NULL, which does not change a prepared plan.</summary>
    public static List<string> Explain(SqliteConnection connection, string sql)
    {
        using var eqp = connection.CreateCommand();
        eqp.CommandText = "EXPLAIN QUERY PLAN " + sql;
        return Read(eqp);
    }

    /// <summary>
    /// The alias of every table loop (SEARCH/SCAN line) in plan order, excluding the tables of
    /// subqueries, so the loop nesting of the main query can be asserted.
    /// </summary>
    public static List<string> LoopOrder(IEnumerable<string> plan, params string[] ignoreAliases) =>
        plan.Select(line => LoopLine().Match(line))
            .Where(m => m.Success && !ignoreAliases.Contains(m.Groups[2].Value))
            .Select(m => m.Groups[2].Value)
            .ToList();

    private static List<string> Read(SqliteCommand eqp)
    {
        using var reader = eqp.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
            lines.Add(reader.GetString(3));
        return lines;
    }

    [GeneratedRegex(@"^(SEARCH|SCAN) (\S+)")]
    private static partial Regex LoopLine();
}
