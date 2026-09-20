using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Read/write adapter presenting the daemon/incremental fingerprint view (a project's per-file content
/// hash) over the Phase-7 <c>files</c> + <c>file_versions</c> tables, which REPLACE the old
/// <c>file_index</c> table. The public <see cref="FileIndexEntry"/> shape (absolute <c>FilePath</c>,
/// hex <c>ContentHash</c>) is unchanged so the daemon's "unchanged since last index" comparison keeps
/// working: the stored hash is a raw SHA-256 BLOB and this adapter surfaces it as a lowercase hex
/// string, matching <see cref="Sextant.Indexer"/>'s on-disk hash format.
/// </summary>
public sealed class FileIndexStore(SqliteConnection connection)
{
    private FileStore? _files;

    private FileStore Files => _files ??= new FileStore(connection);

    /// <summary>
    /// Records (or updates) the current content fingerprint for a project file, maintaining a single
    /// current <c>file_versions</c> row per file. The incoming <see cref="FileIndexEntry.ContentHash"/>
    /// is a hex string; it is stored as the raw binary hash.
    /// </summary>
    public long Upsert(FileIndexEntry entry)
    {
        var repoRoot = Files.GetRepoRoot(entry.ProjectId);
        var repoRelative = SourcePaths.ToRepoRelative(repoRoot, entry.FilePath);
        var fileId = UpsertFile(entry.ProjectId, repoRelative);
        var raw = Convert.FromHexString(entry.ContentHash);

        // Fingerprint semantics are one current version per file, so replace any prior version rather
        // than accumulating history (which the versioned schema would otherwise allow on a hash change).
        using var del = connection.CreateCommand();
        del.CommandText = "DELETE FROM file_versions WHERE file_id = @file_id;";
        del.Parameters.AddWithValue("@file_id", fileId);
        del.ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_versions (file_id, content_hash, last_indexed_at)
            VALUES (@file_id, @hash, @last_indexed_at)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@file_id", fileId);
        cmd.Parameters.AddWithValue("@hash", raw);
        cmd.Parameters.AddWithValue("@last_indexed_at", entry.LastIndexedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    public FileIndexEntry? GetByProjectAndFile(long projectId, string filePath)
    {
        var repoRoot = Files.GetRepoRoot(projectId);
        var repoRelative = SourcePaths.ToRepoRelative(repoRoot, filePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fv.id, f.repo_relative_path, fv.content_hash, fv.last_indexed_at
            FROM files f JOIN file_versions fv ON fv.file_id = f.id
            WHERE f.project_id = @project_id AND f.repo_relative_path = @path
            ORDER BY fv.id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@path", repoRelative);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEntry(reader, projectId, repoRoot) : null;
    }

    public List<FileIndexEntry> GetByProject(long projectId)
    {
        var repoRoot = Files.GetRepoRoot(projectId);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fv.id, f.repo_relative_path, fv.content_hash, fv.last_indexed_at
            FROM files f
            JOIN file_versions fv ON fv.id = (
                SELECT id FROM file_versions WHERE file_id = f.id ORDER BY id DESC LIMIT 1)
            WHERE f.project_id = @project_id;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        var results = new List<FileIndexEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadEntry(reader, projectId, repoRoot));
        return results;
    }

    /// <summary>
    /// Returns the distinct logical-project ids that have an indexed row for the given file path.
    /// Used by the incremental path to map a deleted file (no longer on disk, so no Roslyn document)
    /// back to the per-TFM projects that must be rebuilt to purge its stale contributions.
    /// </summary>
    public List<long> GetProjectIdsByFile(string filePath)
    {
        var candidates = CandidateRelatives(filePath);
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", candidates.Select((_, i) => $"@rel{i}"));
        cmd.CommandText = $"SELECT DISTINCT project_id FROM files WHERE repo_relative_path IN ({placeholders});";
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue($"@rel{i}", candidates[i]);
        var results = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetInt64(0));
        return results;
    }

    public void DeleteByProjectAndFile(long projectId, string filePath)
    {
        Files.DeleteFile(projectId, filePath);
    }

    public void DeleteByProject(long projectId)
    {
        Files.DeleteByProject(projectId);
    }

    private long UpsertFile(long projectId, string repoRelative)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (project_id, repo_relative_path)
            VALUES (@project_id, @path)
            ON CONFLICT(project_id, repo_relative_path) DO UPDATE SET repo_relative_path = excluded.repo_relative_path
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@path", repoRelative);
        return (long)cmd.ExecuteScalar()!;
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

    private static FileIndexEntry ReadEntry(SqliteDataReader reader, long projectId, string? repoRoot)
    {
        var repoRelative = reader.GetString(1);
        return new FileIndexEntry
        {
            Id = reader.GetInt64(0),
            ProjectId = projectId,
            FilePath = SourcePaths.ToAbsolute(repoRoot, repoRelative),
            ContentHash = Convert.ToHexStringLower((byte[])reader[2]),
            LastIndexedAt = reader.GetInt64(3)
        };
    }
}
