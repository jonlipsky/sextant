using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ProjectStore(SqliteConnection connection)
{
    public long Insert(ProjectIdentity project, long lastIndexedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, disk_path, assembly_name, target_framework, is_test_project, last_indexed_at)
            VALUES (@canonical_id, @git_remote_url, @repo_relative_path, @disk_path, @assembly_name, @target_framework, @is_test_project, @last_indexed_at)
            ON CONFLICT(canonical_id) DO UPDATE SET
                git_remote_url = excluded.git_remote_url,
                repo_relative_path = excluded.repo_relative_path,
                disk_path = excluded.disk_path,
                assembly_name = excluded.assembly_name,
                target_framework = excluded.target_framework,
                is_test_project = excluded.is_test_project,
                last_indexed_at = excluded.last_indexed_at
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@canonical_id", project.CanonicalId);
        cmd.Parameters.AddWithValue("@git_remote_url", project.GitRemoteUrl);
        cmd.Parameters.AddWithValue("@repo_relative_path", project.RepoRelativePath);
        cmd.Parameters.AddWithValue("@disk_path", (object?)project.DiskPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@assembly_name", (object?)project.AssemblyName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@target_framework", (object?)project.TargetFramework ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@is_test_project", project.IsTestProject ? 1 : 0);
        cmd.Parameters.AddWithValue("@last_indexed_at", lastIndexedAt);

        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Records the evaluation fingerprint (config/props/global.json/editorconfig/assets hash) for a
    /// project. Kept separate from <see cref="Insert"/> so re-registration never overwrites a stored
    /// fingerprint with a stale value; the indexer refreshes it only for projects it actually rebuilt.
    /// </summary>
    public void SetEvaluationFingerprint(long projectId, string? fingerprint)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE projects SET evaluation_fingerprint = @fp WHERE id = @id;";
        cmd.Parameters.AddWithValue("@fp", (object?)fingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads the stored evaluation fingerprint for a project (null if never recorded).</summary>
    public string? GetEvaluationFingerprint(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT evaluation_fingerprint FROM projects WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        var result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }

    /// <summary>
    /// Deletes a logical project row. Cascades (ON DELETE CASCADE) remove its symbols, references,
    /// call edges, relationships, comments, file_index rows, dependency edges, solution mappings and
    /// api-surface snapshots. Used to purge a project that was removed from the solution entirely.
    /// </summary>
    public void Delete(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM projects WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();
    }

    public (long id, ProjectIdentity project, long lastIndexedAt)? GetByCanonicalId(string canonicalId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, canonical_id, git_remote_url, repo_relative_path, disk_path, assembly_name, target_framework, is_test_project, last_indexed_at FROM projects WHERE canonical_id = @canonical_id;";
        cmd.Parameters.AddWithValue("@canonical_id", canonicalId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return (
            reader.GetInt64(0),
            new ProjectIdentity
            {
                CanonicalId = reader.GetString(1),
                GitRemoteUrl = reader.GetString(2),
                RepoRelativePath = reader.GetString(3),
                DiskPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                AssemblyName = reader.IsDBNull(5) ? null : reader.GetString(5),
                TargetFramework = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsTestProject = reader.GetInt64(7) != 0
            },
            reader.GetInt64(8)
        );
    }

    public (long id, ProjectIdentity project, long lastIndexedAt)? GetById(long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, canonical_id, git_remote_url, repo_relative_path, disk_path, assembly_name, target_framework, is_test_project, last_indexed_at FROM projects WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return (
            reader.GetInt64(0),
            new ProjectIdentity
            {
                CanonicalId = reader.GetString(1),
                GitRemoteUrl = reader.GetString(2),
                RepoRelativePath = reader.GetString(3),
                DiskPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                AssemblyName = reader.IsDBNull(5) ? null : reader.GetString(5),
                TargetFramework = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsTestProject = reader.GetInt64(7) != 0
            },
            reader.GetInt64(8)
        );
    }

    public List<(long id, ProjectIdentity project)> GetAll()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, canonical_id, git_remote_url, repo_relative_path, disk_path, assembly_name, target_framework, is_test_project FROM projects;";

        var results = new List<(long, ProjectIdentity)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt64(0),
                new ProjectIdentity
                {
                    CanonicalId = reader.GetString(1),
                    GitRemoteUrl = reader.GetString(2),
                    RepoRelativePath = reader.GetString(3),
                    DiskPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    AssemblyName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    TargetFramework = reader.IsDBNull(6) ? null : reader.GetString(6),
                    IsTestProject = reader.GetInt64(7) != 0
                }
            ));
        }
        return results;
    }
}
