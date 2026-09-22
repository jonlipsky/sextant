using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Service.Contributions;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>One project version to place in a synthetic contribution payload.</summary>
internal sealed record PayloadProjectSpec(string RepoRelativePath, string? TargetFramework = null, int SymbolCount = 2);

/// <summary>
/// Hermetic fixtures for Phase-16 contribution tests. They build a REAL payload catalog (a portable Sextant
/// catalog containing one complete snapshot stamped with a capability) and then produce the contribution
/// manifest + artifact through the PRODUCTION <see cref="ContributionManifestBuilder"/> and
/// <see cref="ContributionArtifact"/> — so tests exercise the real derivation/packing, not a hand-rolled
/// double. No MSBuild, no git, no network: the payload's semantic rows are inserted directly, exactly as a
/// real orchestrator run would leave them.
/// </summary>
internal static class ContributionTestFixtures
{
    // Fixed manifest creation timestamp so the manifest derivation is byte-deterministic in tests
    // (the only otherwise-varying manifest field is CreatedAtUnixMs).
    private const long ManifestCreatedAtUnixMs = 1_700_000_000_000L;

    /// <summary>
    /// Builds a content-addressed contribution artifact for <paramref name="projects"/> at the given
    /// repo/commit, produced under <paramref name="capabilityFingerprint"/>. <paramref name="mutateManifest"/>
    /// lets a negative test tamper with the manifest AFTER it is built from the honest payload (e.g. flip
    /// the dirty flag, corrupt the schema) so the rejection paths can be driven without a doctored payload.
    /// </summary>
    public static ContributionArtifact BuildArtifact(
        string repository,
        string commit,
        string capabilityFingerprint,
        IReadOnlyList<PayloadProjectSpec> projects,
        string tenant = "octo",
        string producer = "ci-runner",
        bool workingTreeDirty = false,
        bool stableCanonicalIds = false,
        Func<ContributionManifest, ContributionManifest>? mutateManifest = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sextant_payload_{Guid.NewGuid():N}.db");
        string identityHash;
        byte[] payloadBytes;
        ContributionManifest manifest;

        try
        {
            using (var db = new IndexDatabase(dbPath))
            {
                db.RunMigrations();
                identityHash = BuildCompletePayloadSnapshot(db, repository, commit, capabilityFingerprint, projects, stableCanonicalIds);
            }
            SqliteConnection.ClearAllPools();

            using (var conn = OpenReadOnly(dbPath))
            {
                manifest = ContributionManifestBuilder.Build(conn, identityHash, new ContributionProvenance
                {
                    Tenant = tenant,
                    Producer = producer,
                    CliVersion = "test-cli",
                    WorkingTreeDirty = workingTreeDirty
                }, ManifestCreatedAtUnixMs);
            }

            if (mutateManifest is not null)
                manifest = mutateManifest(manifest);

            payloadBytes = File.ReadAllBytes(dbPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
        }

        return ContributionArtifact.Create(manifest, payloadBytes);
    }

    /// <summary>Inserts a complete snapshot (repo + commit + run + snapshot + projects/symbols) and returns its identity hash.</summary>
    private static string BuildCompletePayloadSnapshot(
        IndexDatabase db, string repository, string commit, string capabilityFingerprint,
        IReadOnlyList<PayloadProjectSpec> projects, bool stableCanonicalIds = false)
    {
        var conn = db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = repository,
            CommitSha = commit,
            TreeSha = "tree-" + commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
            ConfigHash = null,
            ToolchainFingerprint = ToolchainFingerprint.Current,
            CapabilityFingerprint = capabilityFingerprint
        };

        var repoId = snapshots.EnsureRepository(repository, now);
        var commitId = snapshots.EnsureCommit(repoId, commit, identity.TreeSha, now);
        var runStore = new IndexRunStore(conn);
        var runId = runStore.BeginRun("full", now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, now, projects.Count);

        var (snapId, _, _) = snapshots.BeginPending(identity, repoId, commitId, runId, now);

        var projectIndex = 0;
        foreach (var spec in projects)
        {
            // The commit-invariant logical canonical id is (git-remote|repo-relative-path|tfm). A stable id
            // lets a test model the SAME logical project version arriving in two payloads (overlap detection);
            // the default appends the snapshot id + index to keep every fixture project row trivially unique.
            var canonical = stableCanonicalIds
                ? $"{repository}|{spec.RepoRelativePath}|{spec.TargetFramework ?? "net8.0"}"
                : $"{repository}|{spec.RepoRelativePath}|{spec.TargetFramework ?? "net8.0"}:{snapId}:{projectIndex}";
            long projectId;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO projects
                        (canonical_id, git_remote_url, repo_relative_path, target_framework, last_indexed_at,
                         snapshot_id, capability_fingerprint)
                    VALUES (@c, @g, @p, @tfm, @now, @snap, @cap) RETURNING id;
                    """;
                cmd.Parameters.AddWithValue("@c", canonical);
                cmd.Parameters.AddWithValue("@g", repository);
                cmd.Parameters.AddWithValue("@p", spec.RepoRelativePath);
                cmd.Parameters.AddWithValue("@tfm", (object?)spec.TargetFramework ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@now", now);
                cmd.Parameters.AddWithValue("@snap", snapId);
                cmd.Parameters.AddWithValue("@cap", capabilityFingerprint);
                projectId = (long)cmd.ExecuteScalar()!;
            }
            snapshots.MapProject(snapId, projectId);

            var filePath = spec.RepoRelativePath.Replace(".csproj", ".cs");
            long fileId;
            using (var f = conn.CreateCommand())
            {
                f.CommandText = "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
                f.Parameters.AddWithValue("@p", projectId);
                f.Parameters.AddWithValue("@path", filePath);
                fileId = (long)f.ExecuteScalar()!;
            }

            long fvId;
            using (var fv = conn.CreateCommand())
            {
                fv.CommandText =
                    "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@f, @h, @now) RETURNING id;";
                fv.Parameters.AddWithValue("@f", fileId);
                fv.Parameters.AddWithValue("@h", Hash(projectId));
                fv.Parameters.AddWithValue("@now", now);
                fvId = (long)fv.ExecuteScalar()!;
            }

            for (var i = 0; i < spec.SymbolCount; i++)
            {
                using var s = conn.CreateCommand();
                s.CommandText = """
                    INSERT INTO symbols
                        (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                         file_version_id, line_start, line_end, last_indexed_at)
                    VALUES (@p, @key, @fqn, @name, 0, 0, @fv, 1, 10, @now);
                    """;
                s.Parameters.AddWithValue("@p", projectId);
                s.Parameters.AddWithValue("@key", $"global::P{projectIndex}.Type{i}:{snapId}");
                s.Parameters.AddWithValue("@fqn", $"global::P{projectIndex}.Type{i}");
                s.Parameters.AddWithValue("@name", $"Type{i}");
                s.Parameters.AddWithValue("@fv", fvId);
                s.Parameters.AddWithValue("@now", now);
                s.ExecuteNonQuery();
            }

            projectIndex++;
        }

        snapshots.MarkComplete(snapId, now);
        return identity.Hash;
    }

    /// <summary>Reads the count of snapshot-project rows mapped for the target repository's complete assembly snapshot.</summary>
    public static (long snapshotId, int projectCount, string?[] capabilities) ReadAssembly(IndexDatabase db, string identityHash)
    {
        var snapshots = new SnapshotStore(db.GetConnection());
        var snapshot = snapshots.GetByIdentityHash(identityHash)!;
        var projectIds = snapshots.GetSnapshotProjectIds(snapshot.Id);
        var capabilities = new List<string?>();
        foreach (var id in projectIds)
        {
            using var cmd = db.GetConnection().CreateCommand();
            cmd.CommandText = "SELECT capability_fingerprint FROM projects WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            var value = cmd.ExecuteScalar();
            capabilities.Add(value as string);
        }
        return (snapshot.Id, projectIds.Count, capabilities.ToArray());
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        conn.Open();
        return conn;
    }

    private static byte[] Hash(long seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((seed + i) & 0xFF);
        return bytes;
    }

    private static void TryDelete(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(path + suffix)) File.Delete(path + suffix); } catch { /* best effort */ }
        }
    }
}
