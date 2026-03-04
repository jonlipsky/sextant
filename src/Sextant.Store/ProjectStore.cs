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
