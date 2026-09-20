using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Stores structured source comments (TODO/FIXME/etc). Phase 7: a comment's file identity is the
/// integer <c>file_version_id</c> (no repeated absolute path); the public <see cref="CommentInfo.FilePath"/>
/// is reconstructed at read time from the file version and its project's disk path.
/// </summary>
public sealed class CommentStore(SqliteConnection connection)
{
    public FileStore? Files { get; set; }

    private FileStore FilesOrDefault => Files ??= new FileStore(connection);

    private const string InsertSql = """
        INSERT INTO comments (project_id, file_version_id, line, tag, text, enclosing_symbol_id, last_indexed_at)
        VALUES (@project_id, @file_version_id, @line, @tag, @text, @enclosing_symbol_id, @last_indexed_at)
        RETURNING id;
        """;

    private const string SelectPrefix = """
        SELECT c.id, c.project_id, c.line, c.tag, c.text, c.enclosing_symbol_id, c.last_indexed_at,
               f.repo_relative_path AS repo_relative_path, p.disk_path AS disk_path,
               p.repo_relative_path AS project_repo_relative
        FROM comments c
        LEFT JOIN file_versions fv ON fv.id = c.file_version_id
        LEFT JOIN files f ON f.id = fv.file_id
        JOIN projects p ON p.id = c.project_id
        """;

    public SqliteCommand CreateInsertCommand()
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = InsertSql;
        return cmd;
    }

    public long Insert(long projectId, string filePath, int line, string tag,
                       string text, long? enclosingSymbolId, long lastIndexedAt)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, projectId, filePath, line, tag, text, enclosingSymbolId, lastIndexedAt);
    }

    public long Insert(SqliteCommand cmd, long projectId, string filePath, int line, string tag,
                       string text, long? enclosingSymbolId, long lastIndexedAt)
    {
        var fileVersionId = FilesOrDefault.ResolveFileVersionId(projectId, filePath, contentHash: null, lastIndexedAt: lastIndexedAt);
        SqlParam.Set(cmd, "@project_id", projectId);
        SqlParam.Set(cmd, "@file_version_id", fileVersionId);
        SqlParam.Set(cmd, "@line", line);
        SqlParam.Set(cmd, "@tag", tag);
        SqlParam.Set(cmd, "@text", text);
        SqlParam.Set(cmd, "@enclosing_symbol_id", enclosingSymbolId.HasValue ? enclosingSymbolId.Value : (object?)null);
        SqlParam.Set(cmd, "@last_indexed_at", lastIndexedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<CommentInfo> GetByTag(string tag, long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        var projectClause = projectId.HasValue ? " AND c.project_id = @projectId" : "";
        cmd.CommandText = SelectPrefix + $" WHERE c.tag = @tag{projectClause} ORDER BY repo_relative_path, c.line;";
        cmd.Parameters.AddWithValue("@tag", tag);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        return ReadAll(cmd);
    }

    public List<CommentInfo> GetAll(long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        if (projectId.HasValue)
        {
            cmd.CommandText = SelectPrefix + " WHERE c.project_id = @projectId ORDER BY repo_relative_path, c.line;";
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        }
        else
        {
            cmd.CommandText = SelectPrefix + " ORDER BY repo_relative_path, c.line;";
        }
        return ReadAll(cmd);
    }

    public List<CommentInfo> GetByFile(string filePath)
    {
        var candidates = CandidateRelatives(filePath);
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", candidates.Select((_, i) => $"@rel{i}"));
        cmd.CommandText = SelectPrefix + $" WHERE f.repo_relative_path IN ({placeholders}) ORDER BY c.line;";
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue($"@rel{i}", candidates[i]);
        return ReadAll(cmd);
    }

    public List<CommentInfo> GetBySymbol(long symbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE c.enclosing_symbol_id = @symbolId ORDER BY c.line;";
        cmd.Parameters.AddWithValue("@symbolId", symbolId);
        return ReadAll(cmd);
    }

    public List<CommentInfo> Search(string query, long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        var projectClause = projectId.HasValue ? " AND c.project_id = @projectId" : "";
        cmd.CommandText = SelectPrefix + $" WHERE c.text LIKE '%' || @query || '%'{projectClause} ORDER BY repo_relative_path, c.line;";
        cmd.Parameters.AddWithValue("@query", query);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        var candidates = CandidateRelatives(filePath);
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", candidates.Select((_, i) => $"@rel{i}"));
        cmd.CommandText = $"""
            DELETE FROM comments WHERE file_version_id IN (
                SELECT fv.id FROM files f JOIN file_versions fv ON fv.file_id = f.id
                WHERE f.repo_relative_path IN ({placeholders}));
            """;
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue($"@rel{i}", candidates[i]);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: comments carry their owning project, so clearing one logical (per-TFM)
    // project's comments for a shared source file leaves the sibling framework's comments intact.
    public void DeleteByFile(string filePath, long projectId)
    {
        var rel = SourcePaths.ToRepoRelative(FilesOrDefault.GetRepoRoot(projectId), filePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM comments WHERE project_id = @projectId AND file_version_id IN (
                SELECT fv.id FROM files f JOIN file_versions fv ON fv.file_id = f.id
                WHERE f.project_id = @projectId AND f.repo_relative_path = @rel);
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@rel", rel);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: clears every comment owned by one logical (per-TFM) project, used by the
    // project-version replacement path so a rebuild removes comments for files that no longer exist
    // (deleted/renamed) as well as the current ones. Comments are cascade-deleted when their file
    // version is removed (fileStore.DeleteByProject), but the project reset also clears them explicitly
    // so any comment in a file that lost all versions is removed even without a file rebuild.
    public void DeleteByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM comments WHERE project_id = @projectId;";
        cmd.Parameters.AddWithValue("@projectId", projectId);
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

    private static List<CommentInfo> ReadAll(SqliteCommand cmd)
    {
        var results = new List<CommentInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var enclosingOrdinal = reader.GetOrdinal("enclosing_symbol_id");
            var repoRelOrdinal = reader.GetOrdinal("repo_relative_path");
            var repoRel = reader.IsDBNull(repoRelOrdinal) ? null : reader.GetString(repoRelOrdinal);
            var diskOrdinal = reader.GetOrdinal("disk_path");
            var projRelOrdinal = reader.GetOrdinal("project_repo_relative");
            var disk = reader.IsDBNull(diskOrdinal) ? null : reader.GetString(diskOrdinal);
            var projRel = reader.IsDBNull(projRelOrdinal) ? null : reader.GetString(projRelOrdinal);
            var filePath = repoRel is null ? "" : SourcePaths.ToAbsolute(SourcePaths.DeriveRepoRoot(disk, projRel), repoRel);

            results.Add(new CommentInfo
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ProjectId = reader.GetInt64(reader.GetOrdinal("project_id")),
                FilePath = filePath,
                Line = reader.GetInt32(reader.GetOrdinal("line")),
                Tag = reader.GetString(reader.GetOrdinal("tag")),
                Text = reader.GetString(reader.GetOrdinal("text")),
                EnclosingSymbolId = reader.IsDBNull(enclosingOrdinal) ? null : reader.GetInt64(enclosingOrdinal),
                LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
            });
        }
        return results;
    }
}
