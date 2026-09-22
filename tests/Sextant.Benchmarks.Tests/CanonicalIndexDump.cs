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
/// <remarks>
/// Phase 7 normalized source identity: a file's path lives once in <c>files</c>, its content identity
/// in <c>file_versions</c>, and both <c>references</c> and <c>call_graph</c> collapsed into the unified
/// <c>occurrences</c> table (a pure reference has <c>source_symbol_id IS NULL</c>; a call edge carries
/// the enclosing caller in <c>source_symbol_id</c>). This dump reconstructs each usage's repository-
/// relative path from its <c>file_version_id</c> so the canonical projection stays path-stable across
/// the two code paths without ever storing an absolute path.
/// </remarks>
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
                   COALESCE(f.repo_relative_path,''), s.line_start, s.line_end, COALESCE(s.attributes,'')
            FROM symbols s
            JOIN projects p ON s.project_id = p.id
            LEFT JOIN file_versions fv ON fv.id = s.file_version_id
            LEFT JOIN files f ON f.id = fv.file_id
            ORDER BY 1, 2, 14, 15
            """);

        // Pure references = occurrences with no enclosing source symbol. Mirrors the old `references`
        // table one-for-one (kind is now the integer ordinal; the stored snippet is gone — dropped
        // from the projection since it is no longer persisted).
        Section(sb, conn, "references", """
            SELECT tp.canonical_id, ts.symbol_key, ip.canonical_id,
                   COALESCE(f.repo_relative_path,''), o.line, o.kind
            FROM occurrences o
            JOIN symbols ts ON o.target_symbol_id = ts.id
            JOIN projects tp ON ts.project_id = tp.id
            JOIN projects ip ON o.in_project_id = ip.id
            LEFT JOIN file_versions fv ON fv.id = o.file_version_id
            LEFT JOIN files f ON f.id = fv.file_id
            WHERE o.source_symbol_id IS NULL
            ORDER BY 1, 2, 3, 4, 5, 6
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

        // Call edges = occurrences with an enclosing source symbol (the caller). callee = target.
        Section(sb, conn, "call_graph", """
            SELECT cp.canonical_id, cs.symbol_key, ep.canonical_id, es.symbol_key,
                   COALESCE(f.repo_relative_path,''), o.line
            FROM occurrences o
            JOIN symbols cs ON o.source_symbol_id = cs.id
            JOIN projects cp ON cs.project_id = cp.id
            JOIN symbols es ON o.target_symbol_id = es.id
            JOIN projects ep ON es.project_id = ep.id
            LEFT JOIN file_versions fv ON fv.id = o.file_version_id
            LEFT JOIN files f ON f.id = fv.file_id
            WHERE o.source_symbol_id IS NOT NULL
            ORDER BY 1, 2, 3, 4, 5, 6
            """);

        Section(sb, conn, "comments", """
            SELECT p.canonical_id, COALESCE(f.repo_relative_path,''), c.line, c.tag, c.text,
                   COALESCE(es.symbol_key,'')
            FROM comments c
            JOIN projects p ON c.project_id = p.id
            LEFT JOIN file_versions fv ON fv.id = c.file_version_id
            LEFT JOIN files f ON f.id = fv.file_id
            LEFT JOIN symbols es ON c.enclosing_symbol_id = es.id
            ORDER BY 1, 2, 3, 4, 5, 6
            """);

        // Files + their content identity replace the old file_index fingerprint table.
        Section(sb, conn, "files", """
            SELECT p.canonical_id, f.repo_relative_path, hex(fv.content_hash)
            FROM files f
            JOIN file_versions fv ON fv.file_id = f.id
            JOIN projects p ON f.project_id = p.id
            ORDER BY 1, 2, 3
            """);

        Section(sb, conn, "argument_flow", """
            SELECT cp.canonical_id, cs.symbol_key, ep.canonical_id, es.symbol_key,
                   COALESCE(f.repo_relative_path,''), o.line,
                   af.parameter_ordinal, af.parameter_name, af.argument_expression,
                   af.argument_kind, COALESCE(af.source_symbol_fqn,'')
            FROM argument_flow af
            JOIN occurrences o ON af.occurrence_id = o.id
            JOIN symbols cs ON o.source_symbol_id = cs.id
            JOIN projects cp ON cs.project_id = cp.id
            JOIN symbols es ON o.target_symbol_id = es.id
            JOIN projects ep ON es.project_id = ep.id
            LEFT JOIN file_versions fv ON fv.id = o.file_version_id
            LEFT JOIN files f ON f.id = fv.file_id
            ORDER BY 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11
            """);

        Section(sb, conn, "return_flow", """
            SELECT cp.canonical_id, cs.symbol_key, ep.canonical_id, es.symbol_key,
                   COALESCE(f.repo_relative_path,''), o.line,
                   rf.destination_kind, COALESCE(rf.destination_variable,''),
                   COALESCE(rf.destination_symbol_fqn,'')
            FROM return_flow rf
            JOIN occurrences o ON rf.occurrence_id = o.id
            JOIN symbols cs ON o.source_symbol_id = cs.id
            JOIN projects cp ON cs.project_id = cp.id
            JOIN symbols es ON o.target_symbol_id = es.id
            JOIN projects ep ON es.project_id = ep.id
            LEFT JOIN file_versions fv ON fv.id = o.file_version_id
            LEFT JOIN files f ON f.id = fv.file_id
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
