using System.Text;
using Microsoft.Data.Sqlite;

namespace Sextant.Benchmarks.Tests;

/// <summary>
/// Produces a stable, order-independent textual snapshot of the semantic content of an index
/// database. Every numeric primary key and foreign key (which are autoincrement rowids assigned in
/// insertion order) is projected away and replaced by the stable semantic identity of the row it
/// references — a project's <c>canonical_id</c> and a symbol's <c>(canonical_id, symbol_key)</c> —
/// and volatile columns (row ids, <c>last_indexed_at</c>/<c>captured_at</c> timestamps, ledger run
/// and generation ids) are excluded. Two databases built by different code paths (a from-scratch
/// full index versus a full index followed by incremental edits) are therefore byte-for-byte equal
/// here iff they encode the same semantic graph, which is the Phase 4 full/incremental equivalence
/// acceptance criterion.
/// </summary>
public static class CanonicalIndexDump
{
    /// <summary>Renders the canonical dump of every semantically-meaningful table.</summary>
    public static string Dump(SqliteConnection conn)
    {
        var sb = new StringBuilder();

        Section(sb, conn, "projects", """
            SELECT canonical_id, git_remote_url, repo_relative_path,
                   COALESCE(assembly_name,''), COALESCE(target_framework,''), is_test_project
            FROM projects
            ORDER BY 1, 5
            """);

        Section(sb, conn, "symbols", """
            SELECT p.canonical_id, s.symbol_key, s.fully_qualified_name, s.display_name, s.kind,
                   s.accessibility, s.is_static, s.is_abstract, s.is_virtual, s.is_override,
                   COALESCE(s.signature,''), COALESCE(s.signature_hash,''), COALESCE(s.doc_comment,''),
                   s.file_path, s.line_start, s.line_end, COALESCE(s.attributes,'')
            FROM symbols s
            JOIN projects p ON s.project_id = p.id
            ORDER BY 1, 2, 14, 15
            """);

        Section(sb, conn, "references", """
            SELECT tp.canonical_id, ts.symbol_key, ip.canonical_id, r.file_path, r.line,
                   r.reference_kind, COALESCE(r.context_snippet,'')
            FROM "references" r
            JOIN symbols ts ON r.symbol_id = ts.id
            JOIN projects tp ON ts.project_id = tp.id
            JOIN projects ip ON r.in_project_id = ip.id
            ORDER BY 1, 2, 3, 4, 5, 6, 7
            """);

        Section(sb, conn, "relationships", """
            SELECT fp.canonical_id, fs.symbol_key, tp.canonical_id, ts.symbol_key, rel.kind
            FROM relationships rel
            JOIN symbols fs ON rel.from_symbol_id = fs.id
            JOIN projects fp ON fs.project_id = fp.id
            JOIN symbols ts ON rel.to_symbol_id = ts.id
            JOIN projects tp ON ts.project_id = tp.id
            ORDER BY 1, 2, 3, 4, 5
            """);

        Section(sb, conn, "call_graph", """
            SELECT cp.canonical_id, cs.symbol_key, ep.canonical_id, es.symbol_key,
                   cg.call_site_file, cg.call_site_line
            FROM call_graph cg
            JOIN symbols cs ON cg.caller_symbol_id = cs.id
            JOIN projects cp ON cs.project_id = cp.id
            JOIN symbols es ON cg.callee_symbol_id = es.id
            JOIN projects ep ON es.project_id = ep.id
            ORDER BY 1, 2, 3, 4, 5, 6
            """);

        Section(sb, conn, "comments", """
            SELECT p.canonical_id, c.file_path, c.line, c.tag, c.text,
                   COALESCE(es.symbol_key,'')
            FROM comments c
            JOIN projects p ON c.project_id = p.id
            LEFT JOIN symbols es ON c.enclosing_symbol_id = es.id
            ORDER BY 1, 2, 3, 4, 5, 6
            """);

        Section(sb, conn, "file_index", """
            SELECT p.canonical_id, fi.file_path, fi.content_hash
            FROM file_index fi
            JOIN projects p ON fi.project_id = p.id
            ORDER BY 1, 2, 3
            """);

        Section(sb, conn, "argument_flow", """
            SELECT cp.canonical_id, cs.symbol_key, ep.canonical_id, es.symbol_key,
                   cg.call_site_file, cg.call_site_line,
                   af.parameter_ordinal, af.parameter_name, af.argument_expression,
                   af.argument_kind, COALESCE(af.source_symbol_fqn,'')
            FROM argument_flow af
            JOIN call_graph cg ON af.call_graph_id = cg.id
            JOIN symbols cs ON cg.caller_symbol_id = cs.id
            JOIN projects cp ON cs.project_id = cp.id
            JOIN symbols es ON cg.callee_symbol_id = es.id
            JOIN projects ep ON es.project_id = ep.id
            ORDER BY 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11
            """);

        Section(sb, conn, "return_flow", """
            SELECT cp.canonical_id, cs.symbol_key, ep.canonical_id, es.symbol_key,
                   cg.call_site_file, cg.call_site_line,
                   rf.destination_kind, COALESCE(rf.destination_variable,''),
                   COALESCE(rf.destination_symbol_fqn,'')
            FROM return_flow rf
            JOIN call_graph cg ON rf.call_graph_id = cg.id
            JOIN symbols cs ON cg.caller_symbol_id = cs.id
            JOIN projects cp ON cs.project_id = cp.id
            JOIN symbols es ON cg.callee_symbol_id = es.id
            JOIN projects ep ON es.project_id = ep.id
            ORDER BY 1, 2, 3, 4, 5, 6, 7, 8, 9
            """);

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, SqliteConnection conn, string table, string sql)
    {
        sb.Append("=== ").Append(table).Append(" ===\n");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) sb.Append('\u001f'); // unit separator — cannot appear in source text
                sb.Append(reader.IsDBNull(i) ? "\u2205" : reader.GetValue(i)?.ToString() ?? "\u2205");
            }
            sb.Append('\n');
            count++;
        }
        sb.Append("(rows: ").Append(count).Append(")\n\n");
    }
}
