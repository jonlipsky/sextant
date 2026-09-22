using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class ProjectStore(SqliteConnection connection)
{
    /// <summary>
    /// The Phase-11 pinned read scope (issue #42). When set, project resolution reuses this ONE scope —
    /// resolved once for the whole MCP request by <c>FederatedReadContext</c> — instead of independently
    /// re-reading the mutable selected branch pointer, so a publish that lands mid-request can never
    /// splice a different generation's project rows into a request already pinned to another (symbols from
    /// generation A, project/canonical-id resolution from generation B). Null (the write path and any
    /// unpinned caller) resolves the current selected snapshot per call via
    /// <see cref="SnapshotReadScope.ForSelected"/>, exactly as before.
    /// </summary>
    public SnapshotReadScope? Scope { get; set; }

    private SnapshotReadScope EffectiveScope => Scope ?? SnapshotReadScope.ForSelected(connection);

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
    /// leaks to clients — read methods surface the logical canonical id (<c>logical_projects.canonical_id</c>).
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
        var scope = EffectiveScope;
        using var cmd = connection.CreateCommand();
        // Match the LOGICAL canonical id — lp.canonical_id within a snapshot, or the mutable row's
        // p.canonical_id for a legacy row (COALESCE resolves whichever applies) — restricted to the
        // pinned scope's project versions. The scope fragment resolves membership via snapshot_projects
        // (required for Phase-10 overlays, whose SHARED unchanged rows physically keep the BASE snapshot's
        // id), the legacy rows when legacy-pinned, no filter for a pure legacy DB, or no rows for a
        // deny-all (fail-closed) scope — the SAME authoritative scope every other store uses.
        cmd.CommandText = SelectProject
            + " WHERE COALESCE(lp.canonical_id, p.canonical_id) = @canon"
            + scope.And("p.id") + " LIMIT 1;";
        cmd.Parameters.AddWithValue("@canon", canonicalId);
        scope.Bind(cmd);

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
        var scope = EffectiveScope;
        using var cmd = connection.CreateCommand();
        // Default to the pinned scope's project versions (criterion 6): a scope-less caller never
        // iterates another (pending/superseded) snapshot's or a swept legacy row. Membership is resolved
        // through snapshot_projects (not projects.snapshot_id) so an overlay's SHARED, unchanged
        // project-version rows — which physically keep the base snapshot's id — are still enumerated
        // (Phase-10 overlays). A pure legacy DB is unscoped (all rows are legacy); a deny-all scope
        // yields no rows.
        cmd.CommandText = SelectProject + scope.Where("p.id") + ";";
        scope.Bind(cmd);

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
