using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ProjectDependencyStore(SqliteConnection connection)
{
    public long Insert(ProjectDependency dependency)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO project_dependencies (consumer_project_id, dependency_project_id, reference_kind, submodule_pinned_commit)
            VALUES (@consumer_project_id, @dependency_project_id, @reference_kind, @submodule_pinned_commit)
            ON CONFLICT(consumer_project_id, dependency_project_id) DO UPDATE SET
                reference_kind = excluded.reference_kind,
                submodule_pinned_commit = excluded.submodule_pinned_commit
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@consumer_project_id", dependency.ConsumerProjectId);
        cmd.Parameters.AddWithValue("@dependency_project_id", dependency.DependencyProjectId);
        cmd.Parameters.AddWithValue("@reference_kind", dependency.ReferenceKind);
        cmd.Parameters.AddWithValue("@submodule_pinned_commit", (object?)dependency.SubmodulePinnedCommit ?? DBNull.Value);

        return (long)cmd.ExecuteScalar()!;
    }

    public List<ProjectDependency> GetByConsumer(long consumerProjectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, consumer_project_id, dependency_project_id, reference_kind, submodule_pinned_commit
            FROM project_dependencies WHERE consumer_project_id = @consumer_project_id;
            """;
        cmd.Parameters.AddWithValue("@consumer_project_id", consumerProjectId);

        return ReadDependencies(cmd);
    }

    public List<ProjectDependency> GetByDependency(long dependencyProjectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, consumer_project_id, dependency_project_id, reference_kind, submodule_pinned_commit
            FROM project_dependencies WHERE dependency_project_id = @dependency_project_id;
            """;
        cmd.Parameters.AddWithValue("@dependency_project_id", dependencyProjectId);

        return ReadDependencies(cmd);
    }

    public void DeleteByConsumer(long consumerProjectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM project_dependencies WHERE consumer_project_id = @consumer_project_id;";
        cmd.Parameters.AddWithValue("@consumer_project_id", consumerProjectId);
        cmd.ExecuteNonQuery();
    }

    private static List<ProjectDependency> ReadDependencies(SqliteCommand cmd)
    {
        var results = new List<ProjectDependency>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ProjectDependency
            {
                Id = reader.GetInt64(0),
                ConsumerProjectId = reader.GetInt64(1),
                DependencyProjectId = reader.GetInt64(2),
                ReferenceKind = reader.GetString(3),
                SubmodulePinnedCommit = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
        return results;
    }
}
