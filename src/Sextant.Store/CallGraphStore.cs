using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Adapter over the unified <c>occurrences</c> table for call edges (Phase 7). A call is an occurrence
/// with a non-NULL <c>source_symbol_id</c> (the enclosing caller) and <c>target_symbol_id</c> = callee,
/// kind <see cref="ReferenceKind.Invocation"/>. It shares the table with pure references (source NULL,
/// see <see cref="ReferenceStore"/>); every call site persists BOTH rows, so the call projection here
/// and the reference projection there together reproduce the pre-Phase-7 <c>call_graph</c> +
/// <c>references</c> tables exactly.
/// </summary>
/// <remarks>
/// The public <see cref="CallGraphEdge"/> shape (absolute <c>CallSiteFile</c>) is unchanged; this class
/// maps it to/from the compact columns (integer <c>file_version_id</c>). A call occurs in the caller's
/// project, so <c>in_project_id</c> is the caller symbol's project — resolved from the caller for the
/// convenience overloads, or supplied explicitly by the orchestrator's batched persist path.
/// </remarks>
public sealed class CallGraphStore(SqliteConnection connection)
{
    public FileStore? Files { get; set; }

    private FileStore FilesOrDefault => Files ??= new FileStore(connection);

    private const string InsertSql = """
        INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags, last_indexed_at)
        VALUES (@in_project_id, @callee, @caller, @file_version_id, @line, @col, @kind, 0, @last_indexed_at)
        RETURNING id;
        """;

    // A call occurrence carries the caller (source) and callee (target); its file version reconstructs
    // the absolute CallSiteFile from the caller project's disk path. `col` records the call site's 0-based
    // column so two distinct calls on one line (e.g. `F(a); F(b);`) stay distinguishable occurrences —
    // the discriminator the unified schema reserves for same-line disambiguation.
    private const string SelectPrefix = """
        SELECT o.id AS id, o.source_symbol_id, o.target_symbol_id, o.line, o.col, o.last_indexed_at,
               f.repo_relative_path AS repo_relative_path, p.disk_path AS disk_path,
               p.repo_relative_path AS project_repo_relative
        FROM occurrences o
        JOIN file_versions fv ON fv.id = o.file_version_id
        JOIN files f ON f.id = fv.file_id
        JOIN projects p ON p.id = o.in_project_id
        """;

    public SqliteCommand CreateInsertCommand()
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = InsertSql;
        return cmd;
    }

    public long Insert(CallGraphEdge edge)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, edge);
    }

    /// <summary>Convenience overload that derives the call's project from the caller symbol.</summary>
    public long Insert(SqliteCommand cmd, CallGraphEdge edge)
        => Insert(cmd, edge, ResolveCallerProject(edge.CallerSymbolId));

    public long Insert(CallGraphEdge edge, long inProjectId)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, edge, inProjectId);
    }

    public long Insert(SqliteCommand cmd, CallGraphEdge edge, long inProjectId)
    {
        var fileVersionId = FilesOrDefault.ResolveFileVersionId(
            inProjectId, edge.CallSiteFile, contentHash: null, lastIndexedAt: edge.LastIndexedAt);
        SqlParam.Set(cmd, "@in_project_id", inProjectId);
        SqlParam.Set(cmd, "@callee", edge.CalleeSymbolId);
        SqlParam.Set(cmd, "@caller", edge.CallerSymbolId);
        SqlParam.Set(cmd, "@file_version_id", fileVersionId);
        SqlParam.Set(cmd, "@line", edge.CallSiteLine);
        SqlParam.Set(cmd, "@col", edge.CallSiteColumn);
        SqlParam.Set(cmd, "@kind", (int)ReferenceKind.Invocation);
        SqlParam.Set(cmd, "@last_indexed_at", edge.LastIndexedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<CallGraphEdge> GetByCaller(long callerSymbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE o.source_symbol_id = @caller;";
        cmd.Parameters.AddWithValue("@caller", callerSymbolId);
        return ReadAll(cmd);
    }

    public List<CallGraphEdge> GetByCallee(long calleeSymbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE o.source_symbol_id IS NOT NULL AND o.target_symbol_id = @callee;";
        cmd.Parameters.AddWithValue("@callee", calleeSymbolId);
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        var candidates = CandidateRelatives(filePath);
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", candidates.Select((_, i) => $"@rel{i}"));
        cmd.CommandText = $"""
            DELETE FROM occurrences WHERE source_symbol_id IS NOT NULL AND file_version_id IN (
                SELECT fv.id FROM files f JOIN file_versions fv ON fv.file_id = f.id
                WHERE f.repo_relative_path IN ({placeholders}));
            """;
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue($"@rel{i}", candidates[i]);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: a call occurs in the caller's project (in_project_id), so scoping on it
    // clears only the calls made from one logical (per-TFM) project's code in the shared source file,
    // leaving the sibling framework's call edges for that file intact.
    public void DeleteByFile(string filePath, long projectId)
    {
        var rel = SourcePaths.ToRepoRelative(FilesOrDefault.GetRepoRoot(projectId), filePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM occurrences WHERE source_symbol_id IS NOT NULL AND in_project_id = @project_id AND file_version_id IN (
                SELECT fv.id FROM files f JOIN file_versions fv ON fv.file_id = f.id
                WHERE f.project_id = @project_id AND f.repo_relative_path = @rel);
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@rel", rel);
        cmd.ExecuteNonQuery();
    }

    private long ResolveCallerProject(long callerSymbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM symbols WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", callerSymbolId);
        return cmd.ExecuteScalar() is long id ? id : throw new InvalidOperationException(
            $"Cannot persist a call edge: caller symbol {callerSymbolId} has no project.");
    }

    private List<string> CandidateRelatives(string path)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal) { path };
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT disk_path, repo_relative_path FROM projects;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var disk = reader.IsDBNull(0) ? null : reader.GetString(0);
            var projRel = reader.IsDBNull(1) ? null : reader.GetString(1);
            candidates.Add(SourcePaths.ToRepoRelative(SourcePaths.DeriveRepoRoot(disk, projRel), path));
        }
        return candidates.ToList();
    }

    private static List<CallGraphEdge> ReadAll(SqliteCommand cmd)
    {
        var results = new List<CallGraphEdge>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var repoRel = reader.GetString(reader.GetOrdinal("repo_relative_path"));
            var diskOrdinal = reader.GetOrdinal("disk_path");
            var projRelOrdinal = reader.GetOrdinal("project_repo_relative");
            var disk = reader.IsDBNull(diskOrdinal) ? null : reader.GetString(diskOrdinal);
            var projRel = reader.IsDBNull(projRelOrdinal) ? null : reader.GetString(projRelOrdinal);
            var callSiteFile = SourcePaths.ToAbsolute(SourcePaths.DeriveRepoRoot(disk, projRel), repoRel);

            results.Add(new CallGraphEdge
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                CallerSymbolId = reader.GetInt64(reader.GetOrdinal("source_symbol_id")),
                CalleeSymbolId = reader.GetInt64(reader.GetOrdinal("target_symbol_id")),
                CallSiteFile = callSiteFile,
                CallSiteLine = reader.GetInt32(reader.GetOrdinal("line")),
                CallSiteColumn = reader.GetInt32(reader.GetOrdinal("col")),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
