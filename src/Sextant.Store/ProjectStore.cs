using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ProjectStore(SqliteConnection connection)
{
    private SnapshotStore? _snapshots;
    private SnapshotStore Snapshots => _snapshots ??= new SnapshotStore(connection);

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
    /// Inserts (or refreshes, within the same run) the immutable project-version row for a snapshot.
    /// Every snapshot gets its OWN project rows: the row's stored <c>canonical_id</c> is suffixed with the
    /// snapshot id ("{logicalHash}:{snapshotId}") so the inline global UNIQUE(canonical_id) still holds
    /// while distinct snapshots' rows coexist (criterion 1), and its commit-invariant identity lives in
    /// <paramref name="logicalProjectId"/>. Re-registration of the same (snapshot, logical project)
    /// during one run refreshes the existing row rather than creating a duplicate. The suffix never
    /// leaks to clients — read methods surface the logical canonical id via <see cref="Snapshots"/>.
    /// </summary>
    public long UpsertSnapshotProject(ProjectIdentity project, long snapshotId, long logicalProjectId, long lastIndexedAt)
    {
        var existingId = FindSnapshotProjectRow(snapshotId, logicalProjectId);
        if (existingId is long id)
        {
            using var upd = connection.CreateCommand();
            upd.CommandText = """
                UPDATE projects SET
                    git_remote_url = @git_remote_url, repo_relative_path = @repo_relative_path,
                    disk_path = @disk_path, assembly_name = @assembly_name,
                    target_framework = @target_framework, is_test_project = @is_test_project,
                    last_indexed_at = @last_indexed_at, logical_project_id = @logical, snapshot_id = @snapshot
                WHERE id = @id;
                """;
            BindIdentity(upd, project, lastIndexedAt);
            upd.Parameters.AddWithValue("@logical", logicalProjectId);
            upd.Parameters.AddWithValue("@snapshot", snapshotId);
            upd.Parameters.AddWithValue("@id", id);
            upd.ExecuteNonQuery();
            return id;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, disk_path, assembly_name,
                                  target_framework, is_test_project, last_indexed_at, logical_project_id, snapshot_id)
            VALUES (@canonical_id, @git_remote_url, @repo_relative_path, @disk_path, @assembly_name,
                    @target_framework, @is_test_project, @last_indexed_at, @logical, @snapshot)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@canonical_id", $"{project.CanonicalId}:{snapshotId}");
        BindIdentity(cmd, project, lastIndexedAt);
        cmd.Parameters.AddWithValue("@logical", logicalProjectId);
        cmd.Parameters.AddWithValue("@snapshot", snapshotId);
        return (long)cmd.ExecuteScalar()!;
    }

    private long? FindSnapshotProjectRow(long snapshotId, long logicalProjectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM projects WHERE snapshot_id = @snapshot AND logical_project_id = @logical;";
        cmd.Parameters.AddWithValue("@snapshot", snapshotId);
        cmd.Parameters.AddWithValue("@logical", logicalProjectId);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>
    /// The existing project-version row for a (snapshot, logical project), or null. Used by the
    /// Phase-10 overlay path to SHARE a base snapshot's unchanged project-version: rather than
    /// re-extracting an out-of-closure project, the overlay maps the base's existing row into its own
    /// <c>snapshot_projects</c>. Because the base row is never deleted or updated, the base snapshot
    /// stays byte-identical (issue #44).
    /// </summary>
    public long? GetSnapshotProjectRow(long snapshotId, long logicalProjectId)
        => FindSnapshotProjectRow(snapshotId, logicalProjectId);

    private static void BindIdentity(SqliteCommand cmd, ProjectIdentity project, long lastIndexedAt)
    {
        cmd.Parameters.AddWithValue("@git_remote_url", project.GitRemoteUrl);
        cmd.Parameters.AddWithValue("@repo_relative_path", project.RepoRelativePath);
        cmd.Parameters.AddWithValue("@disk_path", (object?)project.DiskPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@assembly_name", (object?)project.AssemblyName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@target_framework", (object?)project.TargetFramework ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@is_test_project", project.IsTestProject ? 1 : 0);
        cmd.Parameters.AddWithValue("@last_indexed_at", lastIndexedAt);
    }
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
        var selected = Snapshots.GetSelectedSnapshotId();
        using var cmd = connection.CreateCommand();
        if (selected is long snap)
        {
            // A snapshot is selected: resolve the working row for this LOGICAL identity within it via
            // its snapshot_projects membership — the SAME authoritative scope every other store uses
            // (SnapshotReadScope). Resolving through membership (not the physical projects.snapshot_id
            // column) is required for Phase-10 overlays: an overlay SHARES a base snapshot's unchanged
            // project-version row, so that row's snapshot_id stays the BASE while it is mapped into the
            // overlay via snapshot_projects. A projects.snapshot_id = @snap filter would drop every
            // shared (unchanged) project under an overlay; membership resolution includes them. For a
            // full snapshot the two are equivalent (every member row is tagged with @snap).
            cmd.CommandText = SelectProject
                + " WHERE p.id IN (SELECT project_id FROM snapshot_projects WHERE snapshot_id = @snap)"
                + " AND lp.canonical_id = @canon LIMIT 1;";
            cmd.Parameters.AddWithValue("@snap", snap);
            cmd.Parameters.AddWithValue("@canon", canonicalId);
        }
        else
        {
            // Legacy / pre-first-publish: match the mutable legacy row by its stored canonical id.
            cmd.CommandText = SelectProject + " WHERE p.snapshot_id IS NULL AND p.canonical_id = @canon LIMIT 1;";
            cmd.Parameters.AddWithValue("@canon", canonicalId);
        }

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProjectRow(reader) : null;
    }

    public (long id, ProjectIdentity project, long lastIndexedAt)? GetById(long id)
    {
        // By row id: a project id already identifies exactly one snapshot's version, so no snapshot
        // filter is applied — only the client-facing logical canonical id is surfaced.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectProject + " WHERE p.id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProjectRow(reader) : null;
    }

    public List<(long id, ProjectIdentity project)> GetAll()
    {
        var selected = Snapshots.GetSelectedSnapshotId();
        using var cmd = connection.CreateCommand();
        if (selected is long snap)
        {
            // Default to the selected snapshot's project versions (criterion 6): a scope-less caller
            // never iterates another (pending/superseded) snapshot's or a swept legacy row. Membership
            // is resolved through snapshot_projects (not projects.snapshot_id) so an overlay's SHARED,
            // unchanged project-version rows — which physically keep the base snapshot's id — are still
            // enumerated (Phase-10 overlays). For a full snapshot the two are equivalent.
            cmd.CommandText = SelectProject
                + " WHERE p.id IN (SELECT project_id FROM snapshot_projects WHERE snapshot_id = @snap);";
            cmd.Parameters.AddWithValue("@snap", snap);
        }
        else
        {
            cmd.CommandText = SelectProject + " WHERE p.snapshot_id IS NULL;";
        }

        var results = new List<(long, ProjectIdentity)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var (id, project, _) = ReadProjectRow(reader);
            results.Add((id, project));
        }
        return results;
    }

    private const string SelectProject = """
        SELECT p.id, COALESCE(lp.canonical_id, p.canonical_id) AS canonical_id, p.git_remote_url,
               p.repo_relative_path, p.disk_path, p.assembly_name, p.target_framework, p.is_test_project,
               p.last_indexed_at
        FROM projects p
        LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
        """;

    private static (long id, ProjectIdentity project, long lastIndexedAt) ReadProjectRow(SqliteDataReader reader) => (
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
        reader.GetInt64(8));
}
