using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Adapter over the unified <c>occurrences</c> table for pure references (Phase 7). A reference is an
/// occurrence with a NULL <c>source_symbol_id</c>: it locates a usage of a target declaration
/// (<c>target_symbol_id</c>) in a file version, without an enclosing caller. Calls persist a second
/// occurrence row with the caller set (see <see cref="CallGraphStore"/>); find-references collapses the
/// two at read time so the result set matches the pre-Phase-7 <c>references</c> table.
/// </summary>
/// <remarks>
/// The public <see cref="ReferenceInfo"/> shape (absolute <c>FilePath</c>, enum kind/access, snippet)
/// is unchanged so MCP tools and tests keep working; this class maps it to/from the compact columns
/// (integer <c>file_version_id</c>, integer kind, packed access flags). Snippets are no longer stored —
/// <see cref="ReferenceInfo.ContextSnippet"/> reads back null and callers reproduce context at query
/// time from the exact source version.
/// </remarks>
public sealed class ReferenceStore(SqliteConnection connection)
{
    public FileStore? Files { get; set; }

    private FileStore FilesOrDefault => Files ??= new FileStore(connection);

    private const string InsertSql = """
        INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags, last_indexed_at)
        VALUES (@in_project_id, @target_symbol_id, NULL, @file_version_id, @line, 0, @kind, @flags, @last_indexed_at)
        RETURNING id;
        """;

    // Reconstructs the absolute FilePath from the usage file version and its owning project's disk path.
    // A "reference" is an occurrence with a NULL source symbol (see class remarks): filtering on that
    // makes this store's projection byte-for-byte the pre-Phase-7 references table, since the extractor
    // emits one source-NULL reference row per usage (including call sites) alongside each call edge.
    private const string SelectPrefix = """
        SELECT o.id AS id, o.target_symbol_id, o.in_project_id, o.file_version_id, o.line, o.kind, o.flags,
               o.last_indexed_at AS last_indexed_at,
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

    public long Insert(ReferenceInfo reference)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, reference);
    }

    public long Insert(SqliteCommand cmd, ReferenceInfo reference)
    {
        var fileVersionId = FilesOrDefault.ResolveFileVersionId(
            reference.InProjectId, reference.FilePath, contentHash: null, lastIndexedAt: reference.LastIndexedAt);
        SqlParam.Set(cmd, "@in_project_id", reference.InProjectId);
        SqlParam.Set(cmd, "@target_symbol_id", reference.SymbolId);
        SqlParam.Set(cmd, "@file_version_id", fileVersionId);
        SqlParam.Set(cmd, "@line", reference.Line);
        SqlParam.Set(cmd, "@kind", (int)reference.ReferenceKind);
        SqlParam.Set(cmd, "@flags", AccessToFlags(reference.AccessKind));
        SqlParam.Set(cmd, "@last_indexed_at", reference.LastIndexedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<ReferenceInfo> GetBySymbolId(long symbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE o.target_symbol_id = @symbol_id AND o.source_symbol_id IS NULL;";
        cmd.Parameters.AddWithValue("@symbol_id", symbolId);
        return ReadAll(cmd);
    }

    public List<ReferenceInfo> GetByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE o.in_project_id = @project_id AND o.source_symbol_id IS NULL;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        return ReadAll(cmd);
    }

    /// <summary>
    /// Returns the distinct (consumerProjectId, dependencyProjectId) pairs implied by the persisted
    /// cross-project references: a reference row whose usage site (in_project_id) differs from the
    /// project that owns the referenced symbol (symbols.project_id). Incremental reindexing unions
    /// these OLD edges into the invalidation closure so that after a removed project reference (or a
    /// deleted cross-project usage) the previously-referenced project is reprocessed too, letting its
    /// reference extraction rebuild away the now-stale inbound rows its symbols still own.
    /// </summary>
    public List<(long ConsumerProjectId, long DependencyProjectId)> GetCrossProjectPairs()
    {
        var pairs = new List<(long, long)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT o.in_project_id AS consumer, s.project_id AS dependency
            FROM occurrences o
            JOIN symbols s ON s.id = o.target_symbol_id
            WHERE o.source_symbol_id IS NULL AND o.in_project_id != s.project_id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            pairs.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return pairs;
    }

    public void DeleteByFile(string filePath)
    {
        var candidates = CandidateRelatives(filePath);
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", candidates.Select((_, i) => $"@rel{i}"));
        cmd.CommandText = $"""
            DELETE FROM occurrences WHERE source_symbol_id IS NULL AND file_version_id IN (
                SELECT fv.id FROM files f JOIN file_versions fv ON fv.file_id = f.id
                WHERE f.repo_relative_path IN ({placeholders}));
            """;
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue($"@rel{i}", candidates[i]);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: occurrences carry the project they occur in (in_project_id), so clearing
    // one logical (per-TFM) project's references for a shared source file leaves the sibling
    // framework's references for that same file intact.
    public void DeleteByFile(string filePath, long projectId)
    {
        var rel = SourcePaths.ToRepoRelative(FilesOrDefault.GetRepoRoot(projectId), filePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM occurrences WHERE source_symbol_id IS NULL AND in_project_id = @project_id AND file_version_id IN (
                SELECT fv.id FROM files f JOIN file_versions fv ON fv.file_id = f.id
                WHERE f.project_id = @project_id AND f.repo_relative_path = @rel);
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@rel", rel);
        cmd.ExecuteNonQuery();
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

    private static List<ReferenceInfo> ReadAll(SqliteCommand cmd)
    {
        var results = new List<ReferenceInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var repoRel = reader.GetString(reader.GetOrdinal("repo_relative_path"));
            var diskOrdinal = reader.GetOrdinal("disk_path");
            var projRelOrdinal = reader.GetOrdinal("project_repo_relative");
            var disk = reader.IsDBNull(diskOrdinal) ? null : reader.GetString(diskOrdinal);
            var projRel = reader.IsDBNull(projRelOrdinal) ? null : reader.GetString(projRelOrdinal);
            var filePath = SourcePaths.ToAbsolute(SourcePaths.DeriveRepoRoot(disk, projRel), repoRel);

            var flags = reader.GetInt64(reader.GetOrdinal("flags"));
            results.Add(new ReferenceInfo
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SymbolId = reader.GetInt64(reader.GetOrdinal("target_symbol_id")),
                InProjectId = reader.GetInt64(reader.GetOrdinal("in_project_id")),
                FilePath = filePath,
                Line = reader.GetInt32(reader.GetOrdinal("line")),
                ContextSnippet = null,
                ReferenceKind = (ReferenceKind)reader.GetInt64(reader.GetOrdinal("kind")),
                AccessKind = FlagsToAccess(flags),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }

    // Access is packed into occurrence flag bits 0-1: 0 = none, 1 = read, 2 = write, 3 = read/write.
    internal static int AccessToFlags(AccessKind? access) => access switch
    {
        null => 0,
        AccessKind.Read => 1,
        AccessKind.Write => 2,
        AccessKind.ReadWrite => 3,
        _ => 0
    };

    internal static AccessKind? FlagsToAccess(long flags) => (flags & 0b11) switch
    {
        1 => AccessKind.Read,
        2 => AccessKind.Write,
        3 => AccessKind.ReadWrite,
        _ => null
    };
}
