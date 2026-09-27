using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// A prod-shaped synthetic service catalog for the issue-#160 cross-repository usage tests. One provider
/// (submodule) repository publishes a target type (<see cref="TargetKey"/>). A MONOREPO consumer has many
/// logical projects, each carrying many pure references to its own symbols plus call edges, and every one
/// of its projects has a dependency edge to the provider. A second, small consumer repository references
/// the target too, so the authorization filter has something to exclude. The bulk rows are generated with
/// recursive-CTE INSERT ... SELECT statements inside one explicit write transaction, so a catalog of
/// hundreds of thousands of occurrences seeds in about a second.
/// </summary>
internal sealed class CrossRepoUsageSeed
{
    public const string ProviderUrl = "https://github.com/org/mixandmatch";
    public const string MonorepoUrl = "https://github.com/org/monorepo";
    public const string OtherUrl = "https://github.com/org/other-app";
    public const string TargetKey = "T:Mix.ImageButton";
    public const string MonorepoHeadCommit = "mono_head";
    public const int OtherTargetUsages = 5;

    private const int ProviderProjects = 4;
    private const int ProviderFillerSymbolsPerProject = 50;
    private const int ConsumerSymbolsPerProject = 20;

    private CrossRepoUsageSeed(long monorepoId, long otherRepoId, int monorepoTargetUsages)
    {
        MonorepoId = monorepoId;
        OtherRepoId = otherRepoId;
        MonorepoTargetUsages = monorepoTargetUsages;
    }

    public long MonorepoId { get; }
    public long OtherRepoId { get; }
    public int MonorepoTargetUsages { get; }

    private readonly record struct Provider(long RepoId, long SnapshotId, long[] ProjectIds);

    /// <summary>
    /// Seeds the catalog. The monorepo gets <paramref name="logicalProjects"/> projects,
    /// <paramref name="pureReferences"/> pure references (plus a quarter as many call edges) to its own
    /// symbols, and <paramref name="targetUsages"/> pure references to the provider target.
    /// </summary>
    public static CrossRepoUsageSeed Create(SqliteConnection conn, int logicalProjects, int pureReferences, int targetUsages)
    {
        Exec(conn, "BEGIN IMMEDIATE;");
        try
        {
            var provider = SeedProvider(conn);
            var mono = SeedConsumer(conn, provider, MonorepoUrl, MonorepoHeadCommit, "mono", logicalProjects, pureReferences, targetUsages);
            var other = SeedConsumer(conn, provider, OtherUrl, "other_head", "other", 3, 100, OtherTargetUsages);
            Exec(conn, "DROP TABLE IF EXISTS temp.seed_cp; DROP TABLE IF EXISTS temp.seed_sym;");
            Exec(conn, "COMMIT;");
            return new CrossRepoUsageSeed(mono, other, targetUsages);
        }
        catch
        {
            Exec(conn, "ROLLBACK;");
            throw;
        }
    }

    private static Provider SeedProvider(SqliteConnection conn)
    {
        var store = new SnapshotStore(conn);
        var repoId = store.EnsureProviderRepository(ProviderUrl, now: 1);
        var commitId = store.EnsureCommit(repoId, "prov_commit", null, now: 1);
        var (snapshotId, _, _) = store.BeginPending(Identity(ProviderUrl, "prov_commit", provider: true), repoId, commitId, null, now: 1, isProvider: true);
        var projectIds = new long[ProviderProjects];
        for (var p = 0; p < ProviderProjects; p++)
            projectIds[p] = AddProject(conn, store, repoId, snapshotId, ProviderUrl, $"logical_mix_{p}", $"lib/Mix{p}/Mix{p}.csproj");

        Exec(conn, $"""
            INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility, line_start, line_end, last_indexed_at)
            VALUES ({projectIds[0]}, '{TargetKey}', 'global::Mix.ImageButton', 'ImageButton', 0, 0, 1, 1, 1);
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {ProviderFillerSymbolsPerProject - 1})
            INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility, line_start, line_end, last_indexed_at)
            SELECT p.id, printf('T:Mix.P%d.S%d', p.id, n.i), printf('global::Mix.P%d.S%d', p.id, n.i), printf('S%d', n.i), 0, 0, 1, 1, 1
            FROM n CROSS JOIN projects p WHERE p.snapshot_id = {snapshotId};
            """);
        store.MarkComplete(snapshotId, publishedAt: 1);
        return new Provider(repoId, snapshotId, projectIds);
    }

