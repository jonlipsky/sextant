using System.Security.Cryptography;
using System.Text;
using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Owns the normalized <c>files</c> and <c>file_versions</c> tables introduced in Phase 7. A source
/// file's repository-relative path is stored ONCE per (project, path) in <c>files</c>; its content
/// identity (a raw SHA-256 <see cref="byte"/>[] plus optional git-blob hash / source ref) lives in
/// <c>file_versions</c>. Symbols, occurrences and comments carry an integer <c>file_version_id</c>
/// instead of repeating an absolute path.
/// </summary>
/// <remarks>
/// The store resolves an absolute path reported by Roslyn to a <c>file_version_id</c>, upserting the
/// backing rows, and caches (project, path) → id so the many occurrences in one file resolve without
/// re-querying. Resolution is invoked ONLY from the single-threaded symbol phase and the single
/// occurrence-persistence consumer (never the parallel extraction workers), so row-id assignment
/// stays deterministic (Phase 6). The per-project repo root, derived once from the project's stored
/// disk path, drives both write-time relativization and query-time absolute-path reconstruction.
/// </remarks>
public sealed class FileStore(SqliteConnection connection)
{
    private readonly Dictionary<long, string?> _repoRootByProject = new();
    private readonly Dictionary<(long ProjectId, string Path), long> _fileVersionCache = new();