    private static long SeedConsumer(
        SqliteConnection conn, Provider provider, string url, string commitSha, string prefix,
        int logicalProjects, int pureReferences, int targetUsages)
    {
        var store = new SnapshotStore(conn);
        var repoId = store.EnsureRepository(url, now: 1);
        var commitId = store.EnsureCommit(repoId, commitSha, $"tree_{commitSha}", now: 1);
        var (snapshotId, _, _) = store.BeginPending(Identity(url, commitSha, provider: false), repoId, commitId, null, now: 1);

        Exec(conn, """
            DROP TABLE IF EXISTS temp.seed_cp;
            DROP TABLE IF EXISTS temp.seed_sym;
            CREATE TEMP TABLE seed_cp (ord INTEGER PRIMARY KEY, project_id INTEGER NOT NULL, fv_id INTEGER);
            CREATE TEMP TABLE seed_sym (ord INTEGER PRIMARY KEY, symbol_id INTEGER NOT NULL);
            """);
        for (var i = 0; i < logicalProjects; i++)
        {
            var projectId = AddProject(conn, store, repoId, snapshotId, url, $"{prefix}_{i:D4}", $"src/P{i:D4}/P{i:D4}.csproj");
            Exec(conn, $"INSERT INTO seed_cp (ord, project_id) VALUES ({i}, {projectId});");
        }

        // One file version per project, then ConsumerSymbolsPerProject symbols per project, numbered 1..S.
        Exec(conn, $"""
            INSERT INTO files (project_id, repo_relative_path)
            SELECT project_id, printf('src/P%04d/Use.cs', ord) FROM seed_cp;
            INSERT INTO file_versions (file_id, content_hash, last_indexed_at)
            SELECT f.id, CAST(printf('hash_%d', f.id) AS BLOB), 1 FROM files f JOIN seed_cp c ON c.project_id = f.project_id;
            UPDATE seed_cp SET fv_id = (
                SELECT fv.id FROM file_versions fv JOIN files f ON f.id = fv.file_id WHERE f.project_id = seed_cp.project_id);
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {ConsumerSymbolsPerProject - 1})
            INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility, line_start, line_end, last_indexed_at)
            SELECT c.project_id, printf('M:{prefix}.P%d.S%d', c.ord, n.i), printf('global::{prefix}.P%d.S%d', c.ord, n.i), printf('S%d', n.i), 0, 0, 1, 1, 1
            FROM seed_cp c CROSS JOIN n;
            INSERT INTO seed_sym (symbol_id)
            SELECT s.id FROM symbols s JOIN seed_cp c ON c.project_id = s.project_id ORDER BY s.id;
            """);

        var symbolCount = logicalProjects * ConsumerSymbolsPerProject;
        var kind = (int)ReferenceKind.Invocation;
        var callEdges = Math.Max(1, pureReferences / 4);
        var targetSymbolId = Scalar(conn, $"SELECT id FROM symbols WHERE project_id = {provider.ProjectIds[0]} AND symbol_key = '{TargetKey}';");

        // Bulk intra-repo traffic: pure references, a quarter as many call edges, then the target usages
        // (every third also emits its call edge, which the usage query must not double-count).
        Exec(conn, $"""
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {pureReferences - 1})
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
            SELECT c.project_id, s.symbol_id, NULL, c.fv_id, n.i / {logicalProjects} + 1, 1, {kind}, 0
            FROM n CROSS JOIN seed_cp c CROSS JOIN seed_sym s
            WHERE c.ord = n.i % {logicalProjects} AND s.ord = (n.i * 7919) % {symbolCount} + 1;

            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {callEdges - 1})
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
            SELECT c.project_id, s.symbol_id, src.symbol_id, c.fv_id, n.i / {logicalProjects} + 1, 1, {kind}, 0
            FROM n CROSS JOIN seed_cp c CROSS JOIN seed_sym s CROSS JOIN seed_sym src
            WHERE c.ord = n.i % {logicalProjects} AND s.ord = (n.i * 7919) % {symbolCount} + 1
              AND src.ord = (n.i * 104729) % {symbolCount} + 1;

            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {targetUsages - 1})
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
            SELECT c.project_id, {targetSymbolId}, NULL, c.fv_id, 100000 + n.i, 3, {kind}, 0
            FROM n CROSS JOIN seed_cp c WHERE c.ord = (n.i * 7) % {logicalProjects};

            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 3 FROM n WHERE i + 3 < {targetUsages})
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
            SELECT c.project_id, {targetSymbolId}, s.symbol_id, c.fv_id, 100000 + n.i, 3, {kind}, 0
            FROM n CROSS JOIN seed_cp c CROSS JOIN seed_sym s
            WHERE c.ord = (n.i * 7) % {logicalProjects} AND s.ord = c.ord * {ConsumerSymbolsPerProject} + 1;
            """);

        // Every consumer project depends on provider project 0 (which defines the target) and on one more.
        Exec(conn, $"""
            INSERT INTO snapshot_dependencies
                (consumer_snapshot_id, consumer_project_id, provider_snapshot_id, provider_project_id,
                 provider_repository_id, provider_commit_sha, reference_kind, submodule_dirty, created_at)
            SELECT {snapshotId}, c.project_id, {provider.SnapshotId}, {provider.ProjectIds[0]}, {provider.RepoId}, 'prov_commit', 'submodule_ref', 0, 1
            FROM seed_cp c
            UNION ALL
            SELECT {snapshotId}, c.project_id, {provider.SnapshotId},
                   CASE c.ord % 3 WHEN 0 THEN {provider.ProjectIds[1]} WHEN 1 THEN {provider.ProjectIds[2]} ELSE {provider.ProjectIds[3]} END,
                   {provider.RepoId}, 'prov_commit', 'submodule_ref', 0, 1
            FROM seed_cp c;
            """);

        store.MarkComplete(snapshotId, publishedAt: 1);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 2);
        store.SetBranchPointer(branchId, snapshotId, now: 3);
        return repoId;
    }

    private static long AddProject(
        SqliteConnection conn, SnapshotStore store, long repoId, long snapshotId, string url, string canonical, string relPath)
    {
        var logicalId = store.EnsureLogicalProject(repoId, canonical, relPath, "net10.0", now: 1);
        var projectId = new ProjectStore(conn).UpsertSnapshotProject(new ProjectIdentity
        {
            CanonicalId = canonical,
            GitRemoteUrl = url,
            RepoRelativePath = relPath,
            TargetFramework = "net10.0"
        }, snapshotId, logicalId, lastIndexedAt: 1);
        store.MapProject(snapshotId, projectId);
        return projectId;
    }

    private static SnapshotIdentity Identity(string url, string commitSha, bool provider) => new()
    {
        RepositoryRemoteUrl = url,
        CommitSha = commitSha,
        TreeSha = provider ? null : $"tree_{commitSha}",
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc",
        IsOverlay = false
    };

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