    /// <summary>
    /// Resolves the file-version id for a source path within a project, creating the <c>files</c> and
    /// <c>file_versions</c> rows on first encounter. <paramref name="contentHash"/> is the raw SHA-256
    /// of the indexed file; when null it is computed from disk (or a stable path-derived placeholder
    /// when the file is absent, e.g. synthetic test rows) so the query-time snippet gate can compare
    /// hashes. The result is cached for the run.
    /// </summary>
    public long ResolveFileVersionId(long projectId, string path, byte[]? contentHash = null, long lastIndexedAt = 0)
    {
        var repoRoot = GetRepoRoot(projectId);
        var repoRelative = SourcePaths.ToRepoRelative(repoRoot, path);

        var cacheKey = (projectId, repoRelative);
        if (_fileVersionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Prefer an existing version created earlier in this run (e.g. the symbol phase seeds file
        // versions before the occurrence-persistence phase resolves the same files). Reusing it keeps
        // row-id assignment single-sourced and avoids re-hashing the file on disk. The run resets a
        // project's files before rebuilding, so a found row is always the current generation's.
        var existing = FindExistingFileVersion(projectId, repoRelative);
        if (existing.HasValue)
        {
            _fileVersionCache[cacheKey] = existing.Value;
            return existing.Value;
        }

        var hash = contentHash ?? ComputeContentHash(repoRoot, repoRelative, path);
        var fileId = UpsertFile(projectId, repoRelative);
        var fileVersionId = UpsertFileVersion(fileId, hash, lastIndexedAt);
        _fileVersionCache[cacheKey] = fileVersionId;
        return fileVersionId;
    }

    private long? FindExistingFileVersion(long projectId, string repoRelative)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fv.id
            FROM files f JOIN file_versions fv ON fv.file_id = f.id
            WHERE f.project_id = @project_id AND f.repo_relative_path = @path
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@path", repoRelative);
        var result = cmd.ExecuteScalar();
        return result is long id ? id : null;
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

    private long UpsertFileVersion(long fileId, byte[] contentHash, long lastIndexedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_versions (file_id, content_hash, last_indexed_at)
            VALUES (@file_id, @hash, @last_indexed_at)
            ON CONFLICT(file_id, content_hash) DO UPDATE SET last_indexed_at = excluded.last_indexed_at
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@file_id", fileId);
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@last_indexed_at", lastIndexedAt);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Deletes a project's <c>files</c> rows. Cascades remove the project's <c>file_versions</c> and,
    /// through them, every occurrence and comment located in the project's files, and null out the
    /// <c>file_version_id</c> of any surviving symbol. Mirrors the TFM-scoped replacement of the other
    /// stores so re-indexing one logical project reproduces exactly its own file rows.
    /// </summary>
    public void DeleteByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE project_id = @project_id;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.ExecuteNonQuery();

        foreach (var key in _fileVersionCache.Keys.Where(k => k.ProjectId == projectId).ToList())
            _fileVersionCache.Remove(key);
    }

    /// <summary>Per-file content hashes for a project, keyed by reconstructed absolute path.</summary>
    public List<(string filePath, byte[] contentHash)> GetProjectFileHashes(long projectId)
    {
        var repoRoot = GetRepoRoot(projectId);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.repo_relative_path, fv.content_hash
            FROM files f JOIN file_versions fv ON fv.file_id = f.id
            WHERE f.project_id = @project_id;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        var results = new List<(string, byte[])>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var abs = SourcePaths.ToAbsolute(repoRoot, reader.GetString(0));
            results.Add((abs, (byte[])reader[1]));
        }
        return results;
    }

    /// <summary>Deletes a single file (and its versions) within a project by absolute or relative path.</summary>
    public void DeleteFile(long projectId, string path)
    {
        var repoRoot = GetRepoRoot(projectId);
        var repoRelative = SourcePaths.ToRepoRelative(repoRoot, path);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE project_id = @project_id AND repo_relative_path = @path;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@path", repoRelative);
        cmd.ExecuteNonQuery();
        _fileVersionCache.Remove((projectId, repoRelative));
    }

    /// <summary>The absolute source path reconstruction root for a project (cached), or null when unknown.</summary>
    public string? GetRepoRoot(long projectId)
    {
        if (_repoRootByProject.TryGetValue(projectId, out var cached))
            return cached;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT disk_path, repo_relative_path FROM projects WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        string? diskPath = null, projectRel = null;
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                diskPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                projectRel = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        var root = SourcePaths.DeriveRepoRoot(diskPath, projectRel);
        _repoRootByProject[projectId] = root;
        return root;
    }

    /// <summary>Raw SHA-256 of the on-disk file, or a stable path-derived placeholder when absent.</summary>
    public byte[] HashForPath(long projectId, string path)
    {
        var repoRoot = GetRepoRoot(projectId);
        var repoRelative = SourcePaths.ToRepoRelative(repoRoot, path);
        return ComputeContentHash(repoRoot, repoRelative, path);
    }

    /// <summary>
    /// The reconstructed absolute path and stored content hash for a file version, or null if it no
    /// longer exists. Used by query-time source-context retrieval to gate snippet reproduction on an
    /// exact content-hash match.
    /// </summary>
    public (string absolutePath, byte[] contentHash)? GetFileVersion(long fileVersionId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.project_id, f.repo_relative_path, fv.content_hash
            FROM file_versions fv JOIN files f ON f.id = fv.file_id
            WHERE fv.id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", fileVersionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var projectId = reader.GetInt64(0);
        var repoRelative = reader.GetString(1);
        var hash = (byte[])reader[2];
        var abs = SourcePaths.ToAbsolute(GetRepoRoot(projectId), repoRelative);
        return (abs, hash);
    }

    /// <summary>
    /// The stored content hash for a project source path (latest version), or null when the file is
    /// unknown. Query-time snippet reproduction gates on this: it reads the local file only when its
    /// SHA-256 matches the stored hash, so a snippet is never synthesized from drifted source.
    /// </summary>
    public byte[]? TryGetStoredContentHash(long projectId, string path)
    {
        var repoRoot = GetRepoRoot(projectId);
        var repoRelative = SourcePaths.ToRepoRelative(repoRoot, path);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fv.content_hash
            FROM files f JOIN file_versions fv ON fv.file_id = f.id
            WHERE f.project_id = @project_id AND f.repo_relative_path = @path
            ORDER BY fv.id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@path", repoRelative);
        return cmd.ExecuteScalar() is byte[] hash ? hash : null;
    }

    /// <summary>Raw SHA-256 of the on-disk file, or a stable path-derived placeholder when absent.</summary>
    public static byte[] ComputeContentHash(string? repoRoot, string repoRelative, string originalPath)
    {
        var absolute = SourcePaths.ToAbsolute(repoRoot, originalPath is { Length: > 0 } ? originalPath : repoRelative);
        try
        {
            if (File.Exists(absolute))
                return SHA256.HashData(File.ReadAllBytes(absolute));
        }
        catch { /* fall through to placeholder */ }

        // No readable source: a deterministic placeholder that will never match real file content, so
        // the query-time snippet gate correctly declines to synthesize a snippet.
        return SHA256.HashData(Encoding.UTF8.GetBytes("\0missing\0" + repoRelative));
    }
}
